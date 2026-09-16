using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Event-only prediction. No solution is cached across aiming ticks and no
    // projectile is steered after launch. Vanilla saves the rewritten flight.
    internal static class RimKataInterceptionTrajectory
    {
        private static readonly AccessTools.FieldRef<Projectile, Vector3> Origin =
            AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> Destination =
            AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
        private static readonly AccessTools.FieldRef<Projectile, int> RemainingTicks =
            AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");
        private static readonly AccessTools.FieldRef<Projectile, int> Lifetime =
            AccessTools.FieldRefAccess<Projectile, int>("lifetime");
        private static readonly AccessTools.FieldRef<Thing, int> PendingTicks =
            AccessTools.FieldRefAccess<Thing, int>("tickDelta");

        // Floating-point tolerance only, not an added target size/range bonus.
        private const float PositionToleranceSquared = 0.0001f;

        internal readonly struct Flight
        {
            internal readonly Vector3 origin;
            internal readonly Vector3 destination;
            internal readonly double duration;
            internal readonly double remaining;

            internal Flight(Vector3 origin, Vector3 destination, double duration, double remaining)
            {
                this.origin = origin.Yto0();
                this.destination = destination.Yto0();
                this.duration = duration;
                this.remaining = remaining;
            }

            internal Vector3 PositionAfter(double ticks)
            {
                double fraction = Math.Max(0d, Math.Min(1d, 1d - (remaining - ticks) / duration));
                return origin + (destination - origin) * (float)fraction;
            }
        }

        internal static bool CanIntercept(Pawn pawn, Verb verb, Projectile target, int delayTicks,
            float? knownRangeSquared = null)
        {
            if (pawn?.Map == null || !(verb is Verb_LaunchProjectile launchVerb)
                || target?.Map != pawn.Map)
            {
                return false;
            }

            ThingDef projectileDef = launchVerb.Projectile;
            float speed = projectileDef?.projectile?.SpeedTilesPerTick ?? 0f;
            return TryReadFlight(target, out Flight flight)
                && TryPredict(pawn.DrawPos, speed, flight, delayTicks, out Vector3 point, out _)
                && WithinWeaponRange(pawn, verb, point, knownRangeSquared);
        }

        internal static bool TryRedirectHit(Projectile shot, Projectile target, Pawn pawn, Verb verb)
        {
            if (shot?.Spawned != true || shot.Destroyed || shot.Map != target?.Map
                || !TryReadFlight(target, out Flight flight))
            {
                return false;
            }

            Vector3 origin = Origin(shot);
            // Reject an already displaced launch sample. Later custom trajectory
            // changes are not predicted; the actual contact check still applies.
            if ((shot.ExactPosition.Yto0() - origin.Yto0()).sqrMagnitude > PositionToleranceSquared
                || !TryPredict(origin, shot.def.projectile.SpeedTilesPerTick, flight, 0,
                    out Vector3 point, out int ticks)
                || !point.InBounds(shot.Map)
                || !WithinWeaponRange(pawn, verb, point))
            {
                return false;
            }

            Destination(shot) = point;
            RemainingTicks(shot) = ticks;
            Lifetime(shot) = ticks;
            return true;
        }

        internal static void ReleaseUnreachableHit(Projectile shot)
        {
            // Ammo is already spent. Keep the original flight and ground impact,
            // but remove the remote Thing hit; do not reroll accuracy or delete it.
            shot.usedTarget = new LocalTargetInfo(Destination(shot).ToIntVec3());
        }

        internal static bool TryGetContact(Projectile shot, Projectile target, out Vector3 point)
        {
            point = default(Vector3);
            int remaining = RemainingTicks(shot);
            if (!TryReadFlight(target, out Flight flight, true))
            {
                return false;
            }

            if (remaining > 0)
            {
                // The engine stopped scanning at this target's registered cell.
                // Do not jump to a later contact past as-yet unchecked blockers.
                return TryGetEarlyContact(shot, flight, out point)
                    && point.ToIntVec3().Equals(target.Position);
            }

            if (remaining >= flight.remaining)
            {
                return false;
            }

            point = shot.ExactPosition;
            if ((point.Yto0() - Destination(shot).Yto0()).sqrMagnitude > PositionToleranceSquared)
            {
                return false;
            }

            // TickInterval can advance 15 ticks offscreen. Negative remaining is
            // the overshoot past arrival, not extra target radius. Sample back to
            // arrival, then cover the one-tick rounding / Thing tick-order window.
            // Both endpoints are on the target's actual, currently verified path.
            Vector3 before = flight.PositionAfter(remaining - 1d);
            Vector3 after = flight.PositionAfter(remaining + 1d);
            return PointOnSegment(point.Yto0(), before, after);
        }

        private static bool TryGetEarlyContact(Projectile shot, Flight target, out Vector3 point)
        {
            // Vanilla can hit a projectile in a traversed cell before arrival.
            // Confirm both trajectories cross there during the same tick window,
            // not merely that two long path segments cross at unrelated times.
            point = default(Vector3);
            Vector3 origin = Origin(shot);
            Vector3 destination = Destination(shot);
            double duration = (destination - origin).magnitude / (double)shot.def.projectile.SpeedTilesPerTick;
            if (!FinitePositive(duration)) return false;
            Flight flight = new Flight(origin, destination, duration, RemainingTicks(shot));
            if ((shot.ExactPosition.Yto0() - flight.PositionAfter(0)).sqrMagnitude > PositionToleranceSquared)
                return false;

            int elapsed = Math.Max(1, PendingTicks(shot));
            Vector3 start = flight.PositionAfter(-elapsed);
            Vector3 step = flight.PositionAfter(0) - start;
            Vector3 targetStart = target.PositionAfter(-elapsed - 1d);
            Vector3 targetStep = target.PositionAfter(1d) - targetStart;
            Vector3 offset = targetStart - start;
            double cross = Cross(step, targetStep);
            double contactTime;
            if (Math.Abs(cross) > 1e-10)
            {
                double shotFraction = Cross(offset, targetStep) / cross;
                double targetFraction = Cross(offset, step) / cross;
                if (shotFraction < 0d || shotFraction > 1d
                    || targetFraction < 0d || targetFraction > 1d
                    || Math.Abs(shotFraction * elapsed + 1d - targetFraction * (elapsed + 2d)) > 1d)
                    return false;
                point = start + step * (float)shotFraction;
                contactTime = -elapsed + shotFraction * elapsed;
            }
            else
            {
                Vector3 relativeStart = start - target.PositionAfter(-elapsed);
                Vector3 relativeStep = step - (target.PositionAfter(0) - target.PositionAfter(-elapsed));
                float fraction = relativeStep.sqrMagnitude > 0f
                    ? Mathf.Clamp01(-Vector3.Dot(relativeStart, relativeStep) / relativeStep.sqrMagnitude)
                    : 0f;
                double time = -elapsed + fraction * elapsed;
                contactTime = time;
                point = start + step * fraction;
                if (!PointOnSegment(point, target.PositionAfter(time - 1d), target.PositionAfter(time + 1d)))
                    return false;
            }

            if (contactTime >= target.remaining) return false;
            point.y = shot.ExactPosition.y;
            return true;
        }

        private static double Cross(Vector3 first, Vector3 second)
        {
            return (double)first.x * second.z - (double)first.z * second.x;
        }

        internal static void PlaceAtContact(Projectile shot, Vector3 point)
        {
            // This runs only after a real collision was validated, never in flight.
            // Align the interceptor's own explosion/fuse with the target effect.
            shot.Position = point.ToIntVec3();
            Destination(shot) = point.Yto0();
            RemainingTicks(shot) = 0;
            Lifetime(shot) = 0;
        }

        private static bool PointOnSegment(Vector3 point, Vector3 before, Vector3 after)
        {
            Vector3 segment = after - before;
            float lengthSquared = segment.sqrMagnitude;
            float fraction = lengthSquared > 0f
                ? Mathf.Clamp01(Vector3.Dot(point - before, segment) / lengthSquared)
                : 0f;
            return (point - (before + segment * fraction)).sqrMagnitude <= PositionToleranceSquared;
        }

        private static bool WithinWeaponRange(Pawn pawn, Verb verb, Vector3 point,
            float? knownRangeSquared = null)
        {
            if (pawn?.Map == null || verb == null || !point.InBounds(pawn.Map))
            {
                return false;
            }

            float rangeSquared;
            if (knownRangeSquared.HasValue)
            {
                rangeSquared = knownRangeSquared.Value;
            }
            else
            {
                float range = RimKataRangeUtility.ResolveEffectiveRange(pawn, verb.EquipmentSource, verb);
                rangeSquared = range * range;
            }
            // Interception uses exact weapon range; candidate padding does not apply.
            return pawn.Position.DistanceToSquared(point.ToIntVec3()) <= rangeSquared;
        }

        private static bool TryReadFlight(Projectile target, out Flight flight, bool allowElapsedFlight = false)
        {
            flight = default(Flight);
            if (!RimKataTargeting.IsInterceptionTargetActive(target))
            {
                return false;
            }

            Vector3 origin = Origin(target);
            Vector3 destination = Destination(target);
            float speed = target.def.projectile.SpeedTilesPerTick;
            double duration = (destination - origin).magnitude / (double)speed;
            int rawRemaining = RemainingTicks(target);
            int pending = PendingTicks(target);
            if (!FinitePositive(duration) || !FinitePositive(speed)
                || rawRemaining <= 0 || (!allowElapsedFlight && rawRemaining <= pending))
            {
                return false;
            }

            Flight rawFlight = new Flight(origin, destination, duration, rawRemaining);
            // Verify the sampled position instead of assuming every modded
            // Projectile follows vanilla's linear horizontal flight.
            if ((target.ExactPosition.Yto0() - rawFlight.PositionAfter(0)).sqrMagnitude > PositionToleranceSquared)
            {
                return false;
            }

            flight = new Flight(origin, destination, duration, rawRemaining - pending);
            return true;
        }

        internal static bool TryPredict(Vector3 origin, float speed, Flight target, int delayTicks,
            out Vector3 point, out int flightTicks)
        {
            point = default(Vector3);
            flightTicks = 0;
            delayTicks = Math.Max(0, delayTicks);
            double available = target.remaining - delayTicks;
            if (!FinitePositive(speed) || !FinitePositive(target.duration) || available <= 1d)
            {
                return false;
            }

            // Vanilla clamps position at the launch origin during the fractional
            // first tick introduced by CeilToInt(StartingTicksToImpact).
            double hold = Math.Max(0d, available - target.duration);
            double stationaryTime = (target.origin - origin).magnitude / (double)speed;
            double time;
            if (stationaryTime <= hold)
            {
                time = stationaryTime;
            }
            else
            {
                Vector3 velocity = (target.destination - target.origin) / (float)target.duration;
                Vector3 offset = target.destination - velocity * (float)available - origin;
                double a = Vector3.Dot(velocity, velocity) - (double)speed * speed;
                double b = 2d * Vector3.Dot(offset, velocity);
                double c = Vector3.Dot(offset, offset);
                if (!TryFirstRoot(a, b, c, hold, out time))
                {
                    return false;
                }
            }

            if (double.IsNaN(time) || double.IsInfinity(time) || time < 0d || time >= available)
            {
                return false;
            }

            point = target.PositionAfter(delayTicks + time);
            double duration = (point - origin).magnitude / (double)speed;
            if (duration >= int.MaxValue || double.IsNaN(duration) || double.IsInfinity(duration))
            {
                return false;
            }

            flightTicks = Math.Max(1, (int)Math.Ceiling(duration));
            // Landing in the same tick is not a reliably earlier interception.
            return flightTicks < available;
        }

        private static bool TryFirstRoot(double a, double b, double c, double minimum, out double time)
        {
            time = double.PositiveInfinity;
            if (Math.Abs(a) < 1e-10)
            {
                if (Math.Abs(b) < 1e-10)
                {
                    if (c < 1e-10) time = minimum;
                }
                else
                {
                    double root = -c / b;
                    if (root >= minimum) time = root;
                }
            }
            else
            {
                double discriminant = b * b - 4d * a * c;
                if (discriminant < 0d) return false;
                double squareRoot = Math.Sqrt(discriminant);
                // Stable quadratic roots even for a nearly stationary target.
                double q = -0.5d * (b + (b >= 0d ? squareRoot : -squareRoot));
                double first = q / a;
                double second = q != 0d ? c / q : first;
                if (first >= minimum) time = first;
                if (second >= minimum && second < time) time = second;
            }

            return !double.IsInfinity(time);
        }

        private static bool FinitePositive(double value)
        {
            return value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
