using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal sealed class RimKataDownedWeaponState : IExposable
    {
        public Pawn pawn;
        public ThingWithComps weapon;
        public bool fireAllowed = true;
        public bool hasHitWhileCrawling;
        public int nextFireTick;
        internal List<Thing> crawlProjectiles;
        internal bool activeDowned;
        internal bool promotedSecondary;
        internal ThingWithComps originalPrimary;
        internal bool recoveryJobIssued;
        internal bool restoring;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref fireAllowed, "fireAllowed", true);
            Scribe_Values.Look(ref hasHitWhileCrawling, "hasHitWhileCrawling");
            Scribe_Values.Look(ref nextFireTick, "nextFireTick");
            Scribe_Values.Look(ref activeDowned, "activeDowned");
            Scribe_Values.Look(ref promotedSecondary, "promotedSecondary");
            Scribe_References.Look(ref originalPrimary, "originalPrimary");
            Scribe_Values.Look(ref recoveryJobIssued, "recoveryJobIssued");
            RimKataCrawlFireHits.ExposeProjectiles(this);
        }
    }

    public sealed class RimKataDownedWeaponRegistry : GameComponent
    {
        private static RimKataDownedWeaponRegistry instance;
        private readonly Game game;
        private List<RimKataDownedWeaponState> saved = new List<RimKataDownedWeaponState>();
        private readonly Dictionary<Pawn, RimKataDownedWeaponState> states =
            new Dictionary<Pawn, RimKataDownedWeaponState>();

        public RimKataDownedWeaponRegistry(Game game)
        {
            this.game = game;
            instance = this;
            RimKataCrawlFireUtility.ClearRuntime();
        }

        internal static RimKataDownedWeaponRegistry Current =>
            instance?.game == Verse.Current.Game ? instance : null;

        internal bool TryGet(Pawn pawn, out RimKataDownedWeaponState state)
        {
            state = null;
            return pawn != null && states.TryGetValue(pawn, out state);
        }

        internal void Set(RimKataDownedWeaponState state) => states[state.pawn] = state;

        internal void Remove(Pawn pawn)
        {
            if (pawn != null) states.Remove(pawn);
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                saved = new List<RimKataDownedWeaponState>(states.Values);
            Scribe_Collections.Look(ref saved, "rimKataDownedWeapons", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Build before map respawns: vanilla drops downed equipment again
                // in Notify_PawnSpawned, including while loading a save.
                states.Clear();
                if (saved == null) return;
                foreach (RimKataDownedWeaponState state in saved)
                    if (state?.pawn != null && state.weapon != null)
                        states[state.pawn] = state;
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            foreach (RimKataDownedWeaponState state in
                new List<RimKataDownedWeaponState>(states.Values))
            {
                if (state.pawn.Dead || !RimKataDownedWeaponUtility.StillHeld(state))
                {
                    states.Remove(state.pawn);
                    continue;
                }
                state.activeDowned &= state.pawn.Downed;
                if (!state.activeDowned && !state.promotedSecondary)
                {
                    states.Remove(state.pawn);
                    continue;
                }
                RimKataDownedWeaponUtility.RefreshRecovery(state);
                if (state.activeDowned)
                    RimKataCrawlFireUtility.NotifyRetained(state.pawn);
            }
            saved?.Clear();
        }
    }

    internal static class RimKataDownedWeaponUtility
    {
        internal sealed class DowningContext
        {
            internal DowningContext previous;
            internal Pawn pawn;
            internal ThingWithComps primary;
            internal ThingWithComps secondary;
            internal ThingWithComps retained;
            internal bool retentionApplied;
        }

        internal struct DropProtection
        {
            internal Pawn pawn;
            internal ThingWithComps weapon;
        }

        [ThreadStatic] private static DowningContext downing;
        [ThreadStatic] private static Pawn respawning;
        [ThreadStatic] private static DropProtection protectedDrop;

        internal static bool TryGet(Pawn pawn, out RimKataDownedWeaponState state)
        {
            state = null;
            return pawn?.Downed == true && !pawn.Dead
                && RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out state) == true
                && state.activeDowned && state.weapon != null && !state.weapon.Destroyed;
        }

        internal static bool StillHeld(RimKataDownedWeaponState state) =>
            state?.weapon != null && !state.weapon.Destroyed
            && state.pawn?.equipment?.AllEquipmentListForReading.Contains(state.weapon) == true;

        internal static DowningContext BeginDowning(Pawn pawn)
        {
            if (pawn == null || pawn.Downed) return null;
            RimKataDownedWeaponRegistry registry = RimKataDownedWeaponRegistry.Current;
            if (registry == null) return null;
            bool dropPromoted = registry.TryGet(pawn, out RimKataDownedWeaponState old)
                && old.promotedSecondary && StillHeld(old)
                && pawn.equipment.Primary == old.weapon;
            Release(pawn);
            if (dropPromoted)
            {
                RimKataSecondaryWeaponRegistry.CurrentRegistry?.RemoveRecovery(pawn);
                return null;
            }
            ThingWithComps primary = pawn.equipment?.Primary;
            if (!pawn.Spawned || pawn.Dead
                || RimKataTargetAccess.SettingsFor(pawn)?.crawlFireEnabled != true
                || !RimKataWeaponSlotUtility.CanUseOneHandWeapon(pawn, primary)
                || !RimKataGroundPoseHead.Supports(pawn))
                return null;
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            ThingWithComps retained = secondary?.def.IsRangedWeapon == true
                && pawn.equipment.AllEquipmentListForReading.Contains(secondary)
                ? secondary : primary?.def.IsRangedWeapon == true ? primary : null;
            if (retained == null) return null;
            DowningContext context = new DowningContext
            {
                previous = downing,
                pawn = pawn,
                primary = primary,
                secondary = secondary,
                retained = retained
            };
            downing = context;
            return context;
        }

        internal static void EndDowning(DowningContext context, bool succeeded)
        {
            if (context == null) return;
            downing = context.previous;
            Pawn pawn = context.pawn;
            if (!succeeded || !context.retentionApplied || !pawn.Downed || pawn.Dead
                || pawn.equipment?.AllEquipmentListForReading.Contains(context.retained) != true)
                return;
            bool promoted = context.retained == context.secondary
                && pawn.equipment.Primary == context.retained;
            RimKataDownedWeaponState state = new RimKataDownedWeaponState
            {
                pawn = pawn,
                weapon = context.retained,
                activeDowned = true,
                fireAllowed = RimKataTargetAccess.SettingsFor(pawn)?.crawlFireDefaultAllowed
                    ?? RimKataSettings.DefaultCrawlFireDefaultAllowed,
                promotedSecondary = promoted,
                originalPrimary = promoted && pawn.mindState?.droppedWeapon == context.primary
                    ? context.primary : null
            };
            RimKataDownedWeaponRegistry.Current.Set(state);
            RimKataSecondaryWeaponRegistry secondaryRegistry = RimKataSecondaryWeaponRegistry.CurrentRegistry;
            if (context.retained == context.primary)
            {
                if (context.secondary?.Spawned == true)
                    secondaryRegistry?.RecordDroppedLoadout(pawn, context.primary, context.secondary,
                        primaryRetained: true);
                else
                    secondaryRegistry?.KeepRecoveryForRetainedPrimary(pawn, context.primary);
            }
            else
                secondaryRegistry?.RemoveRecovery(pawn);
            RimKataCrawlFireUtility.NotifyRetained(pawn);
        }

        internal static Pawn BeginSpawn(Pawn pawn)
        {
            Pawn previous = respawning;
            if (TryGet(pawn, out RimKataDownedWeaponState state) && StillHeld(state))
                respawning = pawn;
            return previous;
        }

        internal static void EndSpawn(Pawn previous) => respawning = previous;

        internal static DropProtection BeginDrop(Pawn pawn, bool rememberPrimary)
        {
            DropProtection previous = protectedDrop;
            protectedDrop = default;
            if (pawn?.Downed != true || pawn.Dead) return previous;
            if (rememberPrimary && downing?.pawn == pawn)
                protectedDrop = new DropProtection { pawn = pawn, weapon = downing.retained };
            else if (respawning == pawn && TryGet(pawn, out RimKataDownedWeaponState state))
                protectedDrop = new DropProtection { pawn = pawn, weapon = state.weapon };
            return previous;
        }

        internal static void EndDrop(DropProtection previous) => protectedDrop = previous;

        internal static bool PreventDrop(Pawn pawn, ThingWithComps weapon)
        {
            if (protectedDrop.pawn != pawn || protectedDrop.weapon != weapon || weapon == null
                || !pawn.Downed || pawn.Dead)
                return false;
            if (downing?.pawn == pawn && downing.retained == weapon)
                downing.retentionApplied = true;
            return true;
        }

        internal static void NotifyUndowned(Pawn pawn)
        {
            if (pawn?.Downed != false
                || RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) != true)
                return;
            if (state.activeDowned) RimKataCrawlFireUtility.NotifyReleased(pawn);
            state.activeDowned = false;
            if (!state.promotedSecondary)
                RimKataDownedWeaponRegistry.Current.Remove(pawn);
            else RefreshRecovery(state);
        }

        internal static void Release(Pawn pawn)
        {
            RimKataDownedWeaponRegistry registry = RimKataDownedWeaponRegistry.Current;
            if (registry?.TryGet(pawn, out RimKataDownedWeaponState state) != true) return;
            registry.Remove(pawn);
            if (state.activeDowned) RimKataCrawlFireUtility.NotifyReleased(pawn);
        }

        internal static void NotifyCarried(Pawn_CarryTracker carrier, Thing item)
        {
            if (!(item is Pawn pawn) || carrier.CarriedThing != pawn
                || !TryGet(pawn, out RimKataDownedWeaponState state) || !StillHeld(state)
                || pawn.MapHeld == null || !pawn.PositionHeld.IsValid)
                return;

            // The carry has succeeded: use the holder's map and position after
            // despawning, so failed pickup attempts never disarm the pawn.
            pawn.equipment.TryDropEquipment(state.weapon, out _, pawn.PositionHeld, true);
        }

        internal static void NotifyEquipmentChanged(Pawn pawn, ThingWithComps equipment, bool removed)
        {
            if (RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) != true
                || state.restoring || downing?.pawn == pawn)
                return;
            if (pawn.Dead || !StillHeld(state) || pawn.equipment.Primary != state.weapon)
            {
                Release(pawn);
                return;
            }
            if (!removed && equipment != state.weapon)
                ForgetRecovery(state);
        }

        private static void ForgetRecovery(RimKataDownedWeaponState state)
        {
            state.originalPrimary = null;
            state.recoveryJobIssued = false;
        }

        internal static bool IsRecoveryJob(Pawn pawn, Job job)
        {
            return RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) == true
                && state.recoveryJobIssued && state.originalPrimary != null
                && job?.def == JobDefOf.Equip && !job.playerForced
                && job.targetA.Thing == state.originalPrimary;
        }

        internal static void RefreshRecovery(RimKataDownedWeaponState state)
        {
            if (state.originalPrimary == null) return;
            if (state.originalPrimary.Destroyed
                || (state.pawn.mindState?.droppedWeapon != state.originalPrimary
                    && !IsRecoveryJob(state.pawn, state.pawn.CurJob)
                    && !HasQueuedRecovery(state)))
                ForgetRecovery(state);
        }

        private static bool HasQueuedRecovery(RimKataDownedWeaponState state)
        {
            JobQueue queue = state.pawn.jobs?.jobQueue;
            if (!state.recoveryJobIssued || queue == null) return false;
            for (int i = 0; i < queue.Count; i++)
            {
                Job job = queue[i]?.job;
                if (job?.def == JobDefOf.Equip && !job.playerForced
                    && job.targetA.Thing == state.originalPrimary)
                    return true;
            }
            return false;
        }

        internal static void NotifyRecoveryJob(Pawn pawn, Job job)
        {
            if (RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) != true)
                return;
            RefreshRecovery(state);
            if (state.originalPrimary != null && pawn.mindState?.droppedWeapon == state.originalPrimary
                && job?.def == JobDefOf.Equip && !job.playerForced && job.targetA.Thing == state.originalPrimary)
                state.recoveryJobIssued = true;
        }

        internal static void NotifyRecoveryEnded(Pawn pawn)
        {
            if (RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) == true
                && !state.restoring)
                RefreshRecovery(state);
        }

        internal static bool TryGetRestoration(Pawn pawn, ThingWithComps incoming, out ThingWithComps retained)
        {
            retained = null;
            if (pawn?.Downed != false || !IsRecoveryJob(pawn, pawn.CurJob)
                || RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) != true
                || state.originalPrimary != incoming || !StillHeld(state)
                || pawn.equipment.Primary != state.weapon
                || !RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn, incoming, false)
                || !RimKataEquipmentUtility.IsWeaponEnabled(state.weapon.def)
                || RimKataGripUtility.GripTypeFor(state.weapon.def) != RimKataGripType.OneHand)
                return false;
            retained = state.weapon;
            return true;
        }

        internal static void BeginRestoration(Pawn pawn)
        {
            if (RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) == true)
                state.restoring = true;
        }

        internal static void EndRestoration(Pawn pawn, bool restored)
        {
            if (RimKataDownedWeaponRegistry.Current?.TryGet(pawn, out RimKataDownedWeaponState state) != true) return;
            state.restoring = false;
            if (restored) Release(pawn);
            else RefreshRecovery(state);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    internal static class Patch_PawnHealthTracker_RimKataRetainDownedWeapon
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Pawn ___pawn, out RimKataDownedWeaponUtility.DowningContext __state) =>
            __state = RimKataDownedWeaponUtility.BeginDowning(___pawn);

        public static Exception Finalizer(Exception __exception, RimKataDownedWeaponUtility.DowningContext __state)
        {
            RimKataDownedWeaponUtility.EndDowning(__state, __exception == null);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.DropAllEquipment))]
    internal static class Patch_PawnEquipmentTracker_RimKataDownedDropScope
    {
        public static void Prefix(Pawn ___pawn, bool rememberPrimary,
            out RimKataDownedWeaponUtility.DropProtection __state) =>
            __state = RimKataDownedWeaponUtility.BeginDrop(___pawn, rememberPrimary);

        public static void Finalizer(RimKataDownedWeaponUtility.DropProtection __state) =>
            RimKataDownedWeaponUtility.EndDrop(__state);
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.TryDropEquipment))]
    internal static class Patch_PawnEquipmentTracker_RimKataKeepDownedWeapon
    {
        public static bool Prefix(Pawn ___pawn, ThingWithComps eq, ref ThingWithComps resultingEq, ref bool __result)
        {
            if (!RimKataDownedWeaponUtility.PreventDrop(___pawn, eq)) return true;
            resultingEq = null;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_PawnSpawned))]
    internal static class Patch_PawnEquipmentTracker_RimKataDownedSpawn
    {
        public static void Prefix(Pawn ___pawn, out Pawn __state) =>
            __state = RimKataDownedWeaponUtility.BeginSpawn(___pawn);

        public static Exception Finalizer(Pawn ___pawn, Pawn __state, Exception __exception)
        {
            RimKataDownedWeaponUtility.EndSpawn(__state);
            if (__exception == null) RimKataCrawlFireUtility.NotifyRetained(___pawn);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeUndowned")]
    internal static class Patch_PawnHealthTracker_RimKataRetainedWeaponRecovered
    {
        public static void Postfix(Pawn ___pawn) => RimKataDownedWeaponUtility.NotifyUndowned(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_PawnDied))]
    internal static class Patch_PawnEquipmentTracker_RimKataRetainedWeaponDied
    {
        public static void Prefix(Pawn ___pawn) => RimKataDownedWeaponUtility.Release(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
        new[] { typeof(Thing) })]
    internal static class Patch_PawnCarryTracker_RimKataDropRetainedWeapon
    {
        public static void Postfix(Pawn_CarryTracker __instance, Thing item, bool __result)
        {
            if (__result) RimKataDownedWeaponUtility.NotifyCarried(__instance, item);
        }
    }

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
        new[] { typeof(Thing), typeof(int), typeof(bool) })]
    internal static class Patch_PawnCarryTracker_RimKataDropRetainedWeaponCounted
    {
        public static void Postfix(Pawn_CarryTracker __instance, Thing item, int __result)
        {
            if (__result > 0) RimKataDownedWeaponUtility.NotifyCarried(__instance, item);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Strip))]
    internal static class Patch_Pawn_RimKataForbidStrippedRetainedWeapon
    {
        public static void Prefix(Pawn __instance, out ThingWithComps __state)
        {
            // Capture before removal clears the retained record and stripping
            // apparel can change eligibility or faction relations.
            __state = RimKataDownedWeaponUtility.TryGet(__instance, out RimKataDownedWeaponState state)
                && RimKataDownedWeaponUtility.StillHeld(state)
                && __instance.Faction?.HostileTo(Faction.OfPlayer) == true ? state.weapon : null;
        }

        public static void Postfix(ThingWithComps __state)
        {
            if (__state?.Spawned == true) __state.SetForbidden(true, false);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    internal static class Patch_PawnJobTracker_RimKataRetainedWeaponRecoveryEnded
    {
        public static void Prefix(Pawn ___pawn, bool releaseReservations, out bool __state) =>
            __state = releaseReservations && RimKataDownedWeaponUtility.IsRecoveryJob(___pawn, ___pawn.CurJob);

        public static void Postfix(Pawn ___pawn, bool __state)
        {
            if (__state) RimKataDownedWeaponUtility.NotifyRecoveryEnded(___pawn);
        }
    }
}
