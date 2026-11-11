using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Together represents the visiting player's pawns with its ally/enemy faction.
    // Extend only RK's order-validation boundaries, never Pawn's ownership getters.
    internal static class RimKataMultiplayerRemoteOrders
    {
        private static bool installed;
        private static readonly MethodInfo playerControlled = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsPlayerControlled));
        private static readonly MethodInfo authorized = AccessTools.DeclaredMethod(
            typeof(RimKataMultiplayerCommands), nameof(RimKataMultiplayerCommands.IsPlayerOrderPawn));

        internal static void Install(Harmony harmony)
        {
            if (installed) return;
            var targets = new[]
            {
                AccessTools.DeclaredMethod(typeof(RimKataDualWeaponController), "CanUsePlayerEquippedWeaponCommand"),
                AccessTools.DeclaredMethod(typeof(RimKataDualWeaponController), "CanNotifyPlayerMeleeCloseTarget"),
                AccessTools.DeclaredMethod(typeof(RimKataDualWeaponController), "TryConvertPlayerRushOrder"),
                AccessTools.DeclaredMethod(typeof(RimKataDirectionalFire), "CanUseCommand"),
                AccessTools.DeclaredMethod(typeof(RimKataDirectionalFire), "OrderSelectedPawn"),
                AccessTools.DeclaredMethod(typeof(RimKataSubdueUtility), "CanOrder"),
                AccessTools.DeclaredMethod(typeof(RimKataSidearmInventoryEquip), "EquipFailureReason"),
                AccessTools.DeclaredMethod(typeof(JobDriver_RimKataAttack), "QueueNearbyStructureMeleeAttack"),
                AccessTools.DeclaredMethod(typeof(Patch_PawnJobTracker_TryTakeOrderedJob_RimKata), "Prefix"),
                AccessTools.DeclaredMethod(typeof(Patch_PawnJobTracker_StartJob_EnemyRimKata), "Prefix"),
                AccessTools.DeclaredMethod(typeof(Patch_PawnJobStart_RimKataBreach), "Prefix"),
                AccessTools.DeclaredMethod(typeof(Patch_PawnMeleeAttempt_RimKataBreach), "Prefix")
            };
            MethodInfo patch = AccessTools.DeclaredMethod(typeof(RimKataMultiplayerRemoteOrders), nameof(OrderOwnership));
            var applied = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo target in targets)
                {
                    if (target == null) throw new MissingMethodException("RimKata remote order boundary");
                    harmony.Patch(target, transpiler: new HarmonyMethod(patch));
                    applied.Add(target);
                }
                installed = true;
            }
            catch
            {
                foreach (MethodInfo target in applied) harmony.Unpatch(target, patch);
                throw;
            }
        }

        private static IEnumerable<CodeInstruction> OrderOwnership(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int matched = 0;
            bool incomingJob = __originalMethod.DeclaringType == typeof(Patch_PawnJobTracker_TryTakeOrderedJob_RimKata)
                || __originalMethod.DeclaringType == typeof(Patch_PawnJobTracker_StartJob_EnemyRimKata)
                || __originalMethod.DeclaringType == typeof(Patch_PawnJobStart_RimKataBreach);
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(playerControlled))
                {
                    if (incomingJob)
                    {
                        // These RK prefixes receive Pawn, ref Job. The current job still
                        // precedes the incoming order here (often Wait on the host).
                        yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(code).MoveBlocksFrom(code);
                        yield return new CodeInstruction(OpCodes.Ldind_Ref);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(
                            typeof(RimKataMultiplayerRemoteOrders), nameof(IsPlayerOrderPawnForJob)));
                    }
                    else yield return new CodeInstruction(OpCodes.Call, authorized).MoveLabelsFrom(code).MoveBlocksFrom(code);
                    matched++;
                }
                else yield return code;
            }
            if (matched != 1) throw new InvalidOperationException(
                "RimKata remote order ownership boundary changed: " + __originalMethod.Name + " (" + matched + ").");
        }

        private static bool IsPlayerOrderPawnForJob(Pawn pawn, Job job)
        {
            if (RimKataMultiplayerCommands.IsPlayerOrderPawn(pawn)) return true;
            if (job?.playerForced != true || !RimKataMultiplayerCompat.IsRemoteParticipant(pawn)) return false;
            return job.def == RimKataDefOf.RimKata_Attack || job.def == RimKataDefOf.RimKata_Subdue
                || job.def == RimKataDefOf.RimKata_SubdueCombat || job.def == JobDefOf.AttackStatic
                || job.def == JobDefOf.AttackMelee;
        }
    }
}
