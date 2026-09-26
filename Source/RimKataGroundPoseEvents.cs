using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Receive resolved combat events. No pawn, projectile or battle-log polling.
    internal static class RimKataGroundPoseEvents
    {
        internal static void ProjectileImpact(Projectile projectile, ref Thing hitThing, bool blockedByShield)
        {
            Pawn victim = hitThing as Pawn;
            if (victim == null || !RimKataGroundPoseUtility.IsProne(victim)
                || blockedByShield
                || !RimKataDefenseUtility.IsDirectHitBullet(projectile)
                || !RimKataDefenseUtility.TryBeginGroundPoseImpact())
                return;

            Pawn intended = projectile.intendedTarget.Pawn;
            Thing attacker = projectile.Launcher;
            RimKataCloseProjectileState close = RimKataProjectileImpactContext.CurrentCloseShot;
            bool alreadyAvoided = WasCloseAttackAvoided(close, intended);
            if (!alreadyAvoided && victim == intended)
                alreadyAvoided = projectile.Map?.GetComponent<RimKataMapComponent>()?
                    .WasRangedProjectileAvoided(projectile, intended) == true;
            if ((!alreadyAvoided || victim != intended)
                && TryProneDirectMiss(victim, attacker))
            {
                // Let the original impact produce its normal ground-hit effects
                // and miss log; this is not an ordinary dodge or its cooldown.
                hitThing = null;
                return;
            }
        }

        internal static bool WasCloseAttackAvoided(RimKataCloseProjectileState close, Pawn defender)
        {
            return defender != null && close?.target == defender
                && (close.defenseAvoided || close.precheck == RimKataCloseDefensePrecheck.FirstDodgeSucceeded
                    || close.precheck == RimKataCloseDefensePrecheck.ResponseSucceeded
                    || close.precheck == RimKataCloseDefensePrecheck.ResponseSucceededWithAccidentalShot);
        }

        internal static bool TryProneDirectMiss(Pawn defender, Thing attacker)
        {
            if (defender == null || attacker == defender
                || (defender.Faction != null && defender.Faction == attacker?.Faction)
                || !RimKataGroundPoseUtility.TryProneMiss(defender))
                return false;

            RimKataDefenseUtility.RecordProjectileDefense(defender, true);
            RimKataDefenseUtility.MarkProjectileAvoided(defender);
            return true;
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttack), nameof(Verb_MeleeAttack.CreateCombatLog))]
    internal static class Patch_VerbMeleeAttack_RimKataGroundPoseOutcome
    {
        private static void Postfix(Verb_MeleeAttack __instance, BattleLogEntry_MeleeCombat __result)
        {
            Pawn defender = __instance?.CurrentTarget.Pawn;
            ManeuverDef maneuver = __instance?.maneuver;
            if (__result == null || defender == null || maneuver == null)
                return;

            // A false TryCastShot can mean no attack happened. These rule packs
            // are selected only once the real hit/dodge rolls have resolved.
            if (__result.RuleDef == maneuver.combatLogRulesMiss)
                RimKataGroundPoseUtility.NotifyMiss(defender, __instance.CasterPawn, true);
            else if (__result.RuleDef == maneuver.combatLogRulesDodge)
                RimKataGroundPoseUtility.NotifyAvoidance(defender, __instance.CasterPawn, true);
        }
    }

    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.GetManhunterOnDamageChance),
        new[] { typeof(Pawn), typeof(Thing), typeof(float) })]
    internal static class Patch_PawnUtility_RimKataProneHuntingStealth
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getStatValue = AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue));
            var getStealth = AccessTools.Method(typeof(Patch_PawnUtility_RimKataProneHuntingStealth), nameof(GetHuntingStealth));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(getStatValue)) instruction.operand = getStealth;
                yield return instruction;
            }
        }

        private static float GetHuntingStealth(Thing thing, StatDef stat, bool applyPostProcess, int cacheStaleAfterTicks)
        {
            float value = thing.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks);
            // Only the animal revenge calculation uses this wrapper. Read the
            // live prone set after the vanilla stat, outside its cached value.
            if (stat != StatDefOf.HuntingStealth || !(thing is Pawn pawn)
                || !RimKataGroundPoseUtility.IsProne(pawn)) return value;
            float bonus = RimKataTargetAccess.SettingsFor(pawn)?.GetProneHuntingStealthBonus(pawn) ?? 0f;
            return bonus > 0f ? Mathf.Clamp01(value + bonus) : value;
        }
    }
}
