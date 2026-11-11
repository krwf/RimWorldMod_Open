using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMultiplayerCompat
    {
        private enum Provider { None, Rwmt, Colony, Together }
        private static Provider provider;
        private static bool initialized, ready;

        internal static bool Active => provider != Provider.None;
        internal static bool IsAuthorizedRemoteCommand(Pawn pawn) => provider == Provider.Together
            && RimKataMultiplayerTogetherCompat.IsAuthorizedRemoteCommand(pawn);
        internal static bool IsRemoteParticipant(Pawn pawn) => provider == Provider.Together
            && RimKataMultiplayerTogetherCompat.IsRemoteParticipant(pawn);
        internal static bool IsSessionActive
        {
            get
            {
                switch (provider)
                {
                    case Provider.Rwmt: return RimKataMultiplayerRwmtCompat.IsInMultiplayer;
#if RIMWORLD_1_7
                    case Provider.Colony: return RimKataMultiplayerColonyCompat.IsInSession;
#endif
                    case Provider.Together: return RimKataMultiplayerTogetherCompat.IsInSession;
                    default: return false;
                }
            }
        }

        internal static bool CanExecuteCommands => !Active || ready
            && (provider != Provider.Rwmt || RimKataMultiplayerRwmtCompat.Ready)
            && (!IsSessionActive || provider != Provider.Together
                || RimKataMultiplayerTogetherCompat.CanExecuteCommands);

        internal static bool ShouldCaptureInput(Pawn pawn)
        {
            if (Active && (!ready || provider == Provider.Rwmt && !RimKataMultiplayerRwmtCompat.Ready))
                return true;
            switch (provider)
            {
                case Provider.Rwmt:
                    return RimKataMultiplayerRwmtCompat.IsInMultiplayer
                        && RimKataMultiplayerRwmtCompat.InInterface
                        && !RimKataMultiplayerRwmtCompat.IsExecutingSyncCommand;
#if RIMWORLD_1_7
                case Provider.Colony: return RimKataMultiplayerColonyCompat.ShouldCaptureInput(pawn);
#endif
                case Provider.Together: return RimKataMultiplayerTogetherCompat.ShouldCaptureInput(pawn);
                default: return false;
            }
        }

        internal static bool AllowsSimulation(Map map)
        {
#if RIMWORLD_1_7
            if (provider == Provider.Colony) return !RimKataMultiplayerColonyCompat.IsReplicaClient;
#endif
            return provider != Provider.Together || !RimKataMultiplayerTogetherCompat.IsReplicaClient(map);
        }

        internal static void Initialize(Harmony harmony)
        {
            if (initialized) return;
            initialized = true;
            // Installed Workshop folders are irrelevant. Resolve the one running provider
            // before loading optional APIs or registering any input/simulation hooks.
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                string id = mod.ModMetaData?.PackageIdNonUnique ?? mod.PackageIdPlayerFacing;
                if (string.Equals(id, "rwmt.Multiplayer", StringComparison.OrdinalIgnoreCase))
                    provider = Provider.Rwmt;
#if RIMWORLD_1_7
                else if (string.Equals(id, "Rimworld.Multiplayer", StringComparison.OrdinalIgnoreCase))
                    provider = Provider.Colony;
#endif
                else if (string.Equals(id, "nova.rimworldtogether", StringComparison.OrdinalIgnoreCase))
                    provider = Provider.Together;
                if (Active) break;
            }
            if (!Active) return;
            try
            {
                // Install the input boundary first: failed registration must not
                // turn synchronized actions into local-only combat mutations.
                RimKataMultiplayerInput.Install(harmony);
                IReadOnlyList<MethodInfo> commands = RimKataMultiplayerCommands.Methods;
                switch (provider)
                {
                    case Provider.Rwmt:
                        ready = RimKataMultiplayerRwmtCompat.Register(harmony, commands);
                        break;
#if RIMWORLD_1_7
                    case Provider.Colony:
                        ready = RimKataMultiplayerColonyCompat.Register(harmony, commands);
                        break;
#endif
                    case Provider.Together:
                        ready = RimKataMultiplayerTogetherCompat.Register(harmony, commands);
                        break;
                }
                if (ready) Log.Message("[RimKata] Multiplayer integration registered: " + provider + ".");
            }
            catch (Exception exception)
            {
                ready = false;
                Log.Error("[RimKata] Multiplayer integration failed for " + provider + ".\n" + exception);
            }
        }
    }
}
