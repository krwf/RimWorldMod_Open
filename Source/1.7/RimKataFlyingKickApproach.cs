using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataFlyingKickApproach
    {
        internal const int PageShift = 8;
        internal const int PageMask = (1 << PageShift) - 1;
        internal static Slot[][] pages = Array.Empty<Slot[]>();
        private static int targetCount;
        private static readonly HashSet<Pawn> Actors = new HashSet<Pawn>();

        internal sealed class Slot
        {
            internal Pawn pawn;
            internal Approach approach;
            internal List<Approach> watchers;
            internal int usedJobId = -1;
        }

        internal sealed class Approach
        {
            internal Pawn actor, target;
            internal Job job;
        }

        private static Slot Get(Pawn pawn)
        {
            int id = pawn?.thingIDNumber ?? 0;
            int pageIndex = id >> PageShift;
            if (id <= 0 || pageIndex >= pages.Length) return null;
            Slot slot = pages[pageIndex]?[id & PageMask];
            return slot?.pawn == pawn ? slot : null;
        }

        private static Slot GetOrCreate(Pawn pawn)
        {
            int id = pawn.thingIDNumber, pageIndex = id >> PageShift;
            if (pageIndex >= pages.Length)
                Array.Resize(ref pages, Math.Max(pageIndex + 1, pages.Length * 2));
            Slot[] page = pages[pageIndex] ?? (pages[pageIndex] = new Slot[PageMask + 1]);
            return page[id & PageMask] ?? (page[id & PageMask] = new Slot { pawn = pawn });
        }

        private static void Trim(Slot slot)
        {
            if (slot == null || slot.approach != null || slot.usedJobId >= 0) return;
            Actors.Remove(slot.pawn);
            if (slot.watchers != null) return;
            int id = slot.pawn.thingIDNumber;
            pages[id >> PageShift][id & PageMask] = null;
        }

        internal static bool WasUsedForCurrentJob(Pawn pawn)
        {
            Slot slot = Get(pawn);
            return slot?.usedJobId >= 0 && pawn.CurJob?.loadID == slot.usedJobId;
        }

        internal static void NotifyStarted(Pawn pawn)
        {
            Remove(pawn);
            if (pawn?.CurJob == null || pawn.thingIDNumber <= 0) return;
            Slot slot = GetOrCreate(pawn);
            slot.usedJobId = pawn.CurJob.loadID;
            Actors.Add(pawn);
        }

        internal static void NotifyQualifiedPath(Pawn pawn)
            => RegisterPath(RimKataResponseVisualParticipantCache.BodyVisualFor(pawn));

        internal static void RegisterPath(RimKataResponseVisualParticipantCache.BodyVisualEntry entry)
        {
            Pawn pawn = entry?.pawn;
            Job job = pawn?.CurJob;
            if (job == null || job.def != JobDefOf.AttackMelee && job.def != RimKataDefOf.RimKata_Attack
                || pawn.pather?.Moving != true || !(job.targetA.Thing is Pawn target)
                || pawn.pather.Destination.Thing != target || !target.Spawned || target.Map != pawn.Map
                || pawn.thingIDNumber <= 0 || target.thingIDNumber <= 0)
            {
                Remove(pawn);
                return;
            }

            if (entry?.registeredQualified != true || entry.flyingKick.HasValue
                || WasUsedForCurrentJob(pawn) || RimKataTargetAccess.SettingsFor(pawn)?.flyingKickEnabled != true)
            {
                Remove(pawn);
                return;
            }
            Slot actorSlot = Get(pawn);
            if (actorSlot?.approach?.job == job && actorSlot.approach.target == target) return;
            Remove(pawn);
            actorSlot = GetOrCreate(pawn);
            if (actorSlot.usedJobId != job.loadID) actorSlot.usedJobId = -1;
            var approach = new Approach { actor = pawn, target = target, job = job };
            actorSlot.approach = approach;
            Actors.Add(pawn);
            Slot targetSlot = GetOrCreate(target);
            if (targetSlot.watchers == null)
            {
                targetSlot.watchers = new List<Approach>(1);
                targetCount++;
            }
            targetSlot.watchers.Add(approach);
        }

        internal static void Remove(Pawn pawn)
        {
            Slot actorSlot = Get(pawn);
            Approach approach = actorSlot?.approach;
            if (approach == null) return;
            actorSlot.approach = null;
            Slot targetSlot = Get(approach.target);
            if (targetSlot?.watchers != null)
            {
                targetSlot.watchers.Remove(approach);
                if (targetSlot.watchers.Count == 0)
                {
                    targetSlot.watchers = null;
                    targetCount--;
                    Trim(targetSlot);
                }
            }
            Trim(actorSlot);
        }

        internal static void NotifyJobEnded(Pawn pawn)
        {
            Remove(pawn);
            Slot slot = Get(pawn);
            if (slot == null) return;
            slot.usedJobId = -1;
            Trim(slot);
        }

        internal static void NotifyDespawned(Pawn pawn)
        {
            NotifyJobEnded(pawn);
            Slot targetSlot = Get(pawn);
            if (targetSlot?.watchers == null) return;
            List<Approach> watchers = targetSlot.watchers;
            targetSlot.watchers = null;
            targetCount--;
            foreach (Approach approach in watchers)
            {
                Slot actorSlot = Get(approach.actor);
                if (actorSlot?.approach != approach) continue;
                actorSlot.approach = null;
                Trim(actorSlot);
            }
            Trim(targetSlot);
        }

        internal static void Rebuild(Map map)
        {
            IReadOnlyList<Pawn> qualified = RimKataEligibilityCache.GetQualifiedPawns(map);
            for (int i = 0; i < qualified.Count; i++) NotifyQualifiedPath(qualified[i]);
        }

        internal static void ClearMap(Map map)
        {
            if (Actors.Count == 0) return;
            foreach (Pawn pawn in new List<Pawn>(Actors))
                if (pawn.Map == map) NotifyJobEnded(pawn);
        }

        internal static void ResetGame()
        {
            pages = Array.Empty<Slot[]>();
            targetCount = 0;
            Actors.Clear();
        }

        internal static void TargetMoved(Slot targetSlot, IntVec3 previous)
        {
            Pawn target = targetSlot.pawn;
            List<Approach> watchers = targetSlot.watchers;
            if (watchers == null || !target.Spawned || target.Position == previous) return;
            for (int i = watchers.Count - 1; i >= 0; i--)
            {
                Approach approach = watchers[i];
                Pawn pawn = approach.actor;
                if (!pawn.Spawned || pawn.Map != target.Map || pawn.CurJob != approach.job
                    || approach.job.targetA.Thing != target || pawn.pather?.Moving != true
                    || pawn.pather.Destination.Thing != target)
                {
                    Remove(pawn);
                    continue;
                }
                if (RimKataFlyingKick.Distance(pawn.Position, previous) <= 2
                    || RimKataFlyingKick.Distance(pawn.Position, target.Position) != 2) continue;
                IntVec3 delta = target.Position - pawn.Position;
                IntVec3 step = new IntVec3(Math.Sign(delta.x), 0, Math.Sign(delta.z));
                if (delta != step * 2) continue;
                var entry = RimKataResponseVisualParticipantCache.BodyVisualFor(pawn);
                if (entry?.registeredQualified != true)
                {
                    Remove(pawn);
                    continue;
                }
                // The actor is already pursuing this exact target; only the target's new cell made it reachable.
                RimKataFlyingKick.TryStart(entry, target, step);
            }
        }

        internal static IEnumerable<CodeInstruction> TargetMovementHook(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            Label original = generator.DefineLabel(), finished = generator.DefineLabel(), done = generator.DefineLabel();
            LocalBuilder pawn = generator.DeclareLocal(typeof(Pawn));
            LocalBuilder id = generator.DeclareLocal(typeof(int));
            LocalBuilder page = generator.DeclareLocal(typeof(Slot[]));
            LocalBuilder slot = generator.DeclareLocal(typeof(Slot));
            LocalBuilder previous = generator.DeclareLocal(typeof(IntVec3));
            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataFlyingKickApproach), nameof(targetCount)));
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_PathFollower), "pawn"));
            yield return new CodeInstruction(OpCodes.Stloc, pawn);
            yield return new CodeInstruction(OpCodes.Ldloc, pawn);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber)));
            yield return new CodeInstruction(OpCodes.Stloc, id);
            yield return new CodeInstruction(OpCodes.Ldloc, id);
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Ble, original);
            yield return new CodeInstruction(OpCodes.Ldloc, id);
            yield return new CodeInstruction(OpCodes.Ldc_I4, PageShift);
            yield return new CodeInstruction(OpCodes.Shr);
            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataFlyingKickApproach), nameof(pages)));
            yield return new CodeInstruction(OpCodes.Ldlen);
            yield return new CodeInstruction(OpCodes.Conv_I4);
            yield return new CodeInstruction(OpCodes.Bge_Un, original);
            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataFlyingKickApproach), nameof(pages)));
            yield return new CodeInstruction(OpCodes.Ldloc, id);
            yield return new CodeInstruction(OpCodes.Ldc_I4, PageShift);
            yield return new CodeInstruction(OpCodes.Shr);
            yield return new CodeInstruction(OpCodes.Ldelem_Ref);
            yield return new CodeInstruction(OpCodes.Stloc, page);
            yield return new CodeInstruction(OpCodes.Ldloc, page);
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldloc, page);
            yield return new CodeInstruction(OpCodes.Ldloc, id);
            yield return new CodeInstruction(OpCodes.Ldc_I4, PageMask);
            yield return new CodeInstruction(OpCodes.And);
            yield return new CodeInstruction(OpCodes.Ldelem_Ref);
            yield return new CodeInstruction(OpCodes.Stloc, slot);
            yield return new CodeInstruction(OpCodes.Ldloc, slot);
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            Label notWatched = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldloc, slot);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Slot), nameof(Slot.pawn)));
            yield return new CodeInstruction(OpCodes.Ldloc, pawn);
            yield return new CodeInstruction(OpCodes.Bne_Un, notWatched);
            yield return new CodeInstruction(OpCodes.Ldloc, slot);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Slot), nameof(Slot.watchers)));
            Label watched = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Brtrue, watched);
            yield return new CodeInstruction(OpCodes.Ldnull).WithLabels(notWatched);
            yield return new CodeInstruction(OpCodes.Stloc, slot);
            yield return new CodeInstruction(OpCodes.Br, original);
            yield return new CodeInstruction(OpCodes.Ldloc, pawn).WithLabels(watched);
            yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Position)));
            yield return new CodeInstruction(OpCodes.Stloc, previous);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(original);
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ret)
                {
                    instruction.opcode = OpCodes.Br;
                    instruction.operand = finished;
                }
                yield return instruction;
            }
            yield return new CodeInstruction(OpCodes.Ldloc, slot).WithLabels(finished);
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldloc, slot);
            yield return new CodeInstruction(OpCodes.Ldloc, previous);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataFlyingKickApproach), nameof(TargetMoved)));
            yield return new CodeInstruction(OpCodes.Ret).WithLabels(done);
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    internal static class Patch_PathCell_RimKataFlyingKickTarget
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => RimKataFlyingKickApproach.TargetMovementHook(instructions, generator);
    }
}
