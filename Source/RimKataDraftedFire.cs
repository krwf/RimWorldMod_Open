using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public static class RimKataDraftedFireController
    {
        public static void Tick(Pawn pawn)
        {
            RimKataDualWeaponController.TickCombat(pawn, true);
        }

        public static void ProcessJobTrackerTick(Pawn pawn)
        {
            RimKataDualWeaponController.TickCombat(pawn, true);
        }

        public static bool TryApplyResponseCooldown(
            Pawn pawn,
            ThingWithComps weapon,
            Verb selectedVerb,
            LocalTargetInfo focus)
        {
            if (pawn?.IsBurning() == true)
            {
                CancelForFire(pawn);
                return true;
            }

            if (!CanControllerPrerequisites(pawn))
            {
                return false;
            }

            return RimKataDualWeaponController.TryApplyResponseCooldown(pawn, weapon, selectedVerb, focus);
        }

        public static void CancelForFire(Pawn pawn)
        {
            RimKataDualWeaponController.CancelOffenseForFire(pawn, StateFor(pawn, false));
        }

        public static bool ShouldReplacePhysicalMeleeAttack(
            Pawn pawn,
            Thing target)
        {
            return TryQueuePhysicalMeleeAttack(pawn, target);
        }

        public static bool TryQueuePhysicalMeleeAttack(Pawn pawn, Thing target)
        {
            if (pawn?.Map == null
                || pawn.InMentalState
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || RimKataDualWeaponController.UsesVanillaAutomaticTarget(pawn, target))
            {
                return false;
            }

            bool dedicatedJob = pawn.CurJobDef == RimKataDefOf.RimKata_Attack;
            bool controllerDriven = dedicatedJob
                || CanControllerPrerequisites(pawn);
            if (!controllerDriven
                || !pawn.CanReachImmediate(target, PathEndMode.Touch)
                || !RimKataDualWeaponController.HasUsableWeapon(
                    pawn,
                    true,
                    !dedicatedJob))
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            if (state == null)
            {
                return false;
            }

            bool playerForced = false;
            bool killIncappedTarget = false;
            state.TryGetForcedAttackRequestContext(
                target,
                out playerForced,
                out killIncappedTarget);
            if ((!RimKataTargeting.IsAutomaticEnemy(pawn, target)
                    && !playerForced)
                || (target is Pawn targetPawn
                    && !RimKataTargeting.IsPawnTargetStateValid(
                        targetPawn,
                        playerForced && killIncappedTarget))
                || RimKataDualWeaponController.ResolveImmediateCloseTarget(
                    pawn,
                    null,
                    target,
                    playerForced,
                    killIncappedTarget) != target)
            {
                return false;
            }

            state.RequestCloseAttack(target);

            return state.CloseAttackRequestActive;
        }

        public static void NotifyTargetedByHostile(Pawn target, Pawn attacker)
        {
            if (target == null
                || attacker == null
                || target == attacker
                || !target.Spawned
                || !attacker.Spawned
                || target.Map != attacker.Map
                || target.Drafted != true
                || !RimKataTargeting.IsAutomaticEnemy(target, attacker)
                || !RimKataTargeting.IsPawnTargetStateValid(attacker)
                || !CanControllerPrerequisites(target))
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(target, true);
            if (state != null)
            {
                state.NotifyIncomingThreat(attacker);
            }
        }

        private static bool CanControllerPrerequisites(Pawn pawn)
        {
            return pawn?.Drafted == true
                && IsAutomaticFireJob(pawn.CurJobDef)
                && RimKataEligibility.CanBeginGunKataAttack(pawn);
        }

        // !!! Debug HUD !!!
        public static string DebugCombatDemandReasons(Pawn pawn)
        {
            if (pawn == null)
            {
                return "-";
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state == null)
            {
                return "-";
            }

            string reasons = "";

            if (state.DodgeMovementActive)
                reasons += "D";

            if (state.DebugIncomingThreatStored)
                reasons += "I";

            if (state.DebugCloseAttackRequestStored)
                reasons += "C";

            if (state.primaryWeaponCycle?.CombatActive == true)
                reasons += "1";

            if (state.secondaryWeaponCycle?.CombatActive == true)
                reasons += "2";

            if (state.MovementFireContinuityActive)
                reasons += "M";

            if (state.DraftedMovementSearchTriggerPending)
                reasons += "Q";

            Thing enemyTarget = pawn.mindState?.enemyTarget;
            if (enemyTarget != null
                && enemyTarget.Spawned
                && enemyTarget.Map == pawn.Map
                && RimKataTargeting.IsAutomaticEnemy(pawn, enemyTarget))
            {
                reasons += "E";
            }

            return reasons.Length > 0 ? reasons : "-";
        }

        internal static bool IsAutomaticFireJob(JobDef jobDef)
        {
            return jobDef == null
                || jobDef == JobDefOf.Goto
                || jobDef == JobDefOf.Wait
                || jobDef == JobDefOf.Wait_Combat
                || jobDef == JobDefOf.Wait_MaintainPosture
                || jobDef == JobDefOf.AttackMelee;
        }

        private static RimKataPawnCombatState StateFor(Pawn pawn, bool create)
        {
            return pawn?.Map?.GetComponent<RimKataMapComponent>()?.GetState(pawn, create);
        }

    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick))]
    public static class Patch_PawnJobTracker_DraftedRimKataFire
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataDraftedFireController.ProcessJobTrackerTick(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack))]
    public static class Patch_PawnMeleeVerbs_WaitCombatRimKata
    {
        public static bool Prefix(Pawn ___pawn, Thing target, ref bool __result)
        {
            RimKataDraftedFireController.NotifyTargetedByHostile(target as Pawn, ___pawn);
            if (!RimKataDraftedFireController.TryQueuePhysicalMeleeAttack(___pawn, target))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance))]
    public static class Patch_PawnStanceTracker_RimKataHostileAim
    {
        public static void Postfix(
            Pawn ___pawn,
            Stance newStance)
        {
            if (!(newStance is Stance_Warmup)
                && !(newStance is Stance_RimKataAim))
            {
                return;
            }

            Stance_Busy busy = newStance as Stance_Busy;
            Verb verb = busy?.verb;
            Pawn target = busy?.focusTarg.Pawn;
            RimKataDraftedFireController.NotifyTargetedByHostile(target, ___pawn);
        }
    }

    internal static class RimKataDormantHostileMovementRegistry
    {
        private sealed class MapEntry
        {
            internal bool forcedNormalSpeedWasActive;
            internal bool actualCombatWasActive;
            internal bool awaitingCombatEnd;
            internal bool restoreHostileWatchPending;
            internal bool suspendedHostileWatch;
            internal HashSet<IAttackTarget> hostileTargets;
            internal readonly HashSet<Pawn> receivers =
                new HashSet<Pawn>();
            internal readonly HashSet<Pawn> pendingHostiles =
                new HashSet<Pawn>();
            internal readonly List<Pawn> hostileSnapshot =
                new List<Pawn>();
            internal readonly List<Pawn> receiverSnapshot =
                new List<Pawn>();
        }

        private static readonly ConditionalWeakTable<Map, MapEntry> ByMap =
            new ConditionalWeakTable<Map, MapEntry>();
        private static readonly ConditionalWeakTable<Map, MapEntry>
            .CreateValueCallback CreateEntry = delegate { return new MapEntry(); };

        internal static void ExposeData(Map map)
        {
            if (map == null)
            {
                return;
            }

            ByMap.TryGetValue(map, out MapEntry entry);
            bool forcedNormalSpeedWasActive = entry?.forcedNormalSpeedWasActive == true;
            bool actualCombatWasActive = entry?.actualCombatWasActive == true;
            bool awaitingCombatEnd = entry?.awaitingCombatEnd == true;
            bool watchingHostiles = entry?.hostileTargets != null
                || entry?.restoreHostileWatchPending == true
                || entry?.suspendedHostileWatch == true;
            Scribe_Values.Look(ref forcedNormalSpeedWasActive,
                "rimKataHostileWatchForcedSpeedWasActive", false);
            Scribe_Values.Look(ref actualCombatWasActive,
                "rimKataHostileWatchActualCombatWasActive", false);
            Scribe_Values.Look(ref awaitingCombatEnd,
                "rimKataHostileWatchAwaitingCombatEnd", false);
            Scribe_Values.Look(ref watchingHostiles,
                "rimKataHostileWatchActive", false);
            if (Scribe.mode != LoadSaveMode.LoadingVars)
            {
                return;
            }

            if (entry == null && !forcedNormalSpeedWasActive
                && !actualCombatWasActive && !awaitingCombatEnd && !watchingHostiles)
            {
                return;
            }
            entry ??= ByMap.GetValue(map, CreateEntry);
            ClearHostileCache(entry);
            entry.forcedNormalSpeedWasActive = forcedNormalSpeedWasActive;
            entry.actualCombatWasActive = actualCombatWasActive;
            entry.awaitingCombatEnd = awaitingCombatEnd;
            entry.restoreHostileWatchPending = watchingHostiles;
        }

        internal static void NotifyAccessChanged(Pawn pawn, bool hasAccess)
        {
            Map map = pawn?.Map;
            if (map == null)
            {
                return;
            }

            if (hasAccess && IsLiveReceiverMember(pawn, map))
            {
                ByMap.GetValue(map, CreateEntry).receivers.Add(pawn);
            }
            else if (ByMap.TryGetValue(map, out MapEntry entry))
            {
                RemoveReceiver(entry, pawn);
            }
        }

        internal static void NotifyDraftStatusChanged(Pawn pawn)
        {
            Map map = pawn?.Map;
            if (map == null)
            {
                return;
            }

            if (IsLiveReceiverMember(pawn, map))
            {
                ByMap.GetValue(map, CreateEntry).receivers.Add(pawn);
            }
            else if (ByMap.TryGetValue(map, out MapEntry entry))
            {
                RemoveReceiver(entry, pawn);
            }
        }

        internal static void NotifyPathStarted(Pawn pawn)
        {
            NotifyPathMovement(pawn);
        }

        internal static void NotifyPathCellEntered(Pawn pawn)
        {
            NotifyPathMovement(pawn);
        }

        internal static void NotifyPathStopped(Pawn pawn)
        {
            Map map = pawn?.Map;
            if (map != null
                && ByMap.TryGetValue(map, out MapEntry entry))
            {
                RemoveReceiver(entry, pawn);
                entry.pendingHostiles.Remove(pawn);
            }
        }

        internal static void NotifyMapRemoved(Map map)
        {
            if (map != null)
            {
                ByMap.Remove(map);
            }
        }

        internal static void ProcessPending(Map map, bool actualCombatActive)
        {
            if (map == null)
            {
                return;
            }

            bool? forcedNormalSpeed = Find.TickManager?.slower?.ForcedNormalSpeed;
            if (!forcedNormalSpeed.HasValue)
            {
                return;
            }

            bool forcedNormalSpeedActive = forcedNormalSpeed.Value;
            if (!ByMap.TryGetValue(map, out MapEntry entry))
            {
                if (!forcedNormalSpeedActive && !actualCombatActive)
                {
                    return;
                }
                entry = ByMap.GetValue(map, CreateEntry);
            }

            bool forcedSpeedStarted = forcedNormalSpeedActive
                && !entry.forcedNormalSpeedWasActive;
            bool forcedSpeedEnded = !forcedNormalSpeedActive
                && entry.forcedNormalSpeedWasActive;
            bool actualCombatStarted = actualCombatActive
                && !entry.actualCombatWasActive;
            bool actualCombatEnded = !actualCombatActive
                && entry.actualCombatWasActive;
            entry.forcedNormalSpeedWasActive = forcedNormalSpeedActive;
            entry.actualCombatWasActive = actualCombatActive;

            if (forcedSpeedStarted
                || (actualCombatStarted
                    && !entry.awaitingCombatEnd
                    && !forcedSpeedEnded))
            {
                ClearHostileCache(entry);
                entry.awaitingCombatEnd = true;
            }

            if (entry.awaitingCombatEnd)
            {
                if (!forcedSpeedEnded
                    && (forcedNormalSpeedActive || !actualCombatEnded))
                {
                    entry.pendingHostiles.Clear();
                    return;
                }

                // Forced-speed release owns normal combat's end even if a weapon
                // cooldown remains. Without that signal, the actual-combat fall does.
                entry.awaitingCombatEnd = false;
                AcquireHostileCacheAfterCombat(map, entry);
            }

            if (entry.restoreHostileWatchPending
                || (entry.suspendedHostileWatch && entry.receivers.Count > 0))
            {
                AcquireHostileCacheAfterCombat(map, entry);
            }

            if (entry.suspendedHostileWatch)
            {
                return;
            }

            if (entry.receivers.Count == 0)
            {
                if (entry.hostileTargets != null)
                {
                    SuspendHostileWatch(entry);
                }
                return;
            }

            if (entry.hostileTargets == null)
            {
                return;
            }
            if (entry.hostileTargets.Count == 0)
            {
                ClearHostileCache(entry);
                return;
            }

            if (entry.pendingHostiles.Count == 0)
            {
                return;
            }

            BuildLiveReceiverSnapshot(map, entry);
            if (entry.receiverSnapshot.Count == 0)
            {
                SuspendHostileWatch(entry);
                return;
            }
            BuildPendingHostileSnapshot(map, entry);
            for (int i = 0;
                i < entry.receiverSnapshot.Count
                    && entry.hostileSnapshot.Count > 0;
                i++)
            {
                RimKataDualWeaponController.TryReceiveDormantMovingHostiles(
                    entry.receiverSnapshot[i],
                    entry.hostileSnapshot);
            }

            entry.pendingHostiles.Clear();
            entry.hostileSnapshot.Clear();
            entry.receiverSnapshot.Clear();
        }

        private static void NotifyPathMovement(Pawn pawn)
        {
            Map map = pawn?.Map;
            if (map == null
                || !pawn.Spawned)
            {
                return;
            }

            ByMap.TryGetValue(map, out MapEntry existing);
            if (IsLiveReceiverMember(pawn, map))
            {
                // Membership follows path events even during combat; watching
                // hostile movement still waits for the combat-end transition.
                existing ??= ByMap.GetValue(map, CreateEntry);
                existing.receivers.Add(pawn);
                return;
            }

            if (existing == null)
            {
                return;
            }

            RemoveReceiver(existing, pawn);
            if (existing.receivers.Count == 0
                || existing.suspendedHostileWatch
                || existing.awaitingCombatEnd
                || existing.hostileTargets == null
                || Find.TickManager?.slower?.ForcedNormalSpeed != false)
            {
                return;
            }

            if (IsLiveMovingHostile(pawn, map, existing.hostileTargets))
            {
                existing.pendingHostiles.Add(pawn);
            }
            else
            {
                existing.pendingHostiles.Remove(pawn);
            }
        }

        private static void AcquireHostileCacheAfterCombat(
            Map map,
            MapEntry entry)
        {
            BuildLiveReceiverSnapshot(map, entry);
            if (entry.receiverSnapshot.Count == 0)
            {
                // This marker can only originate from completed combat or a
                // saved watch. A peaceful movement event cannot create it.
                SuspendHostileWatch(entry);
                return;
            }
            entry.receiverSnapshot.Clear();
            entry.restoreHostileWatchPending = false;
            entry.suspendedHostileWatch = false;
            entry.hostileTargets = Faction.OfPlayer != null
                ? map.attackTargetsCache?.TargetsHostileToColony
                : null;
            if (entry.hostileTargets == null || entry.hostileTargets.Count == 0)
            {
                ClearHostileCache(entry);
            }
        }

        private static void ClearHostileCache(MapEntry entry)
        {
            entry.restoreHostileWatchPending = false;
            entry.suspendedHostileWatch = false;
            entry.hostileTargets = null;
            entry.pendingHostiles.Clear();
            entry.hostileSnapshot.Clear();
            entry.receiverSnapshot.Clear();
        }

        private static void SuspendHostileWatch(MapEntry entry)
        {
            ClearHostileCache(entry);
            entry.suspendedHostileWatch = true;
        }

        private static void RemoveReceiver(MapEntry entry, Pawn pawn)
        {
            if (!entry.receivers.Remove(pawn) || entry.receivers.Count > 0)
            {
                return;
            }

            if (entry.hostileTargets != null
                || entry.restoreHostileWatchPending
                || entry.suspendedHostileWatch)
            {
                SuspendHostileWatch(entry);
            }
            else
            {
                entry.pendingHostiles.Clear();
            }
        }

        private static void BuildLiveReceiverSnapshot(
            Map map,
            MapEntry entry)
        {
            entry.receiverSnapshot.Clear();
            foreach (Pawn receiver in entry.receivers)
            {
                entry.receiverSnapshot.Add(receiver);
            }

            for (int i = entry.receiverSnapshot.Count - 1; i >= 0; i--)
            {
                Pawn receiver = entry.receiverSnapshot[i];
                if (IsLiveReceiverMember(receiver, map))
                {
                    continue;
                }

                entry.receiverSnapshot.RemoveAt(i);
                entry.receivers.Remove(receiver);
            }
        }

        private static void BuildPendingHostileSnapshot(
            Map map,
            MapEntry entry)
        {
            entry.hostileSnapshot.Clear();
            foreach (Pawn hostile in entry.pendingHostiles)
            {
                if (IsLiveMovingHostile(hostile, map, entry.hostileTargets))
                {
                    entry.hostileSnapshot.Add(hostile);
                }
            }
        }

        private static bool IsLiveReceiverMember(Pawn pawn, Map map)
        {
            return RimKataDualWeaponController.CanReceiveDormantMovingHostiles(pawn)
                && pawn.Map == map;
        }

        private static bool IsLiveMovingHostile(
            Pawn pawn,
            Map map,
            HashSet<IAttackTarget> hostileTargets)
        {
            return pawn != null
                && !pawn.Destroyed
                && pawn.Spawned
                && !pawn.Dead
                && pawn.Map == map
                && pawn.pather?.Moving == true
                && hostileTargets?.Contains(pawn) == true;
        }
    }

    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.FireAtWill),
        MethodType.Setter)]
    public static class Patch_PawnDraftController_RimKataDormantFirePermission
    {
        public static void Prefix(Pawn_DraftController __instance, out bool __state)
        {
            __state = __instance?.FireAtWill == true;
        }

        public static void Postfix(Pawn_DraftController __instance, bool __state)
        {
            RimKataDormantHostileMovementRegistry.NotifyDraftStatusChanged(
                __instance?.pawn);
            if (__instance != null && __state != __instance.FireAtWill)
            {
                RimKataDualWeaponController.NotifyFireAtWillChanged(__instance.pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    public static class Patch_PawnPathFollower_RimKataDormantPathStarted
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (___pawn?.pather?.Moving == true)
            {
                RimKataDormantHostileMovementRegistry.NotifyPathStarted(
                    ___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class Patch_PawnPathFollower_RimKataDormantPathCell
    {
        public static void Prefix(Pawn ___pawn, out IntVec3 __state)
        {
            __state = ___pawn?.Position ?? IntVec3.Invalid;
        }

        public static void Postfix(Pawn ___pawn, IntVec3 __state)
        {
            if (___pawn?.Spawned == true
                && __state.IsValid
                && ___pawn.Position != __state)
            {
                RimKataDormantHostileMovementRegistry.NotifyPathCellEntered(
                    ___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StopDead))]
    public static class Patch_PawnPathFollower_RimKataDormantPathStopped
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataDormantHostileMovementRegistry.NotifyPathStopped(___pawn);
        }
    }
}
