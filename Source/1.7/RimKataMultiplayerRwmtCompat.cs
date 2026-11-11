using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMultiplayerRwmtCompat
    {
        private static bool attempted;
        private static bool preparationAttempted;
        private static Type api;
        private static Func<bool> isInMultiplayer;
        private static Func<bool> inInterface;
        private static Func<bool> isExecutingSyncCommand;
        private static Func<bool> isHosting;
        private static Func<bool> isUsingSynchronizedConfig;

        internal static bool Prepared { get; private set; }
        internal static bool Ready { get; private set; }
        internal static string Failure { get; private set; }
        internal static int RegisteredCommandCount { get; private set; }
        internal static bool IsInMultiplayer => Read(isInMultiplayer, true);
        internal static bool InInterface => Read(inInterface, false);
        internal static bool IsExecutingSyncCommand => Read(isExecutingSyncCommand, false);
        internal static bool IsHosting => Read(isHosting, false);
        internal static bool IsUsingSynchronizedConfig => Read(isUsingSynchronizedConfig, false);

        // Called only after active-provider detection, before reading the RK profile
        // payload. The 1.6 temporary config override can precede command registration.
        internal static bool Prepare()
        {
            if (preparationAttempted) return Prepared;
            preparationAttempted = true;
            try
            {
                api = RimKataActiveModTypes.Find("Multiplayer.API.MP");
                if (api == null) throw new InvalidOperationException("The active rwmt Multiplayer API was not found.");
                FieldInfo enabled = api.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (enabled == null || enabled.FieldType != typeof(bool) || !(bool)enabled.GetValue(null))
                    throw new InvalidOperationException("The active rwmt Multiplayer API bridge is not ready.");

                isInMultiplayer = BindBoolean(api, "IsInMultiplayer");
                inInterface = BindBoolean(api, "InInterface");
                isExecutingSyncCommand = BindBoolean(api, "IsExecutingSyncCommand");
                isHosting = BindBoolean(api, "IsHosting");
                Type configs = RimKataActiveModTypes.Find("Multiplayer.Client.Util.SyncConfigs");
                // AssembliesCustom can be absent from the earlier active-type snapshot.
                // This legacy config hint is not a command synchronization dependency.
                if (configs != null)
                {
                    try { isUsingSynchronizedConfig = BindBoolean(configs, "Applicable"); }
                    catch { isUsingSynchronizedConfig = null; }
                }
                Prepared = true;
                return true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return false;
            }
        }

        // The caller has already identified the active rwmt mod. Do not call this for
        // another multiplayer provider or discover optional types from a runtime getter.
        internal static bool Register(Harmony harmony, IReadOnlyList<MethodInfo> commandMethods)
        {
            if (attempted) return Ready;
            attempted = true;
            try
            {
                if (harmony == null) throw new ArgumentNullException(nameof(harmony));
                if (commandMethods == null || commandMethods.Count == 0)
                    throw new ArgumentException("No RimKata multiplayer commands were supplied.", nameof(commandMethods));
                if (!Prepare()) return false;

                Type syncType = api.Assembly.GetType("Multiplayer.API.SyncType", false);
                Type syncMethod = api.Assembly.GetType("Multiplayer.API.ISyncMethod", false);
                if (syncType == null || syncMethod == null)
                    throw new InvalidOperationException("The rwmt command registration types do not match.");
                MethodInfo register = api.GetMethod("RegisterSyncMethod", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(MethodInfo), syncType.MakeArrayType() }, null);
                if (register == null || register.ReturnType != syncMethod)
                    throw new InvalidOperationException("The rwmt command registration API does not match.");

                var methods = new List<MethodInfo>(commandMethods.Count);
                var distinct = new HashSet<MethodInfo>();
                for (int i = 0; i < commandMethods.Count; i++)
                {
                    MethodInfo method = commandMethods[i];
                    ValidateCommand(method);
                    if (!distinct.Add(method))
                        throw new ArgumentException("A RimKata multiplayer command was supplied twice: " + method);
                    methods.Add(method);
                }
                methods.Sort((left, right) => string.CompareOrdinal(CommandKey(left), CommandKey(right)));

                // rwmt owns the synchronized call interception and replay gate. It uses
                // its existing Pawn/Thing/IntVec3/value serializers; no RK state object,
                // GUI selection or captured delegate is serialized by these commands.
                // The API has no unregister operation, so validate the whole list first
                // and never retry a partially registered list during the same startup.
                object[] arguments = new object[2];
                for (int i = 0; i < methods.Count; i++)
                {
                    arguments[0] = methods[i];
                    object result = register.Invoke(null, arguments);
                    if (result == null || !syncMethod.IsInstanceOfType(result))
                        throw new InvalidOperationException("rwmt did not register " + CommandKey(methods[i]) + ".");
                    RegisteredCommandCount++;
                }
                Ready = true;
                return true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return false;
            }
        }

        private static Func<bool> BindBoolean(Type api, string name)
        {
            MethodInfo getter = api.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetGetMethod(true);
            if (getter == null || getter.ReturnType != typeof(bool) || getter.GetParameters().Length != 0)
                throw new InvalidOperationException("The rwmt state API does not match: " + name + ".");
            return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
        }

        private static void ValidateCommand(MethodInfo method)
        {
            if (method == null || !method.IsStatic || method.IsAbstract || method.ContainsGenericParameters
                || method.ReturnType != typeof(void) || method.DeclaringType == null
                || method.DeclaringType.Assembly != typeof(RimKataMultiplayerRwmtCompat).Assembly)
                throw new ArgumentException("A synchronized RimKata command must be a non-generic static void method.");
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (type == typeof(Pawn) || type == typeof(Thing) || type == typeof(IntVec3)
                    || type == typeof(string) || (type.IsPrimitive && type != typeof(char)
                        && type != typeof(IntPtr) && type != typeof(UIntPtr)))
                    continue;
                throw new ArgumentException("Unsupported synchronized command argument in " + CommandKey(method)
                    + ": " + type.FullName + ". Pass a Pawn, Thing, IntVec3, string or primitive value.");
            }
        }

        private static string CommandKey(MethodInfo method)
        {
            return method.DeclaringType.FullName + "." + method;
        }

        private static bool Read(Func<bool> getter, bool failureValue)
        {
            if (getter == null) return false;
            try { return getter(); }
            catch (Exception exception)
            {
                Fail(exception);
                return failureValue;
            }
        }

        private static void Fail(Exception exception)
        {
            Prepared = false;
            Ready = false;
            if (Failure != null) return;
            Exception cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : exception;
            Failure = cause.Message;
            Log.Warning("[RimKata] rwmt multiplayer synchronization is unavailable ("
                + RegisteredCommandCount + " commands registered).\n" + cause);
        }
    }
}
