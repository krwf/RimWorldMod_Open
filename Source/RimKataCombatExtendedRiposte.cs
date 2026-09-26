using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using static Verse.DamageWorker;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedRiposte
    {
        private sealed class DamageLogScope
        {
            internal Pawn target;
            internal DamageResult combined;
        }

        [ThreadStatic] private static DamageLogScope activeDamageLog;
        private static readonly MethodInfo MeleeDamage = AccessTools.Method(
            typeof(Verb_MeleeAttack), "ApplyMeleeDamageToTarget");
        private static Func<Verb_MeleeAttack, LocalTargetInfo, DamageResult> applyMeleeDamage;
        private static readonly MethodInfo TakeDamage = AccessTools.Method(
            typeof(Thing), nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) });

        internal static void Apply(Harmony harmony, Type ceMelee)
        {
            MethodInfo parry = AccessTools.DeclaredMethod(ceMelee, "DoParry",
                new[] { typeof(Pawn), typeof(Thing), typeof(bool), typeof(bool) });
            MethodInfo damage = AccessTools.DeclaredMethod(ceMelee, "ApplyMeleeDamageToTarget",
                new[] { typeof(LocalTargetInfo) });
            MethodInfo parryPatch = AccessTools.Method(typeof(RimKataCombatExtendedRiposte), nameof(ParryTranspiler));
            MethodInfo damagePatch = AccessTools.Method(typeof(RimKataCombatExtendedRiposte), nameof(DamageTranspiler));
            try
            {
                if (parry?.ReturnType != typeof(void) || damage?.ReturnType != typeof(DamageResult))
                    throw new InvalidOperationException("CE riposte API did not match.");
                ValidateCalls(PatchProcessor.GetOriginalInstructions(parry), 2, 1);
                ValidateCalls(PatchProcessor.GetOriginalInstructions(damage), 0, 1);
                applyMeleeDamage = AccessTools.MethodDelegate<Func<Verb_MeleeAttack, LocalTargetInfo, DamageResult>>(
                    MeleeDamage, virtualCall: true);
                harmony.Patch(parry, transpiler: new HarmonyMethod(parryPatch));
                harmony.Patch(damage, transpiler: new HarmonyMethod(damagePatch));
            }
            catch (Exception exception)
            {
                if (parry != null) harmony.Unpatch(parry, parryPatch);
                if (damage != null) harmony.Unpatch(damage, damagePatch);
                Log.Warning("[RimKata] CE riposte integration was not applied: " + exception.Message);
            }
        }

        private static void ValidateCalls(List<CodeInstruction> codes, int meleeCount, int damageCount)
        {
            if (codes.FindAll(code => code.Calls(MeleeDamage)).Count != meleeCount
                || codes.FindAll(code => code.Calls(TakeDamage)).Count != damageCount)
                throw new InvalidOperationException("CE riposte damage instructions changed.");
        }

        private static IEnumerable<CodeInstruction> ParryTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            ValidateCalls(codes, 2, 1);
            foreach (CodeInstruction code in codes)
            {
                if (code.Calls(MeleeDamage))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataCombatExtendedRiposte), nameof(ApplyMeleeRiposte));
                }
                else if (code.Calls(TakeDamage))
                {
                    // The direct TakeDamage in DoParry is the shield riposte.
                    // Its attacker and shield are explicit; the original Verb
                    // belongs to the pawn receiving the counterblow.
                    var attacker = new CodeInstruction(OpCodes.Ldarg_1);
                    attacker.labels.AddRange(code.labels);
                    attacker.blocks.AddRange(code.blocks);
                    code.labels.Clear();
                    code.blocks.Clear();
                    yield return attacker;
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataCombatExtendedRiposte), nameof(ApplyShieldRiposte));
                }
                yield return code;
            }
        }

        private static IEnumerable<CodeInstruction> DamageTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            ValidateCalls(codes, 0, 1);
            foreach (CodeInstruction code in codes)
            {
                if (code.Calls(TakeDamage))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataCombatExtendedRiposte), nameof(CollectDamage));
                }
                yield return code;
            }
        }

        private static DamageResult ApplyMeleeRiposte(Verb_MeleeAttack verb, LocalTargetInfo target)
        {
            Pawn defender = target.Pawn;
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(defender))
                return applyMeleeDamage(verb, target);

            bool avoided = RimKataDefenseUtility.TryResolveDirectMeleeDefense(
                defender, verb.CasterPawn, verb, out bool parried);
            BattleLogEntry_MeleeCombat entry = CreateLog(verb.CasterPawn, defender, verb, null, avoided, parried);
            if (avoided) return new DamageResult();

            DamageLogScope previous = activeDamageLog;
            var scope = new DamageLogScope { target = defender };
            activeDamageLog = scope;
            try
            {
                DamageResult result = applyMeleeDamage(verb, target);
                // CE returns only its last DamageInfo result. Collect the parts
                // and injuries from every direct hit, then format one full entry.
                (scope.combined ?? result).AssociateWithLog(entry);
                return result;
            }
            finally
            {
                activeDamageLog = previous;
            }
        }

        private static DamageResult ApplyShieldRiposte(Thing target, DamageInfo info, Pawn attacker, Thing shield)
        {
            if (!(target is Pawn defender) || !RimKataEligibilityCache.IsCachedQualifiedPawn(defender))
                return target.TakeDamage(info);

            bool avoided = RimKataDefenseUtility.TryResolveDirectMeleeDefense(
                defender, attacker, null, out bool parried);
            BattleLogEntry_MeleeCombat entry = CreateLog(attacker, defender, null, shield, avoided, parried);
            if (avoided) return new DamageResult();

            DamageResult result = target.TakeDamage(info);
            result.AssociateWithLog(entry);
            return result;
        }

        private static DamageResult CollectDamage(Thing target, DamageInfo info)
        {
            DamageResult result = target.TakeDamage(info);
            DamageLogScope scope = activeDamageLog;
            if (scope == null || target != scope.target) return result;

            DamageResult combined = scope.combined ??= new DamageResult { hitThing = target };
            combined.deflected |= result.deflected;
            if (result.parts != null)
                foreach (BodyPartRecord part in result.parts) combined.AddPart(target, part);
            if (result.hediffs != null)
                foreach (Hediff hediff in result.hediffs) combined.AddHediff(hediff);
            return result;
        }

        private static BattleLogEntry_MeleeCombat CreateLog(
            Pawn attacker, Pawn defender, Verb_MeleeAttack verb, Thing shield, bool avoided, bool parried)
        {
            ManeuverDef maneuver = verb?.maneuver ?? DefDatabase<ManeuverDef>.GetNamed("Smash");
            RulePackDef rules = parried ? maneuver.combatLogRulesDeflect
                : avoided ? maneuver.combatLogRulesDodge : maneuver.combatLogRulesHit;
            var entry = new BattleLogEntry_MeleeCombat(rules, false, attacker, defender,
                shield != null ? ImplementOwnerTypeDefOf.Weapon : verb.ImplementOwnerType,
                verb?.tool?.labelUsedInLogging == true ? verb.tool.label : "",
                shield?.def ?? verb?.EquipmentSource?.def,
                verb?.HediffCompSource?.Def, maneuver.logEntryDef);
            Find.BattleLog?.Add(entry);
            if (avoided && !parried && defender.Spawned)
                MoteMaker.ThrowText(defender.DrawPos, defender.Map, "TextMote_Dodge".Translate());
            return entry;
        }
    }
}
