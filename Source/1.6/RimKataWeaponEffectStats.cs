using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Only weapon-owned calls use these private copies. No pawn/health/stat method is detoured.
    internal static class RimKataWeaponEffectStats
    {
        private static readonly object PreparationLock = new object();
        private static readonly FieldInfo Hediffs = AccessTools.Field(typeof(HediffSet), nameof(HediffSet.hediffs));
        private static readonly MethodInfo StatRead = AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue),
            new[] { typeof(Thing), typeof(StatDef), typeof(bool), typeof(int) });
        private static readonly MethodInfo ScopedRead = AccessTools.Method(typeof(RimKataWeaponEffectStats), nameof(ReadScopedStat));
        private static readonly MethodInfo HediffRead = AccessTools.Method(typeof(RimKataWeaponEffectStats), nameof(ReadHediffs));
        private static readonly MethodInfo OffsetRead = AccessTools.Method(typeof(HediffStatsUtility), nameof(HediffStatsUtility.GetStatOffsetForSeverity));
        private static readonly MethodInfo FactorRead = AccessTools.Method(typeof(HediffStatsUtility), nameof(HediffStatsUtility.GetStatFactorForSeverity));
        private static readonly MethodInfo ScopedOffset = AccessTools.Method(typeof(RimKataWeaponEffectStats), nameof(ReadOffset));
        private static readonly MethodInfo ScopedFactor = AccessTools.Method(typeof(RimKataWeaponEffectStats), nameof(ReadFactor));
        private static readonly AccessTools.StructFieldRef<ShotReport, float> Accuracy =
            AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromShooterAndDist");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> Distance =
            AccessTools.StructFieldRefAccess<ShotReport, float>("distance");
        private static Func<StatWorker, StatRequest, bool, float> readValue;
        private static Func<VerbProperties, Verb, Pawn, int> readCooldown;
        private static bool initialized;
        private static readonly Dictionary<StatDef, bool> supported = new Dictionary<StatDef, bool>();
        [ThreadStatic] private static Dictionary<MethodInfo, MethodInfo> building;
        [ThreadStatic] private static MethodInfo requiredCall;
        [ThreadStatic] private static int requiredCallCount;
        [ThreadStatic] private static Frame current;
        [ThreadStatic] private static Stack<Frame> available;

        private sealed class Frame
        {
            internal Verb verb;
            internal Pawn pawn;
            internal RimKataWeaponEffectState record;
            internal bool hediffsReady;
            internal Frame previous;
            internal readonly List<Hediff> hediffs = new List<Hediff>();
        }

        internal sealed class Snapshot
        {
            internal readonly float[] values = new float[6];
            internal readonly bool[] valid = new bool[6];
            private Hediff[] effects = Array.Empty<Hediff>();
            private HediffStage[] stages = Array.Empty<HediffStage>();
            private float[] severities = Array.Empty<float>();
            private int count = -1;

            internal void Refresh(List<Hediff> source)
            {
                bool changed = count != source.Count;
                if (effects.Length < source.Count)
                {
                    effects = new Hediff[source.Count];
                    stages = new HediffStage[source.Count];
                    severities = new float[source.Count];
                }
                for (int i = 0; i < source.Count; i++)
                {
                    Hediff effect = source[i];
                    HediffStage stage = effect.CurStage;
                    float severity = effect.Severity;
                    changed |= effects[i] != effect || stages[i] != stage || !severities[i].Equals(severity);
                    effects[i] = effect;
                    stages[i] = stage;
                    severities[i] = severity;
                }
                for (int i = source.Count; i < count; i++) { effects[i] = null; stages[i] = null; }
                count = source.Count;
                if (changed) Array.Clear(valid, 0, valid.Length);
            }
        }

        private sealed class FrozenStage : HediffStage
        {
            internal readonly float[] offsets = new float[3];
            internal readonly float[] factors = new float[3];
        }

        private sealed class FrozenHediff : Hediff
        {
            internal HediffStage stage;
            internal float severity;
            public override HediffStage CurStage => stage;
            public override float Severity { get => severity; set => severity = value; }
        }

        // Only called when a participating secondary weapon receives its combat snapshot.
        internal static void Capture(RimKataWeaponEffectState record, HashSet<HediffDef> excluded)
        {
            record.baselineHediffs.Clear();
            foreach (Hediff source in record.pawn.health.hediffSet.hediffs)
            {
                if (source?.def == null || excluded.Contains(source.def)) continue;
                HediffStage sourceStage = source.CurStage;
                FrozenStage stage = null;
                float severity = source.Severity;
                if (sourceStage != null)
                {
                    stage = new FrozenStage();
                    for (int i = 0; i < 3; i++)
                    {
                        StatDef stat = StatAt(i);
                        stage.offsets[i] = HediffStatsUtility.GetStatOffsetForSeverity(stat, sourceStage, record.pawn, severity);
                        stage.factors[i] = HediffStatsUtility.GetStatFactorForSeverity(stat, sourceStage, record.pawn, severity);
                    }
                }
                record.baselineHediffs.Add(new FrozenHediff
                    { def = source.def, pawn = record.pawn, Part = source.Part, severity = severity, stage = stage });
            }
            record.statSnapshot = new Snapshot();
            record.statSnapshot.Refresh(record.effects);
            Frame frame = Begin(record);
            try
            {
                for (int i = 0; i < 3; i++)
                    if (supported.TryGetValue(StatAt(i), out bool supportedStat) && supportedStat)
                        ReadCached(frame, StatAt(i), true);
            }
            finally { End(frame); }
        }

        private static StatDef StatAt(int index) => index == 0 ? StatDefOf.AimingDelayFactor
            : index == 1 ? StatDefOf.RangedCooldownFactor : StatDefOf.ShootingAccuracyPawn;

        private static int StatIndex(StatDef stat) => stat == StatDefOf.AimingDelayFactor ? 0
            : stat == StatDefOf.RangedCooldownFactor ? 1 : stat == StatDefOf.ShootingAccuracyPawn ? 2 : -1;

        internal static void Initialize()
        {
            lock (PreparationLock)
            {
                if (initialized) return;
                initialized = true;
                try
                {
                    building = new Dictionary<MethodInfo, MethodInfo>();
                    MethodInfo unfinalized = AccessTools.Method(typeof(StatWorker), nameof(StatWorker.GetValueUnfinalized),
                        new[] { typeof(StatRequest), typeof(bool) });
                    Copy(unfinalized, nameof(RewriteHediffs));
                    Copy(AccessTools.Method(typeof(StatWorker), nameof(StatWorker.FinalizeValue),
                        new[] { typeof(StatRequest), typeof(float).MakeByRefType(), typeof(bool) }), nameof(RewriteCalls));
                    MethodInfo value = Copy(AccessTools.Method(typeof(StatWorker), nameof(StatWorker.GetValue),
                        new[] { typeof(StatRequest), typeof(bool) }), nameof(RewriteCalls), unfinalized);
                    MethodInfo cooldownStat = AccessTools.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedCooldown),
                        new[] { typeof(Tool), typeof(Pawn), typeof(Thing) });
                    Copy(cooldownStat, nameof(RewriteCalls), StatRead);
                    MethodInfo cooldownVerb = AccessTools.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedCooldown),
                        new[] { typeof(Verb), typeof(Pawn) });
                    Copy(cooldownVerb, nameof(RewriteCalls), cooldownStat);
                    MethodInfo cooldown = Copy(AccessTools.Method(typeof(VerbProperties), nameof(VerbProperties.AdjustedCooldownTicks),
                        new[] { typeof(Verb), typeof(Pawn) }), nameof(RewriteCalls), cooldownVerb);
                    readValue = (Func<StatWorker, StatRequest, bool, float>)value.CreateDelegate(
                        typeof(Func<StatWorker, StatRequest, bool, float>));
                    readCooldown = (Func<VerbProperties, Verb, Pawn, int>)cooldown.CreateDelegate(
                        typeof(Func<VerbProperties, Verb, Pawn, int>));
                }
                catch (Exception exception)
                {
                    readValue = null;
                    readCooldown = null;
                    Log.Warning("[RimKata] Weapon-local effect stats are unavailable; original weapon effects will be retained. "
                        + exception.GetType().Name + ": " + exception.Message);
                }
                finally { building = null; requiredCall = null; }
            }
        }

        internal static bool IsSupported(StatDef stat)
        {
            if (stat == null) return false;
            if (supported.TryGetValue(stat, out bool result)) return result;
            // Preparation only: derived workers may change labels without changing calculation.
            if (!initialized) Initialize();
            result = readValue != null && readCooldown != null
                && (stat == StatDefOf.AimingDelayFactor || stat == StatDefOf.RangedCooldownFactor
                    || stat == StatDefOf.ShootingAccuracyPawn)
                && UsesBaseCalculation(stat.Worker.GetType());
            supported[stat] = result;
            return result;
        }

        private static bool UsesBaseCalculation(Type type)
            => AccessTools.Method(type, nameof(StatWorker.GetValue), new[] { typeof(StatRequest), typeof(bool) })?.DeclaringType == typeof(StatWorker)
                && AccessTools.Method(type, nameof(StatWorker.GetValueUnfinalized), new[] { typeof(StatRequest), typeof(bool) })?.DeclaringType == typeof(StatWorker)
                && AccessTools.Method(type, nameof(StatWorker.FinalizeValue), new[] { typeof(StatRequest), typeof(float).MakeByRefType(), typeof(bool) })?.DeclaringType == typeof(StatWorker);

        internal static float GetStatValue(Verb verb, Pawn pawn, StatDef stat, bool applyPostProcess = true, int cacheStaleAfterTicks = -1)
        {
            if (!TryBegin(verb, pawn, stat, out Frame frame))
                return pawn?.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks) ?? 1f;
            try { return ReadCached(frame, stat, applyPostProcess); }
            finally { End(frame); }
        }

        internal static int AdjustedCooldownTicks(Verb verb, Pawn pawn)
        {
            if (verb.IsMeleeAttack || !TryBegin(verb, pawn, StatDefOf.RangedCooldownFactor, out Frame frame))
                return verb.verbProps.AdjustedCooldownTicks(verb, pawn);
            try { return readCooldown(verb.verbProps, verb, pawn); }
            finally { End(frame); }
        }

        internal static bool TryApplyAccuracy(Verb verb, ref ShotReport report, out float previous)
        {
            previous = 0f;
            if (verb?.verbProps?.canGoWild != true
                || !TryBegin(verb, verb.CasterPawn, StatDefOf.ShootingAccuracyPawn, out Frame frame)) return false;
            try
            {
                float accuracy = ReadCached(frame, StatDefOf.ShootingAccuracyPawn, true);
                float factor = ShotReport.HitFactorFromShooter(frame.pawn, Distance(ref report), accuracy);
                previous = Accuracy(ref report);
                Accuracy(ref report) = factor;
                return true;
            }
            finally { End(frame); }
        }

        internal static void RestoreAccuracy(ref ShotReport report, float previous) => Accuracy(ref report) = previous;

        private static bool TryBegin(Verb verb, Pawn pawn, StatDef stat, out Frame frame)
        {
            frame = null;
            if (pawn == null || !RimKataWeaponEffects.TryGetStatContext(verb, out var record)
                || record.pawn != pawn || record.statSnapshot == null
                || !supported.TryGetValue(stat, out bool supportedStat) || !supportedStat) return false;
            record.statSnapshot.Refresh(record.effects);
            frame = Begin(record);
            return true;
        }

        private static Frame Begin(RimKataWeaponEffectState record)
        {
            if (available == null) available = new Stack<Frame>();
            Frame frame = available.Count == 0 ? new Frame() : available.Pop();
            frame.verb = record.verb;
            frame.pawn = record.pawn;
            frame.record = record;
            frame.previous = current;
            current = frame;
            return frame;
        }

        private static void End(Frame frame)
        {
            current = frame.previous;
            frame.previous = null;
            frame.pawn = null;
            frame.verb = null;
            frame.record = null;
            frame.hediffsReady = false;
            frame.hediffs.Clear();
            available.Push(frame);
        }

        private static List<Hediff> ReadHediffs(HediffSet set)
        {
            Frame frame = current;
            if (frame == null || frame.pawn.health.hediffSet != set) return set.hediffs;
            if (!frame.hediffsReady)
            {
                frame.hediffs.AddRange(frame.record.baselineHediffs);
                frame.hediffs.AddRange(frame.record.effects);
                frame.hediffsReady = true;
            }
            return frame.hediffs;
        }

        private static float ReadOffset(StatDef stat, HediffStage stage, Pawn pawn, float severity)
            => stage is FrozenStage frozen && StatIndex(stat) is int index && index >= 0 ? frozen.offsets[index]
                : HediffStatsUtility.GetStatOffsetForSeverity(stat, stage, pawn, severity);

        private static float ReadFactor(StatDef stat, HediffStage stage, Pawn pawn, float severity)
            => stage is FrozenStage frozen && StatIndex(stat) is int index && index >= 0 ? frozen.factors[index]
                : HediffStatsUtility.GetStatFactorForSeverity(stat, stage, pawn, severity);

        private static float ReadCached(Frame frame, StatDef stat, bool applyPostProcess)
        {
            int index = StatIndex(stat) * 2 + (applyPostProcess ? 1 : 0);
            Snapshot snapshot = frame.record.statSnapshot;
            if (snapshot.valid[index]) return snapshot.values[index];
            float value = readValue(stat.Worker, StatRequest.For(frame.pawn), applyPostProcess);
            snapshot.values[index] = value;
            snapshot.valid[index] = true;
            return value;
        }

        private static float ReadScopedStat(Thing thing, StatDef stat, bool applyPostProcess, int cacheStaleAfterTicks)
        {
            return current != null && thing == current.pawn && supported.TryGetValue(stat, out bool supportedStat) && supportedStat
                ? ReadCached(current, stat, applyPostProcess)
                : thing.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks);
        }

        private static MethodInfo Copy(MethodInfo original, string rewriter, MethodInfo expectedCall = null)
        {
            requiredCall = expectedCall;
            requiredCallCount = 0;
            if (!RimKataWeaponExecutionBundle.TryCreateSnapshot(original,
                AccessTools.Method(typeof(RimKataWeaponEffectStats), rewriter), out MethodInfo result, out string reason))
                throw new NotSupportedException(reason);
            if (expectedCall != null && requiredCallCount == 0)
                throw new NotSupportedException("The vanilla weapon stat call path changed.");
            building.Add(original, result);
            return result;
        }

        private static IEnumerable<CodeInstruction> RewriteHediffs(IEnumerable<CodeInstruction> instructions)
        {
            int replacements = 0;
            foreach (CodeInstruction instruction in RewriteCalls(instructions))
            {
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, Hediffs))
                {
                    var replacement = new CodeInstruction(instruction) { opcode = OpCodes.Call, operand = HediffRead };
                    replacements++;
                    yield return replacement;
                }
                else yield return instruction;
            }
            if (replacements != 1) throw new NotSupportedException("The vanilla Hediff stat calculation changed.");
        }

        private static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo method)
                {
                    MethodInfo replacement = Equals(method, StatRead) ? ScopedRead
                        : Equals(method, OffsetRead) ? ScopedOffset : Equals(method, FactorRead) ? ScopedFactor
                        : building.TryGetValue(method, out MethodInfo copy) ? copy : null;
                    if (replacement != null)
                    {
                        if (Equals(method, requiredCall)) requiredCallCount++;
                        yield return new CodeInstruction(instruction) { opcode = OpCodes.Call, operand = replacement };
                        continue;
                    }
                }
                yield return instruction;
            }
        }
    }
}
