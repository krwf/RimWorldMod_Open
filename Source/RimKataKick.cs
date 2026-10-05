using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Entry = KRWF.RimKata.RimKataResponseVisualParticipantCache.BodyVisualEntry;

namespace KRWF.RimKata
{
    public sealed class RimKataKickState : IExposable
    {
        internal bool observingStagger, opportunity, motionActive, attackResolved;
        internal int cooldownUntil, age, jobId;
        internal int lastTick = -1;
        internal float targetAngle, damageMultiplier;
        internal Thing target, combatTarget;
        internal Rot4 facing, strikeFacing;
        internal RimKataKickVisual visual;
        internal readonly RimKataKickRender.FrameSlot frame = new RimKataKickRender.FrameSlot();
        internal readonly RimKataKickAttack attack = new RimKataKickAttack();

        public void ExposeData()
        {
            Scribe_Values.Look(ref observingStagger, "observingStagger");
            Scribe_Values.Look(ref opportunity, "opportunity");
            Scribe_Values.Look(ref motionActive, "motionActive");
            Scribe_Values.Look(ref attackResolved, "attackResolved");
            Scribe_Values.Look(ref cooldownUntil, "cooldownUntil");
            Scribe_Values.Look(ref age, "age");
            Scribe_Values.Look(ref jobId, "jobId");
            Scribe_Values.Look(ref targetAngle, "targetAngle");
            Scribe_Values.Look(ref damageMultiplier, "damageMultiplier");
            Scribe_References.Look(ref target, "target");
            Scribe_References.Look(ref combatTarget, "combatTarget");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref strikeFacing, "strikeFacing", Rot4.Invalid);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (facing == Rot4.South && !strikeFacing.IsValid)
                    strikeFacing = Rand.Bool ? Rot4.East : Rot4.West;
                visual = new RimKataKickVisual { facing = facing, strikeFacing = strikeFacing, frame = frame,
                    angle = RimKataKick.AngleAt(age, targetAngle) };
                lastTick = -1;
            }
        }
    }

    internal static class RimKataKick
    {
        internal const int TurnTicks = 6;
        internal const int Duration = TurnTicks * 2;
        private static readonly Dictionary<Pawn, RimKataPawnCombatState> Tracked =
            new Dictionary<Pawn, RimKataPawnCombatState>();

        internal static void StaggerApplied(Entry entry, bool wasStaggered)
        {
            Pawn pawn = entry.pawn;
            if (!Tracked.TryGetValue(pawn, out var combat)) combat = entry.snapshotState;
            RimKataMapComponent owner = combat?.ownerComponent ?? entry.snapshotOwner;
            if (owner == null) RimKataCombatStatePresenceCache.TryGetOwner(pawn, out owner);
            owner ??= pawn.Map?.GetComponent<RimKataMapComponent>();
            int now = Find.TickManager.TicksGame;
            if (now < (owner?.KickCooldownUntil(pawn) ?? 0))
            {
                ClearOpportunity(combat);
                return;
            }
            if (combat == null) combat = owner?.GetState(pawn, false);
            RimKataKickState kick = combat?.kick;
            bool observed = kick?.observingStagger == true;
            if (kick != null)
            {
                ClearOpportunity(combat);
                if (now < kick.cooldownUntil) return;
            }
            if (wasStaggered && !observed) return;
            if (RimKataTargetAccess.SettingsFor(pawn)?.kickEnabled != true
                || !ReadyPawn(pawn)) return;
            Thing closeTarget = CloseTarget(pawn, combat);
            if (closeTarget == null) return;
            if (combat == null)
            {
                owner ??= pawn.Map.GetComponent<RimKataMapComponent>();
                combat = owner.GetState(pawn, true);
            }
            kick = combat.kick ??= new RimKataKickState();
            kick.combatTarget = closeTarget;
            kick.observingStagger = true;
            kick.opportunity = false;
            Tracked[pawn] = combat;
        }

        private static Thing CloseTarget(Pawn pawn, RimKataPawnCombatState combat)
        {
            if (combat != null && combat.TryGetLiveCloseCombatTrigger(out Thing trigger))
                return trigger;
            Job job = pawn.CurJob;
            if ((job?.def == JobDefOf.AttackMelee || job?.def == RimKataDefOf.RimKata_Attack)
                && InTouch(pawn, job.targetA.Thing)) return job.targetA.Thing;
            if (RimKataDraftedFireController.IsAutomaticFireJob(job?.def))
            {
                Thing threat = pawn.mindState?.meleeThreat;
                if (InTouch(pawn, threat) && RimKataTargeting.IsAutomaticEnemy(pawn, threat))
                    return threat;
            }
            return null;
        }

        private static bool InTouch(Pawn pawn, Thing target)
            => LiveTarget(pawn, target) && pawn.CanReachImmediate(target, PathEndMode.Touch);

        internal static void ClearOpportunity(RimKataPawnCombatState combat)
        {
            if (combat?.kick is RimKataKickState kick)
            {
                kick.observingStagger = kick.opportunity = false;
                kick.combatTarget = null;
                if (!kick.motionActive) Clear(combat);
            }
        }

        internal static void ClearIdleOpportunity(RimKataPawnCombatState combat)
        {
            if (combat?.kick != null && CloseTarget(combat.pawn, combat) == null)
                ClearOpportunity(combat);
        }

        private static bool ReadyPawn(Pawn pawn)
            => pawn?.Spawned == true && !pawn.Dead && !pawn.Downed && pawn.Awake()
                && !pawn.InMentalState && !pawn.IsBurning() && pawn.kindDef.canMeleeAttack
                && pawn.GetPosture() == PawnPosture.Standing
                && pawn.stances?.stunner?.Stunned != true;

        private static bool OtherMotion(Pawn pawn, RimKataPawnCombatState combat)
            => combat?.groundPose != null || combat?.flyingKick != null || combat?.reactiveMotion != null
                || combat?.VisualActive == true || combat?.CloseDodgeActive == true
                || combat?.DodgeMovementActive == true || combat?.DeflectionSpinActive == true
                || RimKataBreachUtility.Get(pawn) != null || RimKataSubdueUtility.IsHolding(pawn);

        internal static void StaggerEnded(Entry entry)
        {
            if (Tracked.TryGetValue(entry.pawn, out var combat)) StaggerEnded(combat);
        }

        internal static void StaggerEnded(RimKataPawnCombatState combat)
        {
            var kick = combat?.kick;
            if (kick == null || !kick.observingStagger) return;
            kick.observingStagger = false;
            kick.opportunity = Find.TickManager.TicksGame >= kick.cooldownUntil
                && CloseTarget(combat.pawn, combat) != null;
            if (!kick.opportunity) ClearOpportunity(combat);
        }

        internal static void AttackStarting(RimKataNativeAttack request)
        {
            if (request?.state?.kick == null) return;
            TryStart(request.state, request.firedTarget);
        }

        internal static void NativeMeleeStarting(Entry entry, Verb_MeleeAttack verb)
        {
            if (!Tracked.TryGetValue(entry.pawn, out var combat)) return;
            combat.kick.combatTarget = verb.CurrentTarget.Thing;
            TryStart(combat, verb.CurrentTarget.Thing);
        }

        private static void TryStart(RimKataPawnCombatState combat, Thing target)
        {
            var kick = combat.kick;
            int now = Find.TickManager.TicksGame;
            if (kick == null || kick.motionActive || now < kick.cooldownUntil) return;
            if (!kick.opportunity) return;
            Pawn pawn = combat.pawn;
            if (OtherMotion(pawn, combat)) return;
            if (CloseTarget(pawn, combat) == null)
            {
                ClearOpportunity(combat);
                return;
            }
            var settings = RimKataTargetAccess.SettingsFor(pawn);
            if (settings?.kickEnabled != true || !ReadyPawn(pawn)
                || pawn.stances.stagger.Staggered || !InTouch(pawn, target)) return;
            Rot4 facing = pawn.Rotation;
            Rot4 strikeFacing = facing == Rot4.South ? Rand.Bool ? Rot4.East : Rot4.West : facing;
            if (!RimKataKickRender.TryInitialize(pawn, target, facing, strikeFacing, kick.frame, out float angle)) return;
            kick.opportunity = false;
            if (!Rand.Chance(settings.KickChance)) return;
            kick.target = target;
            kick.jobId = pawn.CurJob?.loadID ?? -1;
            kick.age = 0;
            kick.lastTick = now;
            kick.targetAngle = angle;
            kick.damageMultiplier = settings.GetKickDamageMultiplier(pawn);
            kick.facing = facing;
            kick.strikeFacing = strikeFacing;
            kick.motionActive = true;
            kick.attackResolved = false;
            kick.cooldownUntil = now + settings.GetKickCooldownTicks(pawn);
            combat.ownerComponent?.RememberKickCooldown(pawn, kick.cooldownUntil);
            kick.visual = new RimKataKickVisual { facing = facing, strikeFacing = strikeFacing, frame = kick.frame };
            RimKataKickRender.Publish(pawn, kick.visual);
        }

        private static bool LiveTarget(Pawn pawn, Thing target)
            => target?.Spawned == true && !target.Destroyed && target.Map == pawn.Map
                && (!(target is Pawn victim) || RimKataTargeting.IsPawnTargetStateValid(victim));

        internal static float AngleAt(int age, float targetAngle)
            => targetAngle * Mathf.Clamp01((age <= TurnTicks ? age : Duration - age) / (float)TurnTicks);

        internal static void Tick(RimKataPawnCombatState combat)
        {
            var kick = combat.kick;
            int now = Find.TickManager.TicksGame;
            if (kick == null || kick.lastTick == now) return;
            kick.lastTick = now;
            Pawn pawn = combat.pawn;
            if (kick.motionActive)
            {
                if (pawn?.Spawned != true || pawn.Dead)
                    EndMotion(combat);
                else
                {
                    if (!kick.frame.ready)
                        RimKataKickRender.TryInitialize(pawn, null, kick.facing,
                            RimKataKickRender.RenderFacing(kick.visual), kick.frame, out _);
                    kick.age++;
                    kick.visual.angle = AngleAt(kick.age, kick.targetAngle);
                    RimKataKickRender.Publish(pawn, kick.visual);
                    if (kick.age >= TurnTicks && !kick.attackResolved)
                    {
                        if (!kick.attack.Pending)
                        {
                            if (pawn.CurJob?.loadID != kick.jobId || !LiveTarget(pawn, kick.target)
                                || !pawn.CanReachImmediate(kick.target, PathEndMode.Touch)
                                || !kick.attack.Queue(pawn, kick.target, pawn.CurJob, kick.damageMultiplier))
                                kick.attackResolved = true;
                        }
                        if (kick.attack.Pending) kick.attack.Tick();
                        if (combat.kick != kick) return;
                        if (kick.attack.Completed) kick.attackResolved = true;
                    }
                    if (kick.age >= Duration) EndMotion(combat);
                }
            }
            if (kick.observingStagger || kick.opportunity) ClearIdleOpportunity(combat);
            if (!kick.motionActive && !kick.observingStagger && !kick.opportunity) Clear(combat);
        }

        private static void EndMotion(RimKataPawnCombatState combat)
        {
            var kick = combat.kick;
            kick.attack.Cancel();
            kick.motionActive = false;
            kick.target = null;
            RimKataKickRender.Remove(combat.pawn, kick.frame);
        }

        internal static void Clear(RimKataPawnCombatState combat)
        {
            if (combat?.kick == null) return;
            combat.ownerComponent?.RememberKickCooldown(combat.pawn, combat.kick.cooldownUntil);
            if (combat.kick.motionActive) EndMotion(combat);
            combat.kick = null;
            Tracked.Remove(combat.pawn);
        }

        internal static void QualificationLost(Pawn pawn)
        {
            if (pawn != null && Tracked.TryGetValue(pawn, out var combat)) Clear(combat);
        }

        internal static void ResetGame() => Tracked.Clear();

        internal static void Rebuild(RimKataPawnCombatState combat)
        {
            var kick = combat.kick;
            if (kick == null) return;
            combat.ownerComponent?.RememberKickCooldown(combat.pawn, kick.cooldownUntil);
            Tracked[combat.pawn] = combat;
            if (kick.observingStagger && combat.pawn.stances?.stagger?.Staggered == false)
                StaggerEnded(combat);
            if (!kick.motionActive)
            {
                if ((!kick.observingStagger && !kick.opportunity) || CloseTarget(combat.pawn, combat) == null)
                    Clear(combat);
                return;
            }
            kick.visual.frame = kick.frame;
            RimKataKickRender.Publish(combat.pawn, kick.visual);
        }
    }

    [HarmonyPatch(typeof(StaggerHandler), nameof(StaggerHandler.StaggerFor))]
    internal static class Patch_Stagger_RimKataKick
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            Label original = generator.DefineLabel(), finished = generator.DefineLabel(), done = generator.DefineLabel();
            LocalBuilder result = generator.DeclareLocal(typeof(bool));
            LocalBuilder qualified = generator.DeclareLocal(typeof(bool));
            LocalBuilder wasStaggered = generator.DeclareLocal(typeof(bool));
            var gate = RimKataRegisteredPawnGate.Branch(generator, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(StaggerHandler), nameof(StaggerHandler.parent)))
            }, original, true, out LocalBuilder entry);
            foreach (var code in gate) yield return code;
            yield return new CodeInstruction(OpCodes.Ldc_I4_1);
            yield return new CodeInstruction(OpCodes.Stloc, qualified);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(StaggerHandler), nameof(StaggerHandler.Staggered)));
            yield return new CodeInstruction(OpCodes.Stloc, wasStaggered);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(original);
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Ret) { code.opcode = OpCodes.Br; code.operand = finished; }
                yield return code;
            }
            yield return new CodeInstruction(OpCodes.Stloc, result).WithLabels(finished);
            yield return new CodeInstruction(OpCodes.Ldloc, result);
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldloc, qualified);
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldloc, entry);
            yield return new CodeInstruction(OpCodes.Ldloc, wasStaggered);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataKick), nameof(RimKataKick.StaggerApplied)));
            yield return new CodeInstruction(OpCodes.Ldloc, result).WithLabels(done);
            yield return new CodeInstruction(OpCodes.Ret);
        }
    }

    [HarmonyPatch(typeof(StaggerHandler), nameof(StaggerHandler.StaggerHandlerTick))]
    internal static class Patch_StaggerEnded_RimKataKick
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            var speed = AccessTools.Field(typeof(StaggerHandler), "staggerMoveSpeedFactor");
            int ended = codes.FindIndex(code => code.opcode == OpCodes.Stfld && Equals(code.operand, speed));
            if (ended < 0)
            {
                Log.Error("[RimKata] Could not locate the stagger expiry branch for kick opportunities.");
                return codes;
            }
            Label done = generator.DefineLabel();
            var gate = RimKataRegisteredPawnGate.Branch(generator, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(StaggerHandler), nameof(StaggerHandler.parent)))
            }, done, true, out LocalBuilder entry);
            gate.Add(new CodeInstruction(OpCodes.Ldloc, entry));
            gate.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataKick),
                nameof(RimKataKick.StaggerEnded), new[] { typeof(Entry) })));
            gate.Add(new CodeInstruction(OpCodes.Nop).WithLabels(done));
            codes.InsertRange(ended + 1, gate);
            return codes;
        }
    }
}
