using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using static Verse.DamageWorker;

namespace KRWF.RimKata
{
    // Register only when DAC is loaded. No shared damage hook or tick polling.
    internal static class RimKataDynamicAnimeCombatCompat
    {
        private struct MeleeFrame
        {
            internal Verb_MeleeAttack verb;
            internal Pawn attacker, defender;
            internal int sequence;
            internal bool started, resolved, avoided, parried;
        }

        private struct MeleeState
        {
            internal MeleeFrame previous;
            internal bool entered;
        }

        private sealed class DamageScope
        {
            internal DamageScope previous;
            internal Pawn attacker, defender;
            internal Verb_MeleeAttack verb;
            internal int meleeSequence;
            internal bool avoided, parried, reused;
            internal BattleLogEntry_MeleeCombat log;
            internal DamageResult combined;
        }

        private struct GlancingState
        {
            internal DamageScope scope;
            internal Pawn previousSuppressed;
        }

        [ThreadStatic] private static MeleeFrame currentMelee;
        [ThreadStatic] private static int nextMeleeSequence;
        [ThreadStatic] private static DamageScope currentDamage;
        [ThreadStatic] private static Pawn suppressedSpiritPawn;
        private static readonly MethodInfo TakeDamage = AccessTools.Method(typeof(Thing),
            nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) });
        private static readonly MethodInfo DodgeChance = AccessTools.Method(typeof(Verb_MeleeAttack), "GetDodgeChance");
        private static readonly MethodInfo RandChance = AccessTools.Method(typeof(Rand), nameof(Rand.Chance), new[] { typeof(float) });
        private static MethodInfo notifyDamageTaken;
        private static Action<ThingComp, float> notifySpiritDamage;
        private static AccessTools.StructFieldRef<DamageInfo, Thing> damageInstigator;

        internal static void Apply()
        {
            Type mechanics = AccessTools.TypeByName("DynamicAnimeCombat.Core.CombatMechanics");
            if (mechanics == null) return;
            var harmony = new Harmony("krwf.rimkata.dynamic-anime-combat");
            try
            {
                Type glancing = AccessTools.TypeByName("DynamicAnimeCombat.Core.GlancingBlowSystem");
                Type spirit = AccessTools.TypeByName("DynamicAnimeCombat.Components.CompFightingSpirit");
                MethodInfo glance = AccessTools.DeclaredMethod(glancing, "PerformGlancingBlow",
                    new[] { typeof(Pawn), typeof(Pawn), typeof(Verb_MeleeAttack) });
                MethodInfo parry = AccessTools.DeclaredMethod(mechanics, "TryParry");
                MethodInfo deflect = AccessTools.DeclaredMethod(mechanics, "TryDeflect");
                notifyDamageTaken = AccessTools.DeclaredMethod(spirit, "Notify_DamageTaken", new[] { typeof(float) });
                if (glance?.ReturnType != typeof(void) || parry?.ReturnType != typeof(bool)
                    || deflect?.ReturnType != typeof(bool) || notifyDamageTaken?.ReturnType != typeof(void)
                    || !typeof(ThingComp).IsAssignableFrom(spirit))
                    throw new InvalidOperationException("DAC direct-damage API did not match.");
                ValidateDamageCalls(PatchProcessor.GetOriginalInstructions(glance), 1);
                ValidateDamageCalls(PatchProcessor.GetOriginalInstructions(parry), 2);
                ValidateDamageCalls(PatchProcessor.GetOriginalInstructions(deflect), 1);
                damageInstigator = AccessTools.StructFieldRefAccess<DamageInfo, Thing>("instigatorInt");
                var comp = Expression.Parameter(typeof(ThingComp));
                var amount = Expression.Parameter(typeof(float));
                notifySpiritDamage = Expression.Lambda<Action<ThingComp, float>>(
                    Expression.Call(Expression.Convert(comp, spirit), notifyDamageTaken, amount), comp, amount).Compile();

                MethodInfo melee = AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "TryCastShot");
                harmony.Patch(melee, prefix: Patch(nameof(MeleePrefix), Priority.First),
                    transpiler: Patch(nameof(MeleeTranspiler)), finalizer: Patch(nameof(MeleeFinalizer), Priority.Last));
                harmony.Patch(AccessTools.Method(typeof(RimKataDefenseUtility), nameof(RimKataDefenseUtility.TryResolveMeleeParry),
                    new[] { typeof(Verb_MeleeAttack) }), postfix: Patch(nameof(ParryPostfix)));
                // CE overrides the whole attack; keep the same outcome scope when both mods are installed.
                Type ce = AccessTools.TypeByName("CombatExtended.Verb_MeleeAttackCE");
                MethodInfo ceAttack = ce == null ? null : AccessTools.DeclaredMethod(ce, "TryCastShot");
                if (ceAttack != null)
                {
                    harmony.Patch(ceAttack, prefix: Patch(nameof(MeleePrefix), Priority.First),
                        transpiler: Patch(nameof(MeleeTranspiler)), finalizer: Patch(nameof(MeleeFinalizer), Priority.Last));
                    harmony.Patch(AccessTools.Method(typeof(RimKataDefenseUtility),
                        nameof(RimKataDefenseUtility.TryResolveCombatExtendedMeleeDefense)), postfix: Patch(nameof(CeDefensePostfix)));
                }
                harmony.Patch(glance, prefix: Patch(nameof(GlancingPrefix)),
                    transpiler: Patch(nameof(GlancingTranspiler)), finalizer: Patch(nameof(GlancingFinalizer)));
                harmony.Patch(parry, prefix: Patch(nameof(SecondaryPrefix)),
                    transpiler: Patch(nameof(ParryTranspiler)), finalizer: Patch(nameof(SecondaryFinalizer)));
                harmony.Patch(deflect, prefix: Patch(nameof(SecondaryPrefix)),
                    transpiler: Patch(nameof(DeflectTranspiler)), finalizer: Patch(nameof(SecondaryFinalizer)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] Dynamic Anime Combat integration was not applied: " + exception.Message);
            }
        }

        private static HarmonyMethod Patch(string name, int priority = Priority.Normal)
            => new HarmonyMethod(AccessTools.Method(typeof(RimKataDynamicAnimeCombatCompat), name)) { priority = priority };

        private static void MeleePrefix(Verb_MeleeAttack __instance, out MeleeState __state)
        {
            __state = new MeleeState { previous = currentMelee, entered = true };
            currentMelee = new MeleeFrame
            {
                verb = __instance, attacker = __instance.CasterPawn, defender = __instance.CurrentTarget.Pawn,
                sequence = unchecked(++nextMeleeSequence)
            };
        }

        private static Exception MeleeFinalizer(Exception __exception, MeleeState __state)
        {
            // DAC adds damage in its Postfix, so the outcome must survive every Postfix.
            if (__state.entered) currentMelee = __state.previous;
            return __exception;
        }

        private static void MarkStarted(Verb_MeleeAttack verb)
        {
            if (currentMelee.verb == verb) currentMelee.started = true;
        }

        private static bool RecordDodge(bool avoided, Verb_MeleeAttack verb)
        {
            RecordDefense(verb, avoided, false);
            return avoided;
        }

        private static void ParryPostfix(Verb_MeleeAttack attackingVerb, bool __result)
            => RecordDefense(attackingVerb, __result, __result);

        private static void CeDefensePostfix(Verb_MeleeAttack attackingVerb, bool __result, bool parried)
            => RecordDefense(attackingVerb, __result, parried);

        private static void RecordDefense(Verb_MeleeAttack verb, bool avoided, bool parried)
        {
            if (currentMelee.verb != verb) return;
            currentMelee.resolved = true;
            currentMelee.avoided = avoided;
            currentMelee.parried = parried;
        }

        private static IEnumerable<CodeInstruction> MeleeTranspiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            var codes = new List<CodeInstruction>(instructions);
            int dodgeIndex = codes.FindIndex(code => code.Calls(DodgeChance));
            int startIndex = 0;
            if (__originalMethod.DeclaringType == typeof(Verb_MeleeAttack))
            {
                if (dodgeIndex < 0 || dodgeIndex + 1 >= codes.Count || !codes[dodgeIndex + 1].Calls(RandChance))
                    throw new InvalidOperationException("Melee dodge outcome hook was not found.");
                codes.InsertRange(dodgeIndex + 2, new[] { new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataDynamicAnimeCombatCompat), nameof(RecordDodge))) });
                MethodInfo hitChance = AccessTools.Method(typeof(Verb_MeleeAttack), "GetNonMissChance");
                startIndex = codes.FindIndex(code => code.Calls(hitChance));
                if (startIndex < 0) throw new InvalidOperationException("Melee attack start hook was not found.");
            }
            // FullBodyBusy and other early returns are not completed attack attempts.
            codes.InsertRange(startIndex, new[] { new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataDynamicAnimeCombatCompat), nameof(MarkStarted))) });
            return codes;
        }

        private static bool MatchesMelee(Pawn attacker, Pawn defender, Verb_MeleeAttack verb = null)
            => currentMelee.verb != null && currentMelee.attacker == attacker && currentMelee.defender == defender
                && (verb == null || currentMelee.verb == verb);

        private static DamageScope CreateScope(Pawn attacker, Pawn defender, Verb_MeleeAttack verb)
        {
            var scope = new DamageScope
            {
                previous = currentDamage, attacker = attacker, defender = defender, verb = verb,
                meleeSequence = currentMelee.sequence
            };
            for (DamageScope parent = currentDamage; parent != null; parent = parent.previous)
            {
                if (parent.attacker != attacker || parent.defender != defender
                    || parent.meleeSequence != currentMelee.sequence) continue;
                scope.reused = true;
                scope.avoided = parent.avoided;
                scope.parried = parent.parried;
                return scope;
            }
            if (MatchesMelee(attacker, defender, verb) && currentMelee.resolved)
            {
                scope.reused = true;
                scope.avoided = currentMelee.avoided;
                scope.parried = currentMelee.parried;
            }
            else
                scope.avoided = RimKataDefenseUtility.TryResolveDirectMeleeDefense(defender, attacker, verb, out scope.parried);
            return scope;
        }

        private static bool GlancingPrefix(Pawn attacker, Pawn victim, Verb_MeleeAttack verb, out GlancingState __state)
        {
            __state = new GlancingState { previousSuppressed = suppressedSpiritPawn };
            suppressedSpiritPawn = null;
            if (attacker == null || victim == null || verb == null || !attacker.Spawned || !victim.Spawned
                || attacker.Dead || victim.Dead || attacker.Destroyed || victim.Destroyed) return true;
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(victim)) return true;
            // A skipped/replaced attack must not turn into DAC damage.
            if (MatchesMelee(attacker, victim, verb) && !currentMelee.started) return false;
            DamageScope scope = CreateScope(attacker, victim, verb);
            __state.scope = scope;
            currentDamage = scope;
            if (!scope.avoided) return true;
            if (!scope.reused) scope.log = CreateMeleeLog(scope, verb?.EquipmentSource?.def);
            return false;
        }

        private static Exception GlancingFinalizer(Exception __exception, GlancingState __state)
        {
            try
            {
                if (__state.scope != null) FinishScope(__state.scope);
            }
            finally
            {
                if (__state.scope != null) currentDamage = __state.scope.previous;
                suppressedSpiritPawn = __state.previousSuppressed;
            }
            return __exception;
        }

        private static void SecondaryPrefix(out Pawn __state)
        {
            __state = suppressedSpiritPawn;
            suppressedSpiritPawn = null;
        }

        private static Exception SecondaryFinalizer(Exception __exception, Pawn __state)
        {
            suppressedSpiritPawn = __state;
            return __exception;
        }

        private static void ValidateDamageCalls(List<CodeInstruction> codes, int damageCount)
        {
            // Older DAC only notifies partial parries; newer DAC also notifies
            // the counterblow victim. Keep the exact direct-damage call count.
            int spiritCount = codes.FindAll(code => code.Calls(notifyDamageTaken)).Count;
            if (codes.FindAll(code => code.Calls(TakeDamage)).Count != damageCount
                || spiritCount < 1 || spiritCount > damageCount)
                throw new InvalidOperationException("DAC direct-damage instructions changed.");
        }

        private static IEnumerable<CodeInstruction> GlancingTranspiler(IEnumerable<CodeInstruction> instructions)
            => ReplaceDamageCalls(instructions, 1, nameof(ApplyMeleeDamage));

        private static IEnumerable<CodeInstruction> ParryTranspiler(IEnumerable<CodeInstruction> instructions)
            => ReplaceDamageCalls(instructions, 2, nameof(ApplyParryDamage));

        private static IEnumerable<CodeInstruction> DeflectTranspiler(IEnumerable<CodeInstruction> instructions)
            => ReplaceDamageCalls(instructions, 1, nameof(ApplyRangedDamage));

        private static IEnumerable<CodeInstruction> ReplaceDamageCalls(IEnumerable<CodeInstruction> instructions,
            int count, string damageMethod)
        {
            var codes = new List<CodeInstruction>(instructions);
            ValidateDamageCalls(codes, count);
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                string replacement = code.Calls(TakeDamage) ? damageMethod
                    : code.Calls(notifyDamageTaken) ? nameof(NotifySpiritDamage) : null;
                if (replacement == null) continue;
                if (replacement == nameof(ApplyParryDamage))
                {
                    // TryParry's defender is the actual counterblow attacker.
                    // Pass it at the damage boundary, including nested parries.
                    var pawn = new CodeInstruction(OpCodes.Ldarg_0);
                    pawn.labels.AddRange(code.labels);
                    code.labels.Clear();
                    pawn.blocks.AddRange(code.blocks);
                    code.blocks.Clear();
                    codes.Insert(i++, pawn);
                }
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(RimKataDynamicAnimeCombatCompat), replacement);
            }
            return codes;
        }

        private static DamageResult ApplyMeleeDamage(Thing target, DamageInfo info)
            => ApplyDirectMeleeDamage(target, info, null);

        private static DamageResult ApplyParryDamage(Thing target, DamageInfo info, Pawn parryingPawn)
            => ApplyDirectMeleeDamage(target, info, parryingPawn);

        private static DamageResult ApplyDirectMeleeDamage(Thing target, DamageInfo info, Pawn parryingPawn)
        {
            if (!(target is Pawn defender) || !(info.Instigator is Pawn)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(defender)) return target.TakeDamage(info);
            // New DAC reuses the incoming DamageInfo for counterblows. Correct
            // only that self-attributed copy; preserve CE damage and armor data.
            if (parryingPawn != null && defender != parryingPawn && info.Instigator == defender)
                damageInstigator(ref info) = parryingPawn;
            Pawn attacker = (Pawn)info.Instigator;
            // The partial block nested inside a glancing blow shares that blow's decision and log.
            if (currentDamage != null && currentDamage.attacker == attacker && currentDamage.defender == defender
                && currentDamage.meleeSequence == currentMelee.sequence)
                return ApplyScopedDamage(currentDamage, target, info);
            Verb_MeleeAttack verb = MatchesMelee(attacker, defender) ? currentMelee.verb : null;
            DamageScope scope = CreateScope(attacker, defender, verb);
            currentDamage = scope;
            try { return ApplyScopedDamage(scope, target, info); }
            finally
            {
                try { FinishScope(scope); }
                finally { currentDamage = scope.previous; }
            }
        }

        private static DamageResult ApplyScopedDamage(DamageScope scope, Thing target, DamageInfo info)
        {
            if (scope.log == null && (!scope.avoided || !scope.reused)) scope.log = CreateMeleeLog(scope, info.Weapon);
            if (scope.avoided)
            {
                suppressedSpiritPawn = scope.defender;
                return new DamageResult();
            }
            DamageResult result = target.TakeDamage(info);
            DamageResult combined = scope.combined ??= new DamageResult { hitThing = target };
            combined.totalDamageDealt += result.totalDamageDealt;
            combined.deflected |= result.deflected;
            if (result.parts != null)
                foreach (BodyPartRecord part in result.parts) combined.AddPart(target, part);
            if (result.hediffs != null)
                foreach (Hediff hediff in result.hediffs) combined.AddHediff(hediff);
            return result;
        }

        private static DamageResult ApplyRangedDamage(Thing target, DamageInfo info)
        {
            if (!(target is Pawn defender) || !RimKataEligibilityCache.IsCachedQualifiedPawn(defender))
                return target.TakeDamage(info);
            // The normal projectile/shield hooks own this roll, including CE projectile context.
            DamageResult result = target.TakeDamage(info);
            bool avoided = RimKataDefenseUtility.TryGetResolvedProjectileDefense(defender, out bool resolvedAvoided)
                && resolvedAvoided;
            if (avoided) suppressedSpiritPawn = defender;
            var log = new BattleLogEntry_RangedImpact(info.Instigator, avoided ? null : target, target,
                info.Weapon, RimKataProjectileImpactContext.CurrentProjectile?.def, null);
            Find.BattleLog?.Add(log);
            result.AssociateWithLog(log);
            return result;
        }

        private static void NotifySpiritDamage(ThingComp comp, float amount)
        {
            if (comp.parent != suppressedSpiritPawn) notifySpiritDamage(comp, amount);
        }

        private static BattleLogEntry_MeleeCombat CreateMeleeLog(DamageScope scope, ThingDef weapon)
        {
            ManeuverDef maneuver = scope.verb?.maneuver ?? DefDatabase<ManeuverDef>.GetNamed("Smash");
            RulePackDef rules = scope.parried ? maneuver.combatLogRulesDeflect
                : scope.avoided ? maneuver.combatLogRulesDodge : maneuver.combatLogRulesHit;
            var log = new BattleLogEntry_MeleeCombat(rules, false, scope.attacker, scope.defender,
                scope.verb?.ImplementOwnerType ?? (weapon != null ? ImplementOwnerTypeDefOf.Weapon : ImplementOwnerTypeDefOf.Bodypart),
                scope.verb?.tool?.labelUsedInLogging == true ? scope.verb.tool.label : "",
                weapon, scope.verb?.HediffCompSource?.Def, maneuver.logEntryDef);
            Find.BattleLog?.Add(log);
            if (scope.avoided && !scope.parried && scope.defender.Spawned)
                MoteMaker.ThrowText(scope.defender.DrawPos, scope.defender.Map, "TextMote_Dodge".Translate());
            return log;
        }

        private static void FinishScope(DamageScope scope)
        {
            if (scope.log == null || scope.combined == null) return;
            scope.combined.AssociateWithLog(scope.log);
            if (scope.combined.deflected)
                scope.log.RuleDef = (scope.verb?.maneuver ?? DefDatabase<ManeuverDef>.GetNamed("Smash")).combatLogRulesDeflect;
        }
    }
}
