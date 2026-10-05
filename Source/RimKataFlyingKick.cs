using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal enum RimKataFlyingKickPhase { Approach, Strike, Land, Fall, Abort }

    public sealed class RimKataFlyingKickState : IExposable
    {
        internal RimKataFlyingKickPhase phase;
        internal Thing target;
        internal int jobId, age, phaseTicks, descentTicks, recoveryTicks, contactTicks;
        internal int lastTick = -1;
        internal float startHeight, startAngle;
        internal Rot4 facing;
        internal IntVec3 pawnCell, targetCell, approachStep;
        internal bool replaceUnarmedAttack, attackResolved, attackHit, contactStarted;
        internal Vector3 contactStart, contactEnd;
        internal RimKataFlyingKickVisual visual;
        internal readonly RimKataFlyingKickRender.FrameSlot frame = new RimKataFlyingKickRender.FrameSlot();
        internal readonly RimKataFlyingKickAttack attack = new RimKataFlyingKickAttack();

        public void ExposeData()
        {
            Scribe_Values.Look(ref phase, "phase");
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref jobId, "jobId");
            Scribe_Values.Look(ref age, "age");
            Scribe_Values.Look(ref phaseTicks, "phaseTicks");
            Scribe_Values.Look(ref descentTicks, "descentTicks");
            Scribe_Values.Look(ref recoveryTicks, "recoveryTicks");
            Scribe_Values.Look(ref contactTicks, "contactTicks");
            Scribe_Values.Look(ref startHeight, "startHeight");
            Scribe_Values.Look(ref startAngle, "startAngle");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref pawnCell, "pawnCell", IntVec3.Invalid);
            Scribe_Values.Look(ref targetCell, "targetCell", IntVec3.Invalid);
            Scribe_Values.Look(ref approachStep, "approachStep");
            Scribe_Values.Look(ref replaceUnarmedAttack, "replaceUnarmedAttack");
            Scribe_Values.Look(ref attackResolved, "attackResolved");
            Scribe_Values.Look(ref attackHit, "attackHit");
            Scribe_Values.Look(ref contactStarted, "contactStarted");
            Scribe_Values.Look(ref contactStart, "contactStart");
            Scribe_Values.Look(ref contactEnd, "contactEnd");
            Scribe_Values.Look(ref visual.angle, "angle");
            Scribe_Values.Look(ref visual.height, "height");
            Scribe_Values.Look(ref visual.offset, "offset");
            Scribe_Values.Look(ref visual.footPinned, "footPinned");
            Scribe_Values.Look(ref visual.footPosition, "footPosition");
            Scribe_Values.Look(ref visual.strikeFacing, "strikeFacing", Rot4.Invalid);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                visual.facing = facing;
                if (facing == Rot4.South && !visual.strikeFacing.IsValid)
                    visual.strikeFacing = Rand.Bool ? Rot4.East : Rot4.West;
                visual.lieAngle = RimKataGroundPoseUtility.LieAngle(
                    -(approachStep == IntVec3.Zero ? facing.FacingCell : approachStep).ToVector3(), facing);
                visual.frame = frame;
                lastTick = -1;
                if (!contactStarted && phase == RimKataFlyingKickPhase.Strike && visual.footPinned)
                {
                    contactStarted = true;
                    contactTicks += 6;
                    age = 6 + phaseTicks;
                    startAngle = visual.angle;
                    float progress = Mathf.Clamp01(age / (float)Math.Max(1, contactTicks));
                    visual.height = 1.2f * (1f - Mathf.Clamp01((age - 6f) / Math.Max(1, contactTicks - 6)));
                    if (progress < 1f)
                        contactStart = (visual.footPosition - contactEnd * progress
                            - new Vector3(0f, 0f, visual.height)) / (1f - progress);
                }
            }
        }
    }

    internal static class RimKataFlyingKick
    {
        private static readonly Func<Pawn_PathFollower, float> MovementCostPerTick =
            AccessTools.MethodDelegate<Func<Pawn_PathFollower, float>>(
                AccessTools.Method(typeof(Pawn_PathFollower), "CostToPayThisTick"));
        private static readonly Func<Pawn_PathFollower, IntVec3, float> MovementCellCost =
            AccessTools.MethodDelegate<Func<Pawn_PathFollower, IntVec3, float>>(
                AccessTools.Method(typeof(Pawn_PathFollower), "CostToMoveIntoCell", new[] { typeof(IntVec3) }));
        private static readonly Dictionary<Pawn, RimKataPawnCombatState> Active =
            new Dictionary<Pawn, RimKataPawnCombatState>();

        internal static bool IsApproachCellCandidate(Pawn pawn, out IntVec3 previous)
        {
            previous = default;
            Job job = pawn?.CurJob;
            if (job == null || job.def != JobDefOf.AttackMelee && job.def != RimKataDefOf.RimKata_Attack)
                return false;
            Thing target = job.targetA.Thing;
            if (target?.Spawned != true || target.Map != pawn.Map || pawn.pather?.Moving != true
                || pawn.pather.Destination.Thing != target) return false;
            previous = pawn.Position;
            return Distance(previous, target.Position) == 3;
        }

        internal static void CellEntered(RimKataResponseVisualParticipantCache.BodyVisualEntry entry, IntVec3 previous)
        {
            Pawn pawn = entry.pawn;
            if (pawn.Position == previous || pawn.pather?.Moving != true) return;
            Thing target = pawn.pather.Destination.Thing;
            if (target?.Spawned != true || Distance(previous, target.Position) != 3
                || Distance(pawn.Position, target.Position) != 2
                || Active.ContainsKey(pawn) || !LiveTarget(pawn, target)) return;
            var settings = RimKataTargetAccess.SettingsFor(pawn);
            if (settings?.flyingKickEnabled != true || !settings.GetFlyingKickAllowed(pawn)) return;
            IntVec3 approach = target.Position - pawn.Position;
            IntVec3 step = new IntVec3(Math.Sign(approach.x), 0, Math.Sign(approach.z));
            if (pawn.Position - previous != step || target.Position - previous != step * 3) return;
            RimKataPawnCombatState combat = entry.snapshotState;
            if (!ApprovedApproach(pawn, target, ref combat) || !CanStart(pawn, combat)
                || !StraightPath(pawn, target.Position)) return;
            var owner = combat?.ownerComponent ?? pawn.Map.GetComponent<RimKataMapComponent>();
            combat ??= owner.GetState(pawn, true);
            if (combat == null || HasOtherMotion(pawn, combat)) return;
            Rot4 facing = Rot4.FromAngleFlat((target.Position - pawn.Position).AngleFlat);
            var flight = new RimKataFlyingKickState {
                target = target, jobId = pawn.CurJob.loadID, facing = facing,
                pawnCell = pawn.Position, targetCell = target.Position, approachStep = step,
                replaceUnarmedAttack = pawn.equipment?.Primary == null
            };
            flight.visual = new RimKataFlyingKickVisual { facing = facing, frame = flight.frame,
                strikeFacing = facing == Rot4.South ? Rand.Bool ? Rot4.East : Rot4.West : Rot4.Invalid,
                lieAngle = RimKataGroundPoseUtility.LieAngle(-step.ToVector3(), facing) };
            combat.flyingKick = flight;
            Active[pawn] = combat;
            BeginContact(combat);
            Publish(combat);
        }

        internal static int Distance(IntVec3 a, IntVec3 b)
            => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.z - b.z));

        private static bool ApprovedApproach(Pawn pawn, Thing target, ref RimKataPawnCombatState combat)
        {
            Job job = pawn.CurJob;
            if (job == null || job.targetA.Thing != target) return false;
            if (job.def == JobDefOf.AttackMelee)
                return job.playerForced || !pawn.Drafted && pawn.equipment?.Primary == null;
            if (job.def != RimKataDefOf.RimKata_Attack) return false;
            if (combat == null
                && RimKataCombatStatePresenceCache.TryGetOwner(pawn, out var owner))
                combat = owner.GetState(pawn, false);
            if (combat?.IsPlayerRushRequestFor(target) == true) return true;
            return !pawn.Drafted && RimKataDualWeaponController.CanRushTarget(pawn, target, combat);
        }

        private static bool CanStart(Pawn pawn, RimKataPawnCombatState combat)
            => pawn.Spawned && !pawn.Dead && !pawn.Downed && pawn.Awake()
                && !pawn.InMentalState && !pawn.IsBurning() && pawn.kindDef.canMeleeAttack
                && pawn.GetPosture() == PawnPosture.Standing
                && pawn.stances?.stagger?.Staggered != true && pawn.stances?.stunner?.Stunned != true
                && !HasOtherMotion(pawn, combat) && RimKataGroundPoseHead.Supports(pawn);

        private static bool HasOtherMotion(Pawn pawn, RimKataPawnCombatState combat)
            => combat?.groundPose != null || combat?.reactiveMotion != null
                || combat?.kick?.motionActive == true
                || combat?.VisualActive == true || combat?.CloseDodgeActive == true
                || combat?.DodgeMovementActive == true || combat?.DeflectionSpinActive == true
                || RimKataBreachUtility.Get(pawn) != null || RimKataSubdueUtility.IsHolding(pawn);

        private static bool LiveTarget(Pawn pawn, Thing target)
            => target?.Spawned == true && !target.Destroyed && target.Map == pawn.Map
                && RimKataTargeting.IsAutomaticEnemy(pawn, target)
                && (!(target is Pawn victim) || RimKataTargeting.IsPawnTargetStateValid(victim));

        internal static bool StraightPath(Pawn pawn, IntVec3 destination)
        {
            IntVec3 origin = pawn.Position;
            IntVec3 delta = destination - origin;
            int dx = Math.Abs(delta.x), dz = Math.Abs(delta.z);
            if (dx != 0 && dz != 0 && dx != dz) return false;
            int steps = Math.Max(dx, dz);
            if (steps < 1 || steps > 2) return false;
            IntVec3 direction = new IntVec3(Math.Sign(delta.x), 0, Math.Sign(delta.z));
            Map map = pawn.Map;
            var context = map.pathing.For(pawn);
            for (int i = 1; i <= steps; i++)
            {
                IntVec3 next = origin + direction;
                if (!next.InBounds(map) || !next.WalkableBy(map, pawn)) return false;
                Building_Door door = next.GetDoor(map);
                if (door != null && (!door.Open || door.TicksTillFullyOpened > 0)) return false;
                if (direction.x != 0 && direction.z != 0
                    && (PathUtility.BlocksDiagonalMovement(origin.x + direction.x, origin.z, context, pawn.CurJob.canBashFences)
                        || PathUtility.BlocksDiagonalMovement(origin.x, origin.z + direction.z, context, pawn.CurJob.canBashFences))) return false;
                List<Thing> things = next.GetThingList(map);
                for (int j = 0; j < things.Count; j++)
                    if (things[j] is Pawn occupant && occupant != pawn
                        && occupant != pawn.CurJob.targetA.Thing && !occupant.Downed) return false;
                origin = next;
            }
            return true;
        }

        internal static void Tick(RimKataPawnCombatState combat)
        {
            RimKataFlyingKickState flight = combat.flyingKick;
            if (flight == null || flight.lastTick == Find.TickManager.TicksGame) return;
            flight.lastTick = Find.TickManager.TicksGame;
            Pawn pawn = combat.pawn;
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || !pawn.Awake() || HasOtherMotion(pawn, combat))
            {
                Clear(combat);
                return;
            }
            bool airborne = flight.phase == RimKataFlyingKickPhase.Approach || flight.phase == RimKataFlyingKickPhase.Strike;
            bool touch = false;
            if (airborne) ObserveAttackResult(combat);
            if (airborne && !flight.attackResolved && flight.phase != RimKataFlyingKickPhase.Abort)
            {
                if (pawn.CurJob?.loadID != flight.jobId || pawn.CurJob.targetA.Thing != flight.target
                    || !LiveTarget(pawn, flight.target) || pawn.InMentalState || pawn.IsBurning()
                    || pawn.stances.stagger.Staggered || pawn.stances.stunner.Stunned)
                    Abort(combat);
                else
                {
                    if (touch = pawn.CanReachImmediate(flight.target, PathEndMode.Touch))
                        Strike(combat, validated: true);
                    if (combat.flyingKick != flight) return;
                    ObserveAttackResult(combat);
                }
            }
            if (flight.phase == RimKataFlyingKickPhase.Approach || flight.phase == RimKataFlyingKickPhase.Strike)
            {
                if (!flight.contactStarted) BeginContact(combat);
                flight.age++;
                if (flight.phase == RimKataFlyingKickPhase.Approach)
                {
                    flight.visual.angle = Side(flight) * 18f * Mathf.Clamp01(flight.age / 3f);
                    if (flight.age >= 6)
                    {
                        flight.phase = RimKataFlyingKickPhase.Strike;
                        flight.phaseTicks = 0;
                        flight.startAngle = flight.visual.angle;
                    }
                }
                else
                {
                    float turn = Mathf.Clamp01(++flight.phaseTicks / (float)RimKataGroundPoseState.TransitionDuration);
                    float strikeAngle = flight.facing.IsHorizontal ? -Side(flight) * 72f : flight.visual.lieAngle;
                    flight.visual.angle = Mathf.Lerp(flight.startAngle, strikeAngle, turn);
                }
                float progress = Mathf.Clamp01(flight.age / (float)Math.Max(1, flight.contactTicks));
                float lift = flight.age <= 6 ? flight.age / 6f
                    : 1f - Mathf.Clamp01((flight.age - 6f) / Math.Max(1, flight.contactTicks - 6));
                flight.visual.height = 1.2f * lift;
                flight.visual.footPosition = Vector3.Lerp(flight.contactStart, flight.contactEnd, progress)
                    + new Vector3(0f, 0f, flight.visual.height);
                if (progress >= 1f && flight.phase == RimKataFlyingKickPhase.Strike
                    && flight.phaseTicks >= RimKataGroundPoseState.TransitionDuration && flight.attackResolved)
                    BeginDescent(flight, flight.attackHit ? RimKataFlyingKickPhase.Land : RimKataFlyingKickPhase.Fall, 4);
            }
            else
            {
                float progress = Mathf.Clamp01(++flight.phaseTicks / (float)Math.Max(1, flight.descentTicks));
                flight.visual.height = flight.startHeight * (1f - progress);
                float endAngle = flight.phase == RimKataFlyingKickPhase.Fall
                    ? flight.visual.lieAngle : 0f;
                flight.visual.angle = Mathf.Lerp(flight.startAngle, endAngle, progress);
                if (flight.visual.footPinned)
                    flight.visual.footPosition = Vector3.Lerp(flight.contactStart,
                        RimKataFlyingKickRender.NaturalFoot(pawn, flight.visual), progress);
                if (progress >= 1f)
                {
                    bool fall = flight.phase == RimKataFlyingKickPhase.Fall;
                    Clear(combat);
                    if (fall) RimKataGroundPoseUtility.AcceptFlyingKickFall(combat, flight.target,
                        flight.visual.angle, flight.visual.offset);
                    return;
                }
            }
            if (!flight.attackResolved
                && (flight.phase == RimKataFlyingKickPhase.Approach || flight.phase == RimKataFlyingKickPhase.Strike)
                && !touch)
            {
                if (pawn.pather?.Moving != true || pawn.pather.Destination.Thing != flight.target)
                    Abort(combat);
                else if (flight.pawnCell != pawn.Position || flight.targetCell != flight.target.Position)
                {
                    flight.pawnCell = pawn.Position;
                    flight.targetCell = flight.target.Position;
                    if (!StraightPath(pawn, flight.targetCell)) Abort(combat);
                }
            }
            if (combat.flyingKick == flight) Publish(combat);
        }

        private static void ObserveAttackResult(RimKataPawnCombatState combat)
        {
            var flight = combat.flyingKick;
            if (flight.attackResolved || !flight.attack.Completed) return;
            if (flight.attack.Cancelled) { Abort(combat); return; }
            flight.attackResolved = true;
            flight.attackHit = flight.attack.Hit;
            if (flight.attack.AttackVerb is Verb verb)
                flight.recoveryTicks = Math.Max(1, Mathf.RoundToInt(verb.verbProps.AdjustedCooldownTicks(verb, combat.pawn)));
        }

        private static void BeginContact(RimKataPawnCombatState combat)
        {
            var flight = combat.flyingKick;
            if (!RimKataFlyingKickRender.CaptureContact(combat.pawn, flight.target, flight.visual,
                out flight.contactStart, out flight.contactEnd))
                flight.contactEnd = flight.targetCell.ToVector3Shifted();
            flight.contactTicks = Math.Max(6 + RimKataGroundPoseState.TransitionDuration,
                flight.age + ContactDuration(combat.pawn, flight.approachStep));
            flight.contactStarted = true;
            flight.visual.footPinned = true;
            flight.visual.footPosition = flight.contactStart;
            if (flight.age > 0)
            {
                float progress = Mathf.Clamp01(flight.age / (float)flight.contactTicks);
                flight.contactStart = (flight.visual.footPosition - flight.contactEnd * progress
                    - new Vector3(0f, 0f, flight.visual.height)) / (1f - progress);
            }
        }

        private static int ContactDuration(Pawn pawn, IntVec3 step)
        {
            var pather = pawn.pather;
            float cost = pather.Moving ? pather.nextCellCostLeft : 0f;
            float rate = MovementCostPerTick(pather);
            if (cost <= 0f)
            {
                if (step == IntVec3.Zero) step = pawn.Rotation.FacingCell;
                IntVec3 next = pawn.Position + step;
                cost = next.InBounds(pawn.Map) ? MovementCellCost(pather, next)
                    : step.x != 0 && step.z != 0 ? pawn.TicksPerMoveDiagonal : pawn.TicksPerMoveCardinal;
                rate = Mathf.Max(rate, cost / 450f);
            }
            return Math.Max(1, Mathf.CeilToInt(cost / Mathf.Max(0.000001f, rate)));
        }

        private static void BeginDescent(RimKataFlyingKickState flight, RimKataFlyingKickPhase phase, int ticks)
        {
            flight.phase = phase;
            flight.startHeight = flight.visual.height;
            flight.startAngle = flight.visual.angle;
            flight.contactStart = flight.visual.footPosition;
            flight.phaseTicks = 0;
            flight.descentTicks = ticks;
        }

        private static int Side(RimKataFlyingKickState flight)
            => flight.facing == Rot4.East ? 1 : flight.facing == Rot4.West ? -1 : 0;

        private static bool ExecuteAttack(RimKataPawnCombatState combat)
        {
            var settings = RimKataTargetAccess.SettingsFor(combat.pawn);
            return combat.flyingKick.attack.Execute(combat.pawn, combat.flyingKick.target,
                settings?.FlyingKickDamageMultiplier ?? 2f, settings?.GetFlyingKickStunChance(combat.pawn) ?? 0f);
        }

        private static bool Strike(RimKataPawnCombatState combat, bool validated = false)
        {
            var flight = combat.flyingKick;
            if (flight.attackResolved || flight.attack.Completed) return true;
            if (flight.phase != RimKataFlyingKickPhase.Approach && flight.phase != RimKataFlyingKickPhase.Strike) return false;
            Pawn pawn = combat.pawn;
            if (!validated && (!pawn.Spawned || pawn.Dead || pawn.Downed || pawn.InMentalState
                || pawn.stances.stunner.Stunned || pawn.stances.stagger.Staggered
                || pawn.CurJob?.loadID != flight.jobId || pawn.CurJob.targetA.Thing != flight.target
                || !LiveTarget(pawn, flight.target) || !pawn.CanReachImmediate(flight.target, PathEndMode.Touch)))
            {
                Abort(combat);
                return false;
            }
            if (!ExecuteAttack(combat)) { Abort(combat); return false; }
            if (combat.flyingKick == flight) ObserveAttackResult(combat);
            return true;
        }

        internal static bool ReplaceUnarmedAttack(Pawn pawn, Thing target, out bool result)
        {
            result = false;
            if (!Active.TryGetValue(pawn, out var combat)) return false;
            var flight = combat.flyingKick;
            if (flight == null || !flight.replaceUnarmedAttack || flight.target != target
                || pawn.equipment?.Primary != null || pawn.stances.FullBodyBusy
                || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return false;
            if ((flight.phase == RimKataFlyingKickPhase.Approach || flight.phase == RimKataFlyingKickPhase.Strike)
                && !Strike(combat)) return false;
            if (flight.phase == RimKataFlyingKickPhase.Abort) return false;
            if (flight.recoveryTicks <= 0) return true;
            flight.replaceUnarmedAttack = false;
            pawn.stances.SetStance(new Stance_Cooldown(flight.recoveryTicks, target, null));
            result = true;
            return true;
        }

        internal static void Abort(RimKataPawnCombatState combat)
        {
            var flight = combat?.flyingKick;
            if (flight == null || flight.phase == RimKataFlyingKickPhase.Abort) return;
            flight.attack.Cancel();
            BeginDescent(flight, RimKataFlyingKickPhase.Abort, Math.Max(1, Math.Min(6, flight.age)));
            flight.replaceUnarmedAttack = false;
        }

        internal static void QualificationLost(Pawn pawn)
        {
            if (pawn != null && Active.TryGetValue(pawn, out var combat)) Abort(combat);
        }

        internal static void ResetGame() => Active.Clear();

        internal static void Clear(RimKataPawnCombatState combat)
        {
            if (combat?.flyingKick == null) return;
            combat.flyingKick.attack.Cancel();
            RimKataFlyingKickRender.Remove(combat.pawn, combat.flyingKick.frame);
            combat.flyingKick = null;
            Active.Remove(combat.pawn);
        }

        internal static void Rebuild(RimKataPawnCombatState combat)
        {
            if (combat?.flyingKick == null) return;
            Active[combat.pawn] = combat;
            combat.flyingKick.visual.frame = combat.flyingKick.frame;
            Publish(combat);
        }

        private static void Publish(RimKataPawnCombatState combat)
        {
            var flight = combat.flyingKick;
            flight.visual.striking = flight.phase == RimKataFlyingKickPhase.Strike && flight.phaseTicks > 0;
            flight.visual.falling = flight.phase == RimKataFlyingKickPhase.Fall;
            flight.visual.aimTarget = flight.visual.falling ? flight.target : null;
            RimKataFlyingKickRender.Publish(combat.pawn, flight.visual);
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    internal static class Patch_PathCell_RimKataFlyingKick
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            Label original = generator.DefineLabel(), finished = generator.DefineLabel(), absent = generator.DefineLabel();
            Label attackJob = generator.DefineLabel();
            LocalBuilder old = generator.DeclareLocal(typeof(IntVec3));
            LocalBuilder qualified = generator.DeclareLocal(typeof(bool));
            LocalBuilder job = generator.DeclareLocal(typeof(Job));
            var pawnField = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, pawnField);
            yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.CurJob)));
            yield return new CodeInstruction(OpCodes.Stloc, job);
            yield return new CodeInstruction(OpCodes.Ldloc, job);
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldloc, job);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Job), nameof(Job.def)));
            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(JobDefOf), nameof(JobDefOf.AttackMelee)));
            yield return new CodeInstruction(OpCodes.Beq, attackJob);
            yield return new CodeInstruction(OpCodes.Ldloc, job);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Job), nameof(Job.def)));
            yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataDefOf), nameof(RimKataDefOf.RimKata_Attack)));
            yield return new CodeInstruction(OpCodes.Bne_Un, original);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(attackJob);
            var gate = RimKataRegisteredPawnGate.Branch(generator, new[] {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, pawnField)
            }, original, true, out LocalBuilder entry);
            foreach (var code in gate) yield return code;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, pawnField);
            yield return new CodeInstruction(OpCodes.Ldloca, old);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataFlyingKick),
                nameof(RimKataFlyingKick.IsApproachCellCandidate)));
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldc_I4_1);
            yield return new CodeInstruction(OpCodes.Stloc, qualified);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(original);
            foreach (var code in codes)
            {
                if (code.opcode == OpCodes.Ret) { code.opcode = OpCodes.Br; code.operand = finished; }
                yield return code;
            }
            yield return new CodeInstruction(OpCodes.Ldloc, qualified).WithLabels(finished);
            yield return new CodeInstruction(OpCodes.Brfalse, absent);
            var refresh = RimKataRegisteredPawnGate.Branch(generator, new[] {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, pawnField)
            }, absent, true, out LocalBuilder currentEntry);
            foreach (var code in refresh) yield return code;
            yield return new CodeInstruction(OpCodes.Ldloc, currentEntry);
            yield return new CodeInstruction(OpCodes.Ldloc, old);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataFlyingKick), nameof(RimKataFlyingKick.CellEntered)));
            yield return new CodeInstruction(OpCodes.Ret).WithLabels(absent);
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack))]
    internal static class Patch_MeleeAttempt_RimKataFlyingKick
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            Label original = generator.DefineLabel();
            LocalBuilder result = generator.DeclareLocal(typeof(bool));
            var pawnField = AccessTools.Field(typeof(Pawn_MeleeVerbs), "pawn");
            var gate = RimKataRegisteredPawnGate.Branch(generator, new[] {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, pawnField)
            }, original, true, out LocalBuilder entry);
            foreach (var code in gate) yield return code;
            yield return new CodeInstruction(OpCodes.Ldloc, entry);
            yield return new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(typeof(RimKataResponseVisualParticipantCache.BodyVisualEntry), "flyingKick"));
            yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(RimKataFlyingKickVisual?), "HasValue"));
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, pawnField);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Ldloca, result);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataFlyingKick), nameof(RimKataFlyingKick.ReplaceUnarmedAttack)));
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldloc, result);
            yield return new CodeInstruction(OpCodes.Ret);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(original);
            foreach (var code in instructions) yield return code;
        }
    }
}
