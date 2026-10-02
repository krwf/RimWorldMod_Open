using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_JobStart_RimKataSubdue
    {
        [HarmonyPriority(Priority.First + 100)]
        private static bool Prefix(Pawn ___pawn, ref Job newJob, ref bool? keepCarryingThingOverride)
        {
            var state = RimKataSubdueUtility.Get(___pawn);
            if (state == null) return true;
            bool automaticConflict = RimKataSubdueJobs.IsAttackJob(newJob)
                && newJob.def != RimKataDefOf.RimKata_SubdueCombat && !newJob.playerForced
                && (state.attackEnabled || state.HasExternalTarget);
            if (automaticConflict || !RimKataSubdueJobs.PrepareJob(___pawn, newJob, ref keepCarryingThingOverride))
            {
                if (newJob != null && newJob != ___pawn.CurJob) JobMaker.ReturnToPool(newJob);
                newJob = null;
                return false;
            }
            if (newJob != null && newJob.def != RimKataDefOf.RimKata_SubdueCombat
                && newJob != ___pawn.CurJob)
                RimKataSubdueCombat.StopExternalAttack(state);
            state.SuspendAutomaticFire(newJob);
            return true;
        }
        private static void Postfix(Pawn ___pawn) => RimKataSubdueUtility.Get(___pawn)?.SuspendAutomaticFire();
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Cleanup))]
    internal static class Patch_JobCleanup_RimKataSubdue
    {
        private static void Prefix(JobDriver __instance)
        {
            var state = RimKataSubdueUtility.Get(__instance.pawn);
            if (state?.suspendedJob == __instance.job) state.RestoreJobFlag();
            if (state != null && __instance.job.def == RimKataDefOf.RimKata_SubdueCombat
                && state.externalOrderJobId == __instance.job.loadID)
                RimKataSubdueCombat.StopExternalAttack(state);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    internal static class Patch_PawnDespawn_RimKataSubdue
    {
        private static void Prefix(Pawn __instance)
        {
            var state = RimKataSubdueUtility.Get(__instance);
            if (state != null) RimKataSubdueUtility.Release(state);
        }
    }

    internal static class RimKataSubdueGizmos
    {
        internal static Command_Toggle Attack(RimKataSubdueState state, Command original)
        {
            var command = new Command_Toggle
            {
                defaultLabel = "KRWF_RimKata_SubdueAttack".Translate(),
                defaultDesc = "KRWF_RimKata_SubdueAttackDesc".Translate(),
                icon = original?.icon ?? TexCommand.Attack,
                hotKey = original?.hotKey,
                isActive = () => RimKataSubdueUtility.Get(state.pawn) == state
                    && state.attackEnabled && !state.HasExternalTarget,
                toggleAction = () => RimKataSubdueCombat.SetAttackEnabled(state, !state.attackEnabled),
                groupable = false
            };
            if (!state.attackAllowed)
                command.Disable("KRWF_RimKata_SubdueAttackNotAllowed".Translate());
            return command;
        }

        internal static Command_Toggle AllowAttack(RimKataSubdueState state)
            => new Command_Toggle
            {
                defaultLabel = "KRWF_RimKata_SubdueAllowAttack".Translate(),
                defaultDesc = "KRWF_RimKata_SubdueAllowAttackDesc".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => state.attackAllowed,
                toggleAction = () => RimKataSubdueCombat.SetAttackAllowed(state, !state.attackAllowed),
                groupable = false
            };

        internal static IEnumerable<Gizmo> Carry(IEnumerable<Gizmo> original, RimKataSubdueState state)
        {
            foreach (Gizmo gizmo in original)
            {
                if (gizmo is Command_Action command && command.icon == TexCommand.DropCarriedPawn)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "KRWF_RimKata_SubdueActions".Translate(),
                        defaultDesc = "KRWF_RimKata_SubdueActionsDesc".Translate(),
                        icon = command.icon, hotKey = command.hotKey, groupable = false,
                        action = () =>
                        {
                            if (RimKataSubdueUtility.Get(state.pawn) != state) return;
                            bool canThrow = RimKataThrownPawn.CanLaunch(state);
                            var options = new List<FloatMenuOption>
                            {
                                new FloatMenuOption("KRWF_RimKata_SubdueThrow".Translate(),
                                    canThrow ? (Action)(() => RimKataSubdueUtility.Throw(state)) : null),
                                new FloatMenuOption(command.Label, () => RimKataSubdueUtility.Release(state))
                            };
                            if (state.HasExternalTarget)
                                options.Add(new FloatMenuOption("CommandStopForceAttack".Translate(),
                                    () => RimKataSubdueCombat.StopExternalAttack(state)));
                            Find.WindowStack.Add(new FloatMenu(options));
                        }
                    };
                }
                else yield return gizmo;
            }
        }

        internal static IEnumerable<Gizmo> ForPawn(IEnumerable<Gizmo> original, RimKataSubdueState state)
        {
            bool inserted = false;
            foreach (Gizmo gizmo in original)
            {
                if (gizmo is Command_Toggle command && command.icon == TexCommand.FireAtWill)
                {
                    if (!inserted) { yield return AllowAttack(state); inserted = true; }
                }
                else yield return gizmo;
            }
            if (!inserted) yield return AllowAttack(state);
        }
    }

    [HarmonyPatch(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.Notify_MeleeAttackOn))]
    internal static class Patch_PawnDrawTracker_RimKataSubdueMelee
    {
        private static bool Prefix(Pawn ___pawn) => !RimKataSubdueCombat.OwnsAttack(___pawn);
    }

    [HarmonyPatch(typeof(PawnAttackGizmoUtility), "GetMeleeAttackGizmo")]
    internal static class Patch_MeleeGizmo_RimKataSubdue
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pawn pawn, ref Gizmo __result)
        {
            var state = RimKataSubdueUtility.Get(pawn);
            if (state != null) __result = RimKataSubdueGizmos.Attack(state, __result as Command);
        }
    }

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.GetGizmos))]
    internal static class Patch_CarryGizmos_RimKataSubdue
    {
        private static void Postfix(Pawn ___pawn, ref IEnumerable<Gizmo> __result)
        {
            var state = RimKataSubdueUtility.Get(___pawn);
            if (state != null) __result = RimKataSubdueGizmos.Carry(__result, state);
        }
    }
}
