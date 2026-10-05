using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    [DefOf]
    internal static class RimKataKickDefOf
    {
        public static ManeuverDef RimKata_Kick = null;
        public static ToolCapacityDef RimKata_KickCapacity = null;
        public static RulePackDef RimKata_Kick_Blocked = null;

        static RimKataKickDefOf()
            => DefOfHelper.EnsureInitializedInCtor(typeof(RimKataKickDefOf));
    }

    internal sealed class RimKataKickAttack
    {
        private static Tool humanFist;
        private readonly RimKataNativeAttack request = new RimKataNativeAttack();
        private Verb_RimKataKick verb;
        private Pawn pawn;
        private Thing target;
        private Job job;
        private int queuedTick;

        internal bool Completed { get; private set; }
        internal bool Hit { get; private set; }
        internal bool Cancelled { get; private set; }
        internal bool Pending => request.Pending;
        internal Verb AttackVerb => verb;

        internal bool Queue(Pawn attacker, Thing victim, Job attackJob, float damageMultiplier)
        {
            if (request.Pending || attacker == null || victim == null || !Prepare(attacker))
                return false;
            pawn = attacker;
            target = victim;
            job = attackJob;
            Completed = Hit = Cancelled = false;
            verb.Prepare(humanFist.power * Mathf.Max(0f, damageMultiplier));
            request.pawn = pawn;
            request.kickAttack = this;
            request.verb = verb;
            request.cycleVerb = verb;
            request.job = job;
            request.target = target;
            request.firedTarget = target;
            request.assignedTarget = target;
            request.closeCombatContext = true;
            queuedTick = Find.TickManager.TicksGame;
            if (request.Queue()) return true;
            Cancelled = Completed = true;
            request.ClearCompletedReferences();
            return false;
        }

        private bool Prepare(Pawn attacker)
        {
            if (humanFist == null)
            {
                List<Tool> tools = ThingDefOf.Human.tools;
                ToolCapacityDef blunt = DefDatabase<ToolCapacityDef>.GetNamedSilentFail("Blunt");
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool candidate = tools[i];
                    if (candidate.linkedBodyPartsGroup?.defName == "LeftHand"
                        && candidate.capacities.Contains(blunt))
                    {
                        humanFist = candidate;
                        break;
                    }
                }
                if (humanFist == null)
                {
                    Log.WarningOnce("[RimKata] Kick requires the human fist damage definition.", 195830642);
                    return false;
                }
            }
            if (verb != null && verb.CasterPawn == attacker) return true;
            ManeuverDef maneuver = RimKataKickDefOf.RimKata_Kick;
            ToolCapacityDef capacity = RimKataKickDefOf.RimKata_KickCapacity;
            if (maneuver?.verb == null || capacity == null) return false;
            verb = new Verb_RimKataKick
            {
                caster = attacker,
                verbTracker = new VerbTracker(attacker),
                verbProps = maneuver.verb,
                maneuver = maneuver,
                loadID = "RimKata_Kick_" + attacker.thingIDNumber,
                tool = new Tool
                {
                    id = "RimKata_Kick",
                    label = capacity.label,
                    capacities = new List<ToolCapacityDef> { capacity },
                    cooldownTime = humanFist.cooldownTime,
                    armorPenetration = humanFist.armorPenetration
                }
            };
            return true;
        }

        internal void Tick()
        {
            if (!request.Pending)
            {
                if (!Completed) Completed = Cancelled = true;
                return;
            }
            if (Find.TickManager.TicksGame <= queuedTick) return;
            verb.VerbTick();
            if (!request.Pending && !Completed) Completed = Cancelled = true;
        }

        internal bool CanContinue()
            => !Cancelled && pawn?.Spawned == true && !pawn.Dead && !pawn.Downed
                && !pawn.InMentalState && pawn.CurJob == job
                && !pawn.stances.stunner.Stunned
                && target?.Spawned == true && target.Map == pawn.Map
                && (!(target is Pawn victim) || RimKataTargeting.IsPawnTargetStateValid(victim))
                && pawn.CanReachImmediate(target, PathEndMode.Touch);

        internal void Complete(bool cancelled)
        {
            if (Completed) return;
            Cancelled = cancelled;
            Hit = !cancelled && verb.Hit;
            Completed = true;
            if (Hit && target is Pawn victim)
                RimKataPushKick.Apply(pawn, victim, verb.verbProps.meleeDamageDef);
        }

        internal void Cancel()
        {
            Cancelled = true;
            Hit = false;
            Completed = true;
            request.Cancel();
        }
    }

    public sealed class Verb_RimKataKick : Verb_MeleeAttackDamage
    {
        private bool damageEntered;
        private bool damageAllowed;
        internal bool Hit { get; private set; }
        internal bool Blocked => damageEntered && !damageAllowed;

        internal void Prepare(float power)
        {
            tool.power = power;
            damageEntered = false;
            damageAllowed = true;
            Hit = false;
        }

        protected override bool TryCastShot()
        {
            bool result = base.TryCastShot();
            Hit = result && damageEntered && damageAllowed;
            return Hit;
        }

        protected override DamageWorker.DamageResult ApplyMeleeDamageToTarget(LocalTargetInfo victim)
        {
            damageEntered = true;
            DamageWorker.DamageResult result = base.ApplyMeleeDamageToTarget(victim);
            if (result?.hediffs != null)
            {
                for (int i = 0; i < result.hediffs.Count; i++)
                {
                    if (result.hediffs[i] is not Hediff_Injury injury) continue;
                    injury.sourceLabel = verbProps.meleeDamageDef.label;
                    injury.sourceToolLabel = null;
                    injury.sourceBodyPartGroup = null;
                }
            }
            return result;
        }

        internal void RecordDamageDecision(Pawn victim, DamageInfo damage, bool allowed)
        {
            if (damageEntered && victim == CurrentTarget.Thing
                && damage.Instigator == CasterPawn && damage.Amount > 0f)
                damageAllowed &= allowed;
        }

        internal void RecordResolvedDefense(bool allowed) => damageAllowed &= allowed;

        internal static bool FullBodyBusy(Pawn_StanceTracker tracker, Verb_MeleeAttack verb)
            => verb is not Verb_RimKataKick && Verb_RimKataFlyingKick.FullBodyBusy(tracker, verb);
    }

    [HarmonyPatch(typeof(Verb_MeleeAttack), "TryCastShot")]
    internal static class Patch_MeleeAttempt_RimKataKick
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo caster = AccessTools.PropertyGetter(typeof(Verb), nameof(Verb.CasterPawn));
            MethodInfo nonMiss = AccessTools.Method(typeof(Verb_MeleeAttack), "GetNonMissChance");
            int casterIndex = codes.FindIndex(code => code.Calls(caster));
            int rollIndex = codes.FindIndex(code => code.Calls(nonMiss));
            if (casterIndex < 0 || casterIndex + 1 >= codes.Count || rollIndex < 0)
                throw new InvalidOperationException("RimKata kick melee execution boundary did not match.");
            CodeInstruction store = codes[casterIndex + 1];
            OpCode load = store.opcode == OpCodes.Stloc_0 ? OpCodes.Ldloc_0
                : store.opcode == OpCodes.Stloc_1 ? OpCodes.Ldloc_1
                : store.opcode == OpCodes.Stloc_2 ? OpCodes.Ldloc_2
                : store.opcode == OpCodes.Stloc_3 ? OpCodes.Ldloc_3
                : store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S
                : store.opcode == OpCodes.Stloc ? OpCodes.Ldloc
                : throw new InvalidOperationException("RimKata kick caster local did not match.");
            Label original = generator.DefineLabel();
            var gate = RimKataRegisteredPawnGate.Branch(generator,
                new[] { new CodeInstruction(load, store.operand) }, original, true, out LocalBuilder entry);
            gate[0].MoveLabelsFrom(codes[rollIndex]);
            gate[0].MoveBlocksFrom(codes[rollIndex]);
            gate.Add(new CodeInstruction(OpCodes.Ldloc, entry));
            gate.Add(new CodeInstruction(OpCodes.Ldarg_0));
            gate.Add(CodeInstruction.Call(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.NotifyMeleeAttackStarting)));
            codes[rollIndex].labels.Add(original);
            codes.InsertRange(rollIndex, gate);
            return codes;
        }
    }
}
