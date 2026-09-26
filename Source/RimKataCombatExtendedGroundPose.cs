using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // CE owns the shot cycle and samples its own spread, recoil and locked burst
    // angles. Move only the resulting shot, preserving that sampled trajectory.
    internal static class RimKataCombatExtendedGroundPose
    {
        internal sealed class ShotScope
        {
            internal Verb verb;
            internal Vector3 center;
            internal readonly HashSet<Thing> moved = new HashSet<Thing>();
        }

        [ThreadStatic] private static ShotScope current;
        private static bool applied;
        private static Func<Verb, float> distance;
        private static Func<Verb, float, float> minCollisionDistance;
        private static Action<Thing, float> setMinCollisionDistance;
        private static Func<ProjectileProperties, float> gravity;
        private static Func<ProjectileProperties, Vector3, Vector3, float, float?> findAngle;
        private static Func<Verb, ThingDef> projectileDef;
        private static Func<Thing, object> projectileWorker;
        private static Func<ProjectileProperties, object> propertiesWorker;
        private static Func<ProjectileProperties, int> ticksToTruePosition;
        private static Func<ProjectileProperties, bool> isInstant;
        private static Func<object, Vector3, int, int, float, Vector3> drawPosition;

        internal static void Apply(Harmony harmony)
        {
            if (applied) return;
            Type verbType = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
            if (verbType == null) return;
            Type projectileType = AccessTools.TypeByName("CombatExtended.ProjectileCE");
            Type propsType = AccessTools.TypeByName("CombatExtended.ProjectilePropertiesCE");
            Type workerType = AccessTools.TypeByName("CombatExtended.BaseTrajectoryWorker");
            if (projectileType == null || propsType == null || workerType == null)
                throw new InvalidOperationException("CE ground-pose types did not match.");

            MethodInfo shot = AccessTools.DeclaredMethod(verbType, "TryCastShot", Type.EmptyTypes);
            MethodInfo line = AccessTools.DeclaredMethod(verbType, "CanHitCellFromCellIgnoringRange",
                new[] { typeof(Vector3), typeof(IntVec3), typeof(Thing) });
            MethodInfo launch = AccessTools.DeclaredMethod(projectileType, "Launch", new[] {
                typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(Thing), typeof(float) });
            MethodInfo ray = AccessTools.DeclaredMethod(projectileType, "RayCast", new[] {
                typeof(Thing), typeof(VerbProperties), typeof(Vector2), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(bool) });
            FieldInfo distanceField = AccessTools.Field(verbType, "distance");
            FieldInfo collisionField = AccessTools.Field(projectileType, "minCollisionDistance");
            MethodInfo collision = AccessTools.Method(verbType, "GetMinCollisionDistance", new[] { typeof(float) });
            MethodInfo gravityGetter = AccessTools.PropertyGetter(propsType, "GravityPerWidth");
            MethodInfo angle = AccessTools.Method(workerType, "TryFindShotAngle", new[] {
                propsType, typeof(Vector3), typeof(Vector3), typeof(float?) });
            MethodInfo projectileGetter = AccessTools.PropertyGetter(verbType, "Projectile");
            MethodInfo workerGetter = AccessTools.PropertyGetter(projectileType, "TrajectoryWorker");
            MethodInfo propsWorkerGetter = AccessTools.PropertyGetter(propsType, "TrajectoryWorker");
            MethodInfo ticksGetter = AccessTools.PropertyGetter(propsType, "TickToTruePos");
            FieldInfo instantField = AccessTools.Field(propsType, "isInstant");
            MethodInfo projection = AccessTools.Method(workerType, "ExactPosToDrawPos", new[] {
                typeof(Vector3), typeof(int), typeof(int), typeof(float) });
            if (shot?.ReturnType != typeof(bool) || line?.ReturnType != typeof(bool)
                || launch?.ReturnType != typeof(void) || ray?.ReturnType != typeof(void)
                || distanceField?.FieldType != typeof(float) || collisionField?.FieldType != typeof(float)
                || collision?.ReturnType != typeof(float) || gravityGetter?.ReturnType != typeof(float)
                || angle?.ReturnType != typeof(float?) || !angle.IsStatic
                || projectileGetter?.ReturnType != typeof(ThingDef)
                || workerGetter?.ReturnType != workerType || propsWorkerGetter?.ReturnType != workerType
                || ticksGetter?.ReturnType != typeof(int) || instantField?.FieldType != typeof(bool)
                || projection?.ReturnType != typeof(Vector3))
                throw new InvalidOperationException("CE ground-pose firing API did not match.");

            ParameterExpression verb = Expression.Parameter(typeof(Verb), "verb");
            ParameterExpression length = Expression.Parameter(typeof(float), "length");
            ParameterExpression thing = Expression.Parameter(typeof(Thing), "thing");
            ParameterExpression props = Expression.Parameter(typeof(ProjectileProperties), "props");
            ParameterExpression from = Expression.Parameter(typeof(Vector3), "from");
            ParameterExpression to = Expression.Parameter(typeof(Vector3), "to");
            ParameterExpression speed = Expression.Parameter(typeof(float), "speed");
            ParameterExpression worker = Expression.Parameter(typeof(object), "worker");
            ParameterExpression flightTick = Expression.Parameter(typeof(int), "flightTick");
            ParameterExpression trueTick = Expression.Parameter(typeof(int), "trueTick");
            ParameterExpression altitude = Expression.Parameter(typeof(float), "altitude");
            distance = Expression.Lambda<Func<Verb, float>>(
                Expression.Field(Expression.Convert(verb, verbType), distanceField), verb).Compile();
            minCollisionDistance = Expression.Lambda<Func<Verb, float, float>>(
                Expression.Call(Expression.Convert(verb, verbType), collision, length), verb, length).Compile();
            setMinCollisionDistance = Expression.Lambda<Action<Thing, float>>(
                Expression.Assign(Expression.Field(Expression.Convert(thing, projectileType), collisionField), length),
                thing, length).Compile();
            gravity = Expression.Lambda<Func<ProjectileProperties, float>>(
                Expression.Call(Expression.Convert(props, propsType), gravityGetter), props).Compile();
            findAngle = Expression.Lambda<Func<ProjectileProperties, Vector3, Vector3, float, float?>>(
                Expression.Call(angle, Expression.Convert(props, propsType), from, to,
                    Expression.Convert(speed, typeof(float?))), props, from, to, speed).Compile();
            projectileDef = Expression.Lambda<Func<Verb, ThingDef>>(
                Expression.Call(Expression.Convert(verb, verbType), projectileGetter), verb).Compile();
            projectileWorker = Expression.Lambda<Func<Thing, object>>(
                Expression.Convert(Expression.Call(Expression.Convert(thing, projectileType), workerGetter),
                    typeof(object)), thing).Compile();
            propertiesWorker = Expression.Lambda<Func<ProjectileProperties, object>>(
                Expression.Convert(Expression.Call(Expression.Convert(props, propsType), propsWorkerGetter),
                    typeof(object)), props).Compile();
            ticksToTruePosition = Expression.Lambda<Func<ProjectileProperties, int>>(
                Expression.Call(Expression.Convert(props, propsType), ticksGetter), props).Compile();
            isInstant = Expression.Lambda<Func<ProjectileProperties, bool>>(
                Expression.Field(Expression.Convert(props, propsType), instantField), props).Compile();
            drawPosition = Expression.Lambda<Func<object, Vector3, int, int, float, Vector3>>(
                Expression.Call(Expression.Convert(worker, workerType), projection, from, flightTick, trueTick, altitude),
                worker, from, flightTick, trueTick, altitude).Compile();

            // A separate owner lets API/patch failure remove only this optional
            // adapter, leaving all established CE compatibility intact.
            var adapter = new Harmony(harmony.Id + ".groundPoseCE");
            try
            {
                adapter.Patch(shot, prefix: Patch(nameof(ShotPrefix)), finalizer: Patch(nameof(ShotFinalizer)));
                adapter.Patch(line, prefix: Patch(nameof(LinePrefix)));
                adapter.Patch(launch, prefix: Patch(nameof(LaunchPrefix)));
                adapter.Patch(ray, prefix: Patch(nameof(RayPrefix)));
                applied = true;
            }
            catch
            {
                adapter.UnpatchAll(adapter.Id);
                throw;
            }
        }

        private static HarmonyMethod Patch(string name) => new HarmonyMethod(
            typeof(RimKataCombatExtendedGroundPose), name) { priority = Priority.Last };

        private static void ShotPrefix(Verb __instance, out ShotScope __state)
        {
            __state = current;
            current = applied && RimKataGroundPoseUtility.TryGetShotCenter(__instance, out Vector3 center, headCentered: true)
                ? new ShotScope { verb = __instance, center = center } : null;
        }

        private static void ShotFinalizer(ShotScope __state) => current = __state;

        private static void LinePrefix(Verb __instance, ref Vector3 shotSource)
        {
            if (!applied) return;
            Vector3 center;
            if (current?.verb == __instance) center = current.center;
            else if (!RimKataGroundPoseUtility.TryGetShotCenter(__instance, out center, headCentered: true)) return;
            ProjectileProperties props = projectileDef(__instance)?.projectile;
            if (props == null || !TryPhysicalOrigin(center, shotSource.y, props, propertiesWorker(props),
                isInstant(props), out Vector2 origin)) return;
            // The submitted gun position already includes its current aiming
            // placement. A second CE lean source would not match that muzzle.
            shotSource.x = origin.x;
            shotSource.z = origin.y;
        }

        private static void LaunchPrefix(Thing __instance, Thing launcher, Thing equipment,
            ref Vector2 origin, ref float shotAngle, ref float shotRotation, float shotHeight,
            float shotSpeed, ref float distance)
        {
            Adjust(__instance, launcher, equipment, false, shotHeight, shotSpeed,
                ref origin, ref shotAngle, ref shotRotation, ref distance);
        }

        private static void RayPrefix(Thing __instance, Thing launcher, Thing equipment,
            ref Vector2 origin, ref float shotAngle, ref float shotRotation, float shotHeight, float shotSpeed)
        {
            if (current == null) return;
            float length = distance(current.verb);
            Adjust(__instance, launcher, equipment, true, shotHeight, shotSpeed,
                ref origin, ref shotAngle, ref shotRotation, ref length);
        }

        private static void Adjust(Thing projectile, Thing launcher, Thing equipment, bool instant,
            float height, float speed, ref Vector2 origin, ref float angle, ref float rotation, ref float length)
        {
            ShotScope scope = current;
            if (!applied || scope == null || projectile == null || scope.moved.Contains(projectile)
                || launcher != scope.verb.Caster || equipment != scope.verb.EquipmentSource) return;
            ProjectileProperties props = projectile.def?.projectile;
            if (props == null) return;
            if (!TryPhysicalOrigin(scope.center, height, props, projectileWorker(projectile),
                instant, out Vector2 physicalOrigin)) return;
            Vector3 offset = new Vector3(physicalOrigin.x - origin.x, 0f, physicalOrigin.y - origin.y);
            float effectiveSpeed = Mathf.Max(speed, props.speed);
            float shotGravity = instant ? 0f : gravity(props);
            if (!TryMoveTrajectory(origin, offset, height, effectiveSpeed, shotGravity,
                length, angle, rotation, instant, out Vector2 movedOrigin,
                out Vector3 preservedPoint, out float movedLength)) return;
            Vector3 movedFrom = new Vector3(movedOrigin.x, height, movedOrigin.y);
            float? movedAngle = instant || Mathf.Abs(shotGravity) < 0.000001f
                ? (float?)Mathf.Atan2(preservedPoint.y - height, movedLength)
                : findAngle(props, movedFrom, preservedPoint, effectiveSpeed);
            if (!movedAngle.HasValue || !Finite(movedAngle.Value)) return;
            Vector3 delta = preservedPoint - movedFrom;
            float movedRotation = -90f + Mathf.Atan2(delta.z, delta.x) * Mathf.Rad2Deg;

            origin = movedOrigin;
            angle = movedAngle.Value;
            rotation += Mathf.DeltaAngle(rotation, movedRotation);
            length = movedLength;
            scope.moved.Add(projectile);
            LocalTargetInfo target = scope.verb.CurrentTarget;
            if (target.IsValid)
            {
                Vector3 targetPosition = target.Cell.ToVector3Shifted();
                float targetDistance = new Vector2(targetPosition.x - origin.x, targetPosition.z - origin.y).magnitude;
                setMinCollisionDistance(projectile, minCollisionDistance(scope.verb, targetDistance));
            }
        }

        private static bool TryPhysicalOrigin(Vector3 center, float height, ProjectileProperties props,
            object worker, bool instant, out Vector2 origin)
        {
            origin = new Vector2(center.x, center.z);
            if (!Finite(center.x) || !Finite(center.z) || !Finite(height)) return false;
            // RayCast beams use physical x/z directly; their own barrel length
            // remains intact. Flying projectiles use their actual worker's
            // projection, including optional height-to-z and its tick ramp.
            if (instant) return true;
            if (worker == null) return false;
            int ticks = ticksToTruePosition(props);
            Vector3 physical = new Vector3(center.x, height, center.z);
            Vector3 drawn = drawPosition(worker, physical, 0, ticks, 0f);
            if (!Finite(drawn.x) || !Finite(drawn.z)) return false;
            physical.x += center.x - drawn.x;
            physical.z += center.z - drawn.z;
            Vector3 verified = drawPosition(worker, physical, 0, ticks, 0f);
            if (!Finite(verified.x) || !Finite(verified.z)
                || Mathf.Abs(verified.x - center.x) > 0.0001f
                || Mathf.Abs(verified.z - center.z) > 0.0001f) return false;
            origin = new Vector2(physical.x, physical.z);
            return true;
        }

        // Keep the endpoint of CE's already sampled trajectory at its target
        // distance. This also preserves locked mid-burst aim and pellet spread;
        // neither ShiftTarget nor its random draws are replayed or overwritten.
        internal static bool TryMoveTrajectory(Vector2 origin, Vector3 offset, float height,
            float speed, float gravity, float length, float angle, float rotation, bool instant,
            out Vector2 movedOrigin, out Vector3 preservedPoint, out float movedLength)
        {
            movedOrigin = origin;
            preservedPoint = default(Vector3);
            movedLength = length;
            float cosine = Mathf.Cos(angle);
            if (!Finite(length) || length <= 0.0001f || !Finite(angle) || !Finite(rotation)
                || !Finite(height) || !Finite(offset.x) || !Finite(offset.z)
                || (!instant && (!Finite(speed) || speed <= 0f || !Finite(gravity)))
                || cosine <= 0.0001f) return false;
            float radians = rotation * Mathf.Deg2Rad;
            float targetHeight = height + Mathf.Tan(angle) * length;
            if (!instant)
            {
                float seconds = length / (speed * cosine);
                targetHeight -= gravity * seconds * seconds * 0.5f;
            }
            preservedPoint = new Vector3(origin.x - Mathf.Sin(radians) * length, targetHeight,
                origin.y + Mathf.Cos(radians) * length);
            movedOrigin = origin + new Vector2(offset.x, offset.z);
            movedLength = new Vector2(preservedPoint.x - movedOrigin.x, preservedPoint.z - movedOrigin.y).magnitude;
            return Finite(targetHeight) && Finite(movedLength) && movedLength > 0.0001f;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
