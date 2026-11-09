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
        private static readonly AccessTools.StructFieldRef<ShotReport, float> Accuracy =
            AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromShooterAndDist");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> Distance =
            AccessTools.StructFieldRefAccess<ShotReport, float>("distance");
        private static readonly AccessTools.FieldRef<StatWorker, StatDef> WorkerStat =
            AccessTools.FieldRefAccess<StatWorker, StatDef>("stat");
        private static Func<StatWorker, StatRequest, bool, float> readValue;
        private static Func<VerbProperties, Verb, Pawn, int> readCooldown;
        private static bool initialized;
        private static readonly Dictionary<StatDef, bool> supported = new Dictionary<StatDef, bool>();
        private static readonly Dictionary<Pawn, PairContext> owners = new Dictionary<Pawn, PairContext>();
        private static readonly Dictionary<ThingWithComps, StatContext> weapons = new Dictionary<ThingWithComps, StatContext>();
        private static readonly Dictionary<Verb, StatContext> verbs = new Dictionary<Verb, StatContext>();
        [ThreadStatic] private static Dictionary<MethodInfo, MethodInfo> building;
        [ThreadStatic] private static MethodInfo requiredCall;
        [ThreadStatic] private static int requiredCallCount;
        [ThreadStatic] private static Frame current;
        [ThreadStatic] private static Stack<Frame> available;
        [ThreadStatic] private static StatContext activeWeapon;

        internal readonly struct Scope : IDisposable
        {
            private readonly StatContext previous;
            internal Scope(object owner, Verb preferred)
            {
                previous = activeWeapon;
                activeWeapon = Resolve(owner, preferred);
            }
            public void Dispose() { activeWeapon = previous; }
        }

        private static StatContext Resolve(object owner, Verb preferred)
        {
            if (owners.Count == 0) return null;
            if (preferred != null && verbs.TryGetValue(preferred, out var context)) return context;
            if (owner is Verb verb) return verbs.TryGetValue(verb, out context) ? context : null;
            if (owner is ThingComp comp) return comp.parent != null && weapons.TryGetValue(comp.parent, out context) ? context : null;
            Hediff manager = (owner as HediffComp)?.parent ?? owner as Hediff;
            if (manager?.pawn != null && owners.TryGetValue(manager.pawn, out var pair)
                && pair.primary?.plan != null
                && (Array.IndexOf(pair.primary.plan.ManagerDefs, manager.def) >= 0
                    || Array.IndexOf(pair.primary.plan.Effects, manager.def) >= 0))
                return pair.primary;
            return null;
        }

        private sealed class Frame
        {
            internal Verb verb;
            internal Pawn pawn;
            internal StatContext record;
            internal bool hediffsReady;
            internal Frame previous;
            internal readonly List<Hediff> hediffs = new List<Hediff>();
        }

        // Stat readers do not own managers or participate in the local-health Tick.
        private sealed class StatContext
        {
            internal Pawn pawn;
            internal ThingWithComps weapon;
            internal Verb verb;
            internal RimKataWeaponEffectPlan plan;
            internal RimKataWeaponEffectState local;
            internal PairContext pair;
            internal readonly List<Hediff> nativeEffects = new List<Hediff>();
            internal readonly Snapshot statSnapshot = new Snapshot();
            internal List<Hediff> Effects => local?.effects ?? nativeEffects;

            internal void Refresh()
            {
                if (local == null)
                {
                    nativeEffects.Clear();
                    if (plan != null)
                        foreach (Hediff effect in pawn.health.hediffSet.hediffs)
                            if (effect?.def != null && Array.IndexOf(plan.Effects, effect.def) >= 0) nativeEffects.Add(effect);
                }
                statSnapshot.Refresh(Effects);
            }
        }

        private sealed class PairContext
        {
            internal StatContext primary, secondary;
            internal readonly HashSet<HediffDef> excluded = new HashSet<HediffDef>();
        }

        internal sealed class Snapshot
        {
            internal readonly Dictionary<StatDef, ValuePair> values = new Dictionary<StatDef, ValuePair>();
            private int tick = -1;
            private Hediff[] effects = Array.Empty<Hediff>();
            private HediffStage[] stages = Array.Empty<HediffStage>();
            private float[] severities = Array.Empty<float>();
            private int count = -1;

            internal void Refresh(List<Hediff> source)
            {
                int now = Find.TickManager?.TicksGame ?? -1;
                bool changed = tick != now || count != source.Count;
                tick = now;
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
                if (changed) values.Clear();
            }
        }

        internal struct ValuePair
        {
            internal float raw, final;
            internal bool rawValid, finalValid;
        }

        internal static bool HasStatContext(Verb verb) => verb != null && verbs.ContainsKey(verb);
        internal static void NotifyHealthChanged(Pawn pawn, Hediff changed = null)
        {
            if (pawn == null || !owners.TryGetValue(pawn, out var pair)) return;
            if (changed?.def != null && activeWeapon?.pawn == pawn && activeWeapon.plan != null
                && Array.IndexOf(activeWeapon.plan.Effects, changed.def) >= 0)
            { activeWeapon.statSnapshot.values.Clear(); return; }
            if (changed != null && pair.secondary?.local?.effects.Contains(changed) == true)
            { pair.secondary.statSnapshot.values.Clear(); return; }
            if (changed?.def != null && pair.primary?.plan != null
                && Array.IndexOf(pair.primary.plan.Effects, changed.def) >= 0)
            { pair.primary.statSnapshot.values.Clear(); return; }
            pair.primary?.statSnapshot.values.Clear();
            pair.secondary?.statSnapshot.values.Clear();
        }

        internal sealed class PreparationFailure : Exception
        {
            internal readonly ThingWithComps weapon;
            internal PreparationFailure(ThingWithComps weapon, Exception inner)
                : base("Could not prepare weapon-local stats.", inner) { this.weapon = weapon; }
        }

        // A failed instance must not discard the other weapon's healthy stat reader.
        internal static void ReleaseWeaponContext(ThingWithComps weapon)
        {
            if (weapon == null || !weapons.TryGetValue(weapon, out var context)) return;
            Remove(context);
            if (!owners.TryGetValue(context.pawn, out var pair)) return;
            if (pair.primary == context) pair.primary = null;
            if (pair.secondary == context) pair.secondary = null;
            if (pair.primary == null && pair.secondary == null) owners.Remove(context.pawn);
        }

        internal static bool Sync(Pawn pawn, RimKataWeaponCycleState primary, RimKataWeaponCycleState secondary,
            RimKataWeaponEffectPlan primaryPlan, RimKataWeaponEffectPlan secondaryPlan,
            RimKataWeaponEffectState secondaryRecord)
        {
            if (pawn == null) return true;
            if (!Participates(primary) || !Participates(secondary)
                || (primaryPlan == null && secondaryPlan == null))
            { ReleasePawn(pawn); return true; }
            if (owners.TryGetValue(pawn, out var previous)
                && Matches(previous.primary, primary, primaryPlan, null)
                && Matches(previous.secondary, secondary, secondaryPlan, secondaryRecord)) return true;
            ReleasePawn(pawn);
            var pair = new PairContext();
            AddDefinitions(primaryPlan, pair.excluded);
            AddDefinitions(secondaryPlan, pair.excluded);
            pair.primary = Create(pawn, primary, primaryPlan, null, pair);
            pair.secondary = Create(pawn, secondary, secondaryPlan, secondaryRecord, pair);
            owners[pawn] = pair;
            Index(pair.primary);
            Index(pair.secondary);
            try { CaptureValues(pair.primary); CaptureValues(pair.secondary); }
            catch { ReleasePawn(pawn); throw; }
            return true;
        }

        private static bool Participates(RimKataWeaponCycleState cycle)
            => cycle?.weapon != null && cycle.boundVerb != null && cycle.ordinaryWeaponEnabled;

        private static bool Matches(StatContext context, RimKataWeaponCycleState cycle,
            RimKataWeaponEffectPlan plan, RimKataWeaponEffectState local)
            => !Participates(cycle) ? context == null : context != null && context.weapon == cycle.weapon
                && context.verb == cycle.boundVerb && context.plan == plan && context.local == local;

        private static void AddDefinitions(RimKataWeaponEffectPlan plan, HashSet<HediffDef> excluded)
        {
            if (plan == null) return;
            excluded.UnionWith(plan.ManagerDefs);
            excluded.UnionWith(plan.Effects);
        }

        private static StatContext Create(Pawn pawn, RimKataWeaponCycleState cycle,
            RimKataWeaponEffectPlan plan, RimKataWeaponEffectState local, PairContext pair)
        {
            if (!Participates(cycle)) return null;
            return new StatContext
            {
                pawn = pawn, weapon = cycle.weapon, verb = cycle.boundVerb,
                plan = plan, local = local, pair = pair
            };
        }

        private static void CaptureValues(StatContext context)
        {
            if (context == null) return;
            try
            {
                context.Refresh();
                Frame frame = Begin(context);
                try
                {
                    foreach (StatDef stat in PreparedStats(context))
                        if (supported.TryGetValue(stat, out bool supportedStat) && supportedStat)
                            ReadCached(frame, stat, true);
                }
                finally { End(frame); }
            }
            catch (Exception exception) { throw new PreparationFailure(context.weapon, exception); }
        }

        private static void Index(StatContext context)
        {
            if (context == null) return;
            weapons[context.weapon] = context;
            verbs[context.verb] = context;
        }

        private static IEnumerable<StatDef> PreparedStats(StatContext context)
        {
            yield return StatDefOf.AimingDelayFactor;
            yield return StatDefOf.RangedCooldownFactor;
            yield return StatDefOf.ShootingAccuracyPawn;
            if (context.plan?.ChangedStats != null)
                foreach (StatDef stat in context.plan.ChangedStats) if (stat != null) yield return stat;
        }

        internal static void ReleasePawn(Pawn pawn)
        {
            if (pawn == null) return;
            if (!owners.TryGetValue(pawn, out var pair)) return;
            owners.Remove(pawn);
            Remove(pair.primary);
            Remove(pair.secondary);
        }

        private static void Remove(StatContext context)
        {
            if (context == null) return;
            if (weapons.TryGetValue(context.weapon, out var owner) && owner == context) weapons.Remove(context.weapon);
            if (verbs.TryGetValue(context.verb, out owner) && owner == context) verbs.Remove(context.verb);
        }

        internal static void ReleaseWeapon(ThingWithComps weapon)
        {
            ReleaseWeaponContext(weapon);
        }

        internal static List<Pawn> ReleaseDefinition(ThingDef definition)
        {
            List<ThingWithComps> affected = null;
            List<Pawn> pawns = null;
            foreach (var item in weapons)
            {
                if (item.Key.def != definition) continue;
                if (affected == null) affected = new List<ThingWithComps>();
                affected.Add(item.Key);
                if (pawns == null) pawns = new List<Pawn>();
                if (!pawns.Contains(item.Value.pawn)) pawns.Add(item.Value.pawn);
            }
            if (affected != null) foreach (ThingWithComps weapon in affected) ReleaseWeaponContext(weapon);
            return pawns;
        }

        internal static void ResetGame()
        {
            owners.Clear(); weapons.Clear(); verbs.Clear();
            current = null; activeWeapon = null; available?.Clear();
        }

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
                && UsesBaseCalculation(stat.Worker.GetType());
            supported[stat] = result;
            if (result)
            {
                // The copied vanilla calculation reads these definitions recursively.
                // Prepare them with the root stat; firing only consumes this lookup.
                if (stat.statFactors != null)
                    foreach (StatDef dependency in stat.statFactors) IsSupported(dependency);
                if (stat.postProcessStatFactors != null)
                    foreach (StatDef dependency in stat.postProcessStatFactors) IsSupported(dependency);
            }
            return result;
        }

        private static bool UsesBaseCalculation(Type type)
            => AccessTools.Method(type, nameof(StatWorker.GetValue), new[] { typeof(StatRequest), typeof(bool) })?.DeclaringType == typeof(StatWorker)
                && AccessTools.Method(type, nameof(StatWorker.GetValueUnfinalized), new[] { typeof(StatRequest), typeof(bool) })?.DeclaringType == typeof(StatWorker)
                && AccessTools.Method(type, nameof(StatWorker.FinalizeValue), new[] { typeof(StatRequest), typeof(float).MakeByRefType(), typeof(bool) })?.DeclaringType == typeof(StatWorker);

        internal static float GetStatValue(Verb verb, Pawn pawn, StatDef stat, bool applyPostProcess = true, int cacheStaleAfterTicks = -1)
        {
            Frame frame = null;
            Exception failure = null;
            try
            {
                if (TryBegin(verb, pawn, stat, out frame)) return ReadCached(frame, stat, applyPostProcess);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (frame != null) End(frame); }
            if (failure != null) Fail(verb, pawn, failure);
            return pawn?.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks) ?? 1f;
        }

        internal static float GetScopedStatValue(Thing thing, StatDef stat, bool applyPostProcess, int cacheStaleAfterTicks)
            => activeWeapon != null && thing == activeWeapon.pawn
                ? GetStatValue(activeWeapon.verb, activeWeapon.pawn, stat, applyPostProcess, cacheStaleAfterTicks)
                : thing.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks);

        internal static float GetScopedWorkerValue(StatWorker worker, StatRequest request, bool applyPostProcess)
        {
            StatContext owner = activeWeapon;
            if (owner == null || request.Thing != owner.pawn) return worker.GetValue(request, applyPostProcess);
            Frame frame = null;
            Exception failure = null;
            try
            {
                StatDef stat = WorkerStat(worker);
                if (worker != stat.Worker)
                    throw new NotSupportedException("The pawn stat call uses a different worker instance: " + stat.defName);
                if (TryBegin(owner.verb, owner.pawn, stat, out frame))
                    return readValue(worker, request, applyPostProcess);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (frame != null) End(frame); }
            if (failure != null) Fail(owner.verb, owner.pawn, failure);
            return worker.GetValue(request, applyPostProcess);
        }

        private static void Fail(Verb verb, Pawn pawn, Exception exception)
        {
            if (verb != null && verbs.TryGetValue(verb, out var record) && record.pawn == pawn)
                RimKataWeaponEffects.NotifyStatFailure(pawn, record.weapon, exception, verb);
        }

        internal static int AdjustedCooldownTicks(Verb verb, Pawn pawn)
        {
            Frame frame = null;
            Exception failure = null;
            try
            {
                if (!verb.IsMeleeAttack && TryBegin(verb, pawn, StatDefOf.RangedCooldownFactor, out frame))
                    return readCooldown(verb.verbProps, verb, pawn);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (frame != null) End(frame); }
            if (failure != null) Fail(verb, pawn, failure);
            return verb.verbProps.AdjustedCooldownTicks(verb, pawn);
        }

        internal static bool TryApplyAccuracy(Verb verb, ref ShotReport report, out float previous)
        {
            previous = 0f;
            if (verb?.verbProps?.canGoWild != true) return false;
            Frame frame = null;
            Exception failure = null;
            try
            {
                if (!TryBegin(verb, verb.CasterPawn, StatDefOf.ShootingAccuracyPawn, out frame)) return false;
                float accuracy = ReadCached(frame, StatDefOf.ShootingAccuracyPawn, true);
                float factor = ShotReport.HitFactorFromShooter(frame.pawn, Distance(ref report), accuracy);
                previous = Accuracy(ref report);
                Accuracy(ref report) = factor;
                return true;
            }
            catch (Exception exception) { failure = exception; }
            finally { if (frame != null) End(frame); }
            Fail(verb, verb.CasterPawn, failure);
            return false;
        }

        internal static void RestoreAccuracy(ref ShotReport report, float previous) => Accuracy(ref report) = previous;

        private static bool TryBegin(Verb verb, Pawn pawn, StatDef stat, out Frame frame)
        {
            frame = null;
            if (pawn == null || verb == null || !verbs.TryGetValue(verb, out var record)
                || record.pawn != pawn) return false;
            if (stat == null || !supported.TryGetValue(stat, out bool supportedStat) || !supportedStat)
                throw new NotSupportedException("The current pawn stat read has no prepared weapon-local calculation: " + stat?.defName);
            record.Refresh();
            frame = Begin(record);
            return true;
        }

        private static Frame Begin(StatContext record)
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
                // Keep current native health inputs. Only known weapon effects are replaced;
                // common injuries, implants and severity changes are never frozen at combat entry.
                foreach (Hediff hediff in set.hediffs)
                    if (hediff?.def != null && !frame.record.pair.excluded.Contains(hediff.def))
                        frame.hediffs.Add(hediff);
                frame.hediffs.AddRange(frame.record.Effects);
                frame.hediffsReady = true;
            }
            return frame.hediffs;
        }

        private static float ReadCached(Frame frame, StatDef stat, bool applyPostProcess)
        {
            Snapshot snapshot = frame.record.statSnapshot;
            snapshot.values.TryGetValue(stat, out ValuePair cached);
            if (applyPostProcess ? cached.finalValid : cached.rawValid)
                return applyPostProcess ? cached.final : cached.raw;
            float value = readValue(stat.Worker, StatRequest.For(frame.pawn), applyPostProcess);
            if (applyPostProcess) { cached.final = value; cached.finalValid = true; }
            else { cached.raw = value; cached.rawValid = true; }
            snapshot.values[stat] = cached;
            return value;
        }

        private static float ReadScopedStat(Thing thing, StatDef stat, bool applyPostProcess, int cacheStaleAfterTicks)
        {
            if (current == null || thing != current.pawn)
                return thing.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks);
            if (stat == null || !supported.TryGetValue(stat, out bool supportedStat) || !supportedStat)
                throw new NotSupportedException("A dependent pawn stat has no prepared weapon-local calculation: " + stat?.defName);
            return ReadCached(current, stat, applyPostProcess);
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
