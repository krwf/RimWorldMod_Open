using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.DWRN
{
    /// <summary>
    /// Selects real vanilla work jobs before the main need/schedule tree. Work priorities
    /// and needs are never edited. Only negative searches are cached; an unstarted Job
    /// is never kept for a later tick, so reservations cannot become stale in a cache.
    /// </summary>
    public static class WorkController
    {
        public const int RetryInterval = 60;
        private const string TreeName = "DWRN_ForcedWork";
        private static readonly ConditionalWeakTable<Pawn, ProbeState> Probes =
            new ConditionalWeakTable<Pawn, ProbeState>();
        private static readonly MethodInfo ConstantJobMethod =
            AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextConstantThinkTreeJob");
        private static readonly MethodInfo LeaveLordMethod =
            AccessTools.Method(typeof(Pawn_JobTracker), "CheckLeaveJoinableLordBecauseJobIssued");
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, bool> DeterminingJob =
            AccessTools.FieldRefAccess<Pawn_JobTracker, bool>("determiningNextJob");

        [ThreadStatic] private static Pawn scanningPawn;
        [ThreadStatic] private static WorkTypeDef scanningWork;

        private sealed class ProbeState
        {
            internal int FailedAt = int.MinValue;
            internal WorkTypeDef FailedWork;
            internal Job ControlledJob;
            internal int ControlledJobId = -1;
            internal WorkTypeDef ControlledWork;
            internal Map ControlledMap;
            internal Job CleanupJob;
            internal int CleanupJobId = -1;
        }

        internal static ThinkTreeDef ForcedTree
        {
            get { return DefDatabase<ThinkTreeDef>.GetNamedSilentFail(TreeName); }
        }

        internal static bool IsScanning(Pawn pawn)
        {
            return ReferenceEquals(scanningPawn, pawn);
        }

        /// <summary>No timetable, hunger, rest, recreation, or medical-need exemption.</summary>
        private static bool CanBeControlled(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && !pawn.Dead && !pawn.Downed &&
                   !pawn.Drafted && !pawn.InMentalState && !pawn.IsBurning() &&
                   pawn.Faction == Faction.OfPlayer && (pawn.IsColonist || pawn.IsSlaveOfColony) &&
                   pawn.jobs != null && pawn.thinker != null && pawn.workSettings != null &&
                   pawn.workSettings.Initialized && pawn.workSettings.EverWork;
        }

        internal static bool TryGetWork(Pawn pawn, out WorkTypeDef work)
        {
            work = null;
            if (!CanBeControlled(pawn)) return false;
            ForcedWorkMapComponent state = ForcedWorkMapComponent.For(pawn.Map);
            if (state == null || !state.Enabled || state.ForcedWork == null) return false;
            WorkTypeDef selected = state.ForcedWork;
            if (pawn.WorkTypeIsDisabled(selected) || pawn.workSettings.GetPriority(selected) <= 0)
                return false;
            work = selected;
            return true;
        }

        internal static bool IsOurJob(Job job)
        {
            return job != null && job.jobGiverThinkTree != null &&
                   job.jobGiverThinkTree.defName == TreeName;
        }

        internal static bool IsCurrentSelected(Pawn pawn, WorkTypeDef work)
        {
            Job job = pawn.CurJob;
            if (job == null) return false;
            // Scanner jobs carry their real work giver. NonScanJob (patient/fishing,
            // for example) can omit it; our registered tree identifies those jobs.
            if (job.workGiverDef != null)
            {
                if (job.workGiverDef.workType != work) return false;
            }
            else
            {
                if (!IsOurJob(job)) return false;
                ProbeState previous;
                if (Probes.TryGetValue(pawn, out previous) && previous.ControlledJob == job &&
                    previous.ControlledJobId == job.loadID &&
                    (previous.ControlledWork != work || previous.ControlledMap != pawn.Map))
                    return false;
            }
            RememberControl(pawn, job, work);
            return true;
        }

        private static void RememberControl(Pawn pawn, Job job, WorkTypeDef work)
        {
            ProbeState state = Probes.GetValue(pawn, delegate { return new ProbeState(); });
            state.ControlledJob = job;
            state.ControlledJobId = job.loadID;
            state.ControlledWork = work;
            state.ControlledMap = pawn.Map;
        }

        private static bool WasControllingCurrentJob(Pawn pawn)
        {
            ProbeState state;
            return pawn.CurJob != null && Probes.TryGetValue(pawn, out state) &&
                   state.ControlledJob == pawn.CurJob && state.ControlledJobId == pawn.CurJob.loadID;
        }

        private static void ForgetControl(Pawn pawn)
        {
            ProbeState state;
            if (!Probes.TryGetValue(pawn, out state)) return;
            state.ControlledJob = null;
            state.ControlledJobId = -1;
            state.ControlledWork = null;
            state.ControlledMap = null;
        }

        private static bool IsOtherWork(Job job, WorkTypeDef work)
        {
            if (job == null) return false;
            if (job.workGiverDef != null) return job.workGiverDef.workType != work;
            return job.jobGiver is JobGiver_Work && !IsOurJob(job);
        }

        private static bool IsConstantReaction(Pawn pawn)
        {
            return pawn.CurJob != null && pawn.thinker != null &&
                   pawn.CurJob.jobGiverThinkTree != null &&
                   pawn.CurJob.jobGiverThinkTree == pawn.thinker.ConstantThinkTree;
        }

        private static bool IsQueuedSelected(Job job, WorkTypeDef work)
        {
            return IsOurJob(job) && (job.workGiverDef == null || job.workGiverDef.workType == work);
        }

        internal static bool IsFinishingPreviousJob(Pawn pawn, WorkTypeDef work)
        {
            Job current = pawn.CurJob;
            if (current == null) return false;
            // The baby's return-to-safety finalizer can be saved without a queued
            // follow-up or source node. Recognize this specific vanilla cleanup on
            // load instead of broadly allowing unrelated hauling jobs.
            if (current.def == JobDefOf.BringBabyToSafety) return true;
            ProbeState state;
            if (Probes.TryGetValue(pawn, out state) && state.CleanupJob == current &&
                state.CleanupJobId == current.loadID)
                return true;
            // StartJob gives finalizer jobs no source tree/node and queues the new
            // selected work behind them. This also recognizes that state after load.
            bool queuedCleanup = current.jobGiver == null && current.jobGiverThinkTree == null &&
                                 pawn.jobs.jobQueue.Count > 0 &&
                                 IsQueuedSelected(pawn.jobs.jobQueue.Peek().job, work);
            if (queuedCleanup) RememberFinalizer(pawn, current);
            return queuedCleanup;
        }

        internal static void RememberFinalizer(Pawn pawn, Job job)
        {
            if (pawn == null || job == null) return;
            ProbeState state = Probes.GetValue(pawn, delegate { return new ProbeState(); });
            state.CleanupJob = job;
            state.CleanupJobId = job.loadID;
        }

        private static ThinkResult TakeQueuedSelected(Pawn pawn, WorkTypeDef work)
        {
            JobQueue queue = pawn.jobs.jobQueue;
            while (queue.Count > 0 && IsOurJob(queue.Peek().job))
            {
                QueuedJob queued = queue.Peek();
                if (!IsQueuedSelected(queued.job, work) || !queued.job.CanBeginNow(pawn))
                {
                    queue.Dequeue().Cleanup(pawn, canReturnToPool: true);
                    continue;
                }
                queue.Dequeue();
                // Retain the existing reservation and registered source identity.
                return new ThinkResult(queued.job, ForcedTree.thinkRoot, queued.tag, true);
            }
            return ThinkResult.NoJob;
        }

        private static void Invalidate(Pawn pawn)
        {
            ProbeState state;
            if (Probes.TryGetValue(pawn, out state))
            {
                state.FailedAt = int.MinValue;
                state.FailedWork = null;
            }
        }

        internal static ThinkResult TryConstantJob(Pawn pawn)
        {
            if (ConstantJobMethod == null || pawn.thinker.ConstantThinkTree == null)
                return ThinkResult.NoJob;
            // This is the game's own error-handled emergency tree path. Ordinary
            // firefighting etc. remains filtered through JobGiver_Work below.
            return (ThinkResult)ConstantJobMethod.Invoke(pawn.jobs, null);
        }

        internal static ThinkResult FindWork(Pawn pawn, WorkTypeDef work)
        {
            if (IsScanning(pawn)) return ThinkResult.NoJob;
            ThinkTreeDef tree = ForcedTree;
            if (tree == null || tree.thinkRoot == null)
            {
                Log.ErrorOnce("[Do work right now] DWRN_ForcedWork think tree is missing.", 19217101);
                return ThinkResult.NoJob;
            }

            ProbeState probe = Probes.GetValue(pawn, delegate { return new ProbeState(); });
            int tick = Find.TickManager.TicksGame;
            bool hasOurQueuedJob = pawn.jobs.jobQueue.Count > 0 &&
                                   IsOurJob(pawn.jobs.jobQueue.Peek().job);
            if (!hasOurQueuedJob && probe.FailedWork == work && probe.FailedAt <= tick &&
                (long)tick - probe.FailedAt < RetryInterval)
                return ThinkResult.NoJob;

            Pawn previousPawn = scanningPawn;
            WorkTypeDef previousWork = scanningWork;
            bool wasDetermining = DeterminingJob(pawn.jobs);
            scanningPawn = pawn;
            scanningWork = work;
            DeterminingJob(pawn.jobs) = true;
            try
            {
                ThinkResult result = TakeQueuedSelected(pawn, work);
                if (!result.IsValid)
                    result = tree.thinkRoot.TryIssueJobPackage(pawn,
                        new JobIssueParams { ignoreQueue = true });
                if (result.IsValid)
                {
                    probe.FailedWork = null;
                    // Deliberately leave playerForced false: that vanilla flag also
                    // ignores forbidden things/designations. Our scheduler supplies
                    // the persistence without changing those ordinary restrictions.
                    return result;
                }
            }
            catch (Exception exception)
            {
                Log.ErrorOnce("[Do work right now] Work search failed for " + work.defName +
                    ": " + exception, 19217102 ^ work.shortHash);
            }
            finally
            {
                scanningPawn = previousPawn;
                scanningWork = previousWork;
                DeterminingJob(pawn.jobs) = wasDetermining;
            }

            probe.FailedAt = tick;
            probe.FailedWork = work;
            return ThinkResult.NoJob;
        }

        internal static void FilterWorkGivers(Pawn pawn, ref List<WorkGiver> result)
        {
            if (!IsScanning(pawn) || scanningWork == null) return;
            // Include both emergency and normal givers in the chosen category. Using
            // only WorkGiversInOrderNormal would silently break Firefight/Patient.
            List<WorkGiverDef> definitions = scanningWork.workGiversByPriority;
            List<WorkGiver> selected = new List<WorkGiver>(definitions.Count);
            for (int i = 0; i < definitions.Count; i++)
            {
                if (definitions[i].workType == scanningWork)
                    selected.Add(definitions[i].Worker);
            }
            result = selected;
        }

        private static void StartSelected(Pawn pawn, WorkTypeDef work, ThinkResult result)
        {
            if (!result.IsValid) return;
            pawn.jobs.ClearQueuedJobs();
            if (LeaveLordMethod != null)
                LeaveLordMethod.Invoke(pawn.jobs, new object[] { result });
            pawn.jobs.StartJob(result.Job, JobCondition.InterruptForced,
                result.SourceNode, resumeCurJobAfterwards: false,
                cancelBusyStances: true, thinkTree: ForcedTree, tag: result.Tag,
                fromQueue: result.FromQueue, continueSleeping: false,
                preToilReservationsCanFail: true);
            if (pawn.CurJob == result.Job) RememberControl(pawn, result.Job, work);
        }

        private static void Reconcile(Pawn pawn)
        {
            if (pawn == null || pawn.jobs == null || pawn.jobs.startingNewJob ||
                pawn.jobs.DeterminingNextJob) return;

            WorkTypeDef work;
            if (!TryGetWork(pawn, out work))
            {
                // Assignment removal/map departure must release infinite research
                // and other jobs we started. Drafting/mental states keep vanilla
                // control; do not interrupt their replacement jobs.
                if (CanBeControlled(pawn))
                {
                    pawn.jobs.jobQueue.RemoveAll(pawn, IsOurJob);
                    if (IsOurJob(pawn.CurJob))
                    {
                        ForgetControl(pawn);
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    else if (WasControllingCurrentJob(pawn))
                    {
                        ForgetControl(pawn);
                        pawn.jobs.CheckForJobOverride();
                    }
                }
                return;
            }
            if (IsCurrentSelected(pawn, work) || IsConstantReaction(pawn) ||
                IsFinishingPreviousJob(pawn, work)) return;

            ThinkResult result = FindWork(pawn, work);
            if (result.IsValid)
            {
                StartSelected(pawn, work, result);
            }
            else if (IsOurJob(pawn.CurJob) || IsOtherWork(pawn.CurJob, work))
            {
                // No selected work means needs/idle, never a different work type.
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            // An ongoing meal/sleep/recreation is untouched after a failed search.
        }

        public static void Tick(ForcedWorkMapComponent state)
        {
            if (state == null || state.Map == null || Find.TickManager == null) return;
            // FreeHumanlikes includes player slaves but excludes colony animals.
            List<Pawn> pawns = state.Map.mapPawns.FreeHumanlikesSpawnedOfFaction(Faction.OfPlayer);
            int tick = Find.TickManager.TicksGame;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = pawns[i];
                if ((tick + pawn.thingIDNumber) % RetryInterval == 0)
                {
                    // A failed search performed between scheduled probes must not
                    // skip the next probe and stretch response time to 119 ticks.
                    Invalidate(pawn);
                    Reconcile(pawn);
                }
            }
        }

        public static void NotifyChanged(ForcedWorkMapComponent state, WorkTypeDef previous)
        {
            if (state == null || state.Map == null) return;
            // Starting/ending jobs can change pawn registries through other mods.
            List<Pawn> pawns = new List<Pawn>(
                state.Map.mapPawns.FreeHumanlikesSpawnedOfFaction(Faction.OfPlayer));
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                Invalidate(pawn);
                if (pawn.jobs == null || pawn.jobs.startingNewJob ||
                    pawn.jobs.DeterminingNextJob) continue;
                bool reevaluateNativeJob = WasControllingCurrentJob(pawn) ||
                    (previous != null && pawn.CurJob != null &&
                     pawn.CurJob.workGiverDef != null && pawn.CurJob.workGiverDef.workType == previous);
                // Remember an in-flight cleanup before removing its queued work on
                // cancellation/switch, including a freshly loaded paused save.
                if (previous != null) IsFinishingPreviousJob(pawn, previous);
                ForgetControl(pawn);
                pawn.jobs.jobQueue.RemoveAll(pawn, IsOurJob);

                // Clear a previous selected job before inspecting the new state;
                // otherwise a NonScanJob from the old type could be mistaken for it.
                if (CanBeControlled(pawn) && IsOurJob(pawn.CurJob))
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);

                WorkTypeDef work;
                if (TryGetWork(pawn, out work))
                {
                    // Existing queued orders may reserve the very objects the new
                    // forced work requires. Activating the tool replaces those orders.
                    pawn.jobs.ClearQueuedJobs();
                    Reconcile(pawn);
                    if (pawn.CurJob == null)
                        pawn.jobs.CheckForJobOverride();
                }
                else if (CanBeControlled(pawn) && (pawn.CurJob == null || reevaluateNativeJob))
                {
                    pawn.jobs.CheckForJobOverride();
                }
            }
        }

        internal static void NotifyPriorityChanged(Pawn pawn, WorkTypeDef changed)
        {
            if (pawn == null || pawn.Map == null) return;
            ForcedWorkMapComponent state = ForcedWorkMapComponent.For(pawn.Map);
            if (state == null || !state.Enabled || state.ForcedWork != changed) return;
            Invalidate(pawn);
            Reconcile(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
    internal static class DetermineNextJobPatch
    {
        private static bool Prefix(Pawn ___pawn, ref ThinkTreeDef thinkTree,
            ref bool ignoreQueue, ref bool ___determiningNextJob, ref ThinkResult __result)
        {
            WorkTypeDef work;
            if (___determiningNextJob || !WorkController.TryGetWork(___pawn, out work) ||
                WorkController.IsScanning(___pawn))
                return true;

            ignoreQueue = true;
            ___determiningNextJob = true;
            try
            {
                ThinkResult emergency = WorkController.TryConstantJob(___pawn);
                if (emergency.IsValid)
                {
                    thinkTree = ___pawn.thinker.ConstantThinkTree;
                    __result = emergency;
                    return false;
                }

                ThinkResult forced = WorkController.FindWork(___pawn, work);
                if (!forced.IsValid) return true;
                thinkTree = WorkController.ForcedTree;
                __result = forced;
                return false;
            }
            finally
            {
                // Also restore before a no-work fallthrough to the original body.
                ___determiningNextJob = false;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride))]
    internal static class KeepSelectedWorkPatch
    {
        private static bool Prefix(Pawn ___pawn)
        {
            WorkTypeDef work;
            // Constant-tree reactions run separately in JobTrackerTick. Avoiding an
            // ordinary override here prevents needs from interrupting the selected job
            // and avoids making/discarding speculative duplicates of an active job.
            return !WorkController.TryGetWork(___pawn, out work) ||
                   (!WorkController.IsCurrentSelected(___pawn, work) &&
                    !WorkController.IsFinishingPreviousJob(___pawn, work));
        }
    }

    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    internal static class SuppressOtherWorkPatch
    {
        private static bool Prefix(Pawn pawn, ref ThinkResult __result)
        {
            WorkTypeDef work;
            if (WorkController.IsScanning(pawn) || !WorkController.TryGetWork(pawn, out work))
                return true;
            __result = ThinkResult.NoJob;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.WorkGiversInOrderNormal), MethodType.Getter)]
    internal static class SelectedWorkGiversPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pawn ___pawn, ref List<WorkGiver> __result)
        {
            WorkController.FilterWorkGivers(___pawn, ref __result);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryOpportunisticJob))]
    internal static class NoOpportunisticWorkPatch
    {
        private static bool Prefix(Pawn ___pawn, Job finalizerJob, ref Job __result)
        {
            WorkTypeDef work;
            if (!WorkController.TryGetWork(___pawn, out work)) return true;
            // Preserve the old job's required cleanup through the vanilla path.
            // Vanilla returns this before considering unrelated opportunistic hauling.
            if (finalizerJob != null) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
    internal static class PriorityChangedPatch
    {
        private static void Postfix(Pawn ___pawn, WorkTypeDef w)
        {
            WorkController.NotifyPriorityChanged(___pawn, w);
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.GetFinalizerJob))]
    internal static class RememberRequiredCleanupPatch
    {
        private static void Postfix(JobDriver __instance, Job __result)
        {
            if (__result == null) return;
            WorkTypeDef work;
            if (WorkController.IsOurJob(__instance.job) ||
                WorkController.TryGetWork(__instance.pawn, out work))
                WorkController.RememberFinalizer(__instance.pawn, __result);
        }
    }
}
