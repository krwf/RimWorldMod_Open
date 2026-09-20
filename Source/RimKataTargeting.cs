using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace KRWF.RimKata
{
    public static class RimKataTargeting
    {
        private static readonly FieldInfo LandedField = AccessTools.Field(typeof(Projectile), "landed");

        public static bool IsProjectile(Thing thing) => thing is Projectile;

        public static bool IsProjectileVerb(Verb verb) => verb is Verb_LaunchProjectile;

        public static bool IsAutomaticEnemy(Pawn pawn, Thing target)
        {
            if (pawn?.Map == null
                || target == null
                || target == pawn
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || !target.HostileTo(pawn))
            {
                return false;
            }

            return true;
        }

        public static bool IsCombatCapableCrawling(Pawn pawn)
        {
            return pawn?.Downed == true
                && pawn.CanAttackWhileCrawling
                && pawn.Crawling;
        }

        public static bool IsIncapacitatedTarget(Pawn pawn)
        {
            return pawn?.Downed == true
                && !IsCombatCapableCrawling(pawn);
        }

        internal static bool IsSleepingOrDormant(Pawn pawn)
        {
            // Native references are already attached to the Pawn. Avoid Awake's
            // consciousness evaluation and component-list searches for this gate.
            return pawn.jobs?.curDriver?.asleep == true
                || pawn.canBeDormant?.Awake == false
                || pawn.activity?.IsDormant == true;
        }

        public static bool IsPawnTargetStateValid(
            Pawn pawn,
            bool allowIncapacitated = false)
        {
            return pawn != null
                && !pawn.Dead
                && (!pawn.Downed
                    || allowIncapacitated
                    || IsCombatCapableCrawling(pawn))
                && !pawn.IsPsychologicallyInvisible();
        }

        public static float MaximumAutomaticCandidateCellRadius(Pawn pawn)
        {
            if (pawn?.Map == null)
            {
                return 0f;
            }

            ThingWithComps primaryWeapon = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            Verb primaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, primaryWeapon);

            ThingWithComps secondaryWeapon =
                RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                    ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                    : null;

            Verb secondaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn,secondaryWeapon);

            float primaryCandidateCellRadius =
                primaryVerb != null
                && !primaryVerb.IsMeleeAttack
                    ? Mathf.Max(
                        0f,
                        RimKataRangeUtility.ResolveCandidateCellRadius(
                            pawn,
                            primaryWeapon,
                            primaryVerb))
                    : 0f;

            float secondaryCandidateCellRadius =
                secondaryVerb != null
                && !secondaryVerb.IsMeleeAttack
                    ? Mathf.Max(
                        0f,
                        RimKataRangeUtility.ResolveCandidateCellRadius(
                            pawn,
                            secondaryWeapon,
                            secondaryVerb))
                    : 0f;

            return Mathf.Max(
                primaryCandidateCellRadius,
                secondaryCandidateCellRadius);
        }

        private static bool IsValidAttackTarget(Pawn shooter, Thing target)
        {
            if (!(target is IAttackTarget)
                || !IsAutomaticEnemy(shooter, target)
                || target.Position.Fogged(shooter.Map))
            {
                return false;
            }

            return !(target is Pawn targetPawn)
                || IsPawnTargetStateValid(targetPawn);
        }

        internal static bool IsValidAutomaticAttackTarget(
            Pawn shooter,
            Thing target)
        {
            return IsValidAttackTarget(shooter, target);
        }

        private static bool IsValidExplosiveProjectile(Pawn pawn, Verb verb, Projectile projectile, float rangeSquared)
        {
            if (RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled == false)
            {
                return false;
            }
            if (projectile == null
                || !projectile.Spawned
                || projectile.Destroyed
                || projectile.Map != pawn?.Map
                || projectile.def.projectile?.explosionRadius <= 0f
                || (bool)LandedField.GetValue(projectile)
                || !IsEnemyProjectileLauncher(pawn, projectile))
            {
                return false;
            }

            IntVec3 cell = projectile.ExactPosition.ToIntVec3();
            if (!cell.InBounds(pawn.Map)
                || pawn.Position.DistanceToSquared(cell) > rangeSquared
                || !verb.TryFindShootLineFromTo(pawn.Position, cell, out _))
            {
                return false;
            }

            return true;
        }

        internal static bool IsValidExplosiveProjectileForVerb(
            Pawn pawn,
            Verb verb,
            Thing projectile,
            float rangeSquared)
        {
            return IsValidExplosiveProjectile(
                pawn,
                verb,
                projectile as Projectile,
                rangeSquared);
        }

        internal static bool IsEnemyProjectileLauncher(
            Pawn pawn,
            Thing projectile)
        {
            if (pawn == null || projectile == null)
            {
                return false;
            }

            bool defenderHostileToPlayer =
                pawn.Faction?.HostileTo(Faction.OfPlayer) == true;
            bool launchedByPlayer =
                (projectile as Projectile)?.Launcher?.Faction == Faction.OfPlayer;
            return defenderHostileToPlayer
                ? launchedByPlayer
                : !launchedByPlayer;
        }

        internal static bool IsPotentialExplosiveProjectile(
            Thing projectile,
            Map map)
        {
            return projectile is Projectile
                && map != null
                && projectile.Spawned
                && !projectile.Destroyed
                && projectile.Map == map
                && projectile.def.projectile?.explosionRadius > 0f
                && !(bool)LandedField.GetValue(projectile);
        }

        public static bool IsInterceptionTargetActive(Thing projectile)
        {
            return projectile is Projectile
                && projectile.Spawned
                && !projectile.Destroyed
                && projectile.def.projectile?.explosionRadius > 0f
                && !(bool)LandedField.GetValue(projectile);
        }
    }

    public static class RimKataInterceptionUtility
    {
        public static bool Resolve(Pawn pawn, Thing targetProjectile, Vector3 impactPosition)
        {
            Projectile projectile = targetProjectile as Projectile;
            if (pawn?.Map == null
                || projectile == null
                || !projectile.Spawned
                || projectile.Destroyed)
            {
                return false;
            }

            Map impactMap = projectile.Map;
            float impactAngle = projectile.ExactRotation.eulerAngles.y;
            IntVec3 currentCell = impactPosition.ToIntVec3();
            if (impactMap == null || !currentCell.InBounds(impactMap))
            {
                return false;
            }

            bool critical = Rand.Chance(
                RimKataTargetAccess.SettingsFor(pawn)?.GetInterceptionCriticalChance(pawn) ?? 0f);
            bool isGrenade = RimKataDefOf.Grenades != null
                && projectile.EquipmentDef?.IsWithinCategory(RimKataDefOf.Grenades) == true;
            IntVec3 impactCell = currentCell;

            if (!critical)
            {
                List<IntVec3> cells = GenAdj.AdjacentCellsAndInside
                    .Select(offset => currentCell + offset)
                    .Where(cell => cell.InBounds(impactMap))
                    .ToList();

                if (cells.Count == 0)
                {
                    return false;
                }

                impactCell = cells.RandomElement();
            }

            if (critical && !isGrenade)
            {
                projectile.Destroy(DestroyMode.Vanish);
                if (!projectile.Destroyed)
                {
                    return false;
                }
            }
            else
            {
                if (!RimKataProjectileUtility.PrepareImmediateImpact(
                        projectile,
                        impactCell))
                {
                    return false;
                }

                if (critical)
                {
                    RimKataProjectileUtility.DetonateNow(projectile);
                }
                else
                {
                    RimKataProjectileUtility.Impact(projectile, null);
                }
            }

            PlaySuccessEffect(impactMap, impactPosition, impactAngle);
            return true;
        }

        internal static void PlaySuccessEffect(
            Map map,
            Vector3 position,
            float velocityAngle)
        {
            Rand.PushState();
            try
            {
                RimKataDefOf.BulletImpact_Metal.PlayOneShot(
                    new TargetInfo(position.ToIntVec3(), map));

                FleckCreationData data = FleckMaker.GetDataStatic(
                    position,
                    map,
                    FleckDefOf.MicroSparksFast,
                    1f);
                data.velocityAngle = velocityAngle;
                data.velocitySpeed = 0.8f;
                map.flecks.CreateFleck(data);
            }
            finally
            {
                Rand.PopState();
            }
        }
    }
}
