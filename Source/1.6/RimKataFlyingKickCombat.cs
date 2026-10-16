using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    [DefOf]
    internal static class RimKataFlyingKickDefOf
    {
        public static ManeuverDef RimKata_FlyingKick = null;
        public static ToolCapacityDef RimKata_FlyingKickCapacity = null;
        public static RulePackDef RimKata_FlyingKick_Blocked = null;

        static RimKataFlyingKickDefOf()
            => DefOfHelper.EnsureInitializedInCtor(typeof(RimKataFlyingKickDefOf));
    }

    internal sealed class RimKataFlyingKickAttack
    {
        private static Tool humanFist;
        private Verb_RimKataFlyingKick verb;

        internal bool Completed { get; private set; }
        internal bool Hit { get; private set; }
        internal bool Cancelled { get; private set; }
        internal Verb AttackVerb => verb;

        internal bool Execute(Pawn attacker, Thing victim,
            float damageMultiplier, float impactStunChance)
        {
            if (Completed) return !Cancelled;
            if (attacker == null || victim == null || !Prepare(attacker))
                return false;
            Completed = true;
            var previous = RimKataFireContext.Begin(verb, attacker, null,
                false, false, false, null, false, false, default);
            try
            {
                Hit = verb.Execute(victim, humanFist.power * Mathf.Max(0f, damageMultiplier));
                if (Hit && victim is Pawn target && target.Spawned && !target.Dead
                    && !target.Downed && Rand.Chance(Mathf.Clamp01(impactStunChance)))
                    target.stances?.stunner.StunFor(120, attacker);
                return true;
            }
            catch
            {
                Cancelled = true;
                throw;
            }
            finally { RimKataFireContext.End(verb, previous); }
        }

        private bool Prepare(Pawn attacker)
        {
            if (humanFist == null)
            {
                List<Tool> tools = ThingDefOf.Human.tools;
                ToolCapacityDef blunt = DefDatabase<ToolCapacityDef>.GetNamedSilentFail("Blunt");
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool candidate = tools[i];
                    if (candidate.linkedBodyPartsGroup?.defName == "LeftHand"
                        && candidate.capacities.Contains(blunt))
                    {
                        humanFist = candidate;
                        break;
                    }
                }
                if (humanFist == null)
                {
                    Log.WarningOnce("[RimKata] Flying kick requires the human fist damage definition.", 195830641);
                    return false;
                }
            }
            if (verb != null && verb.CasterPawn == attacker) return true;
            ManeuverDef maneuver = RimKataFlyingKickDefOf.RimKata_FlyingKick;
            ToolCapacityDef capacity = RimKataFlyingKickDefOf.RimKata_FlyingKickCapacity;
            if (maneuver?.verb == null || capacity == null) return false;
            verb = new Verb_RimKataFlyingKick
            {
                caster = attacker,
                verbTracker = new VerbTracker(attacker),
                verbProps = maneuver.verb,
                maneuver = maneuver,
                loadID = "RimKata_FlyingKick_" + attacker.thingIDNumber,
                tool = new Tool
                {
                    id = "RimKata_FlyingKick",
                    label = capacity.label,
                    capacities = new List<ToolCapacityDef> { capacity },
                    cooldownTime = humanFist.cooldownTime,
                    armorPenetration = humanFist.armorPenetration
                }
            };
            return true;
        }

        internal void Cancel()
        {
            if (Completed) return;
            Cancelled = true;
            Completed = true;
        }
    }

    public sealed class Verb_RimKataFlyingKick : Verb_MeleeAttackDamage
    {
        private bool damageEntered;
        private bool damageAllowed;
        internal bool Hit { get; private set; }
        internal bool Blocked => damageEntered && !damageAllowed;

        internal bool Execute(LocalTargetInfo victim, float power)
        {
            currentTarget = victim;
            tool.power = power;
            damageEntered = false;
            damageAllowed = true;
            Hit = false;
            try { return TryCastShot(); }
            finally { currentTarget = LocalTargetInfo.Invalid; }
        }

        protected override bool TryCastShot()
        {
            bool result = base.TryCastShot();
            Hit = result && damageEntered && damageAllowed;
            return Hit;
        }

        protected override DamageWorker.DamageResult ApplyMeleeDamageToTarget(LocalTargetInfo victim)
        {
            damageEntered = true;
            DamageWorker.DamageResult result = base.ApplyMeleeDamageToTarget(victim);
            if (result?.hediffs != null)
            {
                for (int i = 0; i < result.hediffs.Count; i++)
                {
                    if (result.hediffs[i] is not Hediff_Injury injury) continue;
                    injury.sourceLabel = verbProps.meleeDamageDef.label;
                    injury.sourceToolLabel = null;
                    injury.sourceBodyPartGroup = null;
                }
            }
            return result;
        }

        internal void RecordDamageDecision(Pawn victim, DamageInfo damage, bool allowed)
        {
            if (damageEntered && victim == CurrentTarget.Thing
                && damage.Instigator == CasterPawn && damage.Amount > 0f)
                damageAllowed &= allowed;
        }

        internal void RecordResolvedDefense(bool allowed) => damageAllowed &= allowed;

        internal static bool FullBodyBusy(Pawn_StanceTracker tracker, Verb_MeleeAttack verb)
            => verb is not Verb_RimKataFlyingKick && tracker.FullBodyBusy;
    }
}
