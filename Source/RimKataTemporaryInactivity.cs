using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public static class RimKataTemporaryInactivity
    {
        private const int Active = 0;
        private const int Inactive = 1;

        private sealed class Entry
        {
            public volatile int state;
        }

        private static readonly ConditionalWeakTable<Pawn, Entry> entries =
            new ConditionalWeakTable<Pawn, Entry>();
        private static readonly ConditionalWeakTable<Pawn, Entry>.CreateValueCallback
            CreateEntry = CreateEntryForPawn;
        private static readonly HashSet<Pawn> inactivePawns =
            new HashSet<Pawn>();
        private static readonly object inactivePawnsLock = new object();
        private static readonly Predicate<Pawn> RemoveRecoveredPawn =
            ShouldRemoveRecoveredPawn;
        private static readonly List<Pawn> recoveredPawns = new List<Pawn>();
        private static Game trackedGame;
        private static volatile int inactivePawnCount;

        public static bool IsInactive(Pawn pawn)
        {
            if (pawn == null)
            {
                return true;
            }

            if (entries.TryGetValue(pawn, out Entry entry))
            {
                return entry.state == Inactive;
            }

            entry = entries.GetValue(pawn, CreateEntry);
            if (entry.state == Inactive)
            {
                TrackInactive(pawn);
                return true;
            }

            return false;
        }

        public static void NotifyPotentiallyInactive(Pawn pawn)
        {
            if (pawn == null
                || !entries.TryGetValue(pawn, out Entry entry)
                || entry.state == Inactive
                || !LiveInactive(pawn))
            {
                return;
            }

            SetInactive(pawn, entry, true);
        }

        public static void RefreshExisting(Pawn pawn)
        {
            if (pawn == null
                || !entries.TryGetValue(pawn, out Entry entry))
            {
                return;
            }

            SetInactive(pawn, entry, LiveInactive(pawn), true);
        }

        public static void TickInactivePawns()
        {
            if (inactivePawnCount == 0)
            {
                return;
            }

            recoveredPawns.Clear();
            lock (inactivePawnsLock)
            {
                RefreshGameScopeNoLock();
                if (inactivePawns.Count > 0)
                {
                    inactivePawns.RemoveWhere(RemoveRecoveredPawn);
                    inactivePawnCount = inactivePawns.Count;
                }
            }

            // Notify after RemoveWhere and outside the tracking lock.  The map
            // records a request; it does not reset combat from this callback.
            for (int i = 0; i < recoveredPawns.Count; i++)
            {
                NotifyMap(recoveredPawns[i], false);
            }
            recoveredPawns.Clear();
        }

        private static Entry CreateEntryForPawn(Pawn pawn)
        {
            return new Entry
            {
                state = LiveInactive(pawn)
                    ? Inactive
                    : Active
            };
        }

        private static bool LiveInactive(Pawn pawn)
        {
            return pawn != null
                && (pawn.InMentalState
                    || pawn.IsBurning()
                    || pawn.stances?.stunner?.Stunned == true);
        }

        private static void SetInactive(
            Pawn pawn,
            Entry entry,
            bool inactive,
            bool synchronizeMap = false)
        {
            int nextState = inactive ? Inactive : Active;
            bool changed = entry.state != nextState;
            entry.state = nextState;
            lock (inactivePawnsLock)
            {
                RefreshGameScopeNoLock();
                if (inactive)
                {
                    inactivePawns.Add(pawn);
                }
                else
                {
                    inactivePawns.Remove(pawn);
                }

                inactivePawnCount = inactivePawns.Count;
            }

            if (changed || synchronizeMap)
            {
                NotifyMap(pawn, inactive);
            }
        }

        private static void NotifyMap(Pawn pawn, bool inactive)
        {
            pawn?.Map?.GetComponent<RimKataMapComponent>()?
                .RequestTemporaryInactivityUpdate(pawn, inactive);
        }

        private static void TrackInactive(Pawn pawn)
        {
            lock (inactivePawnsLock)
            {
                RefreshGameScopeNoLock();
                inactivePawns.Add(pawn);
                inactivePawnCount = inactivePawns.Count;
            }
        }

        private static bool ShouldRemoveRecoveredPawn(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || !pawn.Spawned)
            {
                return true;
            }

            if (!entries.TryGetValue(pawn, out Entry entry))
            {
                return true;
            }

            if (LiveInactive(pawn))
            {
                return false;
            }

            entry.state = Active;
            recoveredPawns.Add(pawn);
            return true;
        }

        private static void RefreshGameScopeNoLock()
        {
            Game currentGame = Current.Game;
            if (trackedGame == currentGame)
            {
                return;
            }

            inactivePawns.Clear();
            inactivePawnCount = 0;
            trackedGame = currentGame;
        }
    }

    [HarmonyPatch(
        typeof(Pawn),
        nameof(Pawn.SpawnSetup),
        new Type[] { typeof(Map), typeof(bool) })]
    public static class Patch_Pawn_SpawnSetup_RimKataTemporaryInactivity
    {
        public static void Postfix(Pawn __instance)
        {
            if (__instance?.Spawned == true)
            {
                RimKataTemporaryInactivity.RefreshExisting(__instance);
            }
        }
    }

    [HarmonyPatch(
        typeof(MentalStateHandler),
        nameof(MentalStateHandler.TryStartMentalState),
        new Type[]
        {
            typeof(MentalStateDef),
            typeof(string),
            typeof(bool),
            typeof(bool),
            typeof(bool),
            typeof(Pawn),
            typeof(bool),
            typeof(bool),
            typeof(bool)
        })]
    public static class Patch_MentalStateHandler_TryStartMentalState_RimKataTemporaryInactivity
    {
        public static void Postfix(bool __result, Pawn ___pawn)
        {
            if (__result)
            {
                RimKataTemporaryInactivity.NotifyPotentiallyInactive(___pawn);
            }
        }
    }

    [HarmonyPatch(
        typeof(StunHandler),
        nameof(StunHandler.StunFor),
        new Type[]
        {
            typeof(int),
            typeof(Thing),
            typeof(bool),
            typeof(bool),
            typeof(bool)
        })]
    public static class Patch_StunHandler_StunFor_RimKataTemporaryInactivity
    {
        public static void Postfix(StunHandler __instance)
        {
            if (__instance?.Stunned == true
                && __instance.parent is Pawn pawn)
            {
                RimKataTemporaryInactivity.NotifyPotentiallyInactive(
                    pawn);
            }
        }
    }

    [HarmonyPatch(
        typeof(AttachableThing),
        nameof(AttachableThing.AttachTo),
        new Type[] { typeof(Thing) })]
    public static class Patch_AttachableThing_AttachTo_RimKataTemporaryInactivity
    {
        public static void Postfix(
            AttachableThing __instance,
            Thing newParent)
        {
            if (__instance is Fire
                && newParent is Pawn pawn
                && pawn.TryGetComp<CompAttachBase>()?
                    .attachments?.Contains(__instance) == true)
            {
                RimKataTemporaryInactivity.NotifyPotentiallyInactive(pawn);
            }
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class Patch_TickManager_DoSingleTick_RimKataTemporaryInactivity
    {
        public static void Postfix()
        {
            RimKataTemporaryInactivity.TickInactivePawns();
        }
    }
}
