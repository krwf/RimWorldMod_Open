using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataMotionJobGate
    {
        private const int PageShift = 8;
        private const int PageSize = 1 << PageShift;
        private static RimKataPawnCombatState[][] pages = Array.Empty<RimKataPawnCombatState[]>();
        private static int count;

        internal static void Refresh(RimKataPawnCombatState state)
        {
            Pawn pawn = state?.pawn;
            Job job = pawn?.CurJob;
            if (job == null || !(state.DodgeMovementActive && state.dodgeMovementJob == job
                || state.reactiveMotion != null && state.reactiveMotion.ownerJobId == job.loadID))
            {
                Clear(state);
                return;
            }
            int id = pawn.thingIDNumber;
            if (id <= 0) return;
            int index = id >> PageShift;
            if (index >= pages.Length) Array.Resize(ref pages, Math.Max(index + 1, pages.Length * 2));
            var page = pages[index] ?? (pages[index] = new RimKataPawnCombatState[PageSize]);
            int slot = id & (PageSize - 1);
            if (page[slot] == null) count++;
            else if (page[slot] != state) page[slot].motionControlJob = null;
            state.motionControlJob = job;
            page[slot] = state;
        }

        internal static void Clear(RimKataPawnCombatState state)
        {
            if (state == null) return;
            state.motionControlJob = null;
            int id = state.pawn?.thingIDNumber ?? 0;
            int index = id >> PageShift;
            if (id <= 0 || index >= pages.Length) return;
            var page = pages[index];
            if (page == null || page[id & (PageSize - 1)] != state) return;
            page[id & (PageSize - 1)] = null;
            count--;
        }

        internal static void Clear(Pawn pawn)
        {
            int id = pawn?.thingIDNumber ?? 0;
            int index = id >> PageShift;
            if (id <= 0 || index >= pages.Length) return;
            var state = pages[index]?[id & (PageSize - 1)];
            if (state?.pawn == pawn) Clear(state);
        }

        internal static void ResetGame()
        {
            pages = Array.Empty<RimKataPawnCombatState[]>();
            count = 0;
        }

        internal static bool AllowJobTick(RimKataPawnCombatState state, JobDriver driver)
        {
            if (state.motionControlJob != driver.job) return true;
            if (state.reactiveMotion?.BlocksCombat == true)
            {
                if (state.reactiveMotion.kind == RimKataReactiveKind.ShakeOff)
                    RimKataReactiveMotion.PrepareShakeOffHits(state.reactiveMotion);
                return false;
            }
            return !state.DodgeMotionBlocksJob
                || driver is JobDriver_RimKataAttack
                    && RimKataDodgeMovementUtility.CalculateIsActive(state.pawn, state);
        }

        internal static bool GateFullBodyBusy(bool busy, RimKataPawnCombatState state)
            => busy && (state.motionControlJob != state.pawn.CurJob
                || !RimKataDodgeMovementUtility.CalculateIsActive(state.pawn, state));

        internal static void JobChanging(RimKataPawnCombatState state, Job newJob)
        {
            if (newJob != state.motionControlJob && state.reactiveMotion != null)
                RimKataReactiveMotion.Remove(state.pawn);
        }

        internal static bool FinishDodge(RimKataPawnCombatState state, bool failed)
            => state.DodgeMovementActive && state.motionControlJob == state.pawn.CurJob
                && state.ownerComponent?.TryFinishDodgeMovement(state.pawn, failed) == true;

        internal static List<CodeInstruction> ParticipantBranch(ILGenerator generator,
            IEnumerable<CodeInstruction> loadPawn, Label absent, out LocalBuilder participant)
        {
            LocalBuilder pawn = generator.DeclareLocal(typeof(Pawn));
            LocalBuilder id = generator.DeclareLocal(typeof(int));
            LocalBuilder page = generator.DeclareLocal(typeof(RimKataPawnCombatState[]));
            participant = generator.DeclareLocal(typeof(RimKataPawnCombatState));
            var codes = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataMotionJobGate), nameof(count))),
                new CodeInstruction(OpCodes.Brfalse, absent)
            };
            codes.AddRange(loadPawn);
            codes.AddRange(new[] {
                new CodeInstruction(OpCodes.Stloc, pawn),
                new CodeInstruction(OpCodes.Ldloc, pawn), new CodeInstruction(OpCodes.Brfalse, absent),
                new CodeInstruction(OpCodes.Ldloc, pawn),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber))),
                new CodeInstruction(OpCodes.Stloc, id),
                new CodeInstruction(OpCodes.Ldloc, id), new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Ble, absent),
                new CodeInstruction(OpCodes.Ldloc, id), new CodeInstruction(OpCodes.Ldc_I4, PageShift),
                new CodeInstruction(OpCodes.Shr),
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataMotionJobGate), nameof(pages))),
                new CodeInstruction(OpCodes.Ldlen), new CodeInstruction(OpCodes.Conv_I4),
                new CodeInstruction(OpCodes.Bge_Un, absent),
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataMotionJobGate), nameof(pages))),
                new CodeInstruction(OpCodes.Ldloc, id), new CodeInstruction(OpCodes.Ldc_I4, PageShift),
                new CodeInstruction(OpCodes.Shr), new CodeInstruction(OpCodes.Ldelem_Ref),
                new CodeInstruction(OpCodes.Stloc, page),
                new CodeInstruction(OpCodes.Ldloc, page), new CodeInstruction(OpCodes.Brfalse, absent),
                new CodeInstruction(OpCodes.Ldloc, page), new CodeInstruction(OpCodes.Ldloc, id),
                new CodeInstruction(OpCodes.Ldc_I4, PageSize - 1), new CodeInstruction(OpCodes.And),
                new CodeInstruction(OpCodes.Ldelem_Ref), new CodeInstruction(OpCodes.Stloc, participant),
                new CodeInstruction(OpCodes.Ldloc, participant), new CodeInstruction(OpCodes.Brfalse, absent),
                new CodeInstruction(OpCodes.Ldloc, participant),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(RimKataPawnCombatState), nameof(RimKataPawnCombatState.pawn))),
                new CodeInstruction(OpCodes.Ldloc, pawn), new CodeInstruction(OpCodes.Bne_Un, absent)
            });
            return codes;
        }
    }
}
