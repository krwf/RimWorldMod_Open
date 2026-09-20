using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedTrajectory
    {
        private static MethodInfo workerGetter, propsWorkerGetter, predict, canReach, shotAngle,
            shotRotation, initialVelocity, flightTime, distanceTraveled, exactSetter, verbProjectile, launch;
        private static FieldInfo intendedTargetField, equipmentField, extraDamagesField, instantField;
        private static FieldInfo[] retainedFields;
        private static FieldInfo originField, destinationField, velocityField, angleField, rotationField,
            heightField, speedField, initialSpeedField, gravityField, flightTicksField, remainingField,
            startingTicksField, lastPositionField, landedField, predictionField;
        private static Action<Thing, Thing> immediateImpact;
        private static readonly AccessTools.FieldRef<Projectile, Vector3> VanillaOrigin =
            AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> VanillaDestination =
            AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
        private static readonly AccessTools.FieldRef<Projectile, int> VanillaRemaining =
            AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");
        private static readonly AccessTools.FieldRef<Projectile, int> VanillaLifetime =
            AccessTools.FieldRefAccess<Projectile, int>("lifetime");
        private const int PredictionTicks = 60;
        private const float ContactToleranceSquared = 0.0001f;

        internal static bool Initialize()
        {
            Type projectile = RimKataCombatExtendedProjectiles.ProjectileType;
            Type worker = AccessTools.TypeByName("CombatExtended.BaseTrajectoryWorker");
            Type props = AccessTools.TypeByName("CombatExtended.ProjectilePropertiesCE");
            if (worker == null || props == null) return false;
            workerGetter = AccessTools.PropertyGetter(projectile, "TrajectoryWorker");
            propsWorkerGetter = AccessTools.PropertyGetter(props, "TrajectoryWorker");
            verbProjectile = AccessTools.PropertyGetter(RimKataCombatExtendedProjectiles.VerbType, "Projectile");
            predict = AccessTools.Method(worker, "PredictPositions");
            canReach = AccessTools.Method(worker, "CanReachPos");
            shotAngle = AccessTools.Method(worker, "TryFindShotAngle");
            shotRotation = AccessTools.Method(worker, "ShotRotation");
            initialVelocity = AccessTools.Method(worker, "GetInitialVelocity");
            flightTime = AccessTools.Method(worker, "GetFlightTime");
            distanceTraveled = AccessTools.Method(worker, "DistanceTraveled");
            exactSetter = AccessTools.PropertySetter(projectile, "ExactPosition");
            originField = AccessTools.Field(projectile, "origin");
            destinationField = AccessTools.Field(projectile, "Destination");
            velocityField = AccessTools.Field(projectile, "velocity");
            angleField = AccessTools.Field(projectile, "shotAngle");
            rotationField = AccessTools.Field(projectile, "shotRotation");
            heightField = AccessTools.Field(projectile, "shotHeight");
            speedField = AccessTools.Field(projectile, "shotSpeed");
            initialSpeedField = AccessTools.Field(projectile, "initialSpeed");
            gravityField = AccessTools.Field(projectile, "GravityPerWidth");
            flightTicksField = AccessTools.Field(projectile, "FlightTicks");
            remainingField = AccessTools.Field(projectile, "intTicksToImpact");
            startingTicksField = AccessTools.Field(projectile, "startingTicksToImpact");
            lastPositionField = AccessTools.Field(projectile, "LastPos");
            landedField = AccessTools.Field(projectile, "landed");
            predictionField = AccessTools.Field(projectile, "cachedPredictedPositions");
            intendedTargetField = AccessTools.Field(projectile, "intendedTarget");
            equipmentField = AccessTools.Field(projectile, "equipment");
            extraDamagesField = AccessTools.Field(projectile, "extraDamages");
            instantField = AccessTools.Field(props, "isInstant");
            launch = AccessTools.Method(projectile, "Launch", new[] { typeof(Thing), typeof(Vector2),
                typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(float) });
            retainedFields = new[] { AccessTools.Field(projectile, "damageDefOverride"),
                AccessTools.Field(projectile, "damageAmount"), AccessTools.Field(projectile, "traitExplosion"),
                AccessTools.Field(projectile, "ballisticCoefficient"), AccessTools.Field(projectile, "mass"),
                AccessTools.Field(projectile, "radius"), AccessTools.Field(projectile, "homingAcceleration") };
            foreach (object member in new object[] { workerGetter, propsWorkerGetter, predict, canReach,
                shotAngle, shotRotation, initialVelocity, flightTime, distanceTraveled, exactSetter,
                originField, destinationField, velocityField, angleField, rotationField, heightField,
                speedField, initialSpeedField, gravityField, flightTicksField, remainingField,
                startingTicksField, lastPositionField, landedField, predictionField, verbProjectile,
                launch, intendedTargetField, equipmentField, extraDamagesField, instantField })
                if (member == null) return false;
            foreach (FieldInfo field in retainedFields) if (field == null) return false;
            // Critical grenade interception must bypass its fuse override, while
            // still using CE's real base explosion/fragment implementation.
            var method = new DynamicMethod("RimKataCEImmediateImpact", typeof(void),
                new[] { typeof(Thing), typeof(Thing) }, typeof(RimKataCombatExtendedTrajectory), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, projectile);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, RimKataCombatExtendedProjectiles.ImpactMethod);
            il.Emit(OpCodes.Ret);
            immediateImpact = (Action<Thing, Thing>)method.CreateDelegate(typeof(Action<Thing, Thing>));
            return true;
        }

        internal static ThingDef ProjectileFor(Verb verb) => verb is Verb_LaunchProjectile vanilla
            ? vanilla.Projectile : RimKataCombatExtendedProjectiles.IsProjectileVerb(verb)
                ? verbProjectile.Invoke(verb, null) as ThingDef : null;

        private static bool TryAngles(object worker, ProjectileProperties props, Vector3 origin, Vector3 target,
            float speed, out float angle, out float rotation)
        {
            angle = rotation = 0f;
            object value = shotAngle.Invoke(worker, new object[] { props, origin, target, (float?)speed });
            if (!(value is float result) || float.IsNaN(result) || float.IsInfinity(result)) return false;
            angle = result;
            rotation = (float)shotRotation.Invoke(worker, new object[] { props, origin, target });
            return !float.IsNaN(rotation) && !float.IsInfinity(rotation);
        }

        private static List<Vector3> Predict(Thing target)
        {
            var result = new List<Vector3>(PredictionTicks + 1);
            if (RimKataCombatExtendedProjectiles.IsProjectile(target))
            {
                Vector3 position = RimKataCombatExtendedProjectiles.Position(target);
                result.Add(position);
                object worker = workerGetter.Invoke(target, null);
                var positions = (IEnumerable<Vector3>)predict.Invoke(worker, new object[] { target, PredictionTicks });
                foreach (Vector3 point in positions)
                {
                    // Lerped CE predictions include their current position;
                    // ballistic CE predictions start at the following tick.
                    if (result.Count == 1 && (point - position).sqrMagnitude <= ContactToleranceSquared) continue;
                    if (point.y < 0f || !point.InBounds(target.Map)) break;
                    result.Add(point);
                    if (result.Count > PredictionTicks) break;
                }
            }
            else if (target is Projectile vanilla)
            {
                int remaining = VanillaRemaining(vanilla);
                if (remaining <= 0) return result;
                Vector3 from = vanilla.ExactPosition;
                Vector3 destination = VanillaDestination(vanilla);
                for (int tick = 0; tick < Math.Min(remaining, PredictionTicks); tick++)
                    result.Add(Vector3.Lerp(from, destination, tick / (float)remaining));
            }
            return result;
        }

        private static bool TryPredictInterception(Thing target, Vector3 origin, ThingDef projectileDef,
            bool nativeCE, float speed, int delayTicks, out Vector3 contact)
        {
            contact = default(Vector3);
            if (target?.Map == null || projectileDef?.projectile == null || speed <= 0f) return false;
            List<Vector3> path = Predict(target);
            object worker = nativeCE ? propsWorkerGetter.Invoke(projectileDef.projectile, null) : null;
            for (int tick = Math.Max(1, delayTicks + 1); tick < path.Count; tick++)
            {
                Vector3 point = path[tick];
                int travelTicks;
                if (nativeCE)
                {
                    object[] args = { projectileDef.projectile, speed, origin, point, 0 };
                    if (!(bool)canReach.Invoke(worker, args)) continue;
                    travelTicks = (int)args[4];
                }
                else travelTicks = Mathf.CeilToInt((point.Yto0() - origin.Yto0()).magnitude / speed);
                if (Math.Abs(travelTicks + delayTicks - tick) > 1) continue;
                contact = point;
                return true;
            }
            return false;
        }

        internal static bool CanIntercept(Pawn pawn, Verb verb, Thing target, int delayTicks, float? knownRangeSquared)
        {
            if (pawn?.Map == null || target?.Map != pawn.Map || !RimKataTargeting.IsInterceptionTargetActive(target)) return false;
            ThingDef projectile = ProjectileFor(verb);
            bool ce = RimKataCombatExtendedProjectiles.IsProjectileVerb(verb);
            float range = knownRangeSquared ?? Mathf.Pow(RimKataRangeUtility.ResolveEffectiveRange(pawn, verb.EquipmentSource, verb), 2f);
            // Hitscan CE weapons have no future flight. Eligibility uses the
            // current target; the actual native ray segment decides contact.
            if (ce && projectile?.projectile != null && (bool)instantField.GetValue(projectile.projectile))
                return pawn.Position.DistanceToSquared(target.Position) <= range;
            float speed = ce ? projectile?.projectile?.speed ?? 0f : projectile?.projectile?.SpeedTilesPerTick ?? 0f;
            Vector3 origin = pawn.DrawPos;
            origin.y = ce ? 1f : 0f;
            if (!TryPredictInterception(target, origin, projectile, ce, speed, Math.Max(0, delayTicks), out Vector3 contact)) return false;
            return contact.InBounds(pawn.Map) && pawn.Position.DistanceToSquared(contact.ToIntVec3()) <= range;
        }

        internal static void AimInterception(Thing shot, Thing target, Vector2 origin2, Verb verb)
        {
            if (target?.Map != shot.Map || !RimKataTargeting.IsInterceptionTargetActive(target)) return;
            Vector3 origin = new Vector3(origin2.x, (float)heightField.GetValue(shot), origin2.y);
            float speed = (float)speedField.GetValue(shot);
            if (!TryPredictInterception(target, origin, shot.def, true, speed, 0, out Vector3 contact)) return;
            object worker = workerGetter.Invoke(shot, null);
            Vector3 previousTarget = RimKataCombatExtendedProjectiles.IsProjectile(target)
                ? RimKataCombatExtendedProjectiles.Position(target) : target.DrawPos;
            if (!TryAngles(worker, shot.def.projectile, origin, contact, speed, out float angle, out float rotation)
                || !TryAngles(worker, shot.def.projectile, origin, previousTarget, speed, out float oldAngle, out float oldRotation)) return;
            // Retain CE's sampled spread/sway; prediction changes only its aim point.
            angleField.SetValue(shot, angle + (float)angleField.GetValue(shot) - oldAngle);
            rotationField.SetValue(shot, rotation + Mathf.DeltaAngle(oldRotation, (float)rotationField.GetValue(shot)));
        }

        internal static bool TryPrepareMiss(Thing shot, Pawn defender, out Vector3 target)
        {
            target = default(Vector3);
            Vector3 origin = RimKataCombatExtendedProjectiles.Position(shot);
            object worker = workerGetter.Invoke(shot, null);
            float speed = (float)speedField.GetValue(shot);
            Vector3 direction = (defender.DrawPos - origin).Yto0().normalized;
            if (direction.sqrMagnitude < 0.1f) direction = Vector3.forward;
            Vector3 side = new Vector3(-direction.z, 0f, direction.x);
            int first = Rand.Bool ? 1 : -1;
            for (int i = 0; i < 2; i++)
            {
                Vector3 candidate = defender.Position.ToVector3Shifted() + side * (i == 0 ? first : -first) * 1.5f;
                candidate.y = 0f;
                if (!candidate.InBounds(shot.Map) || candidate.ToIntVec3() == defender.Position
                    || !GenSight.LineOfSight(shot.Position, candidate.ToIntVec3(), shot.Map, true)) continue;
                if (!TryAngles(worker, shot.def.projectile, origin, candidate, speed, out _, out _)) continue;
                target = candidate;
                return true;
            }
            return false;
        }

        internal static bool Redirect(Thing shot, Vector3 target)
        {
            Vector3 origin = RimKataCombatExtendedProjectiles.Position(shot);
            object worker = workerGetter.Invoke(shot, null);
            float speed = (float)speedField.GetValue(shot);
            if (!TryAngles(worker, shot.def.projectile, origin, target, speed, out float angle, out float rotation)) return false;
            float gravity = (float)gravityField.GetValue(shot);
            float duration = (float)flightTime.Invoke(worker, new object[] { angle, speed, gravity, origin.y }) * 60f;
            if (duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration)) return false;
            Vector3 velocity = (Vector3)initialVelocity.Invoke(worker, new object[] { speed, rotation, angle });
            float distance = (float)distanceTraveled.Invoke(worker, new object[] { origin.y, speed, angle, gravity });
            Vector2 origin2 = new Vector2(origin.x, origin.z);
            originField.SetValue(shot, origin2);
            heightField.SetValue(shot, origin.y);
            angleField.SetValue(shot, angle);
            rotationField.SetValue(shot, rotation);
            velocityField.SetValue(shot, velocity);
            speedField.SetValue(shot, velocity.magnitude * 60f);
            initialSpeedField.SetValue(shot, velocity.magnitude * 60f);
            destinationField.SetValue(shot, origin2 + Vector2.up.RotatedBy(rotation) * distance);
            flightTicksField.SetValue(shot, 0);
            remainingField.SetValue(shot, Mathf.CeilToInt(duration));
            startingTicksField.SetValue(shot, duration);
            lastPositionField.SetValue(shot, origin);
            landedField.SetValue(shot, false);
            predictionField.SetValue(shot, null);
            return true;
        }

        internal static bool TryContact(Thing shot, Thing target, out Vector3 contact)
        {
            contact = default(Vector3);
            if (!RimKataTargeting.IsInterceptionTargetActive(target) || shot.Map != target.Map) return false;
            Vector3 start = RimKataCombatExtendedProjectiles.PreviousPosition(shot);
            Vector3 end = RimKataCombatExtendedProjectiles.Position(shot);
            Vector3 targetEnd, targetStart;
            if (RimKataCombatExtendedProjectiles.IsProjectile(target))
            {
                targetEnd = RimKataCombatExtendedProjectiles.Position(target);
                targetStart = RimKataCombatExtendedProjectiles.PreviousPosition(target);
            }
            else if (target is Projectile vanilla)
            {
                targetEnd = vanilla.ExactPosition;
                Vector3 velocity = (VanillaDestination(vanilla) - VanillaOrigin(vanilla)).normalized
                    * vanilla.def.projectile.SpeedTilesPerTick;
                targetStart = targetEnd - velocity;
                // Verse projectiles collide in their horizontal game plane.
                targetEnd.y = end.y;
                targetStart.y = start.y;
            }
            else return false;
            // Cover either Thing tick order, but require simultaneous trajectories
            // during this one tick, not an arbitrary crossing of flight lines.
            Vector3 targetStep = targetEnd - targetStart;
            for (int offset = 0; offset <= 1; offset++)
            {
                Vector3 relative = start - (targetStart + targetStep * offset);
                Vector3 step = end - start - targetStep;
                float fraction = step.sqrMagnitude > 0f ? Mathf.Clamp01(-Vector3.Dot(relative, step) / step.sqrMagnitude) : 0f;
                if ((relative + step * fraction).sqrMagnitude > ContactToleranceSquared) continue;
                contact = start + (end - start) * fraction;
                return true;
            }
            return false;
        }

        internal static bool TryRayContact(Vector3 origin, Vector3 end, Vector3 target, bool horizontal,
            out Vector3 contact)
        {
            contact = default(Vector3);
            Vector3 start = horizontal ? origin.Yto0() : origin;
            Vector3 finish = horizontal ? end.Yto0() : end;
            Vector3 point = horizontal ? target.Yto0() : target;
            Vector3 segment = finish - start;
            if (segment.sqrMagnitude <= 0f) return false;
            float fraction = Vector3.Dot(point - start, segment) / segment.sqrMagnitude;
            if (fraction < 0f || fraction > 1f
                || (point - (start + segment * fraction)).sqrMagnitude > ContactToleranceSquared) return false;
            contact = Vector3.Lerp(origin, end, fraction);
            return true;
        }

        internal static void Place(Thing shot, Vector3 contact)
        {
            exactSetter.Invoke(shot, new object[] { contact });
            lastPositionField.SetValue(shot, contact);
            shot.Position = contact.ToIntVec3();
        }

        internal static void Impact(Thing shot, bool immediate)
        {
            var record = RimKataCombatExtendedProjectiles.Component(shot.Map)?.Find(shot);
            if (record != null) record.pendingDodge = false;
            landedField.SetValue(shot, true);
            if (immediate) immediateImpact(shot, null);
            else RimKataCombatExtendedProjectiles.ImpactMethod.Invoke(shot, new object[] { null });
        }

        internal static bool RedirectVanilla(Projectile shot, Thing target, Pawn pawn, Verb verb)
        {
            if (target?.Map != shot.Map || !TryPredictInterception(target, VanillaOrigin(shot), shot.def,
                false, shot.def.projectile.SpeedTilesPerTick, 0, out Vector3 contact)) return false;
            float range = RimKataRangeUtility.ResolveEffectiveRange(pawn, verb.EquipmentSource, verb);
            if (!contact.InBounds(shot.Map) || pawn.Position.DistanceToSquared(contact.ToIntVec3()) > range * range) return false;
            int ticks = Mathf.Max(1, Mathf.CeilToInt((contact.Yto0() - VanillaOrigin(shot).Yto0()).magnitude
                / shot.def.projectile.SpeedTilesPerTick));
            VanillaDestination(shot) = contact.Yto0();
            VanillaRemaining(shot) = ticks;
            VanillaLifetime(shot) = ticks;
            return true;
        }

        internal static void SpawnDeflectedMiss(Thing source, Pawn attacker, Pawn defender, Verb verb)
        {
            if (source?.def == null || attacker?.Map == null || defender?.Map != attacker.Map) return;
            IntVec3 missCell = RimKataProjectileUtility.FindMissCell(attacker.Position, defender.Position, attacker.Map);
            if (!missCell.IsValid || missCell == defender.Position) return;
            Vector3 origin = attacker.Position.ToVector3Shifted();
            origin.y = (float)heightField.GetValue(source);
            float speed = (float)speedField.GetValue(source);
            object worker = workerGetter.Invoke(source, null);
            if (!TryAngles(worker, source.def.projectile, origin, missCell.ToVector3Shifted().Yto0(),
                speed, out float angle, out float rotation)) return;
            Thing redirected = ThingMaker.MakeThing(source.def);
            if (!RimKataCombatExtendedProjectiles.IsProjectile(redirected)) return;
            intendedTargetField.SetValue(redirected, new LocalTargetInfo(missCell));
            bool previous = RimKataFireContext.SuppressCloseLaunch;
            RimKataFireContext.SuppressCloseLaunch = true;
            try
            {
                GenSpawn.Spawn(redirected, attacker.Position, attacker.Map);
                launch.Invoke(redirected, new object[] { attacker, new Vector2(origin.x, origin.z), angle,
                    rotation, origin.y, speed, verb?.EquipmentSource ?? equipmentField.GetValue(source),
                    (missCell.ToVector3Shifted().Yto0() - origin.Yto0()).magnitude });
                foreach (FieldInfo field in retainedFields) field.SetValue(redirected, field.GetValue(source));
                initialSpeedField.SetValue(redirected, initialSpeedField.GetValue(source));
                if (extraDamagesField.GetValue(source) is List<ExtraDamage> extra)
                    extraDamagesField.SetValue(redirected, new List<ExtraDamage>(extra));
                RimKataCombatExtendedProjectiles.Component(attacker.Map)?.ExcludeDirectHit(redirected, defender);
            }
            catch
            {
                if (!redirected.Destroyed) redirected.Destroy(DestroyMode.Vanish);
                throw;
            }
            finally { RimKataFireContext.SuppressCloseLaunch = previous; }
        }

        internal static bool VanillaContact(Projectile shot, Thing target, out Vector3 contact)
        {
            contact = shot.ExactPosition;
            if (target?.Map != shot.Map || !RimKataCombatExtendedProjectiles.IsActiveExplosive(target)) return false;
            Vector3 from = RimKataCombatExtendedProjectiles.PreviousPosition(target).Yto0();
            Vector3 to = RimKataCombatExtendedProjectiles.Position(target).Yto0();
            Vector3 step = to - from;
            float fraction = step.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(contact.Yto0() - from, step) / step.sqrMagnitude) : 0f;
            return (contact.Yto0() - from - step * fraction).sqrMagnitude <= ContactToleranceSquared;
        }
    }
}
