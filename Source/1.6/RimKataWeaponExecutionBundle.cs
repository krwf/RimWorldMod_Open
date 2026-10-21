using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // A session-local executable snapshot. It copies code, never the weapon/Verb/Comp state.
    // Creation is restricted to startup or explicit registration; invokers contain no reflection.
    internal sealed class RimKataWeaponExecutionBundle
    {
        private const int MaximumMethods = RimKataWeaponExecutionDiscovery.MaximumStoredMethods;
        private static readonly object BuildLock = new object();
        private static readonly MethodInfo Rewriter = AccessTools.Method(
            typeof(RimKataWeaponExecutionBundle), nameof(RewriteCalls));
        [ThreadStatic] private static Dictionary<MethodInfo, Copy> building;
        [ThreadStatic] private static MethodInfo bodyOnlyEntry;
        [ThreadStatic] private static int linkedCalls;
        [ThreadStatic] private static int retainedCalls;

        private readonly Dictionary<MethodInfo, Action<Verb>> invokers;
        // Keep all generated methods, including mutually recursive forwarders, alive together.
        private readonly Dictionary<MethodInfo, Copy> copies;
        internal int CopiedMethodCount => copies.Count;
        internal int LinkedCallCount { get; }
        internal int RetainedCallCount { get; }

        private RimKataWeaponExecutionBundle(Dictionary<MethodInfo, Copy> copies,
            Dictionary<MethodInfo, Action<Verb>> invokers, int linked, int retained)
        {
            this.copies = copies;
            this.invokers = invokers;
            LinkedCallCount = linked;
            RetainedCallCount = retained;
        }

        internal bool TryGetInvoker(MethodInfo method, out Action<Verb> invoker)
        {
            invoker = null;
            return method != null && invokers.TryGetValue(method, out invoker);
        }

        internal static bool TryCreateSnapshot(MethodInfo original, MethodInfo transpiler,
            out MethodInfo snapshot, out string reason)
        {
            snapshot = null;
            reason = null;
            lock (BuildLock)
            {
                try
                {
                    if (original == null || !SnapshotApi.Available || !CanCopy(original))
                        throw new NotSupportedException("The method cannot be copied by the installed Harmony version.");
                    snapshot = SnapshotApi.Create(original, transpiler);
                    return true;
                }
                catch (Exception exception)
                {
                    while (exception is TargetInvocationException && exception.InnerException != null)
                        exception = exception.InnerException;
                    reason = exception.GetType().Name + ": " + exception.Message;
                    return false;
                }
            }
        }

        internal static bool TryCreate(IEnumerable<MethodInfo> methods,
            out RimKataWeaponExecutionBundle bundle, out string reason,
            MethodInfo wrappedEntry = null, MethodInfo dispatcher = null)
        {
            bundle = null;
            reason = null;
            lock (BuildLock)
            {
                Dictionary<MethodInfo, Copy> previous = building;
                MethodInfo previousBodyEntry = bodyOnlyEntry;
                int previousLinked = linkedCalls;
                int previousRetained = retainedCalls;
                try
                {
                    if (methods == null || !SnapshotApi.Available)
                        throw new NotSupportedException("The installed Harmony snapshot API is unavailable.");
                    var selected = new Dictionary<MethodInfo, Copy>();
                    foreach (MethodInfo method in methods)
                    {
                        if (method == null || selected.ContainsKey(method) || !CanCopy(method)) continue;
                        if (selected.Count >= MaximumMethods)
                            throw new NotSupportedException("The executable bundle exceeds the method limit.");
                        selected.Add(method, new Copy(method));
                    }
                    if (selected.Count == 0)
                        throw new NotSupportedException("No managed executable methods were found.");
                    if (wrappedEntry != null && !selected.ContainsKey(wrappedEntry))
                        throw new NotSupportedException("The wrapped entry has no managed executable body.");

                    building = selected;
                    bodyOnlyEntry = wrappedEntry;
                    linkedCalls = retainedCalls = 0;
                    foreach (Copy copy in selected.Values)
                        copy.replacement = SnapshotApi.Create(copy.original, bodyOnly: copy.original == wrappedEntry,
                            excludedTranspiler: dispatcher);
                    // Every target exists before any forwarder is completed, including cycles.
                    foreach (Copy copy in selected.Values) copy.Complete();
                    var entries = new Dictionary<MethodInfo, Action<Verb>>();
                    foreach (Copy copy in selected.Values)
                    {
                        MethodInfo method = copy.original;
                        if (method.IsStatic || method.ReturnType != typeof(void)
                            || method.GetParameters().Length != 0
                            || !typeof(Verb).IsAssignableFrom(method.DeclaringType)) continue;
                        var entry = new DynamicMethod("RimKataWeaponBundleEntry", typeof(void),
                            new[] { typeof(Verb) }, typeof(RimKataWeaponExecutionBundle), true);
                        ILGenerator il = entry.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, method.DeclaringType);
                        il.Emit(OpCodes.Call, copy.direct);
                        il.Emit(OpCodes.Ret);
                        entries.Add(method, (Action<Verb>)entry.CreateDelegate(typeof(Action<Verb>)));
                    }
                    if (entries.Count == 0)
                        throw new NotSupportedException("No supported Verb entry point was found.");
                    bundle = new RimKataWeaponExecutionBundle(selected, entries, linkedCalls, retainedCalls);
                    return true;
                }
                catch (Exception exception)
                {
                    while (exception is TargetInvocationException && exception.InnerException != null)
                        exception = exception.InnerException;
                    reason = exception.GetType().Name + ": " + exception.Message;
                    return false;
                }
                finally
                {
                    building = previous;
                    bodyOnlyEntry = previousBodyEntry;
                    linkedCalls = previousLinked;
                    retainedCalls = previousRetained;
                }
            }
        }

        private static bool CanCopy(MethodInfo method)
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType == null
                || method.DeclaringType.IsValueType
                || (method.GetMethodImplementationFlags() & MethodImplAttributes.Synchronized) != 0
                || (method.CallingConvention & CallingConventions.VarArgs) != 0) return false;
            // State machines have independent lifecycle hooks; preserving their original reference
            // is safer than duplicating Harmony's persistent-state ownership.
            if (typeof(IEnumerator).IsAssignableFrom(method.DeclaringType)) return false;
            try { return method.GetMethodBody() != null; }
            catch (InvalidOperationException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Calli) retainedCalls++;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo target)
                {
                    bool virtualCall = instruction.opcode == OpCodes.Callvirt;
                    // A virtual call can choose a different implementation for each live object.
                    // Do not replace that dispatch, delegate targets, or unknown external calls.
                    // A call back to the wrapped entry must also retain its original hooks.
                    if (target != bodyOnlyEntry && building != null && building.TryGetValue(target, out Copy copy)
                        && (!virtualCall || !target.IsVirtual || target.IsFinal || target.DeclaringType.IsSealed))
                    {
                        var replacement = new CodeInstruction(instruction);
                        replacement.opcode = OpCodes.Call;
                        replacement.operand = virtualCall ? copy.checkedCall : copy.direct;
                        linkedCalls++;
                        yield return replacement;
                        continue;
                    }
                    retainedCalls++;
                }
                yield return instruction;
            }
        }

        private sealed class Copy
        {
            internal readonly MethodInfo original;
            internal readonly DynamicMethod direct;
            internal readonly DynamicMethod checkedCall;
            internal MethodInfo replacement;
            private readonly Type[] signature;

            internal Copy(MethodInfo original)
            {
                this.original = original;
                ParameterInfo[] parameters = original.GetParameters();
                int offset = original.IsStatic ? 0 : 1;
                signature = new Type[parameters.Length + offset];
                if (offset != 0) signature[0] = original.DeclaringType;
                for (int i = 0; i < parameters.Length; i++) signature[i + offset] = parameters[i].ParameterType;
                direct = new DynamicMethod("RimKataBundle_" + original.Name, original.ReturnType,
                    signature, typeof(RimKataWeaponExecutionBundle), true);
                checkedCall = original.IsStatic ? direct : new DynamicMethod(
                    "RimKataBundleChecked_" + original.Name, original.ReturnType,
                    signature, typeof(RimKataWeaponExecutionBundle), true);
            }

            internal void Complete()
            {
                if (replacement == null || !replacement.IsStatic)
                    throw new InvalidOperationException("Harmony did not create a callable snapshot for " + original.Name);
                ParameterInfo[] actual = replacement.GetParameters();
                if (actual.Length != signature.Length || replacement.ReturnType != original.ReturnType)
                    throw new InvalidOperationException("The snapshot signature changed for " + original.Name);
                for (int i = 0; i < signature.Length; i++)
                    if (actual[i].ParameterType != signature[i])
                        throw new InvalidOperationException("The snapshot parameter changed for " + original.Name);
                Emit(direct.GetILGenerator(), false);
                if (checkedCall != direct) Emit(checkedCall.GetILGenerator(), true);
            }

            private void Emit(ILGenerator il, bool checkInstance)
            {
                if (checkInstance)
                {
                    Label present = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brtrue, present);
                    il.Emit(OpCodes.Newobj, typeof(NullReferenceException).GetConstructor(Type.EmptyTypes));
                    il.Emit(OpCodes.Throw);
                    il.MarkLabel(present);
                }
                for (int i = 0; i < signature.Length; i++) il.Emit(OpCodes.Ldarg, (short)i);
                il.Emit(OpCodes.Call, replacement);
                il.Emit(OpCodes.Ret);
            }
        }

        private static class SnapshotApi
        {
            private static readonly ConstructorInfo Config;
            private static readonly ConstructorInfo Creator;
            private static readonly MethodInfo CreateReplacement;
            private static readonly MethodInfo SortMethods;
            private static readonly MethodInfo SortCalls;
            private static readonly MethodInfo GetInfixes;
            private static readonly bool HasInnerFinalizers;
            private static readonly FieldInfo Persistence;
            internal static readonly bool Available;

            static SnapshotApi()
            {
                try
                {
                    Assembly assembly = typeof(Harmony).Assembly;
                    Type configType = assembly.GetType("HarmonyLib.MethodCreatorConfig");
                    Type creatorType = assembly.GetType("HarmonyLib.MethodCreator");
                    Type functions = assembly.GetType("HarmonyLib.PatchFunctions");
                    SortMethods = AccessTools.Method(functions, "GetSortedPatchMethods",
                        new[] { typeof(MethodBase), typeof(Patch[]), typeof(bool) });
                    SortCalls = AccessTools.Method(functions, "GetSortedPatchCalls",
                        new[] { typeof(MethodBase), typeof(Patch[]), typeof(bool), typeof(Patch[]) });
                    GetInfixes = AccessTools.Method(functions, "GetInfixes", new[] { typeof(Patch[]) });
                    CreateReplacement = AccessTools.Method(creatorType, "CreateReplacement", Type.EmptyTypes);
                    HasInnerFinalizers = AccessTools.Field(typeof(Patches), "InnerFinalizers") != null;
                    foreach (ConstructorInfo constructor in AccessTools.GetDeclaredConstructors(configType))
                    {
                        ParameterInfo[] p = constructor.GetParameters();
                        if (p.Length == (HasInnerFinalizers ? 10 : 9)
                            && p[0].ParameterType == typeof(MethodBase) && p[1].ParameterType == typeof(MethodBase))
                            Config = constructor;
                    }
                    Creator = AccessTools.Constructor(creatorType, SortCalls == null
                        ? new[] { configType } : new[] { configType, typeof(bool) });
                    Persistence = AccessTools.Field(configType, "persistence");
                    Available = Config != null && Creator != null && SortMethods != null
                        && GetInfixes != null && CreateReplacement != null;
                }
                catch { Available = false; }
            }

            internal static MethodInfo Create(MethodInfo original, MethodInfo transpiler = null,
                bool bodyOnly = false, MethodInfo excludedTranspiler = null)
            {
                Patches patches = Harmony.GetPatchInfo(original);
                if (bodyOnly && (ReadPatches(patches, "InnerPrefixes").Length != 0
                    || ReadPatches(patches, "InnerPostfixes").Length != 0
                    || ReadPatches(patches, "InnerFinalizers").Length != 0))
                    throw new NotSupportedException("The tick entry has inner Harmony hooks; its original path must remain intact.");
                object Sorted(string kind, bool calls)
                {
                    // The existing wrapper owns these hooks when it dispatches its body here.
                    Patch[] values = bodyOnly && calls ? Array.Empty<Patch>() : ReadPatches(patches, kind);
                    return calls && SortCalls != null
                        ? SortCalls.Invoke(null, new object[] { original, values, false, kind == "Postfixes" ? values : null })
                        : SortMethods.Invoke(null, new object[] { original, values, false });
                }
                var transpilers = (List<MethodInfo>)Sorted("Transpilers", false);
                if (excludedTranspiler != null) transpilers.RemoveAll(method => method == excludedTranspiler);
                transpilers.Add(transpiler ?? Rewriter);
                var args = new List<object>
                {
                    original, null, Sorted("Prefixes", true), Sorted("Postfixes", true), transpilers,
                    Sorted("Finalizers", true),
                    GetInfixes.Invoke(null, new object[] { ReadPatches(patches, "InnerPrefixes") }),
                    GetInfixes.Invoke(null, new object[] { ReadPatches(patches, "InnerPostfixes") })
                };
                if (HasInnerFinalizers)
                    args.Add(GetInfixes.Invoke(null, new object[] { ReadPatches(patches, "InnerFinalizers") }));
                args.Add(false);
                object config = Config.Invoke(args.ToArray());
                object creator = Creator.Invoke(SortCalls == null ? new[] { config } : new[] { config, (object)true });
                object result = CreateReplacement.Invoke(creator, null);
                // Persistent state needs original detour lifecycle hooks, which a private copy
                // must not install or take ownership of. Retain the whole original path instead.
                object persistence = Persistence?.GetValue(config);
                if (persistence != null && AccessTools.Field(persistence.GetType(), "storage")?.GetValue(persistence)
                    is IDictionary storage && storage.Count != 0)
                    throw new NotSupportedException("The snapshot requires Harmony persistent-state lifecycle hooks.");
                return result?.GetType().GetField("Item1")?.GetValue(result) as MethodInfo
                    ?? throw new InvalidOperationException("Harmony returned no executable snapshot.");
            }

            private static Patch[] ReadPatches(Patches patches, string name)
            {
                var values = new List<Patch>();
                if (patches != null && AccessTools.Field(typeof(Patches), name)?.GetValue(patches) is IEnumerable source)
                    foreach (object value in source) if (value is Patch patch) values.Add(patch);
                return values.ToArray();
            }
        }
    }
}
