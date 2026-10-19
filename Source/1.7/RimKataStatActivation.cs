using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Prepared rules and registered candidates only. No GetStatValue/render interception.
    internal static class RimKataStatActivation
    {
        [Flags] private enum Changes { None = 0, Health = 1, Record = 2, Periodic = 4, All = 7 }
        private sealed class Prepared
        {
            internal RimKataActivationStatItem item;
            internal RimKataStatActivationRule rule;
            internal Changes changes;
        }
        private sealed class Watch
        {
            internal Pawn pawn;
            internal Map map;
            internal bool[] matches;
            internal bool qualified, reading;
            internal int scheduled = -1, nextPeriodic, nextEvent;
            internal Changes pending;
        }
        private struct Due
        {
            internal Watch watch;
            internal int tick;
        }
        private sealed class MapSchedule
        {
            internal readonly List<Due>[] buckets = new List<Due>[256];
            internal readonly HashSet<Watch> members = new HashSet<Watch>();
        }

        private const int Period = 250, EventInterval = 60, Budget = 8;
        private static readonly List<Prepared> rules = new List<Prepared>();
        private static readonly HashSet<RecordDef> records = new HashSet<RecordDef>();
        private static readonly Dictionary<Pawn, Watch> watches = new Dictionary<Pawn, Watch>();
        private static readonly Dictionary<Map, MapSchedule> maps = new Dictionary<Map, MapSchedule>();
        private static bool health, periodic;
        [ThreadStatic] private static Pawn evaluating;
        internal static bool Enabled => rules.Count != 0;
        internal static bool HealthEnabled => health || periodic;
        internal static bool RecordsEnabled => records.Count != 0;
        private static int Now => Find.TickManager?.TicksGame ?? 0;

        internal static bool Rebuild(RimKataSettings settings)
        {
            settings?.SanitizeStatActivationRules();
            var next = new List<Prepared>();
            if (settings?.statActivationRules != null)
                foreach (RimKataStatActivationRule rule in settings.statActivationRules)
                {
                    if (!rule.enabled) continue;
                    RimKataActivationStatItem item = RimKataActivationStatCatalog.Find(rule.key);
                    if (item == null) continue;
                    next.Add(new Prepared
                    {
                        item = item,
                        rule = new RimKataStatActivationRule
                        { key = rule.key, enabled = true, comparison = rule.comparison, value = rule.value },
                        changes = item.Record != null ? Changes.Record
                            : item.NeedsPeriodic ? Changes.Periodic : Changes.Health
                    });
                }
            bool changed = next.Count != rules.Count;
            for (int i = 0; !changed && i < next.Count; i++)
                changed = next[i].rule.key != rules[i].rule.key
                    || next[i].rule.comparison != rules[i].rule.comparison
                    || next[i].rule.value != rules[i].rule.value;
            if (!changed) return false;
            ResetGame();
            rules.Clear();
            rules.AddRange(next);
            records.Clear();
            health = periodic = false;
            foreach (Prepared prepared in rules)
            {
                if (prepared.item.Record != null) records.Add(prepared.item.Record);
                health |= prepared.item.IsHealth;
                periodic |= prepared.item.NeedsPeriodic;
            }
            return true;
        }

        internal static bool ReadQualified(Pawn pawn, bool monitor = true)
        {
            if (!Enabled || pawn == null || pawn.Dead) return false;
            if (monitor && (!RimKataEligibility.FactionEffectsEnabled(pawn)
                || RimKataTargetAccess.IsEnabled(pawn))) return false;
            if (watches.TryGetValue(pawn, out Watch existing)) return existing.qualified;
            // Some information stats ask for RimKata access from ShouldShowFor/GetValue.
            // A world-pawn/card snapshot has no registered watch to break that recursion.
            if (evaluating == pawn) return false;
            bool track = monitor && pawn.Spawned;
            var watch = new Watch { pawn = pawn, map = pawn.Map, matches = new bool[rules.Count] };
            if (track)
            {
                watches.Add(pawn, watch);
                if (!maps.TryGetValue(watch.map, out MapSchedule schedule))
                    maps.Add(watch.map, schedule = new MapSchedule());
                schedule.members.Add(watch);
            }
            Evaluate(watch, Changes.All);
            if (track && periodic)
            {
                watch.nextPeriodic = Now + 1 + (pawn.thingIDNumber & int.MaxValue) % Period;
                Schedule(watch, watch.nextPeriodic);
            }
            return watch.qualified;
        }

        private static void Evaluate(Watch watch, Changes changes)
        {
            watch.reading = true;
            Pawn previous = evaluating;
            evaluating = watch.pawn;
            try
            {
                bool any = false;
                for (int i = 0; i < rules.Count; i++)
                {
                    Prepared prepared = rules[i];
                    if ((prepared.changes & changes) != 0)
                        watch.matches[i] = prepared.item.TryRead(watch.pawn, out float value)
                            && prepared.item.Compare(value, prepared.rule);
                    any |= watch.matches[i];
                }
                watch.qualified = any;
            }
            finally { evaluating = previous; watch.reading = false; }
        }

        internal static void NotifyHealth(Pawn pawn)
        {
            if (HealthEnabled) Mark(pawn, Changes.Health | Changes.Periodic);
        }

        internal static void NotifyRecord(Pawn pawn, RecordDef def)
        {
            if (def != null && records.Contains(def)) Mark(pawn, Changes.Record);
        }

        private static void Mark(Pawn pawn, Changes changes)
        {
            if (pawn == null || !watches.TryGetValue(pawn, out Watch watch) || watch.reading) return;
            watch.pending |= changes;
            Schedule(watch, Math.Max(Now + 1, watch.nextEvent));
        }

        private static void Schedule(Watch watch, int tick)
        {
            if (watch.scheduled >= 0 && watch.scheduled <= tick) return;
            if (!maps.TryGetValue(watch.map, out MapSchedule schedule)) return;
            watch.scheduled = tick;
            (schedule.buckets[tick & 255] ??= new List<Due>()).Add(new Due { watch = watch, tick = tick });
        }

        // Existing map tick consumes only this tick's due bucket, including event-only records.
        internal static void Tick(Map map)
        {
            if (!maps.TryGetValue(map, out MapSchedule schedule)) return;
            int now = Now;
            List<Due> due = schedule.buckets[now & 255];
            if (due == null || due.Count == 0) return;
            int remaining = Budget;
            for (int i = 0; i < due.Count; i++)
            {
                Due entry = due[i];
                Watch watch = entry.watch;
                if (entry.tick != now || watch.scheduled != now) continue;
                watch.scheduled = -1;
                if (watch.pawn.Dead || !watch.pawn.Spawned || watch.pawn.Map != map)
                { Forget(watch.pawn); continue; }
                if (remaining-- <= 0) { Schedule(watch, now + 1); continue; }
                Changes changes = watch.pending;
                watch.pending = Changes.None;
                if (periodic && now >= watch.nextPeriodic)
                {
                    changes |= Changes.Periodic;
                    watch.nextPeriodic = now + Period;
                }
                bool previous = watch.qualified;
                Evaluate(watch, changes);
                watch.nextEvent = now + EventInterval;
                if (previous != watch.qualified)
                    RimKataEligibilityCache.RefreshStatSource(watch.pawn);
                if (watches.TryGetValue(watch.pawn, out Watch current) && current == watch && periodic)
                    Schedule(watch, watch.nextPeriodic);
            }
            due.Clear();
        }

        internal static void Forget(Pawn pawn)
        {
            if (pawn == null || !watches.TryGetValue(pawn, out Watch watch)) return;
            watches.Remove(pawn);
            watch.scheduled = -1;
            if (maps.TryGetValue(watch.map, out MapSchedule schedule))
            {
                schedule.members.Remove(watch);
                if (schedule.members.Count == 0) maps.Remove(watch.map);
            }
        }

        internal static void ForgetMap(Map map)
        {
            if (map == null || !maps.TryGetValue(map, out MapSchedule schedule)) return;
            foreach (Watch watch in schedule.members) watches.Remove(watch.pawn);
            maps.Remove(map);
        }

        internal static void ResetGame()
        {
            watches.Clear();
            maps.Clear();
        }
    }

    [HarmonyPatch(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.Notify_CapacityLevelsDirty))]
    internal static class Patch_PawnCapacities_RimKataActivation
    {
        private static void Postfix(Pawn ___pawn)
        {
            if (RimKataStatActivation.HealthEnabled) RimKataStatActivation.NotifyHealth(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_RecordsTracker), nameof(Pawn_RecordsTracker.Increment))]
    internal static class Patch_PawnRecordsIncrement_RimKataActivation
    {
        private static void Postfix(Pawn ___pawn, RecordDef def)
        {
            if (RimKataStatActivation.RecordsEnabled) RimKataStatActivation.NotifyRecord(___pawn, def);
        }
    }

    [HarmonyPatch(typeof(Pawn_RecordsTracker), nameof(Pawn_RecordsTracker.AddTo))]
    internal static class Patch_PawnRecordsAdd_RimKataActivation
    {
        private static void Postfix(Pawn ___pawn, RecordDef def)
        {
            if (RimKataStatActivation.RecordsEnabled) RimKataStatActivation.NotifyRecord(___pawn, def);
        }
    }
}
