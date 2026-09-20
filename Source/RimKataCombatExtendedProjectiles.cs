using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // CE projectiles are Things, not Verse.Projectiles. Keep their native flight,
    // collision and explosion implementations, and store only RimKata decisions.
    internal static class RimKataCombatExtendedProjectiles
    {
        internal static Type ProjectileType, BulletType, VerbType;
        internal static Func<Thing, Vector3> Position, PreviousPosition;
        internal static Func<Thing, Thing> Launcher;
        internal static Func<Thing, ThingDef> EquipmentDef;
        internal static Func<Thing, LocalTargetInfo> IntendedTarget;
        internal static Func<Thing, bool> Landed;
        internal static MethodInfo ImpactMethod;
        private static Func<Thing, Thing> equipment;
        private static FieldInfo intendedTargetField;
        private static FieldInfo logMissesField;
        private static Type explosiveCompType;
        private static bool applied;

        internal sealed class ImpactScope
        {
            internal ImpactScope previous;
            internal Thing projectile;
            internal Pawn victim;
            internal RimKataCEProjectileState record;
            internal bool ownsFrame;
            internal bool direct;
        }

        private struct RayImpactState
        {
            internal bool modified;
            internal bool logMisses;
        }

        [ThreadStatic] private static ImpactScope current;
        internal static bool HasImpactScope => current != null;
        internal static bool Enabled => applied;
        internal static bool IsOwnedShot(Thing shot) => Launcher(shot) == RimKataFireContext.Shooter
            && RimKataFireContext.ActiveVerb != null && equipment(shot) == RimKataFireContext.ActiveVerb.EquipmentSource;
        internal static Thing CurrentProjectile => current?.direct == true ? current.projectile : null;
        internal static Pawn CurrentVictim => current?.direct == true ? current.victim : null;
        internal static RimKataCloseProjectileState CurrentCloseShot => current?.direct == true
            ? current.record?.closeShot : null;

        internal static void Apply(Harmony harmony)
        {
            if (applied) return;
            ProjectileType = AccessTools.TypeByName("CombatExtended.ProjectileCE");
            if (ProjectileType == null) return;
            BulletType = AccessTools.TypeByName("CombatExtended.BulletCE");
            VerbType = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
            explosiveCompType = AccessTools.TypeByName("CombatExtended.CompProperties_ExplosiveCE");
            Position = Property<Vector3>("ExactPosition");
            PreviousPosition = Field<Vector3>("LastPos");
            Launcher = Field<Thing>("launcher");
            EquipmentDef = Field<ThingDef>("equipmentDef");
            equipment = Field<Thing>("equipment");
            IntendedTarget = Field<LocalTargetInfo>("intendedTarget");
            intendedTargetField = AccessTools.Field(ProjectileType, "intendedTarget");
            logMissesField = AccessTools.Field(ProjectileType, "logMisses");
            Landed = Field<bool>("landed");
            ImpactMethod = AccessTools.DeclaredMethod(ProjectileType, "Impact", new[] { typeof(Thing) });
            if (BulletType == null || VerbType == null || ImpactMethod == null
                || !RimKataCombatExtendedTrajectory.Initialize())
                throw new InvalidOperationException("CE projectile API did not match.");

            harmony.Patch(AccessTools.Method(ProjectileType, "Launch",
                    new[] { typeof(Thing), typeof(Vector2), typeof(Thing) }),
                prefix: Patch(nameof(LaunchPrefix)), postfix: Patch(nameof(LaunchPostfix)));
            harmony.Patch(AccessTools.Method(ProjectileType, "Throw"), postfix: Patch(nameof(LaunchPostfix)));
            harmony.Patch(AccessTools.Method(ProjectileType, "CanCollideWith"), prefix: Patch(nameof(CanCollidePrefix)));
            harmony.Patch(AccessTools.Method(ProjectileType, "CheckForCollisionBetween"),
                postfix: Patch(nameof(CollisionPostfix)));
            Type laserType = AccessTools.TypeByName("CombatExtended.Lasers.LaserBeamCE");
            MethodInfo rayImpact = AccessTools.DeclaredMethod(laserType, "Impact", new[] { typeof(Thing), typeof(Vector3) });
            if (rayImpact != null)
                harmony.Patch(rayImpact, prefix: Patch(nameof(RayImpactPrefix)), finalizer: Patch(nameof(RayImpactFinalizer)));
            foreach (Type type in AccessTools.GetTypesFromAssembly(ProjectileType.Assembly))
            {
                if (!ProjectileType.IsAssignableFrom(type)) continue;
                MethodInfo impact = AccessTools.DeclaredMethod(type, "Impact", new[] { typeof(Thing) });
                if (impact != null && !impact.IsAbstract)
                    harmony.Patch(impact, prefix: Patch(nameof(ImpactPrefix)), finalizer: Patch(nameof(ImpactFinalizer)));
            }
            applied = true;
            RimKataCombatExtendedProjectileHooks.Apply(harmony);
        }

        private static HarmonyMethod Patch(string method) => new HarmonyMethod(
            typeof(RimKataCombatExtendedProjectiles), method) { priority = Priority.First };

        private static Func<Thing, T> Field<T>(string name)
        {
            ParameterExpression thing = Expression.Parameter(typeof(Thing), "thing");
            return Expression.Lambda<Func<Thing, T>>(Expression.Field(
                Expression.Convert(thing, ProjectileType), AccessTools.Field(ProjectileType, name)), thing).Compile();
        }

        private static Func<Thing, T> Property<T>(string name)
        {
            ParameterExpression thing = Expression.Parameter(typeof(Thing), "thing");
            return Expression.Lambda<Func<Thing, T>>(Expression.Call(
                Expression.Convert(thing, ProjectileType), AccessTools.PropertyGetter(ProjectileType, name)), thing).Compile();
        }

        internal static bool IsProjectile(Thing thing) => applied && thing != null && ProjectileType.IsInstanceOfType(thing);
        internal static bool IsProjectileVerb(Verb verb) => applied && verb != null && VerbType.IsInstanceOfType(verb);
        internal static RimKataCEProjectileMapComponent Component(Map map) => applied
            ? map?.GetComponent<RimKataCEProjectileMapComponent>() : null;
        internal static bool IsExplosive(Thing thing)
        {
            if (!IsProjectile(thing)) return false;
            if (thing.def?.projectile?.explosionRadius > 0f) return true;
            List<CompProperties> comps = thing.def?.comps;
            if (comps != null && explosiveCompType != null)
                for (int i = 0; i < comps.Count; i++)
                    if (explosiveCompType.IsInstanceOfType(comps[i])) return true;
            return false;
        }

        internal static bool IsActiveExplosive(Thing thing) => IsExplosive(thing)
            && thing.Spawned && !thing.Destroyed && !Landed(thing);

        internal static bool IsDirectBullet(Thing thing) => IsProjectile(thing)
            && BulletType.IsInstanceOfType(thing) && !IsExplosive(thing);

        internal static bool WasAvoided(Pawn defender, out bool suppressJobNotification)
        {
            RimKataCEProjectileState record = current?.record;
            suppressJobNotification = record?.suppressJobNotification == true;
            return CurrentProjectile != null && record?.target == defender && record.avoided;
        }

        private static void LaunchPrefix(Thing __instance, Thing launcher, Vector2 origin)
        {
            // These fields are consumed by CE's own Launch initialization below.
            if (!RimKataFireContext.SuppressCloseLaunch && RimKataFireContext.Shooter == launcher
                && RimKataFireContext.InterceptionShot)
                RimKataCombatExtendedTrajectory.AimInterception(__instance,
                    RimKataFireContext.InterceptionTarget, origin, RimKataFireContext.ActiveVerb);
        }

        private static void LaunchPostfix(Thing __instance)
        {
            if (__instance?.Spawned != true || __instance.Destroyed) return;
            Component(__instance.Map)?.Register(__instance, true);
        }

        internal static RimKataCloseProjectileState CaptureCloseShot(Thing shot)
        {
            Thing launcher = Launcher(shot);
            Pawn defender = IntendedTarget(shot).Pawn;
            bool owned = launcher == RimKataFireContext.Shooter
                && equipment(shot) == RimKataFireContext.ActiveVerb?.EquipmentSource;
            if (owned && RimKataFireContext.CloseShot && !RimKataFireContext.SuppressCloseLaunch
                && !RimKataFireContext.InterceptionShot)
            {
                return new RimKataCloseProjectileState
                {
                    target = RimKataFireContext.CloseTarget,
                    attackingVerb = RimKataFireContext.ActiveVerb,
                    meleeResolution = RimKataFireContext.CloseMeleeResolution,
                    meleeHit = RimKataFireContext.CloseMeleeHit,
                    precheck = RimKataFireContext.CloseDefensePrecheck
                };
            }
            Pawn attacker = launcher as Pawn;
            if (IsDirectBullet(shot) && attacker != null && defender != null
                && attacker.Map == defender.Map && attacker.Position.AdjacentTo8WayOrInside(defender.Position)
                && RimKataTargeting.IsAutomaticEnemy(defender, attacker)
                && defender.CanReachImmediate(attacker, PathEndMode.Touch)
                && RimKataEligibility.CanUseDefense(defender))
                return new RimKataCloseProjectileState { target = defender, meleeResolution = true,
                    meleeHit = true, attackingVerb = Patch_Verb_TryCastShot_RimKata.CurrentVerb };
            return null;
        }

        private static bool CanCollidePrefix(Thing __instance, Thing thing, ref bool __result)
        {
            RimKataCEProjectileState record = Component(__instance.Map)?.Find(__instance);
            if (record?.missedTarget == thing && thing != null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        private static void CollisionPostfix(Thing __instance, ref bool __result)
        {
            // Native collision checks walls, roofs and shields first. Only a
            // surviving segment can intercept; never teleport through a blocker.
            if (__result || __instance.Destroyed || !__instance.Spawned) return;
            RimKataCEProjectileState record = Component(__instance.Map)?.Find(__instance);
            if (record?.interceptionTarget == null || !RimKataTargeting.IsInterceptionTargetActive(record.interceptionTarget)) return;
            if (!RimKataCombatExtendedTrajectory.TryContact(__instance, record.interceptionTarget, out Vector3 contact)) return;
            Pawn shooter = Launcher(__instance) as Pawn;
            if (shooter == null || !RimKataInterceptionUtility.Resolve(shooter, record.interceptionTarget, contact)) return;
            Thing intercepted = record.interceptionTarget;
            record.interceptionTarget = null;
            RimKataCombatExtendedTrajectory.Place(__instance, contact);
            if (IsExplosive(__instance)) ImpactMethod.Invoke(__instance, new object[] { null });
            else
            {
                Find.BattleLog?.Add(new BattleLogEntry_RangedImpact(shooter, intercepted, intercepted,
                    EquipmentDef(__instance), __instance.def, null));
                __instance.Destroy(DestroyMode.Vanish);
            }
            __result = true;
        }

        private static void RayImpactPrefix(Thing __instance, ref Thing hitThing, Vector3 muzzle, out RayImpactState __state)
        {
            __state = default(RayImpactState);
            RimKataCEProjectileMapComponent component = Component(__instance.Map);
            if (component?.Find(__instance) == null) component?.Register(__instance, true);
            RimKataCEProjectileState record = component?.Find(__instance);
            Thing target = record?.interceptionTarget;
            if (target == null || target.Map != __instance.Map || !RimKataTargeting.IsInterceptionTargetActive(target)) return;
            Vector3 end = Position(__instance);
            Vector3 point = IsProjectile(target) ? Position(target) : ((Projectile)target).ExactPosition;
            if (!RimKataCombatExtendedTrajectory.TryRayContact(muzzle, end, point,
                target is Projectile, out Vector3 contact)) return;
            Pawn shooter = Launcher(__instance) as Pawn;
            if (shooter == null) return;
            // One CE ray can visit several native impact points (penetration).
            // Consume its link once, and never search beyond the native endpoint.
            record.interceptionTarget = null;
            if (!RimKataInterceptionUtility.Resolve(shooter, target, contact)) return;
            RimKataCombatExtendedTrajectory.Place(__instance, contact);
            hitThing = null;
            __state.modified = true;
            __state.logMisses = (bool)logMissesField.GetValue(__instance);
            logMissesField.SetValue(__instance, false);
            Find.BattleLog?.Add(new BattleLogEntry_RangedImpact(shooter, target, target,
                EquipmentDef(__instance), __instance.def, null));
        }

        private static Exception RayImpactFinalizer(Thing __instance, Exception __exception, RayImpactState __state)
        {
            if (__state.modified) logMissesField.SetValue(__instance, __state.logMisses);
            return __exception;
        }

        private static bool ImpactPrefix(Thing __instance, ref Thing hitThing, MethodBase __originalMethod,
            out ImpactScope __state)
        {
            RimKataCEProjectileMapComponent component = Component(__instance.Map);
            // CE's raycast weapons can reach Impact without calling Launch.
            // Their launch context is still live here; no map scan is needed.
            if (component?.Find(__instance) == null) component?.Register(__instance, true);
            RimKataCEProjectileState record = component?.Find(__instance);
            bool owns = current?.projectile != __instance;
            // Base Impact owns explosions. Do not reuse a direct-hit decision for
            // explosion damage to the same pawn or to any collateral victim.
            __state = new ImpactScope { previous = current, projectile = __instance,
                victim = hitThing as Pawn, record = record, ownsFrame = owns,
                direct = __originalMethod.DeclaringType != ProjectileType && hitThing is Pawn };
            current = __state;
            if (owns) RimKataDefenseUtility.EnterProjectileImpact();
            if (record?.pendingDodge == true && (hitThing == record.target
                || (hitThing == null && record.target?.Position == __instance.Position)))
            {
                record.pendingDodge = false;
                if (TryExplosiveDodge(record)) return false;
            }
            return true;
        }

        private static Exception ImpactFinalizer(Exception __exception, ImpactScope __state)
        {
            if (__state == null) return __exception;
            if (__state.ownsFrame) RimKataDefenseUtility.ExitProjectileImpact();
            current = __state.previous;
            return __exception;
        }

        internal static bool TryExplosiveDodge(RimKataCEProjectileState record)
        {
            Thing shot = record.projectile;
            Pawn defender = record.target;
            if (defender?.Map == null || shot?.Spawned != true || shot.Destroyed
                || shot.Map != defender.Map || !IsExplosive(shot)
                || !RimKataEligibility.CanRollRangedDodge(defender, false)) return false;
            Thing attacker = Launcher(shot);
            if (attacker == defender || (attacker?.Faction != null && attacker.Faction == defender.Faction)) return false;
            if (!RimKataCombatExtendedTrajectory.TryPrepareMiss(shot, defender, out Vector3 destination)) return false;
            if (!record.avoided && !RimKataDefenseUtility.TryRangedDodge(defender, attacker, null)) return false;
            if (!RimKataCombatExtendedTrajectory.Redirect(shot, destination)) return false;
            record.missedTarget = defender;
            intendedTargetField.SetValue(shot, new LocalTargetInfo(destination.ToIntVec3()));
            return true;
        }
    }

    internal sealed class RimKataCEProjectileState : IExposable
    {
        internal Thing projectile, interceptionTarget, missedTarget;
        internal Pawn target;
        internal bool avoided, suppressJobNotification, pendingDodge;
        internal bool explosive, activeExplosive;
        internal int dodgeTick;
        internal IntVec3 lastCell;
        internal RimKataCloseProjectileState closeShot;
        public void ExposeData()
        {
            Scribe_References.Look(ref projectile, "projectile");
            Scribe_References.Look(ref interceptionTarget, "interceptionTarget");
            Scribe_References.Look(ref missedTarget, "missedTarget");
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref avoided, "avoided");
            Scribe_Values.Look(ref suppressJobNotification, "suppressJobNotification");
            Scribe_Values.Look(ref pendingDodge, "pendingDodge");
            Scribe_Values.Look(ref dodgeTick, "dodgeTick");
            Scribe_Deep.Look(ref closeShot, "closeShot");
        }
    }

    // A map-local index, populated by launch events and rebuilt once on loading.
    // Per-hit operations never enumerate the map's Things or Pawns.
    public sealed class RimKataCEProjectileMapComponent : CustomMapComponent
    {
        private List<RimKataCEProjectileState> records = new List<RimKataCEProjectileState>();
        private readonly Dictionary<Thing, RimKataCEProjectileState> byProjectile = new Dictionary<Thing, RimKataCEProjectileState>();
        private int activeExplosiveCount;
        public RimKataCEProjectileMapComponent(Map map) : base(map) { }
        internal bool HasExplosives => activeExplosiveCount > 0;
        internal RimKataCEProjectileState Find(Thing projectile) => projectile != null
            && byProjectile.TryGetValue(projectile, out var record) ? record : null;

        internal void Register(Thing shot, bool capture)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectile(shot) || byProjectile.ContainsKey(shot)) return;
            Pawn target = RimKataCombatExtendedProjectiles.IntendedTarget(shot).Pawn;
            bool explosive = RimKataCombatExtendedProjectiles.IsExplosive(shot);
            bool interceptor = capture && RimKataFireContext.InterceptionShot
                && RimKataCombatExtendedProjectiles.IsOwnedShot(shot);
            RimKataCloseProjectileState close = capture ? RimKataCombatExtendedProjectiles.CaptureCloseShot(shot) : null;
            if (!explosive && !interceptor && close == null
                && (target == null || !RimKataEligibility.HasRimKataAccess(target))) return;
            var record = new RimKataCEProjectileState { projectile = shot, target = target,
                closeShot = close, lastCell = shot.Position,
                explosive = explosive, activeExplosive = explosive && !RimKataCombatExtendedProjectiles.Landed(shot),
                interceptionTarget = interceptor ? RimKataFireContext.InterceptionTarget : null,
                pendingDodge = capture && explosive && target != null,
                dodgeTick = (Verse.Find.TickManager?.TicksGame ?? 0) + 1 };
            records.Add(record);
            byProjectile.Add(shot, record);
            if (record.activeExplosive) activeExplosiveCount++;
            if (explosive) map.GetComponent<RimKataMapComponent>()?.NotifyCEProjectileChanged();
        }

        internal bool MarkAvoided(Pawn pawn)
        {
            bool changed = false;
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.target != pawn || record.projectile.Destroyed || !record.projectile.Spawned) continue;
                record.avoided = true;
                record.suppressJobNotification = RimKataEligibility.IsWorkMovementDefenseException(pawn);
                changed = true;
            }
            return changed;
        }

        internal void ExcludeDirectHit(Thing shot, Pawn defender)
        {
            RimKataCEProjectileState record = Find(shot);
            if (record == null)
            {
                record = new RimKataCEProjectileState { projectile = shot, lastCell = shot.Position,
                    explosive = RimKataCombatExtendedProjectiles.IsExplosive(shot) };
                record.activeExplosive = record.explosive && !RimKataCombatExtendedProjectiles.Landed(shot);
                if (record.activeExplosive) activeExplosiveCount++;
                records.Add(record);
                byProjectile.Add(shot, record);
            }
            record.missedTarget = defender;
        }

        internal void Append(Pawn pawn, Verb verb, float rangeSquared, List<Thing> destination)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (!records[i].activeExplosive) continue;
                Thing shot = records[i].projectile;
                if (RimKataTargeting.IsValidExplosiveProjectileForVerb(pawn, verb, shot, rangeSquared)
                    && !destination.Contains(shot)
                    && RimKataInterceptionTrajectory.CanIntercept(pawn, verb, shot, 0, rangeSquared)) destination.Add(shot);
            }
        }

        internal bool TryGetFirst(Pawn pawn, Verb verb, float rangeSquared, out Thing candidate)
        {
            candidate = null;
            for (int i = 0; i < records.Count; i++)
            {
                if (!records[i].activeExplosive) continue;
                Thing shot = records[i].projectile;
                if (!RimKataTargeting.IsValidExplosiveProjectileForVerb(pawn, verb, shot, rangeSquared)
                    || !RimKataInterceptionTrajectory.CanIntercept(pawn, verb, shot, 0, rangeSquared)) continue;
                candidate = shot;
                return true;
            }
            return false;
        }

        internal bool HasHostileInRange(Pawn pawn, float rangeSquared)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (!records[i].activeExplosive) continue;
                Thing shot = records[i].projectile;
                if (RimKataCombatExtendedProjectiles.IsActiveExplosive(shot)
                    && RimKataTargeting.IsEnemyProjectileLauncher(pawn, shot)
                    && pawn.Position.DistanceToSquared(shot.Position) <= rangeSquared) return true;
            }
            return false;
        }

        public override void MapComponentTick()
        {
            bool moved = false;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                Thing shot = record.projectile;
                if (shot == null || shot.Destroyed || !shot.Spawned || shot.Map != map)
                {
                    if (record.activeExplosive) activeExplosiveCount--;
                    if (shot != null) byProjectile.Remove(shot);
                    records.RemoveAt(i);
                    continue;
                }
                bool active = record.explosive && !RimKataCombatExtendedProjectiles.Landed(shot);
                if (active != record.activeExplosive)
                {
                    activeExplosiveCount += active ? 1 : -1;
                    record.activeExplosive = active;
                }
                if (record.pendingDodge && record.dodgeTick <= Verse.Find.TickManager.TicksGame)
                {
                    record.pendingDodge = false;
                    RimKataCombatExtendedProjectiles.TryExplosiveDodge(record);
                }
                if (record.lastCell != shot.Position && RimKataCombatExtendedProjectiles.IsActiveExplosive(shot)) moved = true;
                record.lastCell = shot.Position;
            }
            if (moved) map.GetComponent<RimKataMapComponent>()?.NotifyCEProjectileChanged();
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref records, "rimKataCEProjectiles", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                records ??= new List<RimKataCEProjectileState>();
                byProjectile.Clear();
                activeExplosiveCount = 0;
                for (int i = records.Count - 1; i >= 0; i--)
                {
                    var record = records[i];
                    if (record?.projectile == null || byProjectile.ContainsKey(record.projectile)) records.RemoveAt(i);
                    else
                    {
                        byProjectile.Add(record.projectile, record);
                        record.explosive = RimKataCombatExtendedProjectiles.IsExplosive(record.projectile);
                        record.activeExplosive = record.explosive
                            && RimKataCombatExtendedProjectiles.IsActiveExplosive(record.projectile);
                        if (record.activeExplosive) activeExplosiveCount++;
                    }
                }
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (!RimKataCombatExtendedProjectiles.Enabled) return;
            List<Thing> projectiles = map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
            for (int i = 0; i < projectiles.Count; i++) Register(projectiles[i], false);
        }
    }
}
