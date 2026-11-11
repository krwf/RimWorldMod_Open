using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Together forwards Pawn and real Job Scribe documents unchanged. Keep RK's
    // ownership metadata in those documents. Preview adds a separate, negotiated
    // RK action envelope and host-owned simulation/display snapshots.
    internal static class RimKataMultiplayerTogetherCompat
    {
        private sealed class ImportedSecondary
        {
            internal int index;
            internal string defName;
            internal string weaponId;
        }

        private sealed class PendingJob
        {
            internal Job wait;
            internal Map map;
        }

        private sealed class JobEquipment : IExposable
        {
            public JobEquipment() { }

            internal int version = 2;
            internal int weaponIndex = -1;
            internal string weaponDef;
            internal string weaponId;
            internal int verbIndex = -1;
            internal string verbType;
            internal string verbId;
            internal int targetBIndex = -1;
            internal string targetBDef;
            internal string targetBId;
            internal bool directionalFire;

            public void ExposeData()
            {
                Scribe_Values.Look(ref version, "version", 0);
                Scribe_Values.Look(ref weaponIndex, "weaponIndex", -1);
                Scribe_Values.Look(ref weaponDef, "weaponDef");
                Scribe_Values.Look(ref weaponId, "weaponId");
                Scribe_Values.Look(ref verbIndex, "verbIndex", -1);
                Scribe_Values.Look(ref verbType, "verbType");
                Scribe_Values.Look(ref verbId, "verbId");
                Scribe_Values.Look(ref targetBIndex, "targetBIndex", -1);
                Scribe_Values.Look(ref targetBDef, "targetBDef");
                Scribe_Values.Look(ref targetBId, "targetBId");
                Scribe_Values.Look(ref directionalFire, "directionalFire");
            }
        }

        private struct AskState
        {
            internal bool entered;
            internal Pawn previous;
            internal Pawn pawn;
            internal Job previousJob;
            internal bool preview;
        }

        private static readonly ConditionalWeakTable<Pawn, ImportedSecondary> imported = new();
        private static readonly ConditionalWeakTable<Pawn, PendingJob> waiting = new();
        private static readonly ConditionalWeakTable<Job, JobEquipment> jobEquipment = new();
        private static Func<bool> usingScriber;
        private static Func<bool> bypass;
        private static Func<Map> synchronousMap;
        private static bool installed;
        private static readonly AccessTools.FieldRef<JobDriver_RimKataAttack, bool> directionalFlag =
            AccessTools.FieldRefAccess<JobDriver_RimKataAttack, bool>("directionalFireJob");
        [ThreadStatic] private static Pawn serializingJobPawn;

        internal static bool IsInSession => RimKataMultiplayerTogetherWire.IsInSession;
        internal static bool CanExecuteCommands => RimKataMultiplayerTogetherWire.CanExecuteCommands;
        internal static bool IsReplicaClient(Map map) => RimKataMultiplayerTogetherWire.IsReplicaClient(map);
        internal static bool ShouldCaptureInput(Pawn pawn) => RimKataMultiplayerTogetherWire.ShouldCaptureInput(pawn);
        internal static bool IsAuthorizedRemoteCommand(Pawn pawn) => RimKataMultiplayerTogetherWire.IsAuthorizedRemoteCommand(pawn);
        internal static bool IsRemoteParticipant(Pawn pawn) => RimKataMultiplayerTogetherWire.IsRemoteParticipant(pawn);

        // Registration is local only. Preview commands remain unavailable until
        // both endpoints acknowledge the wire/build/snapshot contract.
        internal static bool Register(Harmony harmony, IReadOnlyList<MethodInfo> commands)
        {
            if (installed) return true;
            if (harmony == null) return false;
            Type session = RimKataActiveModTypes.Find("RTClient.Managers.SessionManager");
            Type patches = RimKataActiveModTypes.Find("RTClient.Misc.PatchHandler");
            Type jobs = RimKataActiveModTypes.Find("RTClient.PacketManagers.Synchronous.PM_SJob");
            MethodInfo scribe = AccessTools.PropertyGetter(session, "IsUsingScriber");
            MethodInfo map = AccessTools.PropertyGetter(session, "SynchronousMap");
            MethodInfo bypassGetter = AccessTools.PropertyGetter(patches, "BypassFlag");
            MethodInfo ask = AccessTools.Method(jobs, "Ask", new[] { typeof(Job), typeof(Pawn) });
            if (scribe?.ReturnType != typeof(bool) || !scribe.IsStatic
                || map?.ReturnType != typeof(Map) || !map.IsStatic
                || bypassGetter?.ReturnType != typeof(bool) || !bypassGetter.IsStatic
                || ask?.ReturnType != typeof(void) || !ask.IsStatic)
            {
                Log.Warning("[RimKata] Together transfer API did not match; integration was not installed.");
                return false;
            }

            var added = new List<(MethodBase target, MethodInfo patch)>();
            try
            {
                usingScriber = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), scribe);
                synchronousMap = (Func<Map>)Delegate.CreateDelegate(typeof(Func<Map>), map);
                bypass = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), bypassGetter);
                Install(AccessTools.Method(typeof(Pawn), nameof(Pawn.ExposeData)), nameof(PawnExposePostfix), 1);
                Install(AccessTools.Method(typeof(Pawn), nameof(Pawn.SpawnSetup)), nameof(PawnSpawnPostfix), 1);
                Install(AccessTools.Method(typeof(Job), nameof(Job.ExposeData)), nameof(JobExposePostfix), 1);
                Install(ask, nameof(AskPrefix), 0);
                Install(ask, nameof(AskPostfix), 1);
                Install(ask, nameof(AskFinalizer), 2);
                Install(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)), nameof(StartJobPrefix), 0);
                Install(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)), nameof(StartJobPostfix), 1);
                Install(AccessTools.Method(typeof(RimKataDualWeaponController), "TickCombat"), nameof(CombatTickPrefix), 0);
                Install(AccessTools.Method(typeof(RimKataDualWeaponController), "TickPreparedWeaponCycles"), nameof(CombatTickPrefix), 0);
                Install(AccessTools.Method(typeof(RimKataNativeAttack), "CanContinue"), nameof(CanContinuePrefix), 0);
                RimKataMultiplayerTogetherAuthority.Register(harmony);
                RimKataMultiplayerRemoteOrders.Install(harmony);
                RimKataMultiplayerTogetherQualifications.Initialize(harmony);
                RimKataMultiplayerTogetherScribe.Initialize(harmony);
                RimKataMultiplayerTogetherSnapshot.Initialize(harmony);
                RimKataMultiplayerTogetherJobs.Register(harmony);
                RimKataMultiplayerReplica.Initialize(harmony, () => IsInSession, IsReplicaClient);
                RimKataMultiplayerTogetherWire.CaptureReplica = RimKataMultiplayerTogetherSnapshot.Capture;
                RimKataMultiplayerTogetherWire.ApplyReplica = RimKataMultiplayerTogetherSnapshot.Apply;
                RimKataMultiplayerTogetherWire.ClearReplica = RimKataMultiplayerTogetherSnapshot.Clear;
                RimKataMultiplayerTogetherWire.Register(harmony, commands);
                installed = true;
                return true;
            }
            catch (Exception exception)
            {
                foreach (var patch in added) harmony.Unpatch(patch.target, patch.patch);
                RimKataMultiplayerTogetherAuthority.Unregister(harmony);
                usingScriber = null;
                synchronousMap = null;
                bypass = null;
                Log.Warning("[RimKata] Together transfer integration could not be installed: " + exception.Message);
                return false;
            }

            void Install(MethodBase target, string name, int kind)
            {
                if (target == null) throw new MissingMethodException(name);
                MethodInfo patch = AccessTools.Method(typeof(RimKataMultiplayerTogetherCompat), name);
                added.Add((target, patch));
                var method = new HarmonyMethod(patch);
                if (name == nameof(StartJobPrefix)) method.priority = Priority.First;
                if (name == nameof(PawnSpawnPostfix)) method.priority = Priority.First;
                RimKataStartupPatches.Patch(harmony, target,
                    prefix: kind == 0 ? method : null,
                    postfix: kind == 1 ? method : null,
                    finalizer: kind == 2 ? method : null);
            }
        }

        private static void PawnExposePostfix(Pawn __instance)
        {
            if (usingScriber?.Invoke() != true || __instance == null) return;
            int index = -1;
            string defName = null;
            string weaponId = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                ThingWithComps secondary = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(__instance);
                index = EquipmentIndex(__instance, secondary);
                if (index >= 0 && secondary != __instance.equipment.Primary)
                { defName = secondary.def.defName; weaponId = secondary.ThingID; }
                else index = -1;
            }
            Scribe_Values.Look(ref index, "rimKataTogetherSecondaryIndex", -1);
            Scribe_Values.Look(ref defName, "rimKataTogetherSecondaryDef");
            Scribe_Values.Look(ref weaponId, "rimKataTogetherSecondaryId");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                imported.Remove(__instance);
                if (index >= 0 && !string.IsNullOrEmpty(defName))
                    imported.Add(__instance, new ImportedSecondary { index = index, defName = defName, weaponId = weaponId });
            }
            else if (Scribe.mode == LoadSaveMode.PostLoadInit)
                RestoreSecondary(__instance);
        }

        private static void PawnSpawnPostfix(Pawn __instance)
        {
            RimKataMultiplayerTogetherWire.RecordImportedPawn(__instance);
            RestoreSecondary(__instance);
        }

        private static void RestoreSecondary(Pawn pawn)
        {
            if (pawn == null || !imported.TryGetValue(pawn, out ImportedSecondary data)) return;
            RimKataSecondaryWeaponRegistry registry = RimKataSecondaryWeaponRegistry.CurrentRegistry;
            if (registry == null) return;
            imported.Remove(pawn);
            ThingWithComps weapon = EquipmentAt(pawn, data.index, data.defName, data.weaponId);
            if (weapon == null || weapon == pawn.equipment.Primary) return;
            // The weapon already belongs to the deserialized pawn. Do not add a
            // duplicate Thing or manufacture an unregistered secondary on old transfers.
            registry.Set(pawn, weapon, invalidateBindings: pawn.Spawned);
            if (pawn.Spawned) RimKataWeaponSlotUtility.NotifyLoadoutChanged(pawn);
        }

        private static void AskPrefix(Job job, Pawn pawn, out AskState __state)
        {
            __state = new AskState { entered = true, previous = serializingJobPawn, pawn = pawn,
                previousJob = pawn?.CurJob, preview = IsPreviewPawn(pawn) };
            serializingJobPawn = pawn;
        }

        private static void AskPostfix(AskState __state)
        {
            Job current = __state.pawn?.CurJob;
            if (!__state.preview || current == __state.previousJob || current?.def != JobDefOf.Wait) return;
            waiting.Remove(__state.pawn);
            waiting.Add(__state.pawn, new PendingJob { wait = current, map = __state.pawn.Map });
        }

        private static Exception AskFinalizer(Exception __exception, AskState __state)
        {
            if (__state.entered) serializingJobPawn = __state.previous;
            return __exception;
        }

        private static void JobExposePostfix(Job __instance)
        {
            if (usingScriber?.Invoke() != true || __instance?.def != RimKataDefOf.RimKata_Attack) return;
            JobEquipment data = null;
            if (Scribe.mode == LoadSaveMode.Saving && serializingJobPawn != null)
            {
                Pawn pawn = serializingJobPawn;
                ThingWithComps weapon = __instance.verbToUse?.EquipmentSource;
                int index = EquipmentIndex(pawn, weapon);
                ThingWithComps targetB = __instance.targetB.Thing as ThingWithComps;
                int targetIndex = EquipmentIndex(pawn, targetB);
                bool directional = __instance.targetA.IsValid && !__instance.targetA.HasThing
                    || pawn.CurJob == __instance && pawn.jobs.curDriver is JobDriver_RimKataAttack driver && driver.IsDirectionalFire;
                if (index >= 0 || targetIndex >= 0 || directional)
                {
                    data = new JobEquipment { weaponIndex = index, weaponDef = index < 0 ? null : weapon.def.defName,
                        weaponId = index < 0 ? null : weapon.ThingID,
                        verbIndex = weapon?.TryGetComp<CompEquippable>()?.AllVerbs.IndexOf(__instance.verbToUse) ?? -1,
                        verbType = __instance.verbToUse?.GetType().FullName,
                        verbId = __instance.verbToUse?.GetUniqueLoadID(),
                        targetBIndex = targetIndex, targetBDef = targetIndex < 0 ? null : targetB.def.defName,
                        targetBId = targetIndex < 0 ? null : targetB.ThingID, directionalFire = directional };
                }
            }
            Scribe_Deep.Look(ref data, "rimKataTogetherEquipment");
            if (Scribe.mode == LoadSaveMode.LoadingVars && data?.version == 2)
            {
                jobEquipment.Remove(__instance);
                jobEquipment.Add(__instance, data);
            }
        }

        private static bool StartJobPrefix(Pawn ___pawn, Job newJob, out JobEquipment __state)
        {
            __state = null;
            if (newJob == null || bypass?.Invoke() != true || !IsPreviewPawn(___pawn)
                || !jobEquipment.TryGetValue(newJob, out JobEquipment data)) return true;
            jobEquipment.Remove(newJob);
            __state = data;
            ThingWithComps weapon = EquipmentAt(___pawn, data.weaponIndex, data.weaponDef, data.weaponId);
            List<Verb> verbs = weapon?.TryGetComp<CompEquippable>()?.AllVerbs;
            if (verbs != null && data.verbIndex >= 0 && data.verbIndex < verbs.Count
                && verbs[data.verbIndex].GetType().FullName == data.verbType
                && verbs[data.verbIndex].GetUniqueLoadID() == data.verbId)
                newJob.verbToUse = verbs[data.verbIndex];
            else if (data.weaponIndex >= 0) return false;
            ThingWithComps targetB = EquipmentAt(___pawn, data.targetBIndex, data.targetBDef, data.targetBId);
            if (targetB != null) newJob.targetB = targetB;
            else if (data.targetBIndex >= 0) return false;
            return true;
        }

        private static void StartJobPostfix(Pawn ___pawn, Job newJob, JobEquipment __state)
        {
            if (__state?.directionalFire == true && IsReplicaClient(___pawn?.Map)
                && ___pawn.CurJob == newJob && ___pawn.jobs.curDriver is JobDriver_RimKataAttack driver)
                directionalFlag(driver) = true;
        }

        private static bool CombatTickPrefix(Pawn pawn) => !IsReplicaClient(pawn?.Map) && !WaitingForJob(pawn);

        private static bool CanContinuePrefix(RimKataNativeAttack __instance, ref bool __result)
        {
            if (!IsReplicaClient(__instance.pawn?.Map) && !WaitingForJob(__instance.pawn)) return true;
            __result = false;
            return false;
        }

        private static bool WaitingForJob(Pawn pawn)
        {
            if (pawn == null || !waiting.TryGetValue(pawn, out PendingJob pending)) return false;
            if (IsInSession && pawn.CurJob == pending.wait && pawn.Map == pending.map && IsPreviewPawn(pawn)) return true;
            waiting.Remove(pawn);
            return false;
        }

        private static bool IsPreviewPawn(Pawn pawn)
        {
            Map map = synchronousMap?.Invoke();
            return map != null && pawn?.Map == map;
        }

        private static int EquipmentIndex(Pawn pawn, ThingWithComps weapon)
            => weapon != null && pawn?.equipment != null
                && weapon.holdingOwner == pawn.equipment.GetDirectlyHeldThings()
                    ? pawn.equipment.AllEquipmentListForReading.IndexOf(weapon) : -1;

        private static ThingWithComps EquipmentAt(Pawn pawn, int index, string defName, string weaponId)
        {
            List<ThingWithComps> equipment = pawn?.equipment?.AllEquipmentListForReading;
            if (equipment == null || index < 0 || index >= equipment.Count) return null;
            ThingWithComps weapon = equipment[index];
            return weapon != null && !weapon.Destroyed && weapon.def.defName == defName && weapon.ThingID == weaponId
                && weapon.holdingOwner == pawn.equipment.GetDirectlyHeldThings() ? weapon : null;
        }
    }
}
