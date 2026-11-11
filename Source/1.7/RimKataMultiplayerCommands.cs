using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMultiplayerCommands
    {
        internal const int AttackGizmoContext = 1;
        internal const int SquadAttackContext = 2;
        internal const int DirectionalContext = 4;
        private static MethodInfo[] methods;

        // Build only when an active provider registers commands. Ordinary single-player
        // calls do not discover multiplayer APIs or enumerate this command class.
        internal static IReadOnlyList<MethodInfo> Methods => methods ?? (methods = BuildMethods());

        private static MethodInfo[] BuildMethods()
        {
            string[] names =
            {
                nameof(OrderWeapon), nameof(OrderSquadGround), nameof(SwapWeapons),
                nameof(SetSubdueAttack), nameof(SetSubdueAttackAllowed), nameof(ReleaseSubdued),
                nameof(ThrowSubdued), nameof(StopSubdueExternalAttack), nameof(SetCrawlFireAllowed),
                nameof(EquipInventorySecondary), nameof(EquipStoredSecondary)
            };
            Array.Sort(names, StringComparer.Ordinal);
            var result = new MethodInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
                result[i] = typeof(RimKataMultiplayerCommands).GetMethod(names[i],
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? throw new MissingMethodException(typeof(RimKataMultiplayerCommands).FullName, names[i]);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void OrderWeapon(Pawn pawn, Thing weapon, int verbIndex, string verbId,
            Thing target, IntVec3 cell, int contextFlags)
        {
            if (!RimKataMultiplayerCompat.CanExecuteCommands || !CanOrder(pawn)) return;
            Verb verb = ResolveVerb(pawn, weapon, verbIndex, verbId);
            if (verb == null) return;
            if ((contextFlags & DirectionalContext) != 0)
            {
                if (target == null && cell.IsValid)
                    RimKataDirectionalFire.Order(pawn, verb, cell);
                return;
            }
            // A despawned/unresolved Thing must not turn an old focus command into a
            // ground-fire command at its former cell.
            if (target == null || target.Destroyed || !target.Spawned || target.Map != pawn.Map) return;
            LocalTargetInfo selected = new LocalTargetInfo(target);
            if ((contextFlags & SquadAttackContext) != 0)
                RimKataAttackGizmoTargetContext.InvokeSquad(verb.OrderForceTarget, selected);
            else if ((contextFlags & AttackGizmoContext) != 0)
                RimKataAttackGizmoTargetContext.Invoke(verb.OrderForceTarget, selected);
            else
                verb.OrderForceTarget(selected);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void OrderSquadGround(Pawn pawn, IntVec3 cell)
        {
            if (RimKataMultiplayerCompat.CanExecuteCommands)
                RimKataDirectionalFire.OrderSelectedPawn(pawn, cell);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SwapWeapons(Pawn pawn)
        {
            if (RimKataMultiplayerCompat.CanExecuteCommands && CanOrder(pawn))
                RimKataDualWeaponController.RequestWeaponSwap(pawn);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetSubdueAttack(Pawn pawn, Pawn heldTarget, bool enabled)
        {
            RimKataSubdueState state = HeldState(pawn, heldTarget);
            if (state != null) RimKataSubdueCombat.SetAttackEnabled(state, enabled);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetSubdueAttackAllowed(Pawn pawn, Pawn heldTarget, bool allowed)
        {
            RimKataSubdueState state = HeldState(pawn, heldTarget);
            if (state != null) RimKataSubdueCombat.SetAttackAllowed(state, allowed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ReleaseSubdued(Pawn pawn, Pawn heldTarget)
        {
            RimKataSubdueState state = HeldState(pawn, heldTarget);
            if (state != null) RimKataSubdueUtility.Release(state);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowSubdued(Pawn pawn, Pawn heldTarget)
        {
            RimKataSubdueState state = HeldState(pawn, heldTarget);
            if (state != null) RimKataSubdueUtility.Throw(state);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void StopSubdueExternalAttack(Pawn pawn, Pawn heldTarget)
        {
            RimKataSubdueState state = HeldState(pawn, heldTarget);
            if (state == null) return;
            RimKataAutoSubdue.NotifyManualControl(state);
            RimKataSubdueCombat.StopExternalAttack(state);
        }

        private static RimKataSubdueState HeldState(Pawn pawn, Pawn heldTarget)
        {
            if (!RimKataMultiplayerCompat.CanExecuteCommands || pawn?.Spawned != true
                || !IsPlayerOrderPawn(pawn) || heldTarget == null) return null;
            RimKataSubdueState state = RimKataSubdueUtility.Get(pawn);
            return state?.target == heldTarget && RimKataSubdueUtility.IsRelationValid(state) ? state : null;
        }

        private static bool CanOrder(Pawn pawn)
        {
            return pawn?.Spawned == true && pawn.health != null && !pawn.Dead && !pawn.Downed
                && IsPlayerOrderPawn(pawn) && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCrawlFireAllowed(Pawn pawn, bool allowed)
        {
            if (RimKataMultiplayerCompat.CanExecuteCommands && pawn?.Spawned == true
                && IsPlayerOrderPawn(pawn)) RimKataCrawlFireUtility.SetFireAllowed(pawn, allowed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void EquipInventorySecondary(Pawn pawn, int weaponIndex, string weaponId)
        {
            if (!RimKataMultiplayerCompat.CanExecuteCommands || !CanOrder(pawn)) return;
            var inventory = pawn.inventory?.innerContainer;
            if (inventory == null || weaponIndex < 0 || weaponIndex >= inventory.Count) return;
            ThingWithComps weapon = inventory[weaponIndex] as ThingWithComps;
            if (weapon != null && weapon.GetUniqueLoadID() == weaponId)
                RimKataSidearmInventoryEquip.EquipFromInventory(pawn, weapon);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void EquipStoredSecondary(Pawn pawn, string storageId, int weaponIndex, string weaponId)
        {
            if (RimKataMultiplayerCompat.CanExecuteCommands && CanOrder(pawn))
                RimKataMonolynCompat.EquipStored(pawn, storageId, weaponIndex, weaponId);
        }

        // Only a verified peer command or its explicit current RK job extends the
        // local player check. No Pawn/Faction getter is changed for ordinary AI.
        internal static bool IsPlayerOrderPawn(Pawn pawn)
        {
            if (pawn == null) return false;
            if (pawn.IsPlayerControlled || RimKataMultiplayerCompat.IsAuthorizedRemoteCommand(pawn)) return true;
            if (!RimKataMultiplayerCompat.IsRemoteParticipant(pawn) || pawn.CurJob?.playerForced != true) return false;
            JobDef def = pawn.CurJobDef;
            return def == RimKataDefOf.RimKata_Attack || def == RimKataDefOf.RimKata_Subdue
                || def == RimKataDefOf.RimKata_SubdueCombat || def == JobDefOf.AttackStatic
                || def == JobDefOf.AttackMelee;
        }

        private static Verb ResolveVerb(Pawn pawn, Thing weapon, int verbIndex, string verbId)
        {
            if (string.IsNullOrEmpty(verbId)) return null;
            if (weapon == null)
            {
                // Non-equipment melee orders have no equipment owner. Resolve the
                // original saved identity, not whichever attack now occupies an index.
                // Preserve native terrain/body/hediff attack eligibility for this order;
                // the narrower held-subdue attack selection is a different policy.
                var melee = pawn.meleeVerbs?.GetUpdatedAvailableVerbsList(true);
                if (melee == null) return null;
                for (int i = 0; i < melee.Count; i++)
                {
                    Verb candidate = melee[i].verb;
                    if (candidate?.EquipmentSource == null && candidate?.CasterPawn == pawn
                        && candidate.IsMeleeAttack && candidate.GetUniqueLoadID() == verbId
                        && candidate.IsStillUsableBy(pawn))
                        return candidate;
                }
                return null;
            }
            ThingWithComps equipped = weapon as ThingWithComps;
            if (equipped == null || equipped.Destroyed
                || pawn.equipment?.AllEquipmentListForReading.Contains(equipped) != true) return null;
            List<Verb> verbs = equipped.TryGetComp<CompEquippable>()?.AllVerbs;
            if (verbs == null || verbIndex < 0 || verbIndex >= verbs.Count) return null;
            Verb verb = verbs[verbIndex];
            return verb?.CasterPawn == pawn && verb.EquipmentSource == equipped
                && verb.GetUniqueLoadID() == verbId && verb.IsStillUsableBy(pawn) ? verb : null;
        }
    }
}
