using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Only projectiles launched by the crawling shot context receive a tag.
    // Neither table owns a projectile, and cleanup happens on launch/save events.
    internal static class RimKataCrawlFireHits
    {
        private static ConditionalWeakTable<Thing, RimKataDownedWeaponState> Shots =
            new ConditionalWeakTable<Thing, RimKataDownedWeaponState>();
        private static ConditionalWeakTable<RimKataDownedWeaponState, List<System.WeakReference<Thing>>> Pending =
            new ConditionalWeakTable<RimKataDownedWeaponState, List<System.WeakReference<Thing>>>();

        internal static void ClearRuntime()
        {
            Shots = new ConditionalWeakTable<Thing, RimKataDownedWeaponState>();
            Pending = new ConditionalWeakTable<RimKataDownedWeaponState, List<System.WeakReference<Thing>>>();
        }

        internal static void NotifyLaunch(Thing projectile, Thing launcher, Thing equipment)
        {
            Verb verb = Patch_Verb_TryCastShot_RimKata.CurrentVerb;
            if (projectile == null || !RimKataCrawlFireUtility.IsCrawlVerb(verb)
                || launcher != verb.Caster || equipment != verb.EquipmentSource
                || !RimKataCrawlFireUtility.CanContinue(verb)
                || !RimKataDownedWeaponUtility.TryGet(verb.CasterPawn, out var record)
                || record.hasHitWhileCrawling || record.weapon != equipment)
                return;
            Remember(projectile, record);
        }

        private static void Remember(Thing projectile, RimKataDownedWeaponState record)
        {
            if (Shots.TryGetValue(projectile, out var existing) && ReferenceEquals(existing, record))
                return;
            Shots.Remove(projectile);
            Shots.Add(projectile, record);
            List<System.WeakReference<Thing>> pending = Pending.GetOrCreateValue(record);
            Prune(pending);
            pending.Add(new System.WeakReference<Thing>(projectile));
        }

        private static void Prune(List<System.WeakReference<Thing>> pending)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
                if (!pending[i].TryGetTarget(out Thing projectile) || projectile.Destroyed)
                    pending.RemoveAt(i);
        }

        internal static bool IsTrackedHit(RimKataDownedWeaponState record, Thing victim)
        {
            Thing projectile = ImpactProjectile(victim);
            return projectile != null && Shots.TryGetValue(projectile, out var owner)
                && ReferenceEquals(owner, record);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Thing ImpactProjectile(Thing victim) => RimKataProjectileImpactContext.CurrentProjectile;

        internal static void ExposeProjectiles(RimKataDownedWeaponState record)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                record.crawlProjectiles = null;
                if (record.activeDowned && !record.hasHitWhileCrawling
                    && Pending.TryGetValue(record, out var pending))
                {
                    Prune(pending);
                    foreach (System.WeakReference<Thing> reference in pending)
                        if (reference.TryGetTarget(out Thing projectile) && projectile.Spawned)
                        {
                            record.crawlProjectiles ??= new List<Thing>();
                            record.crawlProjectiles.Add(projectile);
                        }
                }
            }
            Scribe_Collections.Look(ref record.crawlProjectiles, "crawlProjectiles", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (record.activeDowned && !record.hasHitWhileCrawling && record.crawlProjectiles != null)
                    foreach (Thing projectile in record.crawlProjectiles)
                        if (projectile != null && !projectile.Destroyed)
                            Remember(projectile, record);
                record.crawlProjectiles = null;
            }
            else if (Scribe.mode == LoadSaveMode.Saving)
                record.crawlProjectiles = null;
        }

        // CE's projectiles are Things rather than Verse.Projectiles. Install
        // these launch and impact bridges only when the CE adapter is installed.
        internal static void ApplyCombatExtended(Harmony harmony)
        {
            Type projectile = AccessTools.TypeByName("CombatExtended.ProjectileCE");
            MethodInfo launch = AccessTools.DeclaredMethod(projectile, "Launch",
                new[] { typeof(Thing), typeof(Vector2), typeof(Thing) });
            MethodInfo ray = AccessTools.DeclaredMethod(projectile, "RayCast", new[] {
                typeof(Thing), typeof(VerbProperties), typeof(Vector2), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(bool) });
            if (launch?.ReturnType != typeof(void) || ray?.ReturnType != typeof(void))
                throw new InvalidOperationException("CE crawling shot launch API does not match.");
            var prefix = new HarmonyMethod(typeof(RimKataCrawlFireHits), nameof(LaunchPrefix));
            harmony.Patch(launch, prefix: prefix);
            harmony.Patch(ray, prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(RimKataCrawlFireHits), nameof(ImpactProjectile)),
                postfix: new HarmonyMethod(typeof(RimKataCrawlFireHits), nameof(CEImpactPostfix)));
        }

        private static void LaunchPrefix(Thing __instance, Thing launcher, Thing equipment) =>
            NotifyLaunch(__instance, launcher, equipment);

        private static void CEImpactPostfix(Thing victim, ref Thing __result)
        {
            if (RimKataCombatExtendedProjectiles.HasImpactScope)
                __result = RimKataCombatExtendedProjectiles.CurrentImpactProjectile;
        }
    }

    [HarmonyPatch]
    internal static class Patch_Projectile_RimKataCrawlLaunch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch),
            new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
                typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) });

        private static void Prefix(Projectile __instance, Thing launcher, Thing equipment) =>
            RimKataCrawlFireHits.NotifyLaunch(__instance, launcher, equipment);
    }
}
