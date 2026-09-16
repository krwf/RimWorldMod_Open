using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // One launch-time decision changes the existing projectile's saved flight.
    // No replacement projectile, repeated steering, or explosion suppression.
    internal static class RimKataProjectileMissUtility
    {
        private static readonly AccessTools.FieldRef<Projectile, Vector3> Origin =
            AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> Destination =
            AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
        private static readonly AccessTools.FieldRef<Projectile, int> RemainingTicks =
            AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");
        private static readonly AccessTools.FieldRef<Projectile, int> Lifetime =
            AccessTools.FieldRefAccess<Projectile, int>("lifetime");
        private static readonly AccessTools.FieldRef<Projectile, bool> Landed =
            AccessTools.FieldRefAccess<Projectile, bool>("landed");
        private static readonly AccessTools.FieldRef<Projectile, ProjectileHitFlags> DesiredHitFlags =
            AccessTools.FieldRefAccess<Projectile, ProjectileHitFlags>("desiredHitFlags");

        internal readonly struct MissFlight
        {
            internal readonly Vector3 origin;
            internal readonly Vector3 destination;
            internal readonly int ticks;
            internal readonly int lifetime;
            internal readonly ProjectileHitFlags hitFlags;

            internal MissFlight(Vector3 origin, Vector3 destination, int ticks,
                int lifetime, ProjectileHitFlags hitFlags)
            {
                this.origin = origin;
                this.destination = destination;
                this.ticks = ticks;
                this.lifetime = lifetime;
                this.hitFlags = hitFlags;
            }
        }

        internal static bool IsAtDestination(Projectile projectile)
        {
            return RemainingTicks(projectile) <= 0
                && projectile.Position == Destination(projectile).ToIntVec3();
        }

        internal static bool TryPrepareMiss(Projectile projectile, Pawn defender,
            out MissFlight flight)
        {
            flight = default(MissFlight);
            if (projectile?.Spawned != true || projectile.Destroyed
                || defender?.Spawned != true || defender.Map != projectile.Map
                || projectile.def?.projectile == null || Landed(projectile)
                || projectile.intendedTarget.Pawn != defender)
            {
                return false;
            }

            bool aimedAtPawn = projectile.usedTarget.Pawn == defender;
            bool aimedAtCell = !projectile.usedTarget.HasThing
                && projectile.usedTarget.IsValid
                && projectile.usedTarget.Cell == defender.Position;
            if (!aimedAtPawn && !aimedAtCell)
            {
                return false;
            }

            ProjectileHitFlags flags = DesiredHitFlags(projectile);
            if (aimedAtPawn && (flags & ProjectileHitFlags.IntendedTarget) == 0
                && !projectile.def.projectile.flyOverhead
                && !projectile.def.projectile.alwaysFreeIntercept)
            {
                return false;
            }

            Vector3 previousOrigin = Origin(projectile);
            Vector3 previousDestination = Destination(projectile);
            Vector3 current = projectile.ExactPosition.Yto0();
            float speed = projectile.def.projectile.SpeedTilesPerTick;
            // Vanilla uses the full 3D distance for flight duration, even though
            // ExactPosition interpolates the flight on the ground plane.
            float previousDuration = (previousDestination - previousOrigin).magnitude / speed;
            if (!FinitePositive(speed) || !FinitePositive(previousDuration)
                || !current.InBounds(projectile.Map))
            {
                return false;
            }

            // A custom position implementation may not consume these native
            // flight fields. Do not spend a dodge on a rewrite it would ignore.
            previousOrigin = previousOrigin.Yto0();
            previousDestination = previousDestination.Yto0();
            Vector3 expected = previousOrigin + (previousDestination - previousOrigin)
                * Mathf.Clamp01(1f - RemainingTicks(projectile) / previousDuration);
            if ((current - expected).sqrMagnitude > 0.0001f)
            {
                return false;
            }

            if (!TryFindMissCell(projectile, defender, current,
                    previousOrigin, previousDestination, out IntVec3 cell))
            {
                return false;
            }

            Vector3 destination = cell.ToVector3Shifted().Yto0();
            double duration = (destination - current).magnitude / (double)speed;
            if (!FinitePositive(duration) || duration >= int.MaxValue)
            {
                return false;
            }

            int ticks = Math.Max(1, (int)Math.Ceiling(duration));
            long lifetime = (long)Lifetime(projectile) + ticks - RemainingTicks(projectile);
            if (lifetime < int.MinValue || lifetime > int.MaxValue)
            {
                return false;
            }

            flight = new MissFlight(current, destination, ticks, (int)lifetime,
                flags & ~ProjectileHitFlags.IntendedTarget);
            return true;
        }

        internal static void ApplyMiss(Projectile projectile, MissFlight flight)
        {
            Origin(projectile) = flight.origin;
            Destination(projectile) = flight.destination;
            RemainingTicks(projectile) = flight.ticks;
            Lifetime(projectile) = flight.lifetime;
            projectile.usedTarget = new LocalTargetInfo(flight.destination.ToIntVec3());
            DesiredHitFlags(projectile) = flight.hitFlags;
            // Keep intendedTarget: vanilla CanHit then excludes this pawn while
            // preserving the shot's existing non-target collision permissions.
            // The projectile subclass retains its damage, effects, and fuse.
        }

        private static bool TryFindMissCell(Projectile projectile, Pawn defender,
            Vector3 current, Vector3 previousOrigin, Vector3 previousDestination,
            out IntVec3 missCell)
        {
            missCell = IntVec3.Invalid;
            Vector3 forward = previousDestination - previousOrigin;
            if (forward.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            forward.Normalize();
            Vector3 target = defender.Position.ToVector3Shifted();
            Vector3 anchor = Vector3.Dot(current - target, forward) > 0f ? current : target;
            Vector3 side = new Vector3(-forward.z, 0f, forward.x);
            int preferredSide = (projectile.thingIDNumber & 1) == 0 ? 1 : -1;
            for (int distance = 8; distance >= 0; distance--)
            {
                for (int sideIndex = 0; sideIndex < 2; sideIndex++)
                {
                    int sign = sideIndex == 0 ? preferredSide : -preferredSide;
                    IntVec3 cell = (anchor + forward * distance + side * (2f * sign)).ToIntVec3();
                    if (ValidMissCell(cell, projectile, defender, current,
                            previousDestination, forward))
                    {
                        missCell = cell;
                        return true;
                    }
                }
            }

            // At a map edge, prefer any nearby forward/sideways cell over
            // sending the projectile out of bounds or back towards its launcher.
            for (int i = 0; i < GenAdj.AdjacentCells.Length; i++)
            {
                IntVec3 cell = anchor.ToIntVec3() + GenAdj.AdjacentCells[i];
                if (ValidMissCell(cell, projectile, defender, current,
                        previousDestination, forward))
                {
                    missCell = cell;
                    return true;
                }
            }

            return false;
        }

        private static bool ValidMissCell(IntVec3 cell, Projectile projectile, Pawn defender,
            Vector3 current, Vector3 previousDestination, Vector3 forward)
        {
            return cell.InBounds(projectile.Map)
                && cell != defender.Position
                && cell != previousDestination.ToIntVec3()
                && cell != current.ToIntVec3()
                && (projectile.Launcher == null || cell != projectile.Launcher.Position)
                && Vector3.Dot(cell.ToVector3Shifted().Yto0() - current, forward) >= 0f;
        }

        private static bool FinitePositive(double value)
        {
            return value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
