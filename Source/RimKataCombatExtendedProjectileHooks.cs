using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Installed only after the CE API has been resolved. The unpatched common
    // methods contain no CE calls, delegates or availability checks.
    internal static class RimKataCombatExtendedProjectileHooks
    {
        internal static void Apply(Harmony harmony)
        {
            Postfix(harmony, typeof(RimKataTargeting), "IsProjectile", nameof(IsProjectilePostfix));
            Postfix(harmony, typeof(RimKataTargeting), "IsProjectileVerb", nameof(IsProjectileVerbPostfix));
            Postfix(harmony, typeof(RimKataTargeting), "IsInterceptionTargetActive", nameof(IsActivePostfix));
            Postfix(harmony, typeof(RimKataTargeting), "IsPotentialExplosiveProjectile", nameof(IsPotentialPostfix));
            Prefix(harmony, typeof(RimKataTargeting), "IsValidExplosiveProjectileForVerb", nameof(IsValidPrefix));
            Prefix(harmony, typeof(RimKataTargeting), "IsEnemyProjectileLauncher", nameof(IsEnemyPrefix));
            Prefix(harmony, typeof(RimKataInterceptionUtility), "Resolve", nameof(ResolvePrefix));
            Prefix(harmony, typeof(RimKataInterceptionTrajectory), "CanIntercept", nameof(CanInterceptPrefix));
            Prefix(harmony, typeof(RimKataInterceptionTrajectory), "TryRedirectHit", nameof(RedirectPrefix));
            Prefix(harmony, typeof(RimKataInterceptionTrajectory), "TryGetContact", nameof(ContactPrefix));
            Prefix(harmony, typeof(RimKataProjectileUtility), "SpawnDeflectedMiss", nameof(DeflectedPrefix));
            Postfix(harmony, typeof(RimKataDefenseUtility), "get_HasProjectileImpact", nameof(HasImpactPostfix));
            Postfix(harmony, typeof(RimKataDefenseUtility), "get_CurrentCloseProjectile", nameof(CloseProjectilePostfix));
            Prefix(harmony, typeof(RimKataDefenseUtility), "TryAbsorbAfterShield", nameof(AbsorbPrefix));
            Prefix(harmony, typeof(RimKataDefenseUtility), "TryConsumeAvoidedProjectile", nameof(ConsumeAvoidedPrefix));
            Prefix(harmony, typeof(Pawn), nameof(Pawn.PreApplyDamage), nameof(PawnDamagePrefix));
            Postfix(harmony, typeof(RimKataMapComponent), "get_HasActiveExplosiveProjectiles", nameof(HasExplosivesPostfix));
            Postfix(harmony, typeof(RimKataMapComponent), "MarkCurrentRangedProjectilesAvoided", nameof(MarkAvoidedPostfix));
            Postfix(harmony, typeof(RimKataMapComponent), "HasHostileProjectileInRange", nameof(HostileInRangePostfix));
            Postfix(harmony, typeof(RimKataMapComponent), "TryGetValidHostileProjectile", nameof(FirstProjectilePostfix));
            Postfix(harmony, typeof(RimKataMapComponent), "AppendValidHostileProjectiles", nameof(AppendProjectilesPostfix));
            LongEventHandler.ExecuteWhenFinished(RegisterMapComponents);
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(
            typeof(RimKataCombatExtendedProjectileHooks), name) { priority = Priority.First };
        private static void Prefix(Harmony harmony, Type type, string name, string hook) =>
            harmony.Patch(AccessTools.Method(type, name), prefix: Hook(hook));
        private static void Postfix(Harmony harmony, Type type, string name, string hook) =>
            harmony.Patch(AccessTools.Method(type, name), postfix: Hook(hook));

        private static void RegisterMapComponents()
        {
            // CustomMapComponent is excluded by Map.FillComponents' normal
            // discovery. Registration happens once, only for a CE-enabled game.
            foreach (MapGeneratorDef generator in DefDatabase<MapGeneratorDef>.AllDefsListForReading)
            {
                generator.customMapComponents ??= new List<Type>();
                if (!generator.customMapComponents.Contains(typeof(RimKataCEProjectileMapComponent)))
                    generator.customMapComponents.Add(typeof(RimKataCEProjectileMapComponent));
            }
        }

        private static void IsProjectilePostfix(Thing thing, ref bool __result)
        {
            if (!__result) __result = RimKataCombatExtendedProjectiles.IsProjectile(thing);
        }
        private static void IsProjectileVerbPostfix(Verb verb, ref bool __result)
        {
            if (!__result) __result = RimKataCombatExtendedProjectiles.IsProjectileVerb(verb);
        }
        private static void IsActivePostfix(Thing projectile, ref bool __result)
        {
            if (!__result) __result = RimKataCombatExtendedProjectiles.IsActiveExplosive(projectile);
        }
        private static void IsPotentialPostfix(Thing projectile, Map map, ref bool __result)
        {
            if (!__result && map != null && projectile?.Map == map)
                __result = RimKataCombatExtendedProjectiles.IsActiveExplosive(projectile);
        }
        private static bool IsValidPrefix(Pawn pawn, Verb verb, Thing projectile, float rangeSquared, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(projectile)) return true;
            __result = RimKataCombatExtendedProjectiles.IsActiveExplosive(projectile)
                && projectile.Map == pawn?.Map && RimKataTargeting.IsProjectileVerb(verb)
                && RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled != false
                && RimKataTargeting.IsEnemyProjectileLauncher(pawn, projectile)
                && pawn.Position.DistanceToSquared(projectile.Position) <= rangeSquared
                && verb.TryFindShootLineFromTo(pawn.Position, projectile.Position, out _);
            return false;
        }
        private static bool IsEnemyPrefix(Pawn pawn, Thing projectile, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(projectile)) return true;
            __result = pawn != null && (pawn.Faction?.HostileTo(Faction.OfPlayer) == true
                ? RimKataCombatExtendedProjectiles.Launcher(projectile)?.Faction == Faction.OfPlayer
                : RimKataCombatExtendedProjectiles.Launcher(projectile)?.Faction != Faction.OfPlayer);
            return false;
        }
        private static bool CanInterceptPrefix(Pawn pawn, Verb verb, Thing target, int delayTicks,
            float? knownRangeSquared, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(target)
                && !RimKataCombatExtendedProjectiles.IsProjectileVerb(verb)) return true;
            __result = RimKataCombatExtendedTrajectory.CanIntercept(pawn, verb, target, delayTicks, knownRangeSquared);
            return false;
        }
        private static bool RedirectPrefix(Projectile shot, Thing target, Pawn pawn, Verb verb, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(target)) return true;
            __result = RimKataCombatExtendedTrajectory.RedirectVanilla(shot, target, pawn, verb);
            return false;
        }
        private static bool ContactPrefix(Projectile shot, Thing target, ref Vector3 point, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(target)) return true;
            __result = RimKataCombatExtendedTrajectory.VanillaContact(shot, target, out point);
            return false;
        }
        private static bool DeflectedPrefix(Thing sourceThing, Pawn attacker, Pawn defender, Verb sourceVerb)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(sourceThing)) return true;
            RimKataCombatExtendedTrajectory.SpawnDeflectedMiss(sourceThing, attacker, defender, sourceVerb);
            return false;
        }
        private static void HasImpactPostfix(ref bool __result)
        {
            if (RimKataCombatExtendedProjectiles.HasImpactScope)
                __result = RimKataCombatExtendedProjectiles.CurrentProjectile != null;
        }
        private static void CloseProjectilePostfix(ref RimKataCloseProjectileState __result)
        {
            if (RimKataCombatExtendedProjectiles.HasImpactScope)
                __result = RimKataCombatExtendedProjectiles.CurrentCloseShot;
        }
        private static bool AbsorbPrefix(Pawn defender, DamageInfo dinfo, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.HasImpactScope) return true;
            Thing projectile = RimKataCombatExtendedProjectiles.CurrentProjectile;
            // Base Impact owns area damage, which cannot borrow a direct-hit
            // decision even when it damages the same pawn in a nested scope.
            __result = projectile != null && RimKataCombatExtendedProjectiles.CurrentVictim == defender
                && RimKataDefenseUtility.TryAbsorbProjectileDamage(defender, dinfo, projectile,
                    RimKataCombatExtendedProjectiles.CurrentCloseShot,
                    RimKataCombatExtendedProjectiles.Launcher(projectile),
                    RimKataCombatExtendedProjectiles.IsDirectBullet(projectile),
                    RimKataCombatExtendedProjectiles.IsExplosive(projectile));
            return false;
        }
        private static bool ConsumeAvoidedPrefix(Thing projectile, Pawn defender,
            ref bool suppressJobNotification, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(projectile)) return true;
            __result = RimKataCombatExtendedProjectiles.WasAvoided(defender, out suppressJobNotification);
            return false;
        }
        private static bool PawnDamagePrefix(Pawn __instance, ref DamageInfo dinfo, ref bool absorbed)
        {
            if (RimKataCombatExtendedProjectiles.CurrentProjectile != null
                && RimKataCombatExtendedProjectiles.CurrentVictim == __instance
                && RimKataDefenseUtility.TryAbsorbAfterShield(__instance, dinfo))
            {
                absorbed = true;
                return false;
            }
            return true;
        }
        private static void HasExplosivesPostfix(RimKataMapComponent __instance, ref bool __result)
        {
            if (!__result) __result = RimKataCombatExtendedProjectiles.Component(__instance.map)?.HasExplosives == true;
        }
        private static void MarkAvoidedPostfix(RimKataMapComponent __instance, Pawn target, ref bool __result)
        {
            if (target != null) __result |= RimKataCombatExtendedProjectiles.Component(__instance.map)?.MarkAvoided(target) == true;
        }
        private static void HostileInRangePostfix(RimKataMapComponent __instance, Pawn pawn, float rangeSquared,
            ref bool __result)
        {
            if (!__result) __result = RimKataCombatExtendedProjectiles.Component(__instance.map)
                ?.HasHostileInRange(pawn, rangeSquared) == true;
        }
        private static bool CanSearch(RimKataMapComponent component, Pawn pawn, Verb verb, float rangeSquared) =>
            pawn?.Map == component.map && verb != null && !verb.IsMeleeAttack && rangeSquared > 0f
            && RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled != false;
        private static void FirstProjectilePostfix(RimKataMapComponent __instance, Pawn pawn, Verb verb,
            float rangeSquared, ref Thing candidate, ref bool __result)
        {
            if (__result || !CanSearch(__instance, pawn, verb, rangeSquared)) return;
            RimKataCEProjectileMapComponent component = RimKataCombatExtendedProjectiles.Component(__instance.map);
            if (component != null) __result = component.TryGetFirst(pawn, verb, rangeSquared, out candidate);
        }
        private static void AppendProjectilesPostfix(RimKataMapComponent __instance, Pawn pawn, Verb verb,
            float rangeSquared, List<Thing> destination)
        {
            if (destination != null && CanSearch(__instance, pawn, verb, rangeSquared))
                RimKataCombatExtendedProjectiles.Component(__instance.map)?.Append(pawn, verb, rangeSquared, destination);
        }

        private static bool ResolvePrefix(Pawn pawn, Thing targetProjectile, Vector3 impactPosition, ref bool __result)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(targetProjectile)) return true;
            __result = Resolve(pawn, targetProjectile, impactPosition);
            return false;
        }
        private static bool Resolve(Pawn pawn, Thing projectile, Vector3 impactPosition)
        {
            if (pawn?.Map == null || projectile?.Map != pawn.Map
                || !RimKataCombatExtendedProjectiles.IsActiveExplosive(projectile)) return false;
            Map impactMap = projectile.Map;
            bool critical = Rand.Chance(RimKataTargetAccess.SettingsFor(pawn)?.GetInterceptionCriticalChance(pawn) ?? 0f);
            bool grenade = RimKataDefOf.Grenades != null
                && RimKataCombatExtendedProjectiles.EquipmentDef(projectile)?.IsWithinCategory(RimKataDefOf.Grenades) == true;
            if (critical && !grenade) projectile.Destroy(DestroyMode.Vanish);
            else
            {
                IntVec3 impactCell = impactPosition.ToIntVec3();
                if (!critical)
                {
                    List<IntVec3> cells = GenAdj.AdjacentCellsAndInside.Select(offset => impactCell + offset)
                        .Where(cell => cell.InBounds(impactMap)).ToList();
                    if (cells.Count == 0) return false;
                    impactCell = cells.RandomElement();
                }
                Vector3 position = impactCell.ToVector3Shifted();
                position.y = impactPosition.y;
                RimKataCombatExtendedTrajectory.Place(projectile, position);
                RimKataCombatExtendedTrajectory.Impact(projectile, critical);
            }
            RimKataInterceptionUtility.PlaySuccessEffect(impactMap, impactPosition, 0f);
            return true;
        }
    }
}
