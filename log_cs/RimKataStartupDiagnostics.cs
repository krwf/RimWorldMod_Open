using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataStartupDiagnostics
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, long> Durations = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> Stages = new Dictionary<string, long>();
        private static readonly Dictionary<string, int> Counters = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> Details = new Dictionary<string, string>();
        private static readonly List<PatchTiming> PatchInstalls = new List<PatchTiming>();
        private static bool active;
        private static bool finished;
        [ThreadStatic] private static Timing current;

        internal static void Begin()
        {
            if (finished) return;
            active = true;
            Detail("assembly", typeof(RimKataStartupDiagnostics).Assembly.Location);
            Detail("module", typeof(RimKataStartupDiagnostics).Module.ModuleVersionId.ToString("D"));
        }

        internal static void Detail(string name, string value)
        {
            if (!active) return;
            lock (Sync) Details[name] = value;
        }

        internal static IDisposable Measure(string category, string stage)
            => active ? (IDisposable)new Timing(category, stage) : Empty.Instance;

        internal static PatchTiming MeasurePatch(MethodBase original, string route, Type fallbackClass = null)
            => active ? new PatchTiming(original, route, fallbackClass) : null;

        internal static void Count(string name, int amount = 1)
        {
            if (!active) return;
            lock (Sync)
            {
                Counters.TryGetValue(name, out int value);
                Counters[name] = value + amount;
            }
        }

        internal static void Complete()
        {
            if (finished) return;
            active = false;
            finished = true;
            var text = new StringBuilder();
            lock (Sync)
            {
                long total = 0;
                foreach (long value in Durations.Values) total += value;
                text.Append("[RimKata] Total measured startup time: ").Append(Milliseconds(total)).Append(" ms");
                text.Append("\n[RimKata] Startup timing (measured RimKata work only; exclusive milliseconds)");
                foreach (var detail in Details) text.Append("\n  ").Append(detail.Key).Append('=').Append(detail.Value);
                text.Append("\n  total=").Append(Milliseconds(total)).Append(" ms");
                foreach (string category in new[] { "cache_read", "discovery", "patch_install", "preparation", "cache_write" })
                {
                    Durations.TryGetValue(category, out long ticks);
                    text.Append("\n  ").Append(category).Append('=').Append(Milliseconds(ticks)).Append(" ms");
                }
                foreach (string name in new[] { "type_cache_hit", "type_cache_miss", "render_cache_hit", "render_cache_miss",
                    "definition_cache_hit", "definition_cache_miss", "cache_write_skipped", "cache_file_written",
                    "patch_hooks_skipped", "patch_rebuilds_avoided", "patch_group_retries", "patch_failures",
                    "patch_classes_collected", "patch_classes_fallback", "patch_wrappers_built",
                    "compatibility_entered", "compatibility_skipped", "cache_records_loaded", "cache_records_changed" })
                    if (!Counters.ContainsKey(name)) Counters.Add(name, 0);
                var names = new List<string>(Counters.Keys);
                names.Sort(StringComparer.Ordinal);
                foreach (string name in names) text.Append("\n  ").Append(name).Append('=').Append(Counters[name]);
                var stages = new List<KeyValuePair<string, long>>(Stages);
                stages.Sort((left, right) => right.Value.CompareTo(left.Value));
                text.Append("\n  stages:");
                foreach (var stage in stages)
                    text.Append("\n    ").Append(stage.Key).Append('=').Append(Milliseconds(stage.Value)).Append(" ms");
                AppendPatchInstalls(text);
                PatchInstalls.Clear();
            }
            Log.Message(text.ToString());
        }

        private static string Milliseconds(long ticks)
            => (ticks * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);

        private static string MethodName(MethodBase method)
        {
            if (method == null) return "<unresolved>";
            var name = new StringBuilder("[").Append(method.Module.Assembly.GetName().Name).Append("] ")
                .Append(method.DeclaringType?.FullName).Append("::").Append(method.Name);
            if (method.IsGenericMethod)
            {
                name.Append('<');
                Type[] arguments = method.GetGenericArguments();
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (i != 0) name.Append(", ");
                    name.Append(arguments[i].FullName ?? arguments[i].ToString());
                }
                name.Append('>');
            }
            name.Append('(');
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i != 0) name.Append(", ");
                Type type = parameters[i].ParameterType;
                name.Append(type.FullName ?? type.ToString());
            }
            return name.Append(')').ToString();
        }

        private static void AppendPatchInstalls(StringBuilder text)
        {
            var attempts = new Dictionary<object, int>();
            var installed = new Dictionary<object, int>();
            foreach (PatchTiming patch in PatchInstalls)
            {
                object target = patch.original ?? (object)patch.fallbackClass;
                attempts.TryGetValue(target, out int count);
                attempts[target] = count + 1;
                if (patch.status == "installed")
                {
                    installed.TryGetValue(target, out count);
                    installed[target] = count + 1;
                }
            }
            var ordered = new List<PatchTiming>(PatchInstalls);
            ordered.Sort((left, right) => right.elapsed.CompareTo(left.elapsed));
            text.Append("\n  harmony_installs (slowest first; included in patch_install, not additional time):");
            text.Append("\n    build is the combined original method and all registered patches, not one patch's cost.");
            foreach (PatchTiming patch in ordered)
            {
                object target = patch.original ?? (object)patch.fallbackClass;
                installed.TryGetValue(target, out int count);
                text.Append("\n    ").Append(Milliseconds(patch.elapsed)).Append(" ms; route=").Append(patch.route)
                    .Append("; status=").Append(patch.status).Append("; completion_order=").Append(patch.order)
                    .Append("; target_attempts=").Append(attempts[target]).Append("; target_installs=").Append(count);
                text.Append("\n      target=").Append(patch.original != null ? MethodName(patch.original)
                    : "<class fallback; originals not individually measured> " + patch.fallbackClass.FullName);
                text.Append("\n      phases:");
                foreach (var phase in patch.phases)
                    text.Append(' ').Append(phase.Key).Append('=').Append(Milliseconds(phase.Value)).Append(" ms;");
                if (patch.existingHooks >= 0)
                    text.Append("\n      existing_hooks=").Append(patch.existingHooks)
                        .Append("; existing_owners=").Append(string.Join(",", patch.existingOwners));
                foreach (PatchTiming.Hook hook in patch.hooks)
                    text.Append("\n      ").Append(hook.skipped ? "duplicate_skipped" : "requested")
                        .Append(' ').Append(hook.kind).Append(" owner=").Append(hook.owner)
                        .Append(' ').Append(MethodName(hook.method));
                if (patch.error != null)
                    text.Append("\n      error=").Append(patch.error.GetType().FullName).Append(": ")
                        .Append(patch.error.Message.Replace('\r', ' ').Replace('\n', ' '));
            }
        }

        internal sealed class PatchTiming : IDisposable
        {
            internal readonly MethodBase original;
            internal readonly Type fallbackClass;
            internal readonly string route;
            internal readonly Dictionary<string, long> phases = new Dictionary<string, long>();
            internal readonly List<Hook> hooks = new List<Hook>();
            internal readonly List<string> existingOwners = new List<string>();
            internal int existingHooks = -1;
            internal string status = "failed";
            internal Exception error;
            internal long elapsed;
            internal int order;
            private readonly long started = Stopwatch.GetTimestamp();
            private long phaseStarted;
            private string phase = "wait";
            private bool disposed;

            internal struct Hook
            {
                internal string kind, owner;
                internal MethodInfo method;
                internal bool skipped;
            }

            internal PatchTiming(MethodBase original, string route, Type fallbackClass)
            {
                this.original = original;
                this.route = route;
                this.fallbackClass = fallbackClass;
                phaseStarted = started;
            }

            internal void NextPhase(string next)
            {
                long now = Stopwatch.GetTimestamp();
                FinishPhase(now);
                phaseStarted = now;
                phase = next;
            }

            private void FinishPhase(long now)
            {
                phases.TryGetValue(phase, out long value);
                phases[phase] = value + now - phaseStarted;
            }

            internal void Existing(PatchInfo info)
            {
                existingHooks = 0;
                if (info == null) return;
                foreach (Patch[] patches in new[] { info.prefixes, info.postfixes, info.transpilers, info.finalizers })
                    foreach (Patch patch in patches)
                    {
                        existingHooks++;
                        if (!existingOwners.Contains(patch.owner)) existingOwners.Add(patch.owner);
                    }
            }

            internal void AddHook(HarmonyPatchType kind, string owner, MethodInfo method, bool skipped)
                => hooks.Add(new Hook { kind = kind.ToString(), owner = owner, method = method, skipped = skipped });

            internal void Succeeded(bool changed = true) => status = changed ? "installed" : "skipped";
            internal void Failed(Exception exception)
            {
                status = "failed";
                error = exception.GetBaseException();
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                long now = Stopwatch.GetTimestamp();
                elapsed = now - started;
                FinishPhase(now);
                if (!active) return;
                lock (Sync)
                {
                    order = PatchInstalls.Count + 1;
                    PatchInstalls.Add(this);
                }
            }
        }

        private sealed class Timing : IDisposable
        {
            private readonly string category;
            private readonly string stage;
            private readonly Timing parent;
            private readonly long started;
            private long children;
            private bool disposed;

            internal Timing(string category, string stage)
            {
                this.category = category;
                this.stage = category + "/" + stage;
                parent = current;
                current = this;
                started = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                long elapsed = Stopwatch.GetTimestamp() - started;
                current = parent;
                if (parent != null) parent.children += elapsed;
                if (!active) return;
                long own = Math.Max(0, elapsed - children);
                lock (Sync)
                {
                    Durations.TryGetValue(category, out long value);
                    Durations[category] = value + own;
                    Stages.TryGetValue(stage, out value);
                    Stages[stage] = value + own;
                }
            }
        }

        private sealed class Empty : IDisposable
        {
            internal static readonly Empty Instance = new Empty();
            public void Dispose() { }
        }
    }
}
