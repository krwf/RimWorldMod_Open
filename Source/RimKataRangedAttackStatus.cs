using Verse;

namespace KRWF.RimKata
{
    public enum RimKataRangedAttackPhase
    {
        None,
        Aiming,
        Firing,
        Cooldown
    }

    /// <summary>A read-only snapshot of one weapon's actual RimKata attack.</summary>
    public readonly struct RimKataRangedAttackState
    {
        public RimKataRangedAttackPhase Phase { get; }
        public LocalTargetInfo Target { get; }

        internal RimKataRangedAttackState(RimKataRangedAttackPhase phase, LocalTargetInfo target)
        {
            Phase = phase;
            Target = target;
        }
    }

    /// <summary>
    /// Compatibility API for mods that need to observe independent weapon attacks.
    /// This supplements their native checks; it never changes a stance or Verb,
    /// creates combat state, advances timers, or searches for a target.
    /// </summary>
    public static class RimKataRangedAttackStatus
    {
        /// <summary>
        /// Returns true only for an equipped weapon's active RimKata ranged attack.
        /// Target search, a stored order and a retained aim pose alone return false.
        /// Cooldown means recovery after shooting, not ammunition reloading.
        /// </summary>
        public static bool TryGetState(
            Pawn pawn, ThingWithComps weapon, out RimKataRangedAttackState result)
        {
            result = default;
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || pawn.InMentalState
                || weapon == null || weapon.Destroyed || weapon.def?.IsRangedWeapon != true
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || pawn.equipment?.AllEquipmentListForReading.Contains(weapon) != true
                || (pawn.equipment.Primary != weapon
                    && RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn) != weapon)
                || !RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner))
                return false;

            RimKataPawnCombatState state = owner.GetState(pawn, false);
            if (state == null || state.temporaryInactive)
                return false;

            RimKataWeaponCycleState cycle = state.primaryWeaponCycle?.weapon == weapon
                ? state.primaryWeaponCycle
                : state.secondaryWeaponCycle?.weapon == weapon ? state.secondaryWeaponCycle : null;
            return TryReadCycle(pawn, cycle, state.dualCloseCombatActive, out result);
        }

        internal static bool TryReadCycle(
            Pawn pawn, RimKataWeaponCycleState cycle, bool closeCombatContext,
            out RimKataRangedAttackState result)
        {
            result = default;
            if (cycle?.weapon == null)
                return false;

            RimKataNativeAttack attack = cycle.nativeAttack;
            if (attack?.Pending == true)
            {
                if (attack.verb?.IsMeleeAttack != false)
                    return false;
                result = new RimKataRangedAttackState(
                    attack.Started ? RimKataRangedAttackPhase.Firing : RimKataRangedAttackPhase.Aiming,
                    attack.target);
                return true;
            }

            if (cycle.cooldownTicksRemaining > 0)
            {
                if (!cycle.rangedCooldown)
                    return false;
                result = new RimKataRangedAttackState(RimKataRangedAttackPhase.Cooldown,
                    cycle.lastFiredTarget != null
                        ? new LocalTargetInfo(cycle.lastFiredTarget) : LocalTargetInfo.Invalid);
                return true;
            }

            if (cycle.HasPlan && cycle.warmupTicksRemaining >= 0
                && RimKataDualWeaponController.IsRangedCycleAction(pawn, cycle, closeCombatContext))
            {
                result = new RimKataRangedAttackState(
                    RimKataRangedAttackPhase.Aiming, new LocalTargetInfo(cycle.plannedTarget));
                return true;
            }
            return false;
        }
    }
}
