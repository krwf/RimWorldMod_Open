// Temporary diagnostics. Delete this file, rebuild the normal DLL and restart
// the game to remove every probe. No gameplay source, settings or save field
// depends on this file. Drafted timings describe qualified JobTracker trees;
// SearchProbe windows also cover collection and buffer work outside those trees.
// NativeFireProbe measures handed-off native execution independently of drafted totals.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataPerformanceProbe
    {
        internal const double FrameThresholdMs = 1.0;
        private const string Prefix = "[RimKata.DraftedFireProbe] ";
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly FrameData frame = new FrameData();
        private static readonly FrameData peak = new FrameData();
        private static readonly FrameData deathSampleFrame = new FrameData();
        private static readonly Dictionary<MethodBase, Part> parts =
            new Dictionary<MethodBase, Part>();
        private static Game game;
        private static int generation;
        private static int windowFrames;
        private static int windowHighFrames;
        private static long windowTicks;
        private static long lastReport;
        [ThreadStatic] private static Entry[] stack;
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static int detailDepth;
        [ThreadStatic] private static int logDepth;
        [ThreadStatic] private static int injuryDepth;
        [ThreadStatic] private static int deathDepth;
        [ThreadStatic] private static int deathThoughtDepth;
        [ThreadStatic] private static int modDeathDepth;
        [ThreadStatic] private static DamageWorker.DamageResult lastDamageResult;
        [ThreadStatic] private static DamageContext lastDamage;

        internal enum Part
        {
            Entry, Prepare, Cycle, NestedJob, Close, Normalize, Search, SearchCollect, SearchBuffer,
            Select, Reserve, Slot, Dispatch, CloseHit, Continuity, Aim, BattleLog,
            Available, CanHit, FireContext, ShotPrepare, Warmup, Burst, Cast,
            Sound, Stance, AutoAttack, DirectHit, MeleeDamage, Damage, DamageLog,
            Clamor, Stagger, ExtraDamage, ProjectilePlace, ProjectileImpact, DefenseNotify,
            DamagePre, Injury, DamagePost, LogText, InjuryAdd, HealthState,
            Death, DeathEffects, DeathModify, DeathThoughts, DeathDespawn,
            DeathCorpse, DeathPlace, ModDeath,
            RemoveLost, RemoveRescued, GiveDeathThoughts, GetDeathThoughts,
            HumanDeathThoughts, RelationDeathThoughts, VeneratedDeathThoughts,
            WorldPawnList, WorldPawnListBuild, MapPawnList, ColonistPawnList,
            RelatedPawns, RelatedPawnsNext, FamilyPawns, FamilyPawnsNext,
            RemoveOtherMemories, RemoveMemory, MakeThought, AddIndividualThought,
            AddAllThought, GainMemoryDef, GainMemory, CanGetThought,
            ShouldGetThoughtAbout,
            WitnessedDeath, ThoughtLineOfSight, ImportantRelation, RelationsNext,
            PawnOpinion, TotalOpinion, SocialThoughts, SocialGroupFilter, SocialGroups, GroupOpinion,
            MergeMemory, GroupMemoryCount, GroupMemoryOldest,
            DefMemoryCount, DefMemoryOldest,
            Count
        }

        internal struct Scope
        {
            internal int depth;
            internal int generation;
        }

        internal struct HealthScope
        {
            internal Scope scope;
            internal Pawn pawn;
            internal byte before;
            internal int beforeCount;
            internal string hediffDef;
        }

        private struct Entry
        {
            internal Part part;
            internal long start;
            internal long children;
            internal int pawnId;
            internal string job;
            internal MethodBase method;
            internal ShotContext shot;
            internal DamageContext damage;
            internal int deathPawnId;
        }

        private struct ShotContext
        {
            internal int sequence, tick, pawnId, targetId, weaponId;
            internal string weaponDef;
            internal byte flags;
        }

        private struct DamageContext
        {
            internal int sequence, recipientId, instigatorId;
            internal string recipientDef, damageDef;
            internal ThingCategory recipientCategory;
            internal float incomingAmount;
        }

        private struct HealthSample
        {
            internal long self;
            internal int tick, pawnId, damageId, shotId, beforeCount, afterCount;
            internal byte before, after;
            internal string hediffDef;
            internal bool failed;
            internal bool recorded;
        }

        private struct DeathTraceSample
        {
            internal bool recorded;
            internal long elapsed, captureTicks;
            internal int tick, rootPawnId, deathPawnId;
            internal ShotContext shot;
            internal DamageContext damage;
            internal StackTrace trace;
            internal string failure;
        }

        private sealed class FrameData
        {
            internal readonly long[] self = new long[(int)Part.Count];
            internal readonly long[] inclusive = new long[(int)Part.Count];
            internal readonly int[] calls = new int[(int)Part.Count];
            internal readonly HealthSample[] health = new HealthSample[2];
            internal int number = -1, firstTick = -1, lastTick = -1;
            internal bool complete;
            internal long total, maxCall;
            internal int rootCalls, pawnId, errors;
            internal string job;
            internal int checks, rejected, removed, searchStarts, searchSteps;
            internal int shots, fired, closeShots, interceptionShots;
            internal int gc0, gc1, gc2;
            internal long detailSelf;
            internal MethodBase detailMethod;
            internal int detailPawnId, detailTick;
            internal ShotContext detailShot;
            internal int damageSequence;
            internal DamageContext detailDamage;
            internal int detailDeathPawnId;
            internal long modDeathTreeTicks, modDeathMaxTicks;
            internal int modDeathTreeCalls, modDeathPawnId, modDeathShotId, modDeathTick;
            internal MethodBase modDeathMethod;
            internal DeathTraceSample deathTrace;

            internal void Clear()
            {
                Array.Clear(self, 0, self.Length);
                Array.Clear(inclusive, 0, inclusive.Length);
                Array.Clear(calls, 0, calls.Length);
                Array.Clear(health, 0, health.Length);
                number = firstTick = lastTick = -1;
                complete = false;
                total = maxCall = 0;
                rootCalls = pawnId = errors = 0;
                job = null;
                checks = rejected = removed = searchStarts = searchSteps = 0;
                shots = fired = closeShots = interceptionShots = 0;
                gc0 = gc1 = gc2 = 0;
                detailSelf = 0;
                detailMethod = null;
                detailPawnId = detailTick = 0;
                detailShot = default;
                damageSequence = 0;
                detailDamage = default;
                detailDeathPawnId = 0;
                modDeathTreeTicks = modDeathMaxTicks = 0;
                modDeathTreeCalls = modDeathPawnId = modDeathShotId = modDeathTick = 0;
                modDeathMethod = null;
                deathTrace = default;
            }

            internal void CopyFrom(FrameData value)
            {
                Array.Copy(value.self, self, self.Length);
                Array.Copy(value.inclusive, inclusive, inclusive.Length);
                Array.Copy(value.calls, calls, calls.Length);
                Array.Copy(value.health, health, health.Length);
                number = value.number;
                firstTick = value.firstTick;
                lastTick = value.lastTick;
                total = value.total;
                maxCall = value.maxCall;
                rootCalls = value.rootCalls;
                pawnId = value.pawnId;
                job = value.job;
                errors = value.errors;
                checks = value.checks;
                rejected = value.rejected;
                removed = value.removed;
                searchStarts = value.searchStarts;
                searchSteps = value.searchSteps;
                shots = value.shots;
                fired = value.fired;
                closeShots = value.closeShots;
                interceptionShots = value.interceptionShots;
                gc0 = value.gc0;
                gc1 = value.gc1;
                gc2 = value.gc2;
                detailSelf = value.detailSelf;
                detailMethod = value.detailMethod;
                detailPawnId = value.detailPawnId;
                detailTick = value.detailTick;
                detailShot = value.detailShot;
                damageSequence = value.damageSequence;
                detailDamage = value.detailDamage;
                detailDeathPawnId = value.detailDeathPawnId;
                modDeathTreeTicks = value.modDeathTreeTicks;
                modDeathMaxTicks = value.modDeathMaxTicks;
                modDeathTreeCalls = value.modDeathTreeCalls;
                modDeathPawnId = value.modDeathPawnId;
                modDeathShotId = value.modDeathShotId;
                modDeathTick = value.modDeathTick;
                modDeathMethod = value.modDeathMethod;
                deathTrace = value.deathTrace;
            }
        }

        internal static void Reset()
        {
            RimKataSearchProbe.Reset();
            RimKataNativeFireProbe.Reset();
            generation++;
            depth = 0;
            detailDepth = 0;
            logDepth = 0;
            injuryDepth = 0;
            deathDepth = modDeathDepth = 0;
            deathThoughtDepth = 0;
            ClearDamageResult();
            game = null;
            frame.Clear();
            peak.Clear();
            deathSampleFrame.Clear();
            windowFrames = windowHighFrames = 0;
            windowTicks = lastReport = 0;
        }

        internal static void BeginFrame()
        {
            if (!ReferenceEquals(game, Current.Game))
            {
                Reset();
                game = Current.Game;
            }
            RimKataSearchProbe.BeginFrame();
            RimKataNativeFireProbe.BeginFrame();
            int number = Time.frameCount;
            if (frame.number == number)
            {
                return;
            }
            CompleteFrame();
            ClearDamageResult();
            frame.Clear();
            frame.number = number;
            frame.gc0 = GC.CollectionCount(0);
            frame.gc1 = GC.CollectionCount(1);
            frame.gc2 = GC.CollectionCount(2);
        }

        internal static Scope EnterRoot(Pawn pawn)
        {
            // Use maintained access only. Do not inspect genes, maps or weapons
            // for ordinary Pawns merely to decide whether to measure them.
            if (pawn == null
                || (!RimKataEligibilityCache.IsRegisteredUser(pawn)
                    && !(RimKataMod.Settings?.accessRestrictionsDisabled == true
                        && RimKataCombatStatePresenceCache.TryGetOwner(pawn, out _))))
            {
                return default;
            }
            if (depth == 0)
            {
                BeginFrame();
                if (game == null || frame.complete)
                {
                    return default;
                }
                int tick = Find.TickManager?.TicksGame ?? -1;
                if (frame.firstTick < 0)
                {
                    frame.firstTick = tick;
                }
                frame.lastTick = tick;
                frame.rootCalls++;
            }
            Scope scope = Push(Part.Entry);
            if (scope.depth == 1)
            {
                stack[0].pawnId = pawn.thingIDNumber;
                stack[0].job = pawn.CurJobDef?.defName;
                // Exclude probe-only context capture from this root's clock.
                stack[0].start = Stopwatch.GetTimestamp();
            }
            return scope;
        }

        internal static Scope EnterPart(MethodBase method)
        {
            if (depth == 0 || !parts.TryGetValue(method, out Part part)
                || (part >= Part.Available && detailDepth == 0)) return default;
            if ((part == Part.DamagePre || part == Part.Injury || part == Part.DamagePost)
                && stack[depth - 1].damage.sequence == 0) return default;
            if (part == Part.LogText && logDepth == 0) return default;
            if (part >= Part.DeathEffects && deathDepth == 0) return default;
            if (part >= Part.RemoveLost && deathThoughtDepth == 0) return default;
            Scope scope = Push(part);
            if (scope.depth != 0) stack[depth - 1].method = method;
            return scope;
        }

        internal static Scope EnterDispatch(MethodBase method, Verb verb, LocalTargetInfo target,
            bool moving, bool close, bool interception)
        {
            if (depth == 0)
            {
                return default;
            }
            frame.shots++;
            if (close) frame.closeShots++;
            if (interception) frame.interceptionShots++;
            Scope scope = Push(Part.Dispatch);
            if (scope.depth != 0)
            {
                Thing weapon = verb?.EquipmentSource;
                stack[depth - 1].method = method;
                stack[depth - 1].shot = new ShotContext
                {
                    sequence = frame.shots,
                    tick = Find.TickManager?.TicksGame ?? -1,
                    pawnId = verb?.CasterPawn?.thingIDNumber ?? stack[depth - 1].pawnId,
                    targetId = target.Thing?.thingIDNumber ?? 0,
                    weaponId = weapon?.thingIDNumber ?? 0,
                    weaponDef = weapon?.def?.defName,
                    flags = (byte)((moving ? 1 : 0) | (close ? 2 : 0) | (interception ? 4 : 0))
                };
            }
            return scope;
        }

        internal static Scope EnterDamage(MethodBase method, Thing recipient, DamageInfo info)
        {
            if (depth == 0 || detailDepth == 0) return default;
            Scope scope = Push(Part.Damage);
            if (scope.depth != 0)
            {
                DamageContext damage = RecipientContext(recipient);
                damage.sequence = ++frame.damageSequence;
                damage.instigatorId = info.Instigator?.thingIDNumber ?? 0;
                damage.damageDef = info.Def?.defName;
                damage.incomingAmount = info.Amount;
                stack[depth - 1].method = method;
                stack[depth - 1].damage = damage;
            }
            return scope;
        }

        internal static Scope EnterDeath(MethodBase method, Pawn pawn)
        {
            if (depth == 0 || detailDepth == 0 || pawn == null) return default;
            Scope scope = Push(Part.Death);
            if (scope.depth != 0)
            {
                stack[depth - 1].method = method;
                stack[depth - 1].deathPawnId = pawn.thingIDNumber;
            }
            return scope;
        }

        internal static Scope EnterCorpsePlacement(MethodBase method, Thing thing)
        {
            if (depth == 0 || deathDepth == 0 || !(thing is Corpse corpse)
                || corpse.InnerPawn?.thingIDNumber != stack[depth - 1].deathPawnId) return default;
            Scope scope = Push(Part.DeathPlace);
            if (scope.depth != 0) stack[depth - 1].method = method;
            return scope;
        }

        internal static HealthScope EnterHealth(MethodBase method, Pawn pawn, Hediff hediff, Part part)
        {
            // Health hooks also run for unrelated Pawns. Read their state only
            // inside an already measured injury operation.
            if (depth == 0 || injuryDepth == 0 || pawn == null) return default;
            Scope scope = Push(part);
            if (scope.depth == 0) return default;
            stack[depth - 1].method = method;
            return new HealthScope
            {
                scope = scope, pawn = pawn, before = HealthFlags(pawn),
                beforeCount = pawn.health?.hediffSet?.hediffs?.Count ?? -1,
                hediffDef = hediff?.def?.defName
            };
        }

        private static byte HealthFlags(Pawn pawn)
            => (byte)((pawn.Downed ? 1 : 0) | (pawn.Dead ? 2 : 0));

        internal static void ExitHealth(HealthScope health, bool failed)
        {
            Scope scope = health.scope;
            if (scope.depth == 0 || scope.generation != generation || scope.depth != depth) return;
            long end = Stopwatch.GetTimestamp();
            Entry entry = stack[depth - 1];
            long self = Math.Max(0, end - entry.start - entry.children);
            int index = entry.part == Part.InjuryAdd ? 0 : 1;
            if (!frame.health[index].recorded || self > frame.health[index].self)
            {
                // Each stage retains its own worst call. Nested calls can observe
                // the same transition; these are observations, not event counts.
                frame.health[index] = new HealthSample
                {
                    self = self, tick = Find.TickManager?.TicksGame ?? -1,
                    pawnId = health.pawn.thingIDNumber,
                    damageId = entry.damage.sequence, shotId = entry.shot.sequence,
                    before = health.before, after = HealthFlags(health.pawn),
                    beforeCount = health.beforeCount,
                    afterCount = health.pawn.health?.hediffSet?.hediffs?.Count ?? -1,
                    hediffDef = health.hediffDef, failed = failed, recorded = true
                };
            }
            // Use the same endpoint for exclusive totals and the retained sample;
            // snapshot formatting/copying is not part of this health operation.
            Exit(scope, failed, end);
        }

        internal static Scope EnterDamageLog(MethodBase method, DamageWorker.DamageResult result)
        {
            if (depth == 0 || detailDepth == 0) return default;
            Scope scope = Push(Part.DamageLog);
            if (scope.depth != 0)
            {
                stack[depth - 1].method = method;
                // A result may be associated after another damage event. Never
                // assign that other event's packet merely because it was recent.
                stack[depth - 1].damage = result != null && ReferenceEquals(result, lastDamageResult)
                    ? lastDamage : RecipientContext(result?.hitThing);
            }
            return scope;
        }

        private static DamageContext RecipientContext(Thing recipient)
        {
            return new DamageContext
            {
                recipientId = recipient?.thingIDNumber ?? 0,
                recipientDef = recipient?.def?.defName,
                recipientCategory = recipient?.def?.category ?? default
            };
        }

        private static void ClearDamageResult()
        {
            lastDamageResult = null;
            lastDamage = default;
        }

        internal static void ExitDamage(Scope scope, DamageWorker.DamageResult result, bool failed)
        {
            if (scope.depth == 0 || scope.generation != generation || scope.depth != depth) return;
            if (failed || result == null)
            {
                ClearDamageResult();
            }
            else
            {
                // Outer Pawn damage completes after nested armor damage, restoring
                // the correct result/context pair for an immediate log association.
                lastDamageResult = result;
                lastDamage = stack[depth - 1].damage;
            }
            Exit(scope, failed);
        }

        private static bool OpensDetail(Part part)
            => part == Part.Dispatch || part == Part.CloseHit || part == Part.Aim;

        private static Scope Push(Part part)
        {
            if (stack == null) stack = new Entry[64];
            if (depth == stack.Length)
            {
                // An unmeasured deep child stays charged to its measured parent.
                return default;
            }
            Entry parent = depth > 0 ? stack[depth - 1] : default;
            stack[depth] = new Entry
            {
                part = part, start = Stopwatch.GetTimestamp(),
                pawnId = parent.pawnId, job = parent.job, shot = parent.shot,
                damage = parent.damage, deathPawnId = parent.deathPawnId
            };
            depth++;
            if (OpensDetail(part)) detailDepth++;
            if (part == Part.DamageLog) logDepth++;
            if (part == Part.Injury) injuryDepth++;
            if (part == Part.Death) deathDepth++;
            if (part == Part.DeathThoughts) deathThoughtDepth++;
            if (part == Part.ModDeath) modDeathDepth++;
            return new Scope { depth = depth, generation = generation };
        }

        internal static void Exit(Scope scope, bool failed, long end = 0)
        {
            if (scope.depth == 0 || scope.generation != generation
                || scope.depth != depth)
            {
                return;
            }
            if (end == 0) end = Stopwatch.GetTimestamp();
            Entry entry = stack[--depth];
            long elapsed = Math.Max(0, end - entry.start);
            int index = (int)entry.part;
            long self = Math.Max(0, elapsed - entry.children);
            frame.inclusive[index] += elapsed;
            frame.self[index] += self;
            frame.calls[index]++;
            // Keep one worst exclusive call, not the sum of several unrelated shots.
            // Its shot fields describe the enclosing shot, not necessarily the damage recipient.
            if (detailDepth > 0 && self > frame.detailSelf && entry.method != null)
            {
                frame.detailSelf = self;
                frame.detailMethod = entry.method;
                frame.detailPawnId = entry.pawnId;
                frame.detailTick = Find.TickManager?.TicksGame ?? -1;
                frame.detailShot = entry.shot;
                frame.detailDamage = entry.damage;
                frame.detailDeathPawnId = entry.deathPawnId;
            }
            if (entry.part == Part.ModDeath)
            {
                // Count an audited callback subtree once even when it calls
                // another measured RimKata helper or native death stage.
                if (modDeathDepth == 1)
                {
                    frame.modDeathTreeTicks += elapsed;
                    frame.modDeathTreeCalls++;
                    if (elapsed > frame.modDeathMaxTicks)
                    {
                        frame.modDeathMaxTicks = elapsed;
                        frame.modDeathMethod = entry.method;
                        frame.modDeathPawnId = entry.deathPawnId;
                        frame.modDeathShotId = entry.shot.sequence;
                        frame.modDeathTick = Find.TickManager?.TicksGame ?? -1;
                    }
                }
                modDeathDepth--;
            }
            if (OpensDetail(entry.part)) detailDepth--;
            if (entry.part == Part.DamageLog) logDepth--;
            if (entry.part == Part.Injury) injuryDepth--;
            if (entry.part == Part.Death) deathDepth--;
            if (entry.part == Part.DeathThoughts)
            {
                deathThoughtDepth--;
                // The Harmony finalizer is still under DropBeforeDying's caller.
                // Keep one slow death's real caller chain, not a per-hit trace.
                if (!frame.deathTrace.recorded && !deathSampleFrame.deathTrace.recorded
                    && elapsed * TickToMs >= FrameThresholdMs)
                {
                    CaptureDeathTrace(entry, elapsed);
                }
            }
            if (depth > 0)
            {
                stack[depth - 1].children += elapsed;
            }
            else
            {
                ClearDamageResult();
                frame.total += elapsed;
                if (failed) frame.errors++;
                if (elapsed > frame.maxCall)
                {
                    frame.maxCall = elapsed;
                    frame.pawnId = entry.pawnId;
                    frame.job = entry.job;
                }
            }
            stack[depth] = default;
        }

        private static void CaptureDeathTrace(Entry entry, long elapsed)
        {
            long start = Stopwatch.GetTimestamp();
            var sample = new DeathTraceSample
            {
                recorded = true, elapsed = elapsed,
                tick = Find.TickManager?.TicksGame ?? -1,
                rootPawnId = entry.pawnId, deathPawnId = entry.deathPawnId,
                shot = entry.shot, damage = entry.damage
            };
            try
            {
                sample.trace = new StackTrace(1, false);
            }
            catch (Exception exception)
            {
                // Diagnostic capture must not interrupt native death processing.
                sample.failure = exception.GetType().Name;
            }
            finally
            {
                sample.captureTicks = Math.Max(0, Stopwatch.GetTimestamp() - start);
                // The measured child already ended. Remove only this diagnostic
                // pause from every still-running ancestor, preserving child sums.
                for (int i = 0; i < depth; i++) stack[i].start += sample.captureTicks;
            }
            frame.deathTrace = sample;
        }

        internal static void CountResult(MethodBase method, bool result)
        {
            if (depth == 0) return;
            switch (method.Name)
            {
                case "CanShootRegisteredCandidate":
                    frame.checks++;
                    if (!result) frame.rejected++;
                    break;
                case "RemoveAutomaticCandidateFromCycle":
                    if (result) frame.removed++;
                    break;
                case "Begin":
                    if (result) frame.searchStarts++;
                    break;
                case "Advance":
                    if (result) frame.searchSteps++;
                    break;
            }
        }

        internal static void CountQueued(bool result)
        {
            if (depth > 0 && result) frame.fired++;
        }

        internal static void CompleteFrame()
        {
            if (depth != 0 || !ReferenceEquals(game, Current.Game)) return;
            if (!frame.complete && frame.number >= 0)
            {
                frame.complete = true;
                if (frame.rootCalls > 0)
                {
                    frame.gc0 = GC.CollectionCount(0) - frame.gc0;
                    frame.gc1 = GC.CollectionCount(1) - frame.gc1;
                    frame.gc2 = GC.CollectionCount(2) - frame.gc2;
                    windowFrames++;
                    windowTicks += frame.total;
                    // A later non-death peak must not discard the captured death.
                    if (frame.deathTrace.recorded) deathSampleFrame.CopyFrom(frame);
                    if (frame.total * TickToMs >= FrameThresholdMs)
                    {
                        windowHighFrames++;
                        if (frame.total > peak.total) peak.CopyFrom(frame);
                    }
                }
            }
            if (windowHighFrames == 0) return;
            long now = Stopwatch.GetTimestamp();
            if (lastReport != 0 && now - lastReport < Stopwatch.Frequency) return;
            // The first crossing is immediate; subsequent crossings are coalesced
            // for one second, retaining the worst full frame and window counts.
            Report();
            if (deathSampleFrame.deathTrace.recorded) ReportDeathSample();
            lastReport = Stopwatch.GetTimestamp();
            peak.Clear();
            deathSampleFrame.Clear();
            windowFrames = windowHighFrames = 0;
            windowTicks = 0;
        }

        private static string Ms(long ticks)
        {
            return (ticks * TickToMs).ToString("F3", CultureInfo.InvariantCulture);
        }

        private static void Report()
        {
            var text = new StringBuilder(3800);
            text.Append(Prefix).Append("version=9 occupancy=removed replay=removed native_execution=separate frame=").Append(peak.number)
                .Append(" ticks=").Append(peak.firstTick).Append("..").Append(peak.lastTick)
                .Append(" frame_ms=").Append(Ms(peak.total))
                .Append(" root_calls=").Append(peak.rootCalls)
                .Append(" max_call_ms=").Append(Ms(peak.maxCall))
                .Append(" pawn_id=").Append(peak.pawnId).Append(" job=").Append(peak.job)
                .Append(" window_active_frames=").Append(windowFrames)
                .Append(" window_avg_ms=").Append(Ms(windowTicks / Math.Max(1, windowFrames)))
                .Append(" frames_ge_1ms=").Append(windowHighFrames)
                .Append(" self_ms{");
            bool separator = false;
            for (int i = 0; i < (int)Part.Count; i++)
            {
                if (peak.calls[i] == 0) continue;
                if (separator) text.Append(',');
                separator = true;
                text.Append((Part)i).Append('=').Append(Ms(peak.self[i]))
                    .Append('/').Append(peak.calls[i]);
            }
            text.Append("} inclusive_ms{Cycle=").Append(Ms(peak.inclusive[(int)Part.Cycle]))
                .Append(",Slot=").Append(Ms(peak.inclusive[(int)Part.Slot]))
                .Append(",Dispatch=").Append(Ms(peak.inclusive[(int)Part.Dispatch]))
                .Append(",CloseHit=").Append(Ms(peak.inclusive[(int)Part.CloseHit]))
                .Append(",Warmup=").Append(Ms(peak.inclusive[(int)Part.Warmup]))
                .Append(",Cast=").Append(Ms(peak.inclusive[(int)Part.Cast]))
                .Append(",Damage=").Append(Ms(peak.inclusive[(int)Part.Damage]))
                .Append(",DamagePre=").Append(Ms(peak.inclusive[(int)Part.DamagePre]))
                .Append(",Injury=").Append(Ms(peak.inclusive[(int)Part.Injury]))
                .Append(",InjuryAdd=").Append(Ms(peak.inclusive[(int)Part.InjuryAdd]))
                .Append(",HealthState=").Append(Ms(peak.inclusive[(int)Part.HealthState]))
                .Append(",Death=").Append(Ms(peak.inclusive[(int)Part.Death]))
                .Append(",DeathThoughts=").Append(Ms(peak.inclusive[(int)Part.DeathThoughts]))
                .Append(",DamagePost=").Append(Ms(peak.inclusive[(int)Part.DamagePost]))
                .Append(",LogText=").Append(Ms(peak.inclusive[(int)Part.LogText]))
                .Append(",Stance=").Append(Ms(peak.inclusive[(int)Part.Stance]))
                .Append(",BattleLog=").Append(Ms(peak.inclusive[(int)Part.BattleLog]))
                .Append("} checks=").Append(peak.checks)
                .Append(" rejected=").Append(peak.rejected).Append(" removed=").Append(peak.removed)
                .Append(" search_start/advance=").Append(peak.searchStarts).Append('/').Append(peak.searchSteps)
                .Append(" dispatch_attempt/queued/close/intercept=").Append(peak.shots).Append('/')
                .Append(peak.fired).Append('/').Append(peak.closeShots).Append('/').Append(peak.interceptionShots)
                .Append(" gc_frame=").Append(peak.gc0).Append('/').Append(peak.gc1).Append('/').Append(peak.gc2)
                .Append(" errors=").Append(peak.errors);
            if (peak.detailMethod != null)
            {
                text.Append(" detail_self_ms=").Append(Ms(peak.detailSelf))
                    .Append(" detail_method=").Append(peak.detailMethod.DeclaringType?.FullName)
                    .Append(':').Append(peak.detailMethod.Name)
                    .Append(" detail_root_pawn_id=").Append(peak.detailPawnId)
                    .Append(" detail_tick=").Append(peak.detailTick)
                    .Append(" detail_death_pawn_id=").Append(peak.detailDeathPawnId)
                    .Append(" active_shot_id=").Append(peak.detailShot.sequence)
                    .Append(" shot_tick=").Append(peak.detailShot.tick)
                    .Append(" shot_pawn_id=").Append(peak.detailShot.pawnId)
                    .Append(" shot_target_id=").Append(peak.detailShot.targetId)
                    .Append(" shot_weapon=").Append(peak.detailShot.weaponDef)
                    .Append('#').Append(peak.detailShot.weaponId)
                    .Append(" shot_moving/close/intercept=")
                    .Append((peak.detailShot.flags & 1) != 0 ? 1 : 0).Append('/')
                    .Append((peak.detailShot.flags & 2) != 0 ? 1 : 0).Append('/')
                    .Append((peak.detailShot.flags & 4) != 0 ? 1 : 0);
                text.Append(" damage_id=").Append(peak.detailDamage.sequence)
                    .Append(" actual_recipient_id=").Append(peak.detailDamage.recipientId)
                    .Append(" actual_recipient_def=").Append(peak.detailDamage.recipientDef)
                    .Append(" actual_recipient_category=").Append(peak.detailDamage.recipientCategory);
                if (peak.detailDamage.sequence != 0)
                {
                    text.Append(" damage_incoming_def=").Append(peak.detailDamage.damageDef)
                        .Append(" damage_incoming_amount=")
                        .Append(peak.detailDamage.incomingAmount.ToString("F3", CultureInfo.InvariantCulture))
                        .Append(" damage_instigator_id=").Append(peak.detailDamage.instigatorId);
                }
            }
            AppendHealthSample(text, "injury_add", peak.health[0]);
            AppendHealthSample(text, "health_state", peak.health[1]);
            text.Append(" mod_death_tree_ms=").Append(Ms(peak.modDeathTreeTicks))
                .Append(" mod_death_tree_calls=").Append(peak.modDeathTreeCalls);
            if (peak.modDeathMethod != null)
            {
                text.Append(" mod_death_max_tree_ms=").Append(Ms(peak.modDeathMaxTicks))
                    .Append(" mod_death_method=").Append(peak.modDeathMethod.DeclaringType?.FullName)
                    .Append(':').Append(peak.modDeathMethod.Name)
                    .Append(" mod_death_cause_pawn_id=").Append(peak.modDeathPawnId)
                    .Append(" mod_death_shot_id=").Append(peak.modDeathShotId)
                    .Append(" mod_death_tick=").Append(peak.modDeathTick);
            }
            text.Append(" death_thought_inclusive_ms{");
            separator = false;
            for (int i = (int)Part.RemoveLost; i < (int)Part.Count; i++)
            {
                if (peak.calls[i] == 0) continue;
                if (separator) text.Append(',');
                separator = true;
                text.Append((Part)i).Append('=').Append(Ms(peak.inclusive[i]));
            }
            text.Append('}');
            Log.Message(text.ToString());
        }

        private static void ReportDeathSample()
        {
            FrameData sample = deathSampleFrame;
            var text = new StringBuilder(4000);
            text.Append("[RimKata.DraftedFireProbe.Death] version=9 occupancy=removed replay=removed native_execution=separate frame=")
                .Append(sample.number).Append(" ticks=").Append(sample.firstTick)
                .Append("..").Append(sample.lastTick)
                .Append(" sampled_frame_ms=").Append(Ms(sample.total))
                .Append(" death_frame_self_ms{");
            bool separator = false;
            for (int i = (int)Part.Death; i < (int)Part.Count; i++)
            {
                if (sample.calls[i] == 0) continue;
                if (separator) text.Append(',');
                separator = true;
                text.Append((Part)i).Append('=').Append(Ms(sample.self[i]))
                    .Append('/').Append(sample.calls[i]);
            }
            text.Append("} death_frame_inclusive_ms{");
            separator = false;
            for (int i = (int)Part.Death; i < (int)Part.Count; i++)
            {
                if (sample.calls[i] == 0) continue;
                if (separator) text.Append(',');
                separator = true;
                text.Append((Part)i).Append('=').Append(Ms(sample.inclusive[i]));
            }
            text.Append('}');
            AppendDeathTrace(text, sample.deathTrace);
            Log.Message(text.ToString());
        }

        private static void AppendDeathTrace(StringBuilder text, DeathTraceSample sample)
        {
            if (!sample.recorded) return;
            text.Append(" death_trace_tick=").Append(sample.tick)
                .Append(" death_trace_root_pawn_id=").Append(sample.rootPawnId)
                .Append(" death_trace_pawn_id=").Append(sample.deathPawnId)
                .Append(" death_trace_shot_id=").Append(sample.shot.sequence)
                .Append(" death_trace_shooter_id=").Append(sample.shot.pawnId)
                .Append(" death_trace_target_id=").Append(sample.shot.targetId)
                .Append(" death_trace_weapon=").Append(sample.shot.weaponDef)
                .Append('#').Append(sample.shot.weaponId)
                .Append(" death_trace_damage_id=").Append(sample.damage.sequence)
                .Append(" death_trace_recipient_id=").Append(sample.damage.recipientId)
                .Append(" death_trace_drop_ms=").Append(Ms(sample.elapsed))
                .Append(" death_trace_method=Verse.Pawn:DropBeforeDying")
                .Append(" death_trace_capture_ms=").Append(Ms(sample.captureTicks))
                .Append(" death_trace_failure=").Append(sample.failure ?? "none")
                .Append(" death_trace{");
            // Format only after the measured frame has ended. No Pawn/Verb
            // references or source-file information are retained by the trace.
            int count = sample.trace?.FrameCount ?? 0;
            for (int i = 0; i < count && i < 64; i++)
            {
                if (i > 0) text.Append(" <- ");
                MethodBase method = sample.trace.GetFrame(i)?.GetMethod();
                text.Append(method?.DeclaringType?.FullName ?? "<dynamic>")
                    .Append(':').Append(method?.Name ?? "<unknown>");
            }
            if (count > 64) text.Append(" <- <truncated>");
            text.Append('}');
        }

        private static string HealthStateName(byte flags)
            => (flags & 2) != 0 ? "Dead" : (flags & 1) != 0 ? "Downed" : "Alive";

        private static void AppendHealthSample(StringBuilder text, string name, HealthSample sample)
        {
            if (!sample.recorded) return;
            text.Append(' ').Append(name).Append("_self_ms=").Append(Ms(sample.self))
                .Append(' ').Append(name).Append("_tick=").Append(sample.tick)
                .Append(' ').Append(name).Append("_pawn_id=").Append(sample.pawnId)
                .Append(' ').Append(name).Append("_damage_id=").Append(sample.damageId)
                .Append(' ').Append(name).Append("_shot_id=").Append(sample.shotId)
                .Append(' ').Append(name).Append("_hediff=").Append(sample.hediffDef)
                .Append(' ').Append(name).Append("_before=").Append(HealthStateName(sample.before))
                .Append(' ').Append(name).Append("_after=").Append(HealthStateName(sample.after))
                .Append(' ').Append(name).Append("_hediffs_before=").Append(sample.beforeCount)
                .Append(' ').Append(name).Append("_hediffs_after=").Append(sample.afterCount)
                .Append(' ').Append(name).Append("_failed=").Append(sample.failed ? 1 : 0);
        }

        internal static IEnumerable<MethodBase> TimingTargets()
        {
            if (parts.Count == 0)
            {
                Type controller = typeof(RimKataDualWeaponController);
                Add(controller, "PrepareWeaponCycleTick", 3, Part.Prepare);
                Add(controller, "TickPreparedWeaponCycles", 8, Part.Cycle);
                Add(typeof(JobDriver_RimKataAttack), "TickPreparedCombat", 5, Part.NestedJob);
                Add(controller, "ResolveCloseTarget", 7, Part.Close);
                Add(controller, "HandleCloseCombatTransition", 5, Part.Close);
                Add(controller, "SanitizeCycleForCloseCombat", 3, Part.Close);
                Add(controller, "NormalizeUnavailableCycleWork", 5, Part.Normalize);
                Add(typeof(RimKataSharedTargetSearch), "Begin", 3, Part.Search);
                Add(typeof(RimKataSharedTargetSearch), "Advance", 3, Part.Search);
                Add(typeof(RimKataSharedTargetSearch), "CollectAutomaticTargetsInRing", 2, Part.SearchCollect);
                Add(typeof(RimKataSharedTargetSearch), "ProcessNextBufferedCandidate", 7, Part.SearchBuffer);
                Add(typeof(RimKataSharedTargetSearch), "TrySelectCandidate", 10, Part.Select);
                Add(controller, "TryCacheSharedCandidate", 7, Part.Reserve);
                Add(controller, "TickWeaponCycle", 14, Part.Slot);
                Add(controller, "RefreshDualEngagementState", 3, Part.Continuity);
                Add(controller, "UpdateBodyAimStance", 2, Part.Aim);
                Add(typeof(Battle), "Add", 1, Part.BattleLog);
                // Deep native hooks only open clocks inside Dispatch, CloseHit or Aim.
                // Include inherited melee paths as well as projectile overrides.
                Add(typeof(Verb), "Available", 0, Part.Available);
                Add(typeof(Verb_LaunchProjectile), "Available", 0, Part.Available);
                Add(typeof(Verb), "CanHitTarget", 1, Part.CanHit);
                Add(typeof(RimKataFireContext), "Begin", 10, Part.FireContext);
                Add(typeof(RimKataFireContext), "End", 2, Part.FireContext);
                Add(typeof(Verb), "WarmupComplete", 0, Part.Warmup);
                Add(typeof(Verb_LaunchProjectile), "WarmupComplete", 0, Part.Warmup);
                Add(typeof(Verb), "TryCastNextBurstShot", 0, Part.Burst);
                Add(typeof(Verb_LaunchProjectile), "TryCastShot", 0, Part.Cast);
                Add(typeof(RimWorld.Verb_MeleeAttack), "TryCastShot", 0, Part.Cast);
                Add(typeof(Verse.Sound.SoundStarter), "PlayOneShot", 2, Part.Sound);
                Add(typeof(Pawn_StanceTracker), "SetStance", 1, Part.Stance);
                Add(typeof(Verse.AI.JobDriver_Wait), "CheckForAutoAttack", 0, Part.AutoAttack);
                Add(typeof(RimWorld.Verb_MeleeAttackDamage), "ApplyMeleeDamageToTarget", 1, Part.MeleeDamage);
                // TakeDamage/AssociateWithLog have typed patches below to capture
                // the actual recipient and match the returned result by identity.
                Add(typeof(Pawn), "PreApplyDamage", 2, Part.DamagePre);
                Add(typeof(DamageWorker_AddInjury), "Apply", 2, Part.Injury);
                Add(typeof(Pawn), "PostApplyDamage", 2, Part.DamagePost);
                Add(typeof(LogEntry), "ToGameStringFromPOV", 2, Part.LogText);
                Add(typeof(GenClamor), "DoClamor", new[]
                    { typeof(Thing), typeof(IntVec3), typeof(float), typeof(ClamorDef) }, Part.Clamor);
                Add(typeof(RimKataProjectileUtility), "Impact", 3, Part.ProjectileImpact);
                Add(controller, "NotifyDefensiveCombatEvent", 2, Part.DefenseNotify);
                Add(typeof(Pawn), "DoKillSideEffects", 3, Part.DeathEffects);
                Add(typeof(Pawn), "PreDeathPawnModifications", 2, Part.DeathModify);
                Add(typeof(Pawn), "DropBeforeDying", 3, Part.DeathThoughts);
                Add(typeof(Thing), "DeSpawnOrDeselect", 1, Part.DeathDespawn);
                Add(typeof(Pawn), "MakeCorpse", 3, Part.DeathCorpse);
                // Audited helper roots, not a claim to cover every patch dispatch
                // or every possible contribution from this or another mod.
                Add(typeof(RimKataWeaponSlotUtility), "NotifyEquipmentChanged", 3, Part.ModDeath);
                Add(typeof(RimKataWeaponSlotUtility), "InvalidateCombatVerbCache", 1, Part.ModDeath);
                Add(typeof(RimKataMapComponent), "NotifyThingDespawned", 1, Part.ModDeath);
                Add(typeof(RimKataMapComponent), "NotifyThingSpawned", 1, Part.ModDeath);
                Add(typeof(RimKataEligibilityCache), "InvalidateRelations", 1, Part.ModDeath);
                Add(typeof(RimKataEligibilityCache), "InvalidateHediff", 2, Part.ModDeath);
                Add(typeof(RimKataEligibilityCache), "InvalidateGenes", 1, Part.ModDeath);
                Add(typeof(RimKataEligibilityCache), "InvalidateRole", 1, Part.ModDeath);
                Add(typeof(JobDriver_RimKataAttack), "ClearAimStance", 0, Part.ModDeath);
                Add(controller, "NotifyDedicatedCombatJobFinished", 1, Part.ModDeath);
                Add(typeof(RimKataSecondaryWeaponRegistry), "NotifySecondaryRecoveryJobFinished", 3, Part.ModDeath);
                Add(typeof(ThoughtWorker_MindNumbSerumWithdrawal), "CurrentStateInternal", 1, Part.ModDeath);
                Add(typeof(ThoughtWorker_MindNumbSerumEmotionRemoval), "CurrentStateInternal", 1, Part.ModDeath);
                Add(typeof(ThoughtWorker_MindNumbSerumDependencyOvercome), "CurrentStateInternal", 1, Part.ModDeath);
                Add(typeof(Thought_MindNumbSerumWithdrawal), "MoodOffset", 0, Part.ModDeath);
                // Descendants are timed only while a measured DropBeforeDying
                // is active. Do not patch generic lists or per-memory predicates.
                Type deathThoughts = typeof(RimWorld.PawnDiedOrDownedThoughtsUtility);
                Add(deathThoughts, "RemoveLostThoughts", 1, Part.RemoveLost);
                Add(deathThoughts, "RemoveResuedRelativeThought", 1, Part.RemoveRescued);
                Add(deathThoughts, "TryGiveThoughts", 3, Part.GiveDeathThoughts);
                Add(deathThoughts, "GetThoughts", 5, Part.GetDeathThoughts);
                Add(deathThoughts, "AppendThoughts_ForHumanlike", 5, Part.HumanDeathThoughts);
                Add(deathThoughts, "AppendThoughts_Relations", 5, Part.RelationDeathThoughts);
                Add(deathThoughts, "GiveVeneratedAnimalDiedThoughts", 2, Part.VeneratedDeathThoughts);
                Type finder = typeof(RimWorld.PawnsFinder);
                Add(finder, "get_AllMapsWorldAndTemporary_Alive", 0, Part.WorldPawnList);
                Add(finder, "get_AllMapsWorldAndTemporary_AliveOrDead", 0, Part.WorldPawnListBuild);
                Add(finder, "get_AllMapsCaravansAndTravellingTransporters_Alive", 0, Part.MapPawnList);
                Add(finder, "get_AllMapsCaravansAndTravellingTransporters_Alive_Colonists", 0, Part.ColonistPawnList);
                Type relations = typeof(RimWorld.Pawn_RelationsTracker);
                Add(relations, "get_PotentiallyRelatedPawns", 0, Part.RelatedPawns);
                AddIterator(relations, "get_PotentiallyRelatedPawns", Part.RelatedPawnsNext);
                Add(relations, "get_FamilyByBlood", 0, Part.FamilyPawns);
                AddIterator(relations, "get_FamilyByBlood_Internal", Part.FamilyPawnsNext);
                Add(relations, "OpinionOf", 1, Part.PawnOpinion);
                Type relationUtility = typeof(RimWorld.PawnRelationUtility);
                Add(relationUtility, "GetMostImportantRelation", 2, Part.ImportantRelation);
                AddIterator(relationUtility, "GetRelations", Part.RelationsNext);
                Type memories = typeof(RimWorld.MemoryThoughtHandler);
                Add(memories, "RemoveMemoriesOfDefWhereOtherPawnIs", 2, Part.RemoveOtherMemories);
                Add(memories, "RemoveMemory", 1, Part.RemoveMemory);
                Add(memories, "TryGainMemory", new[]
                    { typeof(RimWorld.ThoughtDef), typeof(Pawn), typeof(RimWorld.Precept) }, Part.GainMemoryDef);
                Add(memories, "TryGainMemory", new[]
                    { typeof(RimWorld.Thought_Memory), typeof(Pawn) }, Part.GainMemory);
                Add(memories, "NumMemoriesInGroup", 1, Part.GroupMemoryCount);
                Add(memories, "OldestMemoryInGroup", 1, Part.GroupMemoryOldest);
                Add(memories, "NumMemoriesOfDef", 1, Part.DefMemoryCount);
                Add(memories, "OldestMemoryOfDef", 1, Part.DefMemoryOldest);
                Add(typeof(RimWorld.Thought_Memory), "TryMergeWithExistingMemory", 1, Part.MergeMemory);
                Add(typeof(RimWorld.Thought_MemorySocial), "TryMergeWithExistingMemory", 1, Part.MergeMemory);
                Add(typeof(RimWorld.IndividualThoughtToAdd), "Add", 0, Part.AddIndividualThought);
                Add(typeof(RimWorld.ThoughtToAddToAll), "Add", 1, Part.AddAllThought);
                Add(typeof(RimWorld.ThoughtMaker), "MakeThought", 1, Part.MakeThought);
                Add(typeof(RimWorld.ThoughtMaker), "MakeThought", new[]
                    { typeof(RimWorld.ThoughtDef), typeof(RimWorld.Precept) }, Part.MakeThought);
                Add(typeof(RimWorld.ThoughtUtility), "CanGetThought", 3, Part.CanGetThought);
                Add(typeof(RimWorld.ThoughtUtility), "Witnessed", 2, Part.WitnessedDeath);
                Add(typeof(RimWorld.PawnUtility), "ShouldGetThoughtAbout", 2, Part.ShouldGetThoughtAbout);
                Add(typeof(GenSight), "LineOfSight", new[]
                    { typeof(IntVec3), typeof(IntVec3), typeof(Map) }, Part.ThoughtLineOfSight);
                Type thoughts = typeof(RimWorld.ThoughtHandler);
                Add(thoughts, "TotalOpinionOffset", 1, Part.TotalOpinion);
                Add(thoughts, "GetSocialThoughts", 2, Part.SocialThoughts);
                Add(thoughts, "GetSocialThoughts", 3, Part.SocialGroupFilter);
                Add(thoughts, "GetDistinctSocialThoughtGroups", 2, Part.SocialGroups);
                Add(thoughts, "OpinionOffsetOfGroup", 2, Part.GroupOpinion);
                Log.Message(Prefix + "enabled version=9; occupancy=removed replay=removed; qualified ProcessJobTrackerTick trees only; "
                    + "frame threshold=1.000ms; self_ms entries are exclusive ms/calls; "
                    + "inclusive_ms entries overlap; window averages use active sampled frames; "
                    + "outside JobDriver work excluded, synchronous nested Job work included; "
                    + "Dispatch measures request setup only; queued does not mean fired; native execution uses separate NativeFireProbe rows; "
                    + "deep scopes only inside Dispatch/CloseHit/Aim; detail_self_ms is the worst single exclusive call; "
                    + "active_shot_id is frame-local, zero means outside a shot; shot_target_id is the shot target, "
                    + "not an asserted damage recipient; nested same-category inclusive times overlap too; "
                    + "damage_id is frame-local; damage_incoming_def/amount are the original input before mitigation; "
                    + "actual_recipient describes the measured damage, not the shot target; "
                    + "damage_id=0 means no matched packet and recipient_id=0 means unknown; "
                    + "LogText is scoped to damage-log association; "
                    + "InjuryAdd/HealthState are scoped to Injury; each keeps its own worst exclusive call "
                    + "with Pawn state and hediff count before/after; Alive means not downed/dead; "
                    + "nested observations are not unique injury/death counts; "
                    + "Death stages and ModDeath helper clocks run only inside a measured Pawn.Kill; "
                    + "DeathPlace measures only the active victim's corpse, not dropped items; "
                    + "mod_death_tree_ms counts audited outermost RimKata callback subtrees once, "
                    + "including their descendants; it overlaps stage totals and is not all mod overhead; "
                    + "mod_death_cause_pawn_id is the death victim, not necessarily the helper's receiver; "
                    + "first crossing then worst frame per 1s; "
                    + "death-thought descendants, including relation iterator MoveNext, run only inside measured DropBeforeDying; "
                    + "death_thought_inclusive_ms overlaps self_ms and parent totals; "
                    + "the separate DraftedFireProbe.Death row retains the first DropBeforeDying >=1ms per reporting window even if a non-death frame becomes the peak; "
                    + "death-frame child totals cover that entire frame, while death_trace IDs identify one invocation; "
                    + "death_trace is the caller chain at DropBeforeDying completion (not returned child frames), capped at64 frames; "
                    + "stack capture cost is reported separately and excluded from active ancestor clocks; other timing includes instrumentation overhead.");
            }
            return parts.Keys;
        }

        private static void AddIterator(Type type, string name, Part part)
        {
            MethodInfo factory = AccessTools.DeclaredMethod(type, name);
            Type iterator = factory?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            MethodInfo moveNext = iterator == null ? null
                : AccessTools.DeclaredMethod(iterator, "MoveNext", Type.EmptyTypes);
            if (moveNext == null || moveNext.ReturnType != typeof(bool))
            {
                Log.Warning(Prefix + "missing iterator body " + type.Name + "." + name);
                return;
            }
            parts.Add(moveNext, part);
        }

        private static void Add(Type type, string name, int parameters, Part part)
        {
            MethodBase found = null;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.Name != name || method.GetParameters().Length != parameters) continue;
                if (found != null)
                {
                    Log.Warning(Prefix + "skipped ambiguous target " + type.Name + "." + name);
                    return;
                }
                found = method;
            }
            if (found == null)
            {
                Log.Warning(Prefix + "missing target " + type.Name + "." + name);
                return;
            }
            parts.Add(found, part);
        }

        private static void Add(Type type, string name, Type[] parameters, Part part)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null)
            {
                Log.Warning(Prefix + "missing exact target " + type.Name + "." + name);
                return;
            }
            parts.Add(method, part);
        }
    }

    [HarmonyPatch(typeof(RimKataDraftedFireController), nameof(RimKataDraftedFireController.ProcessJobTrackerTick))]
    internal static class RimKataProbeRootPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Pawn pawn, out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterRoot(pawn);
        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(RimKataPerformanceProbe.Scope __state, Exception __exception)
            => RimKataPerformanceProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch]
    internal static class RimKataProbePartsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => RimKataPerformanceProbe.TimingTargets();
        private static void Prefix(MethodBase __originalMethod, out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterPart(__originalMethod);
        private static void Finalizer(RimKataPerformanceProbe.Scope __state, Exception __exception)
            => RimKataPerformanceProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class RimKataProbeDamagePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(MethodBase __originalMethod, Thing __instance, DamageInfo __0,
            out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterDamage(__originalMethod, __instance, __0);
        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(RimKataPerformanceProbe.Scope __state,
            DamageWorker.DamageResult __result, Exception __exception)
            => RimKataPerformanceProbe.ExitDamage(__state, __result, __exception != null);
    }

    [HarmonyPatch(typeof(DamageWorker.DamageResult), nameof(DamageWorker.DamageResult.AssociateWithLog))]
    internal static class RimKataProbeDamageLogPatch
    {
        private static void Prefix(MethodBase __originalMethod, DamageWorker.DamageResult __instance,
            out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterDamageLog(__originalMethod, __instance);
        private static void Finalizer(RimKataPerformanceProbe.Scope __state, Exception __exception)
            => RimKataPerformanceProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch(typeof(HediffSet), nameof(HediffSet.AddDirect))]
    internal static class RimKataProbeInjuryAddPatch
    {
        private static void Prefix(MethodBase __originalMethod, Pawn ___pawn, Hediff __0,
            out RimKataPerformanceProbe.HealthScope __state)
            => __state = RimKataPerformanceProbe.EnterHealth(__originalMethod, ___pawn, __0,
                RimKataPerformanceProbe.Part.InjuryAdd);
        private static void Finalizer(RimKataPerformanceProbe.HealthScope __state, Exception __exception)
            => RimKataPerformanceProbe.ExitHealth(__state, __exception != null);
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.CheckForStateChange))]
    internal static class RimKataProbeHealthStatePatch
    {
        private static void Prefix(MethodBase __originalMethod, Pawn ___pawn, Hediff __1,
            out RimKataPerformanceProbe.HealthScope __state)
            => __state = RimKataPerformanceProbe.EnterHealth(__originalMethod, ___pawn, __1,
                RimKataPerformanceProbe.Part.HealthState);
        private static void Finalizer(RimKataPerformanceProbe.HealthScope __state, Exception __exception)
            => RimKataPerformanceProbe.ExitHealth(__state, __exception != null);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    internal static class RimKataProbeDeathPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(MethodBase __originalMethod, Pawn __instance,
            out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterDeath(__originalMethod, __instance);
        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(RimKataPerformanceProbe.Scope __state, Exception __exception)
            => RimKataPerformanceProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch]
    internal static class RimKataProbeCorpsePlacementPatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.DeclaredMethod(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[]
            {
                typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode),
                typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>),
                typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int)
            });
        private static void Prefix(MethodBase __originalMethod, Thing __0,
            out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterCorpsePlacement(__originalMethod, __0);
        private static void Finalizer(RimKataPerformanceProbe.Scope __state, Exception __exception)
            => RimKataPerformanceProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.Queue))]
    internal static class RimKataProbeFirePatch
    {
        private static void Prefix(MethodBase __originalMethod, RimKataNativeAttack __instance,
            out RimKataPerformanceProbe.Scope __state)
            => __state = RimKataPerformanceProbe.EnterDispatch(__originalMethod, __instance.verb,
                __instance.target, __instance.movingShot, __instance.closeShot, __instance.interceptionShot);
        private static void Postfix(bool __result) => RimKataPerformanceProbe.CountQueued(__result);
        private static void Finalizer(RimKataPerformanceProbe.Scope __state, Exception __exception)
            => RimKataPerformanceProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch]
    internal static class RimKataProbeCountPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = typeof(RimKataSharedTargetSearch);
            foreach (string name in new[] { "CanShootRegisteredCandidate", "RemoveAutomaticCandidateFromCycle", "Begin", "Advance" })
            {
                MethodInfo method = AccessTools.DeclaredMethod(type, name);
                if (method != null) yield return method;
            }
        }
        private static void Postfix(MethodBase __originalMethod, bool __result)
            => RimKataPerformanceProbe.CountResult(__originalMethod, __result);
    }

    [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
    internal static class RimKataProbeFramePatch
    {
        private static void Prefix() => RimKataPerformanceProbe.BeginFrame();
        private static void Postfix()
        {
            RimKataPerformanceProbe.CompleteFrame();
            RimKataSearchProbe.CompleteFrame();
            RimKataNativeFireProbe.CompleteFrame();
        }
    }

    [HarmonyPatch(typeof(Current), nameof(Current.Game), MethodType.Setter)]
    internal static class RimKataProbeGamePatch
    {
        private static void Prefix(Game __0)
        {
            if (!ReferenceEquals(Current.Game, __0)) RimKataPerformanceProbe.Reset();
        }
    }

    // The native owner executes outside JobTracker. Keep that duration in a
    // separate window, never in the drafted frame accumulator or timing stack.
    internal static class RimKataNativeFireProbe
    {
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
        private static int generation, frames, activeFrames, calls, acted, errors;
        private static int lastFrame = -1, lastActiveFrame = -1, firstTick = -1, lastTick = -1;
        private static long windowStart, elapsed, maximum;
        private static Timer maximumContext;
        [ThreadStatic] private static bool measuring;

        internal struct Timer
        {
            internal bool entered;
            internal int generation, tick, pawnId, targetId, weaponId;
            internal long start;
            internal string weaponDef;
            internal MethodBase method;
        }

        internal static void Reset()
        {
            generation++;
            measuring = false;
            ClearWindow();
        }

        private static void ClearWindow()
        {
            frames = activeFrames = calls = acted = errors = 0;
            lastFrame = lastActiveFrame = firstTick = lastTick = -1;
            windowStart = elapsed = maximum = 0;
            maximumContext = default;
        }

        internal static void BeginFrame()
        {
            if (Current.Game == null) return;
            if (windowStart == 0) windowStart = Stopwatch.GetTimestamp();
            int number = Time.frameCount;
            if (lastFrame == number) return;
            lastFrame = number;
            frames++;
        }

        internal static Timer Enter(MethodBase method, Verb verb)
        {
            // Only an actual queued native cast opens FireContext. Inherited
            // WarmupComplete calls remain inside the one outer execution sample.
            if (measuring || RimKataFireContext.ActiveVerb != verb || Current.Game == null)
                return default;
            BeginFrame();
            measuring = true;
            int tick = Find.TickManager?.TicksGame ?? -1;
            if (firstTick < 0) firstTick = tick;
            lastTick = tick;
            if (lastActiveFrame != Time.frameCount)
            {
                lastActiveFrame = Time.frameCount;
                activeFrames++;
            }
            Thing weapon = verb.EquipmentSource;
            Timer timer = new Timer
            {
                entered = true,
                generation = generation,
                tick = tick,
                pawnId = verb.CasterPawn?.thingIDNumber ?? 0,
                targetId = verb.CurrentTarget.Thing?.thingIDNumber ?? 0,
                weaponId = weapon?.thingIDNumber ?? 0,
                weaponDef = weapon?.def?.defName,
                method = method
            };
            timer.start = Stopwatch.GetTimestamp();
            return timer;
        }

        internal static void Exit(Timer timer, bool failed)
        {
            if (!timer.entered || timer.generation != generation) return;
            long duration = Math.Max(0, Stopwatch.GetTimestamp() - timer.start);
            measuring = false;
            calls++;
            elapsed += duration;
            if (failed) errors++;
            else if (RimKataFireContext.ShotFired) acted++;
            if (duration > maximum)
            {
                maximum = duration;
                maximumContext = timer;
            }
        }

        internal static void CompleteFrame()
        {
            if (measuring || windowStart == 0 || Current.Game == null) return;
            if (Stopwatch.GetTimestamp() - windowStart < Stopwatch.Frequency) return;
            if (calls > 0)
            {
                string Ms(double ticks) => (ticks * TickToMs).ToString("F3", CultureInfo.InvariantCulture);
                Timer context = maximumContext;
                Log.Message("[RimKata.NativeFireProbe] version=1 owner=VerbTick scope=outer_WarmupComplete "
                    + "included_in_drafted_total=0 context_setup_and_completion=excluded ticks=" + firstTick + ".." + lastTick
                    + " frames=" + frames + " active_frames=" + activeFrames
                    + " total_ms=" + Ms(elapsed) + " avg_frame_ms=" + Ms(elapsed / (double)Math.Max(1, frames))
                    + " avg_active_frame_ms=" + Ms(elapsed / (double)Math.Max(1, activeFrames))
                    + " calls=" + calls + " acted=" + acted + " errors=" + errors
                    + " avg_call_ms=" + Ms(elapsed / (double)Math.Max(1, calls)) + " max_call_ms=" + Ms(maximum)
                    + " max_tick=" + context.tick + " pawn_id=" + context.pawnId + " target_id=" + context.targetId
                    + " weapon=" + context.weaponDef + "#" + context.weaponId
                    + " method=" + context.method?.DeclaringType?.FullName + ":" + context.method?.Name);
            }
            ClearWindow();
        }
    }

    [HarmonyPatch]
    internal static class RimKataProbeNativeFirePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
            => Patch_Verb_WarmupComplete_RimKataOpeningSingleShot.TargetMethods();

        // Begin after the gameplay context was installed and finish before it
        // completes the cycle. Removing this source removes the entire observer.
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(MethodBase __originalMethod, Verb __instance,
            out RimKataNativeFireProbe.Timer __state)
            => __state = RimKataNativeFireProbe.Enter(__originalMethod, __instance);

        [HarmonyPriority(Priority.First)]
        private static void Finalizer(RimKataNativeFireProbe.Timer __state, Exception __exception)
            => RimKataNativeFireProbe.Exit(__state, __exception != null);
    }

    // Independent windows include collection and buffer work outside JobTracker.
    // Only real collection call sites are counted; no replay or occupancy hooks.
    internal static class RimKataSearchProbe
    {
        private static readonly long[] calls = new long[(int)Metric.Count];
        private static readonly long[] elapsed = new long[(int)Metric.Count];
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
        private static bool enabled, announced, hadSearch;
        private static int generation, frames, searchFrames, firstTick;
        private static long windowStart, offsets, inMap, lists, listEntries, uniquePawns;
        private static long rings, draws, accepted, failures;

        internal static bool CellCountersAvailable;
        internal enum Metric { Collection, Buffer, Count }

        internal struct Timer
        {
            internal long start;
            internal Metric metric;
            internal bool entered;
            internal int generation, beforeCount;
        }

        internal static void Reset()
        {
            generation++;
            enabled = hadSearch = false;
            ClearWindow();
        }

        private static void ClearWindow()
        {
            Array.Clear(calls, 0, calls.Length);
            Array.Clear(elapsed, 0, elapsed.Length);
            frames = searchFrames = 0;
            offsets = inMap = lists = listEntries = uniquePawns = rings = 0;
            draws = accepted = failures = 0;
            windowStart = 0;
            firstTick = -1;
        }

        internal static void BeginFrame()
        {
            enabled = Current.Game != null;
            if (!enabled || windowStart != 0) return;
            windowStart = Stopwatch.GetTimestamp();
            firstTick = Find.TickManager?.TicksGame ?? -1;
        }

        internal static Timer Enter(Metric metric)
        {
            if (!enabled) return default;
            calls[(int)metric]++;
            hadSearch = true;
            return new Timer
            {
                metric = metric,
                entered = true,
                generation = generation,
                start = Stopwatch.GetTimestamp()
            };
        }

        internal static void Exit(Timer timer, bool failed)
        {
            if (!timer.entered || timer.generation != generation) return;
            elapsed[(int)timer.metric] += Math.Max(0, Stopwatch.GetTimestamp() - timer.start);
            if (failed) failures++;
        }

        internal static Timer EnterCollection(RimKataRingSearchRuntime runtime)
        {
            Timer timer = Enter(Metric.Collection);
            if (timer.entered) timer.beforeCount = runtime.discovered.Count;
            return timer;
        }

        internal static void ExitCollection(Timer timer, RimKataRingSearchRuntime runtime, bool failed)
        {
            Exit(timer, failed);
            if (!timer.entered || timer.generation != generation) return;
            uniquePawns += runtime.discovered.Count - timer.beforeCount;
            if (runtime.collectionComplete) rings++;
        }

        // Each wrapper invokes the original call exactly once and returns its result.
        // There are no per-cell clocks or additional grid lookups.
        internal static bool Next(RimKataRingTraversal traversal, out IntVec3 offset)
        {
            bool result = traversal.TryNext(out offset);
            if (enabled && result) offsets++;
            return result;
        }

        internal static List<Thing> ThingsListAtFast(ThingGrid grid, int index)
        {
            List<Thing> things = grid.ThingsListAtFast(index);
            if (enabled)
            {
                inMap++;
                lists++;
                listEntries += things.Count;
            }
            return things;
        }

        internal static void CountAdmission(bool result)
        {
            if (!enabled) return;
            draws++;
            if (result) accepted++;
        }

        private static string Ms(long ticks) => (ticks * TickToMs).ToString("F6", CultureInfo.InvariantCulture);

        internal static void CompleteFrame()
        {
            if (!enabled || windowStart == 0) return;
            frames++;
            if (hadSearch) searchFrames++;
            hadSearch = false;
            long now = Stopwatch.GetTimestamp();
            if (now - windowStart < Stopwatch.Frequency) return;
            bool work = false;
            for (int i = 0; i < calls.Length; i++) work |= calls[i] != 0;
            if (work)
            {
                if (!announced)
                {
                    announced = true;
                    Log.Message("[RimKata.SearchProbe] enabled version=2; occupancy=removed replay=removed; "
                        + "collection/buffer windows include work outside drafted roots, not peak-only; "
                        + "scope_ms format=sum_ms/samples/calls; every collection/buffer call is timed; "
                        + "cell counters use the actual TryNext and ThingsListAtFast(int) collection call sites; "
                        + "no per-cell clocks or replay; probe/Harmony overhead remains; no total-CPU saving established.");
                }
                var text = new StringBuilder(700);
                text.Append("[RimKata.SearchProbe] version=2 occupancy=removed replay=removed ticks=").Append(firstTick)
                    .Append("..").Append(Find.TickManager?.TicksGame ?? -1)
                    .Append(" wall_ms=").Append(Ms(now - windowStart)).Append(" frames=").Append(frames)
                    .Append(" search_frames=").Append(searchFrames)
                    .Append(" cell_counters=").Append(CellCountersAvailable ? "available" : "unavailable")
                    .Append(" scope_ms{");
                for (int i = 0; i < calls.Length; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append((Metric)i).Append('=').Append(Ms(elapsed[i]))
                        .Append('/').Append(calls[i]).Append('/').Append(calls[i]);
                }
                text.Append("} cells{offsets=").Append(offsets).Append(",in_map=").Append(inMap)
                    .Append(",lists=").Append(lists).Append(",list_entries=").Append(listEntries)
                    .Append(",new_ids=").Append(uniquePawns).Append(",slices_finishing_ring=").Append(rings)
                    .Append("} buffer{draws=").Append(draws).Append(",accepted=").Append(accepted)
                    .Append("} errors=").Append(failures);
                Log.Message(text.ToString());
            }
            ClearWindow();
        }
    }

    [HarmonyPatch(typeof(RimKataSharedTargetSearch), "CollectAutomaticTargetsInRing")]
    internal static class RimKataSearchProbeCollectionPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(RimKataRingSearchRuntime runtime, out RimKataSearchProbe.Timer __state)
            => __state = RimKataSearchProbe.EnterCollection(runtime);
        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(RimKataRingSearchRuntime runtime,
            RimKataSearchProbe.Timer __state, Exception __exception)
            => RimKataSearchProbe.ExitCollection(__state, runtime, __exception != null);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo next = AccessTools.Method(typeof(RimKataRingTraversal), "TryNext",
                new[] { typeof(IntVec3).MakeByRefType() });
            MethodInfo things = AccessTools.Method(typeof(ThingGrid), "ThingsListAtFast", new[] { typeof(int) });
            MethodInfo nextWrapper = AccessTools.Method(typeof(RimKataSearchProbe), "Next",
                new[] { typeof(RimKataRingTraversal), typeof(IntVec3).MakeByRefType() });
            MethodInfo thingsWrapper = AccessTools.Method(typeof(RimKataSearchProbe), "ThingsListAtFast",
                new[] { typeof(ThingGrid), typeof(int) });
            int nextCount = 0, listCount = 0;
            foreach (CodeInstruction item in code)
            {
                if (next != null && item.Calls(next)) nextCount++;
                if (things != null && item.Calls(things)) listCount++;
            }
            RimKataSearchProbe.CellCountersAvailable = nextCount == 1 && listCount == 1
                && nextWrapper != null && thingsWrapper != null;
            if (!RimKataSearchProbe.CellCountersAvailable)
            {
                Log.Warning("[RimKata.SearchProbe] collection call sites changed; cell counters unavailable");
                return code;
            }
            foreach (CodeInstruction item in code)
            {
                if (item.Calls(next))
                {
                    item.opcode = OpCodes.Call;
                    item.operand = nextWrapper;
                }
                else if (item.Calls(things))
                {
                    item.opcode = OpCodes.Call;
                    item.operand = thingsWrapper;
                }
            }
            return code;
        }
    }

    [HarmonyPatch(typeof(RimKataSharedTargetSearch), "ProcessNextBufferedCandidate")]
    internal static class RimKataSearchProbeBufferPatch
    {
        private static void Prefix(out RimKataSearchProbe.Timer __state)
            => __state = RimKataSearchProbe.Enter(RimKataSearchProbe.Metric.Buffer);
        private static void Finalizer(RimKataSearchProbe.Timer __state, Exception __exception)
            => RimKataSearchProbe.Exit(__state, __exception != null);
    }

    [HarmonyPatch(typeof(RimKataSharedTargetSearch), "TryAddBufferedAutomaticTarget")]
    internal static class RimKataSearchProbeAdmissionPatch
    {
        private static void Postfix(bool __result) => RimKataSearchProbe.CountAdmission(__result);
    }
}
