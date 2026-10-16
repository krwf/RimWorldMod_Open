using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public static class RimKataEligibility
    {
        public static bool HasRimKataAccess(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            bool accessRestrictionsDisabled =
                RimKataTargetAccess.IsEnabled(pawn);
            if (!accessRestrictionsDisabled
                && RimKataEligibilityCache.TryGetCachedAccess(
                    pawn,
                    out bool cachedAccess)
                && !cachedAccess)
            {
                return false;
            }

            if (!FactionEffectsEnabled(pawn))
            {
                return false;
            }

            if (accessRestrictionsDisabled)
            {
                return true;
            }

            return RimKataEligibilityCache.HasAnyAccessSource(pawn);
        }

        public static bool HasActiveRimKataAccess(Pawn pawn)
        {
            return HasRimKataAccess(pawn)
                && !RimKataTemporaryInactivity.IsInactive(pawn);
        }

        public static bool RandomAttackEnabledForPawn(Pawn pawn)
        {
            return pawn != null
                && RimKataTargetAccess.SettingsFor(pawn)?.randomAttackEnabled != false
                && HasActiveRimKataAccess(pawn);
        }

        public static bool FactionEffectsEnabled(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            RimKataSettings settings = RimKataMod.Settings;
            if (settings == null)
            {
                return true;
            }

            if (settings.enableFriendlyPawnEffects
                == settings.enableHostilePawnEffects)
            {
                return settings.enableFriendlyPawnEffects;
            }

            bool hostileToPlayer = IsHostileToPlayerFaction(pawn);
            return hostileToPlayer
                ? settings.enableHostilePawnEffects
                : settings.enableFriendlyPawnEffects;
        }

        public static bool IsHostileToPlayerFaction(Pawn pawn)
        {
            Faction playerFaction = Faction.OfPlayer;
            return pawn != null
                && playerFaction != null
                && pawn.Faction?.HostileTo(playerFaction) == true;
        }

        public static bool IsWorking(Pawn pawn)
        {
            return pawn != null
                && !pawn.Drafted
                && pawn.CurJob?.workGiverDef != null;
        }

        public static bool IsWorkMovementDefenseException(Pawn pawn)
        {
            return IsWorkMovementDefenseExceptionCore(pawn, IsWorking(pawn));
        }

        private static bool IsWorkMovementDefenseExceptionCore(
            Pawn pawn,
            bool isWorking)
        {
            return isWorking
                && pawn.pather?.MovingNow == true
                && pawn.carryTracker?.CarriedThing == null;
        }

        public static bool CanUseDefense(Pawn pawn)
        {
            return TryGetDefenseEligibility(pawn, out _);
        }

        internal static bool TryGetDefenseEligibility(
            Pawn pawn,
            out bool workMovementDefenseException)
        {
            workMovementDefenseException = false;
            if (!HasActiveRimKataAccess(pawn)
                || !IsConsciousAndMobile(pawn))
            {
                return false;
            }

            bool isWorking = IsWorking(pawn);
            if (isWorking)
            {
                workMovementDefenseException =
                    IsWorkMovementDefenseExceptionCore(pawn, true);
                if (!workMovementDefenseException)
                {
                    return false;
                }
            }

            return pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving);
        }

        public static bool CanRollRangedDodge(Pawn pawn, bool shieldAbsorbed)
        {
            return CanRollRangedDodgeVerified(pawn, shieldAbsorbed)
                && CanUseDefense(pawn);
        }

        internal static bool CanRollRangedDodgeVerified(
            Pawn pawn,
            bool shieldAbsorbed)
        {
            return pawn != null
                && RimKataTargetAccess.SettingsFor(pawn)?.rangedDodgeEnabled != false
                && !shieldAbsorbed;
        }

        public static bool CanRollMeleeDodge(Pawn pawn)
        {
            return CanUseDefense(pawn);
        }

        public static bool CanUseMeleeResponse(Pawn pawn)
        {
            return CanUseDefense(pawn)
                && pawn.kindDef?.canMeleeAttack == true;
        }

        public static bool CanBeginGunKataAttack(Pawn pawn)
        {
            return CanOperateCombatWeapon(pawn)
                && RimKataEquipmentUtility.IsPrimaryWeaponEnabled(pawn);
        }

        internal static bool CanUseProjectileInterception(Pawn pawn)
        {
            return RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled != false
                && CanOperateCombatWeapon(pawn);
        }

        internal static bool CanOperateCombatWeapon(Pawn pawn)
        {
            return HasActiveRimKataAccess(pawn)
                && IsConsciousAndMobile(pawn)
                && !pawn.WorkTagIsDisabled(WorkTags.Violent);
        }

        public static bool TryGetEnabledCombatVerb(Pawn pawn, out Verb verb)
        {
            verb = null;
            if (!CanBeginGunKataAttack(pawn))
            {
                return false;
            }

            verb = RimKataWeaponSlotUtility.CombatVerb(pawn, pawn.equipment?.Primary);
            return verb != null;
        }

        public static bool IsRangedVerbAvailableInCloseCombat(Pawn pawn, Verb verb)
        {
            if (RimKataTargetAccess.SettingsFor(pawn)?.closeFireEnabled == false
                || pawn == null
                || verb == null
                || verb.IsMeleeAttack
                || verb.ApparelPreventsShooting())
            {
                return false;
            }

            bool hostileNpc = Faction.OfPlayer != null
                && pawn.Faction?.HostileTo(Faction.OfPlayer) == true;
            if (!hostileNpc || !RimKataTargeting.IsProjectileVerb(verb) || verb.verbProps == null)
            {
                return verb.Available();
            }

            bool ignoredMeleeThreat = verb.verbProps.ai_ProjectileLaunchingIgnoresMeleeThreats;
            try
            {
                verb.verbProps.ai_ProjectileLaunchingIgnoresMeleeThreats = true;
                return verb.Available();
            }
            finally
            {
                verb.verbProps.ai_ProjectileLaunchingIgnoresMeleeThreats = ignoredMeleeThreat;
            }
        }

        private static bool IsConsciousAndMobile(Pawn pawn)
        {
            return pawn != null
                && pawn.Spawned
                && !pawn.Dead
                && !pawn.Downed
                && pawn.Awake();
        }
    }
}
