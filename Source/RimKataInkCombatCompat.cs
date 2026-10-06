using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace KRWF.RimKata
{
    internal static class RimKataInkCombatCompat
    {
        private struct MeleeScope
        {
            internal Verb verb;
            internal Verb_RimKataFlyingKick flyingKick;
            internal Verb_RimKataKick kick;
            internal Pawn attacker, defender;
            internal bool defenseOwned, resolved, avoided, rimKataAvoided, parried, resolving, continueDamage;
            internal float dodgeChance;
            internal BattleLogEntry_MeleeCombat hitLog;
            internal MoteRequest mote;
            internal FleckRequest fleck;
        }

        private struct MoteRequest
        {
            internal Vector3 location;
            internal Map map;
            internal ThingDef def;
            internal float scale, rotation;
            internal bool overrideVisibility;
        }

        private struct FleckRequest
        {
            internal Vector3 location;
            internal Map map;
            internal FleckDef def;
            internal float scale;
        }

        private struct DodgeScope
        {
            internal Verb verb;
            internal Pawn target;
            internal bool defenseOwned;
        }

        private struct MeleeState
        {
            internal MeleeScope previous;
            internal bool entered;
        }

        private struct DodgeState
        {
            internal DodgeScope previous;
            internal bool entered;
        }

        [ThreadStatic] private static MeleeScope melee;
        [ThreadStatic] private static DodgeScope dodge;
        private static Action<Verb_MeleeAttack, bool, bool> recordSharedDefense;
        private static readonly MethodInfo ParryMethod = AccessTools.DeclaredMethod(
            typeof(RimKataDefenseUtility), "TryResolveMeleeParry",
            new[] { typeof(Pawn), typeof(Pawn), typeof(Verb), typeof(bool) });
        private static readonly Func<Pawn, Pawn, Verb, bool, bool> ResolveParry =
            AccessTools.MethodDelegate<Func<Pawn, Pawn, Verb, bool, bool>>(ParryMethod);
        private static readonly Func<Verb_MeleeAttack, Thing, SoundDef> DodgeSound =
            AccessTools.MethodDelegate<Func<Verb_MeleeAttack, Thing, SoundDef>>(
                AccessTools.Method(typeof(Verb_MeleeAttack), "SoundDodge"));

        internal static void Apply()
        {
            Type reactions = RimKataActiveModTypes.Find("NinjaCombat.DamageReactions");
            if (reactions == null) return;
            var harmony = new Harmony("krwf.rimkata.ink-combat");
            try
            {
                MethodInfo damagePrefix = AccessTools.DeclaredMethod(reactions, "Prefix");
                MethodInfo dodgePostfix = AccessTools.DeclaredMethod(
                    RimKataActiveModTypes.Find("NinjaCombat.DisableVanillaHumanlikeDodge"), "Postfix");
                Validate(damagePrefix, typeof(bool), typeof(Pawn),
                    typeof(DamageInfo).MakeByRefType(), typeof(bool).MakeByRefType());
                Validate(dodgePostfix, typeof(void), typeof(Verb_MeleeAttack), typeof(float).MakeByRefType());
                if (RimKataActiveModTypes.Find("DynamicAnimeCombat.Core.CombatMechanics") != null)
                    recordSharedDefense = AccessTools.MethodDelegate<Action<Verb_MeleeAttack, bool, bool>>(
                        AccessTools.DeclaredMethod(typeof(RimKataDynamicAnimeCombatCompat), "RecordDefense"));

                RimKataStartupPatches.Patch(harmony, AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "TryCastShot"),
                    prefix: Patch(nameof(MeleePrefix), Priority.First),
                    transpiler: Patch(nameof(MeleeTranspiler), Priority.Last),
                    finalizer: Patch(nameof(MeleeFinalizer), Priority.Last));
                RimKataStartupPatches.Patch(harmony, AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "GetDodgeChance"),
                    prefix: Patch(nameof(DodgePrefix), Priority.First),
                    postfix: Patch(nameof(DeferDodge), Priority.Last),
                    finalizer: Patch(nameof(DodgeFinalizer), Priority.Last));
                RimKataStartupPatches.Patch(harmony, AccessTools.DeclaredMethod(typeof(RimKataCombatMath), "ApplyConfiguredMeleeDodgeBonus"),
                    postfix: Patch(nameof(RecordDodgeEligibility)));
                RimKataStartupPatches.Patch(harmony, ParryMethod, prefix: Patch(nameof(DeferParry)));
                RimKataStartupPatches.Patch(harmony, dodgePostfix, prefix: Patch(nameof(KeepResolvedDodge)));
                RimKataStartupPatches.Patch(harmony, damagePrefix, prefix: Patch(nameof(ReuseDefense)),
                    postfix: Patch(nameof(ResolveAfterInk)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] Ink Combat melee defense integration was not applied. " + exception.Message);
            }
        }

        private static HarmonyMethod Patch(string name, int priority = Priority.Normal)
            => new HarmonyMethod(typeof(RimKataInkCombatCompat), name) { priority = priority };

        private static void Validate(MethodInfo method, Type result, params Type[] parameters)
        {
            if (method == null || !method.IsStatic || method.ReturnType != result)
                throw new InvalidOperationException("Ink Combat defense API did not match.");
            ParameterInfo[] actual = method.GetParameters();
            if (actual.Length != parameters.Length)
                throw new InvalidOperationException("Ink Combat defense parameters did not match.");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i].ParameterType != parameters[i])
                    throw new InvalidOperationException("Ink Combat defense parameters did not match.");
        }

        private static void MeleePrefix(Verb_MeleeAttack __instance, out MeleeState __state)
        {
            __state = new MeleeState { previous = melee, entered = true };
            melee = new MeleeScope
            {
                verb = __instance,
                flyingKick = __instance as Verb_RimKataFlyingKick,
                kick = __instance as Verb_RimKataKick,
                attacker = __instance.CasterPawn,
                defender = __instance.CurrentTarget.Pawn
            };
        }

        private static Exception MeleeFinalizer(Exception __exception, MeleeState __state)
        {
            if (__state.entered) melee = __state.previous;
            return __exception;
        }

        private static void DodgePrefix(Verb_MeleeAttack __instance, LocalTargetInfo __0, out DodgeState __state)
        {
            __state = new DodgeState { previous = dodge, entered = true };
            dodge = new DodgeScope { verb = __instance, target = __0.Pawn };
        }

        private static Exception DodgeFinalizer(Exception __exception, DodgeState __state)
        {
            if (__state.entered) dodge = __state.previous;
            return __exception;
        }

        private static void RecordDodgeEligibility(Pawn target)
        {
            if (target == null || dodge.target != target || dodge.verb == null) return;
            dodge.defenseOwned = true;
            if (melee.verb == dodge.verb && melee.defender == target)
                melee.defenseOwned = true;
        }

        private static bool IsCurrentAttack(Verb verb)
            => verb != null && melee.verb == verb && Patch_Verb_TryCastShot_RimKata.CurrentVerb == verb;

        private static void DeferDodge(Verb_MeleeAttack __instance, ref float __result)
        {
            if (!dodge.defenseOwned || dodge.target != melee.defender || melee.resolving
                || !IsCurrentAttack(__instance)) return;
            melee.dodgeChance = __result;
            __result = 0f;
        }

        private static bool DeferParry(Pawn defender, Pawn attacker, Verb attackingVerb,
            bool defenseEligibilityVerified, ref bool __result)
        {
            if (!defenseEligibilityVerified || melee.resolving || !IsCurrentAttack(attackingVerb)
                || melee.defender != defender || melee.attacker != attacker) return true;
            melee.defenseOwned = true;
            __result = false;
            return false;
        }

        private static bool KeepResolvedDodge(Verb_MeleeAttack __0)
            => !dodge.defenseOwned || dodge.verb != __0 || dodge.target != melee.defender
                || !IsCurrentAttack(__0);

        private static bool MatchesDamage(Pawn defender, DamageInfo damage)
            => melee.defenseOwned && IsCurrentAttack(melee.verb)
                && melee.defender == defender && melee.attacker == damage.Instigator
                && damage.Amount > 0f;

        private static bool ReuseDefense(Pawn __0, ref DamageInfo __1, ref bool __2, ref bool __result)
        {
            if (!MatchesDamage(__0, __1) || !melee.resolved) return true;
            if (melee.avoided) __2 = true;
            __result = melee.continueDamage && !__2;
            return false;
        }

        private static void ResolveAfterInk(Pawn __0, ref DamageInfo __1, ref bool __2, ref bool __result)
        {
            if (Patch_Verb_TryCastShot_RimKata.CurrentVerb is Verb_RimKataFlyingKick kick)
                kick.RecordDamageDecision(__0, __1, __result && !__2);
            else if (Patch_Verb_TryCastShot_RimKata.CurrentVerb is Verb_RimKataKick standingKick)
                standingKick.RecordDamageDecision(__0, __1, __result && !__2);
            if (!MatchesDamage(__0, __1) || melee.resolved) return;
            melee.resolved = true;
            melee.continueDamage = __result;
            if (!__result || __2)
            {
                melee.avoided = __2;
                PublishDefense();
                return;
            }

            melee.resolving = true;
            try
            {
                if (Rand.Chance(melee.dodgeChance))
                {
                    melee.rimKataAvoided = true;
                    RimKataGroundPoseUtility.NotifyAvoidance(melee.defender, melee.attacker, true);
                    MoteMaker.ThrowText(melee.defender.DrawPos, melee.defender.Map,
                        "TextMote_Dodge".Translate(), 1.9f);
                }
                else
                {
                    melee.parried = ResolveParry(melee.defender, melee.attacker, melee.verb, true);
                    melee.rimKataAvoided = melee.parried;
                }
                melee.avoided = melee.rimKataAvoided;
                if (melee.avoided)
                {
                    melee.continueDamage = false;
                    __2 = true;
                    __result = false;
                }
            }
            finally { melee.resolving = false; }
            PublishDefense();
        }

        private static void PublishDefense()
        {
            recordSharedDefense?.Invoke((Verb_MeleeAttack)melee.verb, melee.avoided, melee.parried);
            if (melee.verb is Verb_RimKataFlyingKick kick)
                kick.RecordResolvedDefense(melee.continueDamage && !melee.avoided);
            else if (melee.verb is Verb_RimKataKick standingKick)
                standingKick.RecordResolvedDefense(melee.continueDamage && !melee.avoided);
        }

        private static IEnumerable<CodeInstruction> MeleeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo stagger = AccessTools.Method(typeof(StaggerHandler), nameof(StaggerHandler.StaggerFor),
                new[] { typeof(int), typeof(float) });
            MethodInfo log = AccessTools.Method(typeof(Verb_MeleeAttack), nameof(Verb_MeleeAttack.CreateCombatLog));
            MethodInfo sound = AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot),
                new[] { typeof(SoundDef), typeof(SoundInfo) });
            MethodInfo mote = AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeStaticMote),
                new[] { typeof(Vector3), typeof(Map), typeof(ThingDef), typeof(float), typeof(bool), typeof(float) });
            MethodInfo fleck = AccessTools.Method(typeof(FleckMaker), nameof(FleckMaker.Static),
                new[] { typeof(Vector3), typeof(Map), typeof(FleckDef), typeof(float) });
            MethodInfo damage = AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "ApplyMeleeDamageToTarget");
            int staggerCalls = 0, logCalls = 0, soundCalls = 0, moteCalls = 0, fleckCalls = 0, damageCalls = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(stagger))
                {
                    staggerCalls++;
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataInkCombatCompat), nameof(ApplyStagger));
                }
                else if (code.Calls(sound))
                {
                    soundCalls++;
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataInkCombatCompat), nameof(PlaySound));
                }
                else if (code.Calls(mote))
                {
                    moteCalls++;
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataInkCombatCompat), nameof(DeferImpactMote));
                }
                else if (code.Calls(fleck))
                {
                    fleckCalls++;
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataInkCombatCompat), nameof(DeferImpactFleck));
                }
                if (code.opcode == OpCodes.Ret)
                {
                    var owner = new CodeInstruction(OpCodes.Ldarg_0);
                    owner.MoveLabelsFrom(code);
                    owner.MoveBlocksFrom(code);
                    yield return owner;
                    yield return CodeInstruction.Call(typeof(RimKataInkCombatCompat), nameof(FinishResult));
                }
                yield return code;
                if (code.Calls(log))
                {
                    logCalls++;
                    yield return CodeInstruction.Call(typeof(RimKataInkCombatCompat), nameof(CaptureLog));
                }
                else if (code.Calls(damage))
                {
                    damageCalls++;
                    yield return CodeInstruction.Call(typeof(RimKataInkCombatCompat), nameof(FlushImpactEffects));
                }
            }
            if (staggerCalls != 1 || soundCalls != 1 || logCalls != 3
                || moteCalls != 1 || fleckCalls != 1 || damageCalls != 1)
                throw new InvalidOperationException("Ink Combat melee continuation layout did not match.");
        }

        private static Mote DeferImpactMote(Vector3 location, Map map, ThingDef def,
            float scale, bool overrideVisibility, float rotation)
        {
            if ((!melee.defenseOwned && melee.flyingKick == null && melee.kick == null) || melee.resolved)
                return MoteMaker.MakeStaticMote(location, map, def, scale, overrideVisibility, rotation);
            melee.mote = new MoteRequest
            {
                location = location, map = map, def = def, scale = scale,
                overrideVisibility = overrideVisibility, rotation = rotation
            };
            return null;
        }

        private static void DeferImpactFleck(Vector3 location, Map map, FleckDef def, float scale)
        {
            if ((!melee.defenseOwned && melee.flyingKick == null && melee.kick == null) || melee.resolved)
            {
                FleckMaker.Static(location, map, def, scale);
                return;
            }
            melee.fleck = new FleckRequest { location = location, map = map, def = def, scale = scale };
        }

        private static DamageWorker.DamageResult FlushImpactEffects(DamageWorker.DamageResult result)
        {
            MoteRequest mote = melee.mote;
            FleckRequest fleck = melee.fleck;
            melee.mote = default;
            melee.fleck = default;
            if (!melee.rimKataAvoided && melee.flyingKick?.Blocked != true && melee.kick?.Blocked != true)
            {
                if (mote.def != null)
                    MoteMaker.MakeStaticMote(mote.location, mote.map, mote.def,
                        mote.scale, mote.overrideVisibility, mote.rotation);
                if (fleck.def != null)
                    FleckMaker.Static(fleck.location, fleck.map, fleck.def, fleck.scale);
            }
            return result;
        }

        private static BattleLogEntry_MeleeCombat CaptureLog(BattleLogEntry_MeleeCombat log)
        {
            if ((melee.defenseOwned || melee.flyingKick != null || melee.kick != null) && !melee.resolved) melee.hitLog = log;
            return log;
        }

        private static bool ApplyStagger(StaggerHandler handler, int ticks, float speed)
            => melee.parried && melee.defender == handler.parent
                ? false : handler.StaggerFor(ticks, speed);

        private static void PlaySound(SoundDef sound, SoundInfo info)
        {
            if ((melee.flyingKick?.Blocked == true || melee.kick?.Blocked == true) && !melee.rimKataAvoided) return;
            if (melee.rimKataAvoided)
            {
                if (melee.parried) return;
                sound = DodgeSound((Verb_MeleeAttack)melee.verb, melee.defender);
            }
            sound?.PlayOneShot(info);
        }

        private static bool FinishResult(bool result, Verb_MeleeAttack verb)
        {
            if (melee.verb == verb && (melee.flyingKick?.Blocked == true || melee.kick?.Blocked == true))
            {
                result = false;
                if (!melee.rimKataAvoided && melee.hitLog != null)
                {
                    melee.hitLog.RuleDef = melee.kick != null
                        ? RimKataKickDefOf.RimKata_Kick_Blocked : RimKataFlyingKickDefOf.RimKata_FlyingKick_Blocked;
                    melee.hitLog.alwaysShowInCompact = false;
                }
            }
            if (melee.verb != verb || !melee.rimKataAvoided) return result;
            if (melee.hitLog != null)
            {
                if (melee.parried) Find.BattleLog.RemoveEntry(melee.hitLog);
                else
                {
                    melee.hitLog.RuleDef = verb.maneuver.combatLogRulesDodge;
                    melee.hitLog.alwaysShowInCompact = false;
                }
            }
            return false;
        }
    }
}
