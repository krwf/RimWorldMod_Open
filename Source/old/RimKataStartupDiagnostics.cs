using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
                foreach (string name in new[] { "type_cache_hit", "type_cache_miss", "type_cache_bypassed", "render_cache_hit", "render_cache_miss",
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
            }
            Log.Message(text.ToString());
        }

        private static string Milliseconds(long ticks)
            => (ticks * 1000d / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);

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
