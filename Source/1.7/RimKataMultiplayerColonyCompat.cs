using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // ColonyTogether is host authoritative. Its API and replication model are independent of rwmt.
    internal static class RimKataMultiplayerColonyCompat
    {
        private static Func<bool> inSession, isHost;
        private static bool registered;
        private static bool refusedCommandLogged;
        private static Func<bool> ownsInput;
        private static FieldInfo applying;
        private static readonly RegistryBinding breachBinding = new(typeof(RimKataBreachUtility), "AttackStateVersion");
        private static readonly RegistryBinding reactiveBinding = new(typeof(RimKataReactiveMotion), "AttackStateVersion");
        private static readonly RegistryBinding subdueBinding = new(typeof(RimKataSubdueUtility), "RelationVersion");

        internal static bool IsInSession => inSession?.Invoke() == true;
        internal static bool RegistrationReady => registered;
        internal static bool ShouldCaptureInput(Pawn pawn) =>
            pawn != null && IsReplicaClient && (!registered
                || ownsInput?.Invoke() == true && !(bool)applying.GetValue(null));

        internal static bool IsReplicaClient => IsInSession && isHost?.Invoke() == false;

        internal static bool Register(Harmony harmony, IReadOnlyList<MethodInfo> commands)
        {
            if (registered) return true;
            Type api = AccessTools.TypeByName("RimworldMultiplayer.Compat.CompatApi");
            if (api == null) return false;
            try
            {
                if (Convert.ToInt32(AccessTools.Field(api, "Version")?.GetRawConstantValue()) < 4)
                    throw new NotSupportedException("ColonyTogether compatibility API 4 is required.");
                inSession = AccessTools.MethodDelegate<Func<bool>>(AccessTools.DeclaredMethod(api, "InSession"));
                isHost = AccessTools.MethodDelegate<Func<bool>>(AccessTools.DeclaredMethod(api, "IsHost"));
                Type controls = AccessTools.TypeByName("ColonyTogether.NativeControls");
                Type replica = AccessTools.TypeByName("ColonyTogether.GenericReplica");
                ownsInput = AccessTools.MethodDelegate<Func<bool>>(AccessTools.PropertyGetter(controls, "OwnsInput"));
                applying = AccessTools.Field(replica, "Applying") ?? throw new MissingFieldException(replica?.FullName, "Applying");
                MethodInfo sync = AccessTools.DeclaredMethod(api, "RegisterSyncMethod");
                MethodInfo hostOnly = AccessTools.DeclaredMethod(api, "RegisterHostOnlyMethod");
                if (sync == null || hostOnly == null) throw new MissingMethodException(api.FullName);
                foreach (MethodInfo command in commands)
                {
                    if (command == null || !command.IsStatic || command.ReturnType != typeof(void))
                        throw new ArgumentException("RimKata multiplayer commands must be static void methods.");
                    harmony.Patch(command, prefix: new HarmonyMethod(typeof(RimKataMultiplayerColonyCompat), nameof(GuardCommand))
                        { priority = Priority.First + 100 });
                }
                foreach (MethodInfo command in commands)
                {
                    Check(sync.Invoke(null, new object[] { command, 0 }), command);
                }

                // Components already travel through GenericManagers/ExposeData. Their simulation must
                // not be replayed by the client's generic visual-tick discovery.
                foreach (Type type in typeof(RimKataMapComponent).Assembly.GetTypes())
                {
                    if (!typeof(GameComponent).IsAssignableFrom(type) && !typeof(MapComponent).IsAssignableFrom(type)) continue;
                    foreach (string name in new[] { "GameComponentTick", "MapComponentTick" })
                    {
                        MethodInfo tick = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
                        if (tick != null) Check(hostOnly.Invoke(null, new object[] { tick }), tick);
                    }
                }
                foreach (Type type in new[] { typeof(RimKataPushMotion), typeof(RimKataThrownPawn) })
                {
                    MethodInfo tick = AccessTools.DeclaredMethod(type, "TickInterval");
                    Check(hostOnly.Invoke(null, new object[] { tick }), tick);
                }

                Patch(harmony, typeof(RimKataBreachRegistry), "ExposeData", after: nameof(BreachLoaded));
                Patch(harmony, typeof(RimKataReactiveRegistry), "ExposeData", after: nameof(ReactiveLoaded));
                Patch(harmony, typeof(RimKataSubdueRegistry), "ExposeData", after: nameof(SubdueLoaded));
                Patch(harmony, typeof(RimKataSecondaryWeaponRegistry), "ExposeData", after: nameof(SecondaryLoaded));
                Patch(harmony, typeof(RimKataBreachRegistry), "FinalizeInit", before: nameof(BreachFinalize));
                Patch(harmony, typeof(RimKataReactiveRegistry), "FinalizeInit", before: nameof(ReactiveFinalize));
                Patch(harmony, typeof(RimKataSubdueRegistry), "FinalizeInit", before: nameof(SubdueFinalize));
                Patch(harmony, typeof(RimKataSecondaryWeaponRegistry), "FinalizeInit", before: nameof(SecondaryFinalize));
                Patch(harmony, typeof(RimKataDownedWeaponRegistry), "ExposeData", after: nameof(CrawlLoaded));
                Patch(harmony, typeof(RimKataDownedWeaponRegistry), "FinalizeInit", before: nameof(CrawlFinalize));
                Patch(harmony, typeof(RimKataBreachUtility), "SetRegistry", before: nameof(BreachBind));
                Patch(harmony, typeof(RimKataReactiveMotion), "SetRegistry", before: nameof(ReactiveBind));
                Patch(harmony, typeof(RimKataSubdueUtility), "SetRegistry", before: nameof(SubdueBind));
                Check(hostOnly.Invoke(null, new object[] { AccessTools.DeclaredMethod(typeof(RimKataWeaponEffectRegistry), "LoadedGame") }),
                    AccessTools.DeclaredMethod(typeof(RimKataWeaponEffectRegistry), "LoadedGame"));
                RimKataMultiplayerReplica.Initialize(harmony, () => IsInSession, _ => IsReplicaClient);
                registered = true;
                return true;
            }
            catch (Exception error)
            {
                Log.Warning("[RimKata] ColonyTogether compatibility registration failed: " + (error.InnerException ?? error));
                return false;
            }
        }

        private static void Check(object result, MethodInfo method)
        {
            string status = result as string;
            if (status == null || !status.StartsWith("installed|", StringComparison.Ordinal))
                throw new InvalidOperationException(method.DeclaringType?.Name + "." + method.Name + ": " + status);
        }

        private static bool GuardCommand()
        {
            if (!IsInSession || registered) return true;
            if (!refusedCommandLogged)
            {
                refusedCommandLogged = true;
                Log.Message("[RimKata] Multiplayer command refused because ColonyTogether compatibility registration did not complete.");
            }
            return false;
        }

        private static void Patch(Harmony harmony, Type type, string method, string before = null, string after = null)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method)
                ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target,
                prefix: before == null ? null : new HarmonyMethod(typeof(RimKataMultiplayerColonyCompat), before),
                postfix: after == null ? null : new HarmonyMethod(typeof(RimKataMultiplayerColonyCompat), after));
        }

        private static bool Loaded => IsReplicaClient && Scribe.mode == LoadSaveMode.PostLoadInit;
        private static void BreachLoaded(RimKataBreachRegistry __instance) { if (Loaded) RimKataMultiplayerReplica.PublishBreach(__instance); }
        private static void ReactiveLoaded(RimKataReactiveRegistry __instance) { if (Loaded) RimKataMultiplayerReplica.PublishReactive(__instance); }
        private static void SubdueLoaded(RimKataSubdueRegistry __instance) { if (Loaded) RimKataMultiplayerReplica.PublishSubdue(__instance); }
        private static void SecondaryLoaded(RimKataSecondaryWeaponRegistry __instance) { if (Loaded) RimKataMultiplayerReplica.PublishSecondary(__instance); }
        private static void CrawlLoaded(RimKataDownedWeaponRegistry __instance) { if (Loaded) RimKataMultiplayerReplica.PublishCrawl(__instance); }

        private static bool BreachFinalize(RimKataBreachRegistry __instance)
        {
            if (!IsReplicaClient) return true;
            RimKataMultiplayerReplica.PublishBreach(__instance);
            return false;
        }
        private static bool ReactiveFinalize(RimKataReactiveRegistry __instance)
        {
            if (!IsReplicaClient) return true;
            RimKataMultiplayerReplica.PublishReactive(__instance);
            return false;
        }
        private static bool SubdueFinalize(RimKataSubdueRegistry __instance)
        {
            if (!IsReplicaClient) return true;
            RimKataMultiplayerReplica.PublishSubdue(__instance);
            return false;
        }
        private static bool SecondaryFinalize(RimKataSecondaryWeaponRegistry __instance)
        {
            if (!IsReplicaClient) return true;
            RimKataMultiplayerReplica.PublishSecondary(__instance);
            return false;
        }
        private static bool CrawlFinalize(RimKataDownedWeaponRegistry __instance)
        {
            if (!IsReplicaClient) return true;
            RimKataMultiplayerReplica.PublishCrawl(__instance);
            return false;
        }

        // GenericFreshFields creates a temporary component before loading each received snapshot.
        // A replica must not End/Cancel the previous combat while merely constructing that shell.
        private static bool BreachBind(RimKataBreachRegistry value) => breachBinding.BindReplica(value);
        private static bool ReactiveBind(RimKataReactiveRegistry value) => reactiveBinding.BindReplica(value);
        private static bool SubdueBind(RimKataSubdueRegistry value) => subdueBinding.BindReplica(value);

        private sealed class RegistryBinding
        {
            private readonly FieldInfo registry, version;
            internal RegistryBinding(Type owner, string versionName)
            {
                registry = AccessTools.Field(owner, "registry");
                version = AccessTools.Field(owner, "<" + versionName + ">k__BackingField");
            }
            internal bool BindReplica(object value)
            {
                if (!IsReplicaClient) return true;
                if (!ReferenceEquals(registry.GetValue(null), value))
                {
                    registry.SetValue(null, value);
                    version.SetValue(null, unchecked((int)version.GetValue(null) + 1));
                }
                return false;
            }
        }
    }
}
