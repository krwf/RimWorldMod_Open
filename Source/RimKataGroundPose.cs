using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal enum RimKataFallPhase { None, Falling, Fallen, Rising, Lowering }

    // Visual state only. Neither a job, posture, map position nor a hitbox is changed.
    public sealed class RimKataGroundPoseState : IExposable
    {
        internal const int TransitionDuration = 6;
        internal RimKataFallPhase phase;
        internal bool prone;
        // Legacy saves can carry a deadline here; Rebuild moves it out of combat state.
        internal long resumeProneTick = -1;
        internal bool risingFromProne;
        internal float riseStartProgress = 1f;
        internal float loweringStartProgress;
        internal Vector3 riseWeaponOffset;
        internal Rot4 riseRollFacing = Rot4.Invalid;
        internal LocalTargetInfo focus = LocalTargetInfo.Invalid;
        internal ThingWithComps aimWeapon;
        internal Verb aimVerb;
        internal int transitionTicks;
        internal int holdTicks;
        internal float angle;
        internal float headAimAngle;
        internal Rot4 originalFacing;
        internal Vector3 rollAxis;
        internal Vector3 offset;
        internal Vector3 rollOrigin;
        internal bool rolling;
        internal int rollStep;
        internal int rollDirection;
        internal int rollTicks;

        internal bool VisualActive => prone || phase != RimKataFallPhase.None;
        internal bool KeepsState => VisualActive;
        internal bool PronePose => prone || phase == RimKataFallPhase.Lowering;
        internal float DrawProgress => phase == RimKataFallPhase.Falling
            ? 1f - transitionTicks / (float)TransitionDuration
            : phase == RimKataFallPhase.Lowering
                ? Mathf.Lerp(loweringStartProgress, 1f, 1f - transitionTicks / (float)TransitionDuration)
            : phase == RimKataFallPhase.Rising
                ? riseStartProgress * transitionTicks / TransitionDuration : VisualActive ? 1f : 0f;
        internal float DrawAngle => angle * DrawProgress;
        internal Vector3 DrawOffset => phase == RimKataFallPhase.Rising
            ? offset * (transitionTicks / (float)TransitionDuration) : offset;
        internal Vector3 DrawWeaponOffset => phase == RimKataFallPhase.Rising
            ? riseWeaponOffset * (transitionTicks / (float)TransitionDuration) : rolling ? rollOrigin : DrawOffset;
        internal Rot4 DrawFacing
        {
            get
            {
                if (!VisualActive) return Rot4.Invalid;
                // A roll keeps its initial frame of reference even if aim changes.
                if (rolling)
                    return new Rot4((originalFacing.AsInt + rollStep % 4 + 4) % 4);
                if (phase == RimKataFallPhase.Rising && riseRollFacing.IsValid) return riseRollFacing;
                float aim = Mathf.FloorToInt((Mathf.Repeat(headAimAngle, 360f) + 22.5f) / 45f) % 8 * 45f;
                // Select a shared body/head picture relative to the lying tilt.
                // The target changes the picture, never the established fall angle.
                return Rot4.FromAngleFlat(Mathf.Repeat(aim - DrawAngle, 360f));
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref phase, "phase");
            Scribe_Values.Look(ref prone, "prone");
            Scribe_Values.Look(ref resumeProneTick, "resumeProneTick", -1L);
            Scribe_Values.Look(ref risingFromProne, "risingFromProne");
            Scribe_Values.Look(ref riseStartProgress, "riseStartProgress", 1f);
            Scribe_Values.Look(ref loweringStartProgress, "loweringStartProgress");
            Scribe_Values.Look(ref riseWeaponOffset, "riseWeaponOffset");
            Scribe_Values.Look(ref riseRollFacing, "riseRollFacing", Rot4.Invalid);
            Scribe_TargetInfo.Look(ref focus, "focus");
            Scribe_References.Look(ref aimWeapon, "aimWeapon");
            Scribe_Values.Look(ref transitionTicks, "transitionTicks");
            Scribe_Values.Look(ref holdTicks, "holdTicks");
            Scribe_Values.Look(ref angle, "angle");
            Scribe_Values.Look(ref headAimAngle, "headAimAngle");
            Scribe_Values.Look(ref originalFacing, "originalFacing");
            Scribe_Values.Look(ref rollAxis, "rollAxis");
            Scribe_Values.Look(ref offset, "offset");
            Scribe_Values.Look(ref rollOrigin, "rollOrigin");
            Scribe_Values.Look(ref rolling, "rolling");
            Scribe_Values.Look(ref rollStep, "rollStep");
            Scribe_Values.Look(ref rollDirection, "rollDirection");
            Scribe_Values.Look(ref rollTicks, "rollTicks");
            int transitionVersion = 2;
            Scribe_Values.Look(ref transitionVersion, "transitionVersion");
            if (Scribe.mode == LoadSaveMode.LoadingVars) RestoreTransitionVersion(transitionVersion);
        }

        internal void RestoreTransitionVersion(int version)
        {
            if (version == 0 && phase == RimKataFallPhase.Rising)
                riseWeaponOffset = offset;
            else if (version == 1 && phase == RimKataFallPhase.Rising)
            {
                // Preserve the saved eight-tick pose, then finish in six ticks.
                float remaining = Mathf.Clamp01(transitionTicks / 8f);
                riseStartProgress *= remaining;
                offset *= remaining;
                riseWeaponOffset *= remaining;
                transitionTicks = TransitionDuration;
            }
            else if (version == 1 && phase == RimKataFallPhase.Lowering)
            {
                loweringStartProgress = Mathf.Clamp01(1f - transitionTicks / 8f);
                transitionTicks = TransitionDuration;
            }
        }

        internal void TickFall()
        {
            if (phase == RimKataFallPhase.Falling)
            {
                if (--transitionTicks <= 0) phase = RimKataFallPhase.Fallen;
            }
            else if (phase == RimKataFallPhase.Lowering)
            {
                if (--transitionTicks <= 0)
                {
                    phase = RimKataFallPhase.None;
                    prone = true;
                }
            }
            else if (phase == RimKataFallPhase.Fallen)
            {
                if (rolling)
                {
                    if (++rollTicks < 2) return;
                    rollTicks = 0;
                    rollStep += rollDirection;
                    offset = rollOrigin + rollAxis * (rollStep * 0.1f);
                    if (rollStep == 0 || Mathf.Abs(rollStep) == 4)
                    {
                        rolling = false;
                        rollStep = 0;
                        rollOrigin = offset;
                    }
                    return;
                }
                if (holdTicks > 0) --holdTicks;
                if (holdTicks <= 0)
                {
                    BeginRise();
                }
            }
            else if (phase == RimKataFallPhase.Rising && --transitionTicks <= 0)
            {
                phase = RimKataFallPhase.None;
                offset = rollOrigin = Vector3.zero;
                angle = 0f;
            }
        }

        internal void BeginRoll()
        {
            if (rolling)
            {
                if (rollStep == 0)
                {
                    rolling = false;
                    return;
                }
                rollDirection = -rollDirection;
                return;
            }
            rolling = true;
            rollOrigin = offset;
            rollStep = rollTicks = 0;
            float displacement = Vector3.Dot(offset, rollAxis);
            rollDirection = displacement >= 0.399f ? -1
                : displacement <= -0.399f ? 1 : Rand.Bool ? 1 : -1;
        }

        internal void BeginRise()
        {
            if (!VisualActive || phase == RimKataFallPhase.Rising) return;
            risingFromProne = PronePose;
            riseStartProgress = DrawProgress;
            riseWeaponOffset = DrawWeaponOffset;
            riseRollFacing = rolling ? DrawFacing : Rot4.Invalid;
            offset = DrawOffset;
            prone = false;
            phase = RimKataFallPhase.Rising;
            transitionTicks = TransitionDuration;
            holdTicks = 0;
            rolling = false;
            rollStep = rollDirection = rollTicks = 0;
        }
    }

    internal static class RimKataGroundPoseUtility
    {
        // Only currently prone pawns enter this set. Projectile hooks can return
        // before a pawn/state/settings lookup when no prone participant exists.
        private static readonly Dictionary<Pawn, RimKataPawnCombatState> Prone =
            new Dictionary<Pawn, RimKataPawnCombatState>();
        private static readonly Dictionary<Pawn, RimKataPawnCombatState> Active =
            new Dictionary<Pawn, RimKataPawnCombatState>();
        private static readonly Dictionary<Thing, List<Pawn>> TargetWatchers =
            new Dictionary<Thing, List<Pawn>>();

        internal static bool IsProne(Pawn pawn)
            => Prone.Count != 0 && pawn != null && Prone.TryGetValue(pawn, out var state)
                && state.groundPose?.prone == true && pawn.Spawned && !pawn.Dead && !pawn.Downed;

        internal static bool TryProneMiss(Pawn pawn)
            => IsProne(pawn) && Rand.Chance(RimKataTargetAccess.SettingsFor(pawn)?.GetProneMissChance(pawn) ?? 0f);

        internal static void NotifyAttackJob(Pawn pawn)
        {
            // A stopped, explicitly ordered out-of-range shot has no warmup yet.
            // This is a Job-start event, never a standing-pawn polling request.
            if (pawn?.CurJobDef != JobDefOf.AttackStatic
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)) return;
            NotifyAimStarted(pawn, pawn.CurJob.verbToUse
                ?? RimKataWeaponSlotUtility.PrimaryVerb(pawn.equipment?.Primary), pawn.CurJob.targetA);
        }

        internal static void NotifyAimStarted(Pawn pawn, Verb verb, LocalTargetInfo target,
            bool knownInsideCandidateRange = false)
        {
            if (pawn?.Spawned != true || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)) return;
            Active.TryGetValue(pawn, out var state);
            if (state?.groundPose?.phase == RimKataFallPhase.Rising) return;
            if (state?.groundPose != null && !state.groundPose.PronePose) return;
            bool allowed = !knownInsideCandidateRange
                && RimKataGroundPoseConditions.CanEnterProne(pawn, verb, target);
            if (!allowed)
            {
                if (state?.groundPose?.PronePose == true) BeginRise(state);
                return;
            }
            RimKataMapComponent owner = pawn.Map.GetComponent<RimKataMapComponent>();
            if (owner == null) return;
            if (owner.groundPoseResumeTicks.TryGetValue(pawn, out long deadline))
            {
                if ((long)Find.TickManager.TicksGame < deadline) return;
                owner.groundPoseResumeTicks.Remove(pawn);
            }
            state ??= owner.GetState(pawn, false);
            if (state?.VisualActive == true || state?.DodgeMovementActive == true
                || state?.CloseDodgeActive == true) return;
            if (state?.groundPose?.VisualActive != true && !RimKataGroundPoseHead.Supports(pawn)) return;
            state ??= owner.GetState(pawn, true);
            RimKataGroundPoseState pose = state.groundPose ??= new RimKataGroundPoseState();
            bool starting = !pose.VisualActive;
            if (starting)
            {
                pose.phase = RimKataFallPhase.Lowering;
                pose.transitionTicks = RimKataGroundPoseState.TransitionDuration;
                pose.angle = LieAngle(target.CenterVector3 - pawn.DrawPos, pawn.Rotation);
                pose.originalFacing = pawn.Rotation;
                Register(state);
            }
            pose.aimWeapon = verb.EquipmentSource;
            pose.aimVerb = verb;
            SetFocus(pawn, pose, target);
            pose.headAimAngle = RimKataGroundPoseConditions.HeadAimAngle(pawn);
            if (starting) RimKataResponseVisualParticipantCache.RefreshBodyVisual(state);
        }

        internal static void NotifyMovement(Pawn pawn)
        {
            // Movement callbacks for ordinary pawns stop before any state lookup.
            if (Active.Count == 0 || pawn == null || !Active.TryGetValue(pawn, out var state)
                || state.groundPose == null
                || !RimKataGroundPoseConditions.HasMovementJob(pawn)) return;
            BeginRise(state);
        }

        internal static void NotifyAimCancelled(Pawn pawn)
        {
            if (Active.Count != 0 && pawn != null && Active.TryGetValue(pawn, out var state)
                && state.groundPose?.PronePose == true) BeginRise(state);
        }

        internal static void NotifyJobChanged(Pawn pawn)
        {
            NotifyMovement(pawn);
            if (Active.Count == 0 || pawn == null || !Active.TryGetValue(pawn, out var state)
                || state.groundPose?.PronePose != true) return;
            if (pawn.CurJobDef == JobDefOf.AttackStatic || pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                || pawn.CurJobDef == JobDefOf.Wait_Combat || pawn.jobs?.curDriver is JobDriver_Hunt) return;
            if (pawn.Drafted && pawn.drafter.FireAtWill && state.dualEngagementActive) return;
            BeginRise(state);
        }

        internal static void NotifyLoadoutChanged(Pawn pawn)
        {
            if (Active.Count == 0 || pawn == null || !Active.TryGetValue(pawn, out var state)
                || state.groundPose?.PronePose != true) return;
            ThingWithComps weapon = state.groundPose.aimWeapon;
            if (weapon == null || weapon.Destroyed
                || pawn.equipment?.AllEquipmentListForReading.Contains(weapon) != true) BeginRise(state);
        }

        private static void BeginRise(RimKataPawnCombatState state)
        {
            state.groundPose.BeginRise();
            Prone.Remove(state.pawn);
            Unwatch(state.pawn, state.groundPose.focus.Thing);
        }

        private static void SetFocus(Pawn pawn, RimKataGroundPoseState pose, LocalTargetInfo target)
        {
            if (pose.focus.Equals(target)) return;
            Unwatch(pawn, pose.focus.Thing);
            pose.focus = target;
            if (!target.HasThing) return;
            if (!TargetWatchers.TryGetValue(target.Thing, out var watchers))
                TargetWatchers[target.Thing] = watchers = new List<Pawn>(1);
            if (!watchers.Contains(pawn)) watchers.Add(pawn);
        }

        private static void Unwatch(Pawn pawn, Thing target)
        {
            if (target == null || !TargetWatchers.TryGetValue(target, out var watchers)) return;
            watchers.Remove(pawn);
            if (watchers.Count == 0) TargetWatchers.Remove(target);
        }

        internal static void NotifyTargetMoved(Pawn target)
        {
            // Reuse the existing cell-change event; only a pose watching this target is visited.
            if (TargetWatchers.Count == 0 || target == null
                || !TargetWatchers.TryGetValue(target, out var watchers)) return;
            for (int i = watchers.Count - 1; i >= 0; i--)
            {
                Pawn pawn = watchers[i];
                if (!Active.TryGetValue(pawn, out var state)) continue;
                RimKataGroundPoseState pose = state.groundPose;
                if (pose?.PronePose == true)
                    NotifyAimStarted(pawn, pose.aimVerb
                        ?? RimKataWeaponSlotUtility.PrimaryVerb(pose.aimWeapon), pose.focus);
                else if (pose != null && pose.phase != RimKataFallPhase.Rising
                    && !RimKataGroundPoseConditions.HasAttackableOpponent(pawn, state)) BeginRise(state);
            }
        }

        internal static void Tick(RimKataPawnCombatState state)
        {
            RimKataGroundPoseState pose = state.groundPose;
            if (pose?.VisualActive != true) return;
            Pawn pawn = state.pawn;
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || !pawn.Awake()
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                Clear(state);
                return;
            }
            // Active visuals only: liveness is cheap; range/cover/admission belongs to combat events.
            if (pose.phase != RimKataFallPhase.Rising && pose.focus.HasThing
                && (pose.focus.Thing.Destroyed || !pose.focus.Thing.Spawned
                    || (pose.focus.Thing is Pawn victim
                        && !RimKataTargeting.IsPawnTargetStateValid(victim,
                            pawn.CurJob?.killIncappedTarget == true
                            && pawn.CurJob.targetA.Thing == victim))))
            {
                if (RimKataGroundPoseConditions.TryGetAttackableOpponent(pawn, state, out var next))
                {
                    SetFocus(pawn, pose, next);
                    if (pose.PronePose) NotifyAimStarted(pawn, pose.aimVerb
                        ?? RimKataWeaponSlotUtility.PrimaryVerb(pose.aimWeapon), next);
                }
                else BeginRise(state);
            }
            pose.headAimAngle = RimKataGroundPoseConditions.HeadAimAngle(pawn);
            if (pose.PronePose) pose.angle = pose.headAimAngle;
            pose.TickFall();
            if (pose.prone) Prone[pawn] = state;
            if (!pose.VisualActive)
            {
                if (pose.risingFromProne && state.ownerComponent != null)
                    state.ownerComponent.groundPoseResumeTicks[pawn] = (long)Find.TickManager.TicksGame
                        + (RimKataTargetAccess.SettingsFor(pawn)?.proneResumeDelayTicks ?? 50);
                Clear(state);
            }
        }

        internal static void NotifyAvoidance(Pawn defender, Thing attacker, bool melee)
        {
            RimKataPawnCombatState state = StateFor(defender);
            if (state == null) return;
            if (!melee)
            {
                if (state.groundPose?.PronePose == true) BeginRise(state);
                return;
            }
            TryFallOrRoll(state, attacker, melee);
        }

        internal static void NotifyMiss(Pawn defender, Thing attacker, bool melee)
        {
            if (!melee) return;
            RimKataPawnCombatState state = StateFor(defender);
            if (state != null) TryFallOrRoll(state, attacker, melee);
        }

        private static RimKataPawnCombatState StateFor(Pawn pawn)
        {
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)) return null;
            return RimKataCombatStatePresenceCache.TryGetOwner(pawn, out var owner)
                ? owner.GetState(pawn, false) : null;
        }

        private static void TryFallOrRoll(RimKataPawnCombatState state, Thing attacker, bool melee)
        {
            Pawn pawn = state.pawn;
            if (!melee || attacker == null || attacker == pawn || attacker.Map != pawn.Map) return;
            RimKataGroundPoseState pose = state.groundPose;
            if (pose?.phase == RimKataFallPhase.Fallen)
            {
                if (melee) pose.BeginRoll();
                return;
            }
            if (pose?.VisualActive == true || state.DodgeMovementActive) return;
            // A single opponent gates entering the fall, not subsequent rolls.
            if (!RimKataGroundPoseConditions.HasSingleCloseOpponent(pawn, state, attacker)) return;
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(pawn);
            if (!Rand.Chance(settings?.MeleeFallChance ?? 0f)) return;
            if (!RimKataGroundPoseHead.Supports(pawn)) return;
            pose ??= state.groundPose = new RimKataGroundPoseState();
            Vector3 away = pawn.DrawPos - attacker.DrawPos;
            away.y = 0f;
            if (away.sqrMagnitude < 0.001f) away = -pawn.Rotation.FacingCell.ToVector3();
            away.Normalize();
            pose.phase = RimKataFallPhase.Falling;
            pose.angle = LieAngle(away, pawn.Rotation);
            pose.headAimAngle = RimKataGroundPoseConditions.HeadAimAngle(pawn);
            pose.originalFacing = pawn.Rotation;
            pose.rollAxis = new Vector3(away.z, 0f, -away.x);
            pose.transitionTicks = RimKataGroundPoseState.TransitionDuration;
            pose.holdTicks = settings?.meleeFallDurationTicks ?? 50;
            pose.offset = pose.rollOrigin = Vector3.zero;
            pose.rolling = false;
            pose.rollStep = pose.rollTicks = 0;
            SetFocus(pawn, pose, attacker);
            Register(state);
            RimKataResponseVisualParticipantCache.RefreshBodyVisual(state);
        }

        private static float LieAngle(Vector3 direction, Rot4 facing)
        {
            if (direction.x * direction.x + direction.z * direction.z < 0.0001f)
                direction = facing.FacingCell.ToVector3();
            // The sprite's head points north before rotation. Keep the complete
            // direction, including diagonals, rather than selecting a side.
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        internal static bool TryGetShotCenter(Verb verb, out Vector3 center, bool headCentered = false)
        {
            center = Vector3.zero;
            if (!TryGetShotState(verb, out var state)) return false;
            Vector3 aimOrigin = headCentered
                ? RimKataGroundPoseGeometry.AimOrigin(state.pawn, state.groundPose) : state.pawn.DrawPos;
            center = RimKataGroundPoseGeometry.StandingCenter(verb, aimOrigin);
            // Vanilla, CE and Muzzle Flash share the finalized head-pivot
            // placement; each adapter retains its own trajectory and effects.
            center += RimKataGroundPoseGeometry.Displacement(state.pawn, center, state.groundPose, headCentered);
            return true;
        }

        internal static bool TryGetResponseCenter(Pawn pawn, ThingWithComps weapon,
            LocalTargetInfo focus, out Vector3 center)
        {
            center = Vector3.zero;
            if (Active.Count == 0 || pawn == null || !focus.IsValid
                || weapon?.def.IsRangedWeapon != true || weapon.Destroyed
                || !Active.TryGetValue(pawn, out var state) || state.groundPose?.VisualActive != true
                || !pawn.Spawned || pawn.Dead || pawn.Downed
                || !RimKataGroundPoseGeometry.HasUsableAnchor(pawn, weapon)) return false;
            // Match the response renderer's target angle, not the weapon's
            // unrelated firing target or a previous world-space render frame.
            Vector3 target = focus.HasThing && focus.Thing.Spawned
                ? focus.Thing.DrawPos : focus.Cell.ToVector3Shifted();
            Vector3 direction = target - RimKataGroundPoseGeometry.AimOrigin(pawn, state.groundPose);
            float aim = direction.sqrMagnitude > 0.001f ? direction.AngleFlat() : pawn.Rotation.AsAngle;
            center = RimKataGroundPoseGeometry.StandingCenter(pawn, weapon, aim);
            center += RimKataGroundPoseGeometry.Displacement(pawn, center, state.groundPose, headCentered: true);
            return true;
        }

        private static bool TryGetShotState(Verb verb, out RimKataPawnCombatState state)
        {
            state = null;
            if (Active.Count == 0 || verb?.IsMeleeAttack != false) return false;
            Pawn pawn = verb.CasterPawn;
            ThingWithComps weapon = verb.EquipmentSource;
            return pawn != null && Active.TryGetValue(pawn, out state)
                && state.groundPose?.VisualActive == true && pawn.Spawned && !pawn.Dead && !pawn.Downed
                && weapon != null && !weapon.Destroyed
                && pawn.equipment?.AllEquipmentListForReading.Contains(weapon) == true
                && RimKataGroundPoseHead.Supports(pawn)
                && RimKataGroundPoseGeometry.HasUsableAnchor(pawn, weapon);
        }

        internal static void Clear(RimKataPawnCombatState state)
        {
            if (state?.pawn == null || state.groundPose == null) return;
            state.ownerComponent?.UnregisterGroundPose(state);
            Prone.Remove(state.pawn);
            Active.Remove(state.pawn);
            Unwatch(state.pawn, state.groundPose.focus.Thing);
            RimKataGroundPoseRender.Clear(state.pawn);
            state.groundPose = null;
            RimKataResponseVisualParticipantCache.RefreshBodyVisual(state);
        }

        internal static void Rebuild(RimKataPawnCombatState state)
        {
            if (state?.pawn == null || state.groundPose == null) return;
            RimKataGroundPoseState pose = state.groundPose;
            if (pose.resumeProneTick >= 0 && state.ownerComponent != null)
            {
                state.ownerComponent.groundPoseResumeTicks[state.pawn] = pose.resumeProneTick;
                pose.resumeProneTick = -1;
            }
            if (!pose.VisualActive)
            {
                state.groundPose = null;
                return;
            }
            if (pose.prone)
                Prone[state.pawn] = state;
            Register(state);
            if (pose.phase == RimKataFallPhase.Rising) return;
            LocalTargetInfo savedFocus = pose.focus;
            if (!savedFocus.IsValid && state.pawn.stances?.curStance is Stance_Busy busy)
                savedFocus = busy.focusTarg;
            if (!savedFocus.IsValid && (state.pawn.CurJobDef == JobDefOf.AttackStatic
                || state.pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                || state.pawn.jobs?.curDriver is JobDriver_Hunt))
                savedFocus = state.pawn.CurJob.targetA;
            if (!savedFocus.IsValid)
                RimKataGroundPoseConditions.TryGetAttackableOpponent(state.pawn, state, out savedFocus);
            pose.focus = LocalTargetInfo.Invalid;
            SetFocus(state.pawn, pose, savedFocus);
            pose.aimWeapon ??= state.pawn.equipment?.Primary;
        }

        private static void Register(RimKataPawnCombatState state)
        {
            Active[state.pawn] = state;
            state.ownerComponent?.RegisterGroundPose(state);
        }

        internal static void ClearMap(Map map)
        {
            RimKataGroundPoseRender.ClearMap(map);
            var activeRemove = new List<Pawn>();
            foreach (var pair in Active)
                if (pair.Key.Map == map || pair.Value.ownerComponent?.map == map) activeRemove.Add(pair.Key);
            foreach (Pawn pawn in activeRemove)
            {
                RimKataPawnCombatState state = Active[pawn];
                state.ownerComponent?.UnregisterGroundPose(state);
                Unwatch(pawn, state.groundPose?.focus.Thing);
                Active.Remove(pawn);
            }
            if (Prone.Count == 0) return;
            var remove = new List<Pawn>();
            foreach (var pair in Prone)
                if (pair.Key.Map == map || pair.Value.ownerComponent?.map == map) remove.Add(pair.Key);
            foreach (Pawn pawn in remove) Prone.Remove(pawn);
        }
    }
}
