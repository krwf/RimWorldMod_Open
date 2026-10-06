using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataStartupPatches
    {
        private static readonly FieldInfo Methods;
        private static readonly FieldInfo Auxiliary;
        private static readonly FieldInfo Info;
        private static readonly FieldInfo Kind;
        private static readonly MethodInfo Bulk;
        private static readonly MethodInfo Original;
        private static readonly MethodInfo ReadInfo;
        private static readonly MethodInfo Wrapper;
        private static readonly MethodInfo Publish;
        private static readonly MethodInfo Clone;
        private static readonly object Locker;
        private static readonly bool Available;
        private static readonly HashSet<string> AppliedClasses = new HashSet<string>(StringComparer.Ordinal);

        static RimKataStartupPatches()
        {
            try
            {
                Assembly assembly = typeof(Harmony).Assembly;
                Type attributes = assembly.GetType("HarmonyLib.AttributePatch");
                Type shared = assembly.GetType("HarmonyLib.HarmonySharedState");
                Methods = AccessTools.Field(typeof(PatchClassProcessor), "patchMethods");
                Auxiliary = AccessTools.Field(typeof(PatchClassProcessor), "auxilaryMethods");
                Info = AccessTools.Field(attributes, "info");
                Kind = AccessTools.Field(attributes, "type");
                Bulk = AccessTools.Method(typeof(PatchClassProcessor), "GetBulkMethods");
                Original = AccessTools.Method(assembly.GetType("HarmonyLib.PatchTools"), "GetOriginalMethod",
                    new[] { typeof(HarmonyMethod) });
                ReadInfo = AccessTools.Method(shared, "GetPatchInfo", new[] { typeof(MethodBase) });
                Wrapper = AccessTools.Method(assembly.GetType("HarmonyLib.PatchFunctions"), "UpdateWrapper");
                Publish = AccessTools.Method(shared, "UpdatePatchInfo");
                Clone = AccessTools.Method(typeof(object), "MemberwiseClone");
                Locker = AccessTools.Field(typeof(PatchProcessor), "locker")?.GetValue(null);
                Available = Methods != null && Auxiliary != null && Info != null && Kind != null && Bulk != null
                    && Original != null && ReadInfo != null && Wrapper != null && Publish != null && Locker != null;
            }
            catch { Available = false; }
        }

        private sealed class ClassPlan
        {
            internal Type type;
            internal PatchClassProcessor processor;
            internal MethodInfo prepare;
            internal MethodInfo cleanup;
            internal Harmony harmony;
        }

        private sealed class Entry
        {
            internal ClassPlan owner;
            internal HarmonyPatchType kind;
            internal HarmonyMethod method;
        }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            var groups = new Dictionary<MethodBase, List<Entry>>();
            var order = new List<MethodBase>();
            var classes = new List<ClassPlan>();
            foreach (Type type in assembly.GetTypes())
            {
                if (!type.IsDefined(typeof(HarmonyPatch), true)) continue;
                if (AppliedClasses.Contains(harmony.Id + ":" + type.AssemblyQualifiedName))
                {
                    RimKataStartupDiagnostics.Count("patch_classes_skipped");
                    continue;
                }
                var plan = new ClassPlan { type = type, harmony = harmony };
                try
                {
                    bool batch;
                    var collected = new List<KeyValuePair<MethodBase, Entry>>();
                    using (RimKataStartupDiagnostics.Measure("discovery", "core_patch_targets"))
                    {
                        plan.processor = harmony.CreateClassProcessor(type);
                        batch = Available && Collect(plan, collected);
                    }
                    if (!batch)
                    {
                        using (RimKataStartupDiagnostics.Measure("patch_install", "class_fallback"))
                        using (var timing = RimKataStartupDiagnostics.MeasurePatch(null, "class_fallback", type))
                        {
                            try
                            {
                                timing?.NextPhase("harmony_fallback");
                                var result = plan.processor.Patch();
                                timing?.Succeeded(result != null && result.Count != 0);
                            }
                            catch (Exception exception) { timing?.Failed(exception); throw; }
                        }
                        RimKataStartupDiagnostics.Count("patch_classes_fallback");
                        AppliedClasses.Add(harmony.Id + ":" + type.AssemblyQualifiedName);
                        continue;
                    }
                    classes.Add(plan);
                    foreach (var pair in collected)
                    {
                        if (!groups.TryGetValue(pair.Key, out List<Entry> entries))
                        {
                            entries = new List<Entry>();
                            groups.Add(pair.Key, entries);
                            order.Add(pair.Key);
                        }
                        entries.Add(pair.Value);
                    }
                }
                catch (Exception exception)
                {
                    Report(plan, null, exception);
                }
            }
            foreach (MethodBase original in order)
            {
                var selected = new List<Entry>();
                var prepared = new Dictionary<ClassPlan, bool>();
                foreach (Entry entry in groups[original])
                {
                    if (!prepared.TryGetValue(entry.owner, out bool allowed))
                    {
                        try { allowed = Prepare(entry.owner, original); }
                        catch (Exception exception) { Report(entry.owner, original, exception); allowed = false; }
                        prepared.Add(entry.owner, allowed);
                    }
                    if (allowed) selected.Add(entry);
                }
                if (selected.Count == 0)
                {
                    foreach (var pair in prepared) Report(pair.Key, original, null);
                    continue;
                }
                try
                {
                    using (RimKataStartupDiagnostics.Measure("patch_install", "core_grouped")) Install(original, selected, "core_grouped");
                    foreach (var pair in prepared) Report(pair.Key, original, null);
                }
                catch
                {
                    // A rejected external target must not discard the other original methods.
                    RimKataStartupDiagnostics.Count("patch_group_retries");
                    foreach (var pair in prepared)
                    {
                        if (!pair.Value || !Prepare(pair.Key, original))
                        {
                            Report(pair.Key, original, null);
                            continue;
                        }
                        var isolated = selected.FindAll(entry => entry.owner == pair.Key);
                        try
                        {
                            using (RimKataStartupDiagnostics.Measure("patch_install", "group_retry")) Install(original, isolated, "group_retry");
                            Report(pair.Key, original, null);
                        }
                        catch (Exception exception) { Report(pair.Key, original, exception); }
                    }
                }
            }
            foreach (ClassPlan plan in classes)
            {
                Report(plan, null, null);
                AppliedClasses.Add(harmony.Id + ":" + plan.type.AssemblyQualifiedName);
            }
        }

        private static bool Collect(ClassPlan plan, List<KeyValuePair<MethodBase, Entry>> collected)
        {
            var auxiliary = (Dictionary<Type, MethodInfo>)Auxiliary.GetValue(plan.processor);
            auxiliary.TryGetValue(typeof(HarmonyPrepare), out plan.prepare);
            auxiliary.TryGetValue(typeof(HarmonyCleanup), out plan.cleanup);
            var patches = (IEnumerable)Methods.GetValue(plan.processor);
            var entries = new List<Entry>();
            foreach (object patch in patches)
            {
                object kindValue = Kind.GetValue(patch);
                if (!(kindValue is HarmonyPatchType kind) || (kind != HarmonyPatchType.Prefix
                    && kind != HarmonyPatchType.Postfix && kind != HarmonyPatchType.Transpiler
                    && kind != HarmonyPatchType.Finalizer)) return false;
                entries.Add(new Entry { owner = plan, kind = kind, method = (HarmonyMethod)Info.GetValue(patch) });
            }
            if (!Prepare(plan, null)) return true;
            var originals = (List<MethodBase>)Bulk.Invoke(plan.processor, null);
            foreach (Entry entry in entries)
            {
                if (originals.Count == 0)
                {
                    var original = (MethodBase)Original.Invoke(null, new object[] { entry.method });
                    if (original == null) throw new InvalidOperationException("Missing original for " + entry.method.method);
                    collected.Add(new KeyValuePair<MethodBase, Entry>(original, entry));
                }
                else
                {
                    foreach (MethodBase original in originals)
                        collected.Add(new KeyValuePair<MethodBase, Entry>(original, entry));
                }
            }
            RimKataStartupDiagnostics.Count("patch_classes_collected");
            return true;
        }

        private static bool Prepare(ClassPlan plan, MethodBase original)
            => plan.prepare == null || !(InvokeAuxiliary(plan.prepare, plan, original, null) is bool result) || result;

        private static object InvokeAuxiliary(MethodInfo method, ClassPlan plan, MethodBase original, Exception exception)
            => method.Invoke(null, AccessTools.ActualParameters(method, new object[] { plan.harmony, original, exception }));

        private static void Report(ClassPlan plan, MethodBase original, Exception exception)
        {
            exception = exception is TargetInvocationException wrapped ? wrapped.InnerException : exception;
            try
            {
                if (plan.cleanup != null)
                {
                    object result = InvokeAuxiliary(plan.cleanup, plan, original, exception);
                    if (plan.cleanup.ReturnType == typeof(Exception)) exception = result as Exception;
                }
            }
            catch (Exception cleanup) { exception = cleanup.GetBaseException(); }
            if (exception == null) return;
            RimKataStartupDiagnostics.Count("patch_failures");
            Log.Error("[RimKata] Could not register " + plan.type.FullName + " on " + original + ": " + exception);
        }

        private static MethodInfo Install(MethodBase original, List<Entry> entries, string route)
        {
            using (var timing = RimKataStartupDiagnostics.MeasurePatch(original, route))
            {
                try
                {
                    lock (Locker)
                    {
                        timing?.NextPhase("merge");
                        var existing = (PatchInfo)ReadInfo.Invoke(null, new object[] { original });
                        timing?.Existing(existing);
                        var combined = existing == null ? new PatchInfo() : (PatchInfo)Clone.Invoke(existing, null);
                        int added = 0;
                        var owners = new HashSet<ClassPlan>();
                        foreach (Entry entry in entries)
                        {
                            Patch[] patches = PatchesFor(combined, entry.kind);
                            bool duplicate = false;
                            foreach (Patch patch in patches)
                                if (patch.owner == entry.owner.harmony.Id && patch.PatchMethod == entry.method.method)
                                { duplicate = true; break; }
                            timing?.AddHook(entry.kind, entry.owner.harmony.Id, entry.method.method, duplicate);
                            if (duplicate)
                            {
                                RimKataStartupDiagnostics.Count("patch_hooks_skipped");
                                continue;
                            }
                            var replacement = new Patch[patches.Length + 1];
                            Array.Copy(patches, replacement, patches.Length);
                            replacement[patches.Length] = new Patch(entry.method, patches.Length, entry.owner.harmony.Id);
                            SetPatches(combined, entry.kind, replacement);
                            owners.Add(entry.owner);
                            added++;
                        }
                        if (added == 0)
                        {
                            timing?.Succeeded(false);
                            return null;
                        }
                        timing?.NextPhase("build");
                        var result = (MethodInfo)Wrapper.Invoke(null, new object[] { original, combined });
                        timing?.NextPhase("publish");
                        Publish.Invoke(null, new object[] { original, result, combined });
                        timing?.Succeeded();
                        RimKataStartupDiagnostics.Count("patch_wrappers_built");
                        RimKataStartupDiagnostics.Count("patch_hooks_installed", added);
                        RimKataStartupDiagnostics.Count("patch_rebuilds_avoided", Math.Max(0, owners.Count - 1));
                        return result;
                    }
                }
                catch (Exception exception) { timing?.Failed(exception); throw; }
            }
        }

        private static Patch[] PatchesFor(PatchInfo info, HarmonyPatchType kind)
            => kind == HarmonyPatchType.Prefix ? info.prefixes : kind == HarmonyPatchType.Postfix ? info.postfixes
                : kind == HarmonyPatchType.Transpiler ? info.transpilers : info.finalizers;

        private static void SetPatches(PatchInfo info, HarmonyPatchType kind, Patch[] patches)
        {
            if (kind == HarmonyPatchType.Prefix) info.prefixes = patches;
            else if (kind == HarmonyPatchType.Postfix) info.postfixes = patches;
            else if (kind == HarmonyPatchType.Transpiler) info.transpilers = patches;
            else info.finalizers = patches;
        }

        internal static MethodInfo Patch(Harmony harmony, MethodBase original, HarmonyMethod prefix = null,
            HarmonyMethod postfix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            using (RimKataStartupDiagnostics.Measure("patch_install", "manual"))
            {
                var plan = new ClassPlan { harmony = harmony };
                var entries = new List<Entry>();
                if (prefix != null) entries.Add(new Entry { owner = plan, kind = HarmonyPatchType.Prefix, method = prefix });
                if (postfix != null) entries.Add(new Entry { owner = plan, kind = HarmonyPatchType.Postfix, method = postfix });
                if (transpiler != null) entries.Add(new Entry { owner = plan, kind = HarmonyPatchType.Transpiler, method = transpiler });
                if (finalizer != null) entries.Add(new Entry { owner = plan, kind = HarmonyPatchType.Finalizer, method = finalizer });
                if (!Available)
                {
                    using (var timing = RimKataStartupDiagnostics.MeasurePatch(original, "manual_fallback"))
                    {
                        foreach (Entry entry in entries)
                            timing?.AddHook(entry.kind, harmony.Id, entry.method.method, false);
                        try
                        {
                            timing?.NextPhase("harmony_fallback");
                            var result = harmony.Patch(original, prefix, postfix, transpiler, finalizer);
                            timing?.Succeeded();
                            return result;
                        }
                        catch (Exception exception) { timing?.Failed(exception); throw; }
                    }
                }
                try { return Install(original, entries, "manual"); }
                catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
            }
        }
    }
}
