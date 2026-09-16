using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataInspectUtility
    {
        public static void ReplaceEquipmentLine(Pawn pawn, ref string inspectString)
        {
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                return;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            if (primary == null
                || secondary == null
                || secondary.Destroyed
                || pawn?.equipment?.AllEquipmentListForReading?.Contains(secondary) != true
                || inspectString.NullOrEmpty())
            {
                return;
            }

            string prefix = "Equipped".TranslateSimple() + ": ";
            int index = inspectString.StartsWith(prefix, StringComparison.Ordinal)
                ? 0
                : inspectString.IndexOf("\n" + prefix, StringComparison.Ordinal);
            if (index < 0)
            {
                return;
            }
            if (inspectString[index] == '\n') index++;
            int end = inspectString.IndexOf('\n', index);
            if (end < 0) end = inspectString.Length;
            if (end > index && inspectString[end - 1] == '\r') end--;

            string replacement = prefix + primary.Label.CapitalizeFirst()
                + ", " + secondary.Label.CapitalizeFirst();
            inspectString = inspectString.Substring(0, index)
                + replacement
                + inspectString.Substring(end);
        }

        public static bool TryGetCombatReport(Pawn pawn, out string report)
        {
            report = null;
            JobDef jobDef = pawn?.CurJobDef;
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || pawn?.Map == null
                || (jobDef != JobDefOf.Wait_Combat
                    && jobDef != JobDefOf.Wait
                    && jobDef != JobDefOf.Wait_MaintainPosture
                    && jobDef != JobDefOf.Goto
                    && jobDef != JobDefOf.AttackStatic
                    && jobDef != JobDefOf.AttackMelee
                    && jobDef != RimKataDefOf.RimKata_Attack))
            {
                return false;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            if (primary == null || secondary == null)
            {
                return false;
            }

            RimKataPawnCombatState state = pawn.Map
                .GetComponent<RimKataMapComponent>()?
                .GetState(pawn, false);
            if (state == null
                || (!state.dualEngagementActive
                    && state.sharedTargetSearch?.scanActive != true
                    && !HasInspectWork(state.primaryWeaponCycle)
                    && !HasInspectWork(state.secondaryWeaponCycle)))
            {
                return false;
            }

            string primaryLine = SlotReport(
                pawn,
                "KRWF_RimKata_InspectSlotPrimary".Translate(),
                primary,
                state.primaryWeaponCycle,
                state.sharedTargetSearch?.scanActive == true);
            string secondaryLine = SlotReport(
                pawn,
                "KRWF_RimKata_InspectSlotSecondary".Translate(),
                secondary,
                state.secondaryWeaponCycle,
                state.sharedTargetSearch?.scanActive == true);
            report = primaryLine.EndWithPeriod() + "\n" + secondaryLine;
            return true;
        }

        private static bool HasInspectWork(RimKataWeaponCycleState cycle)
        {
            return cycle?.Active == true
                || cycle?.DedicatedActive == true;
        }

        private static string SlotReport(
            Pawn pawn,
            string slot,
            ThingWithComps weapon,
            RimKataWeaponCycleState cycle,
            bool sharedSearchActive)
        {
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(pawn, weapon);
            RimKataDualWeaponController.GetDebugWeaponState(
                pawn,
                weapon,
                out char weaponState,
                out bool _);

            Thing target = CurrentTarget(pawn, verb, cycle);
            if (target != null)
            {
                string targetLabel = target.LabelCap;
                if (cycle?.plannedInterception == true || target is Projectile)
                {
                    return "KRWF_RimKata_InspectIntercepting".Translate(
                        slot,
                        targetLabel);
                }

                return NativeSlotReport(slot,
                    verb?.IsMeleeAttack == true ? JobDefOf.AttackMelee : JobDefOf.AttackStatic,
                    target);
            }

            Thing cached = LiveTarget(pawn, cycle?.cachedCandidateTarget);
            if (cached != null)
            {
                return "KRWF_RimKata_InspectCachedTarget".Translate(
                    slot,
                    cached.LabelCap);
            }

            if (weaponState == 'C' || cycle?.cooldownTicksRemaining > 0)
            {
                return "KRWF_RimKata_InspectCooldown".Translate(slot);
            }

            if (sharedSearchActive)
            {
                return NativeSlotReport(slot, JobDefOf.Wait_Combat);
            }

            return NativeSlotReport(slot, JobDefOf.Wait);
        }

        private static string NativeSlotReport(
            string slot, JobDef jobDef, Thing target = null)
        {
            string report = JobUtility.GetResolvedJobReport(
                jobDef.reportString, target).CapitalizeFirst();
            return slot + ": " + report;
        }

        private static Thing CurrentTarget(
            Pawn pawn,
            Verb verb,
            RimKataWeaponCycleState cycle)
        {
            Thing target = LiveTarget(pawn, cycle?.plannedTarget);
            target ??= LiveTarget(pawn, cycle?.focusedTarget);
            target ??= LiveTarget(pawn, cycle?.visualTarget);
            if (target != null)
            {
                return target;
            }

            if (pawn?.stances?.curStance is Stance_Warmup warmup
                && warmup.verb == verb
                && warmup.focusTarg.HasThing)
            {
                target = LiveTarget(pawn, warmup.focusTarg.Thing);
            }

            if (target == null
                && verb?.Bursting == true
                && verb.CurrentTarget.HasThing)
            {
                target = LiveTarget(pawn, verb.CurrentTarget.Thing);
            }

            return target;
        }

        private static Thing LiveTarget(Pawn pawn, Thing target)
        {
            return target != null
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn?.Map
                    ? target
                    : null;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    internal static class Patch_Pawn_RimKataDualEquipmentInspect
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn __instance, ref string __result)
        {
            RimKataInspectUtility.ReplaceEquipmentLine(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetJobReport))]
    internal static class Patch_Pawn_RimKataDualCombatReport
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn __instance, ref string __result)
        {
            if (RimKataInspectUtility.TryGetCombatReport(__instance, out string report))
            {
                __result = report;
            }
        }
    }
}
