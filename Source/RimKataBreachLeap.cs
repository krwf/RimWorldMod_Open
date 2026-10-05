using System;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataBreachLeap : IExposable
    {
        private int age, approachTicks;
        private Rot4 strikeFacing;
        private float startHeight, startAngle, startProgress, startWeaponRotation;

        internal static RimKataBreachLeap Start(RimKataBreachState state)
            => new RimKataBreachLeap
            {
                approachTicks = Math.Max(12, state.rate > 0f
                    ? Mathf.CeilToInt(2f / state.rate) : Mathf.CeilToInt(state.pawn.TicksPerMoveCardinal * 2f)),
                strikeFacing = state.facing == Rot4.South
                    ? Rand.Bool ? Rot4.East : Rot4.West : state.facing
            };

        internal void Tick() => age++;

        internal void Capture(RimKataBreachState state)
        {
            RimKataBreachVisual visual = default;
            Apply(state, ref visual);
            startHeight = visual.height;
            startAngle = visual.angle;
            startProgress = visual.progress;
            startWeaponRotation = visual.weaponRotation;
        }

        internal void Apply(RimKataBreachState state, ref RimKataBreachVisual visual)
        {
            if (state.phase != BreachPhase.Run && state.phase != BreachPhase.Slide
                && state.phase != BreachPhase.Rise) return;
            float lieAngle = Mathf.DeltaAngle(0f, state.facing.AsAngle + 180f);
            if (state.phase == BreachPhase.Run)
            {
                int side = state.facing == Rot4.East ? 1 : state.facing == Rot4.West ? -1 : 0;
                float turn = Mathf.Clamp01((age - 6f) / 6f);
                float strikeAngle = state.facing.IsHorizontal ? -side * 72f : lieAngle;
                visual.angle = age <= 6 ? side * 18f * Mathf.Clamp01(age / 3f)
                    : Mathf.Lerp(side * 18f, strikeAngle, turn);
                visual.height = 1.2f * (age <= 6 ? age / 6f
                    : 1f - 0.5f * Mathf.Clamp01((age - 6f) / Math.Max(6, approachTicks - 6)));
                visual.progress = turn;
                visual.weaponRotation = 0f;
            }
            else
            {
                float progress = Mathf.Clamp01(state.elapsed / 6f);
                bool rising = state.phase == BreachPhase.Rise;
                visual.height = startHeight * (1f - progress);
                visual.angle = Mathf.Lerp(startAngle, rising ? 0f : lieAngle, progress);
                visual.progress = Mathf.Lerp(startProgress, rising ? 0f : 1f, progress);
                visual.weaponRotation = Mathf.Lerp(startWeaponRotation, 1f, progress);
            }
            visual.poseActive = true;
            visual.bodyFacing = state.phase != BreachPhase.Run && visual.height <= 0f ? state.facing
                : state.facing == Rot4.North
                ? RimKataGroundPoseState.FacingAt(state.facing.AsAngle, visual.angle)
                : state.facing == Rot4.South && visual.progress > 0f ? strikeFacing : state.facing;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref age, "age");
            Scribe_Values.Look(ref approachTicks, "approachTicks", 12);
            Scribe_Values.Look(ref strikeFacing, "strikeFacing");
            Scribe_Values.Look(ref startHeight, "startHeight");
            Scribe_Values.Look(ref startAngle, "startAngle");
            Scribe_Values.Look(ref startProgress, "startProgress");
            Scribe_Values.Look(ref startWeaponRotation, "startWeaponRotation");
        }
    }
}
