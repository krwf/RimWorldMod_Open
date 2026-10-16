using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace KRWF.RimKata
{
    internal static class RimKataReactiveDefense
    {
        private static readonly Func<Verb_MeleeAttack, Func<ManeuverDef, RulePackDef>, bool, BattleLogEntry_MeleeCombat> Log =
            AccessTools.MethodDelegate<Func<Verb_MeleeAttack, Func<ManeuverDef, RulePackDef>, bool, BattleLogEntry_MeleeCombat>>(
                AccessTools.Method(typeof(Verb_MeleeAttack), "CreateCombatLog"));
        private static readonly Func<ManeuverDef, RulePackDef> Parry = m => m.combatLogRulesDeflect;
        private static readonly Func<ManeuverDef, RulePackDef> Dodge = m => m.combatLogRulesDodge;

        internal static bool TryDefense(Pawn defender, out bool parried)
        {
            var state = RimKataReactiveMotion.Participant(defender);
            parried = state?.kind == RimKataReactiveKind.ShakeOff;
            return state?.BlocksCombat == true;
        }
        internal static void NotifyDefense(Pawn defender, bool parried)
        {
            if (defender?.Map == null) return;
            if (parried)
            {
                var effect = EffecterDefOf.Deflect_General.Spawn(defender.Position, defender.Map);
                effect?.Cleanup();
            }
            else MoteMaker.ThrowText(defender.DrawPos, defender.Map, "TextMote_Dodge".Translate(), 1.9f);
        }
        internal static bool TryMelee(Verb_MeleeAttack verb)
        {
            Pawn defender = verb?.CurrentTarget.Pawn;
            if (!TryDefense(defender, out bool parried)) return false;
            Pawn attacker = verb.CasterPawn;
            // TryCastShot runs from Stance_Busy, so FullBodyBusy is expected here.
            if (attacker?.Spawned != true || attacker.Dead || attacker.Downed
                || attacker.stances?.stunner?.Stunned == true
                || !verb.CanHitTarget(defender)) return false;
            Log(verb, parried ? Parry : Dodge, false);
            NotifyDefense(defender, parried);
            attacker.Drawer.Notify_MeleeAttackOn(defender);
            attacker.caller?.Notify_DidMeleeAttack();
            return true;
        }
    }
}
