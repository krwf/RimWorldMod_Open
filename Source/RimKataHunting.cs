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
    // The Hunt driver still owns movement, its selected gun, execution and hauling.
    // Only its stationary CastVerb toil drives the other gun; ordinary pawns and
    // other jobs never enter this scheduler.
    internal sealed class RimKataHuntingSession
    {
        private readonly JobDriver_Hunt driver;
        private bool casting;
        private RimKataPawnCombatState state;
        private RimKataWeaponCycleState cycle;
        private Verb leadVerb;
        private Verb companionVerb;
        internal Job Job => driver.job;

        internal RimKataHuntingSession(JobDriver_Hunt driver)
        {
            this.driver = driver;
        }

        internal static Verb SelectHuntingVerb(Pawn pawn)
        {
            if (!RimKataEligibility.HasActiveRimKataAccess(pawn)
                || !RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)) return null;
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn);
            Verb first = UsableHuntingVerb(pawn, primary);
            Verb second = UsableHuntingVerb(pawn, secondary);
            // Do not require the prey to be in range before vanilla has chosen
            // a shooting position. Strictly greater leaves ties to the primary.
            if (first == null) return second;
            if (second == null) return first;
            return RimKataRangeUtility.ResolveEffectiveRange(pawn, secondary, second)
                > RimKataRangeUtility.ResolveEffectiveRange(pawn, primary, first) ? second : first;
        }

        internal static Verb HuntingVerb(Pawn pawn, ThingWithComps weapon)
        {
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(pawn, weapon);
            return verb != null && !verb.IsMeleeAttack && verb.HarmsHealth()
                && !verb.verbProps.onlyManualCast && !verb.UsesExplosiveProjectiles()
                && !verb.ApparelPreventsShooting() ? verb : null;
        }

        private static Verb UsableHuntingVerb(Pawn pawn, ThingWithComps weapon)
        {
            Verb verb = HuntingVerb(pawn, weapon);
            return verb?.Available() == true ? verb : null;
        }

        internal void EnterCast()
        {
            casting = true;
            Pawn pawn = driver.pawn;
            if (state?.huntingSession == this && leadVerb == Job.verbToUse
                && cycle?.weapon != null && cycle.boundVerb == companionVerb
                && !state.weaponBindingsDirty
                && state.weaponConfigurationRevision == RimKataEquipmentUtility.WeaponConfigurationRevision)
                return;

            Pause();
            casting = true;
            leadVerb = Job.verbToUse;
            if (pawn.CurJob != Job || pawn.Map == null || leadVerb == null
                || leadVerb.IsMeleeAttack
                || !RimKataEligibility.HasActiveRimKataAccess(pawn)
                || !RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)) return;

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn);
            ThingWithComps companion = leadVerb.EquipmentSource == primary ? secondary
                : leadVerb.EquipmentSource == secondary ? primary : null;
            companionVerb = HuntingVerb(pawn, companion);
            if (companionVerb == null || companionVerb == leadVerb) return;

            state = pawn.Map.GetComponent<RimKataMapComponent>()?.GetState(pawn, true);
            if (state == null) return;
            // Keep recovery already owed by either weapon when entering Hunt.
            ThingWithComps oldPrimary = state.primaryWeaponCycle.weapon;
            ThingWithComps oldSecondary = state.secondaryWeaponCycle.weapon;
            int primaryCooldown = RecoveryBeforeCancel(state.primaryWeaponCycle, pawn);
            int secondaryCooldown = RecoveryBeforeCancel(state.secondaryWeaponCycle, pawn);
            RimKataWeaponCycleState savedCycle = oldPrimary == companion ? state.primaryWeaponCycle
                : oldSecondary == companion ? state.secondaryWeaponCycle : null;
            int savedWarmup = savedCycle?.plannedTarget == Job.targetA.Thing
                && !savedCycle.plannedCloseAttack && !savedCycle.plannedInterception
                ? savedCycle.warmupTicksRemaining : -1;
            int savedWarmupTotal = savedCycle?.warmupTotalTicks ?? 0;
            state.CancelDraftedFire(false);
            state.ClearDraftedMovementSearchTracking();
            state.CancelWeaponCycles();
            RimKataDualWeaponController.BindCurrentWeapons(pawn, state, true);
            RestoreRecovery(state.primaryWeaponCycle, oldPrimary, primaryCooldown, oldSecondary, secondaryCooldown);
            RestoreRecovery(state.secondaryWeaponCycle, oldPrimary, primaryCooldown, oldSecondary, secondaryCooldown);
            cycle = companion == primary ? state.primaryWeaponCycle : state.secondaryWeaponCycle;
            companionVerb = cycle.boundVerb;
            if (savedWarmup >= 0 && cycle.cooldownTicksRemaining <= 0)
            {
                cycle.plannedTarget = cycle.visualTarget = Job.targetA.Thing;
                cycle.plannedActionVerb = companionVerb;
                cycle.warmupTicksRemaining = savedWarmup;
                cycle.warmupTotalTicks = savedWarmupTotal;
            }
            // Binding both slots must not leave prepared companion properties on
            // the gun whose warmup and burst are owned by the native Hunt toil.
            RimKataPreparedWeaponData.Restore(leadVerb);
            state.huntingSession = this;
        }

        private static int RecoveryBeforeCancel(RimKataWeaponCycleState weaponCycle, Pawn pawn)
            => weaponCycle.nativeAttack?.HasFired == true && weaponCycle.NativeAttackPending
                ? Mathf.Max(weaponCycle.cooldownTicksRemaining,
                    RimKataCombatMath.CooldownTicksForSingleShot(weaponCycle.boundVerb, pawn, false))
                : weaponCycle.cooldownTicksRemaining;

        private static void RestoreRecovery(RimKataWeaponCycleState weaponCycle,
            ThingWithComps first, int firstTicks, ThingWithComps second, int secondTicks)
        {
            int saved = weaponCycle.weapon == first ? firstTicks
                : weaponCycle.weapon == second ? secondTicks : 0;
            weaponCycle.cooldownTicksRemaining = Mathf.Max(weaponCycle.cooldownTicksRemaining, saved);
            weaponCycle.rangedCooldown = weaponCycle.cooldownTicksRemaining > 0
                && weaponCycle.boundVerb?.IsMeleeAttack == false;
        }

        // Also checked at each native burst shot, so movement, prey death or a
        // change to another job cannot leave a queued shot running behind it.
        internal bool CanContinue()
        {
            Pawn pawn = driver.pawn;
            Pawn prey = Job.targetA.Pawn;
            return state?.huntingSession == this && pawn.CurJob == Job
                && casting && Job.verbToUse == leadVerb
                && pawn.Spawned && !pawn.Dead && !pawn.Downed && pawn.Awake()
                && !pawn.InMentalState && !pawn.stances.stunner.Stunned
                && !pawn.IsBurning() && pawn.pather?.MovingNow != true
                && !state.DodgeMotionBlocksJob && !state.RangedDodgeDelayActive
                && !cycle.ResponseCooldownAppliedThisTick
                && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                && !state.weaponBindingsDirty
                && cycle.boundVerb == companionVerb && cycle.weapon != null
                && pawn.equipment.AllEquipmentListForReading.Contains(cycle.weapon)
                && prey?.Spawned == true && !prey.Dead && prey.Map == pawn.Map
                // Hunt keeps shooting only dangerous-to-execute downed animals.
                && (!prey.Downed || prey.RaceProps.DeathActionWorker.DangerousInMelee)
                && (!(pawn.stances.curStance is Stance_Busy busy)
                    || busy.verb == leadVerb || busy.verb == companionVerb)
                && companionVerb.CanHitTarget(prey);
        }

        internal void Tick()
        {
            // Recreate only the currently running hunting toil after loading.
            EnterCast();
            if (state?.huntingSession != this) return;
            state.primaryWeaponCycle.TickTimers();
            state.secondaryWeaponCycle.TickTimers();
            if (!CanContinue())
            {
                CancelShot();
                return;
            }
            if (cycle.NativeAttackPending || cycle.cooldownTicksRemaining > 0) return;
            if (!cycle.HasPlan)
            {
                if (!companionVerb.Available() || companionVerb.ApparelPreventsShooting()) return;
                cycle.plannedTarget = Job.targetA.Thing;
                cycle.plannedActionVerb = companionVerb;
                cycle.visualTarget = cycle.plannedTarget;
                cycle.warmupTotalTicks = RimKataCombatMath.WarmupTicksForSingleShot(companionVerb);
                cycle.warmupTicksRemaining = cycle.warmupTotalTicks;
            }
            if (cycle.warmupTicksRemaining > 0 || companionVerb.state != VerbState.Idle) return;

            RimKataNativeAttack attack = cycle.nativeAttack ??= new RimKataNativeAttack();
            attack.pawn = driver.pawn;
            attack.state = state;
            attack.cycle = cycle;
            attack.weapon = cycle.weapon;
            attack.verb = companionVerb;
            attack.cycleVerb = companionVerb;
            attack.job = Job;
            attack.assignedTarget = attack.firedTarget = cycle.plannedTarget;
            attack.target = Job.targetA;
            attack.huntingSession = this;
            attack.playerForced = false;
            attack.killIncappedTarget = false;
            attack.closeCombatContext = false;
            attack.allowAutomaticRangedFire = false;
            attack.randomAttackEnabled = false;
            attack.firedFromVanillaOpening = false;
            attack.movingShot = false;
            attack.closeShot = false;
            attack.interceptionShot = false;
            attack.interceptionTarget = null;
            attack.closeMeleeResolution = false;
            attack.closeMeleeHit = false;
            attack.closeDefensePrecheck = RimKataCloseDefensePrecheck.None;
            // The native owner ticks this verb, including optional CE/Muzzle
            // hooks. A failed queue can start a native reload job; do nothing
            // further to the pawn or hunting state on this stack.
            if (!attack.Queue()) attack.ClearCompletedReferences();
        }

        internal void Complete(RimKataNativeAttack attack, bool acted)
        {
            // A native shot can end Hunt from inside its damage/reload callback.
            // Its own cycle still owes recovery even after Pause detached us.
            RimKataWeaponCycleState firedCycle = attack.cycle;
            if (firedCycle.weapon != attack.weapon) return;
            if (acted)
            {
                firedCycle.StampNativeActionTick();
                firedCycle.cooldownTicksRemaining = Mathf.Max(firedCycle.cooldownTicksRemaining,
                    RimKataCombatMath.CooldownTicksForSingleShot(attack.verb, attack.pawn, false));
                firedCycle.rangedCooldown = true;
                firedCycle.lastFiredTarget = attack.firedTarget;
                if (state?.huntingSession == this)
                {
                    firedCycle.visualTarget = attack.firedTarget;
                    firedCycle.visualAimTicksRemaining = firedCycle.cooldownTicksRemaining;
                }
            }
            if (firedCycle.plannedTarget == attack.firedTarget) firedCycle.ClearPlan();
        }

        private void CancelShot()
        {
            if (cycle == null) return;
            cycle.cooldownTicksRemaining = RecoveryBeforeCancel(cycle, driver.pawn);
            cycle.ClearPlan();
            cycle.visualTarget = null;
            cycle.visualAimTicksRemaining = 0;
        }

        // Keep warmup across successive shots, but stop native execution while
        // vanilla transitions between toils. Do not depend on toil indices:
        // another mod can insert its own hunting steps.
        internal void LeaveCast() => casting = false;

        internal void Pause()
        {
            casting = false;
            if (state?.huntingSession == this)
            {
                CancelShot();
                cycle?.nativeAttack?.ForgetBinding();
                if (cycle?.nativeAttack?.Executing != true)
                    RimKataPreparedWeaponData.Restore(companionVerb);
                state.huntingSession = null;
            }
            state = null;
            cycle = null;
        }
    }

    [HarmonyPatch(typeof(JobDriver_Hunt), "MakeNewToils")]
    internal static class Patch_JobDriver_Hunt_RimKataPair
    {
        private static void Postfix(JobDriver_Hunt __instance, ref IEnumerable<Toil> __result)
        {
            __result = Decorate(__instance, __result);
        }

        private static IEnumerable<Toil> Decorate(JobDriver_Hunt driver, IEnumerable<Toil> source)
        {
            // Admission happens once when the job builds its toils. Single-gun
            // hunters and pawns without slot access receive no per-tick hook.
            Pawn pawn = driver.pawn;
            if (!RimKataEligibility.HasActiveRimKataAccess(pawn)
                || !RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                || RimKataHuntingSession.HuntingVerb(pawn, RimKataWeaponSlotUtility.PrimaryWeapon(pawn)) == null
                || RimKataHuntingSession.HuntingVerb(pawn,
                    RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn)) == null)
            {
                foreach (Toil original in source) yield return original;
                yield break;
            }
            RimKataHuntingSession session = null;
            foreach (Toil toil in source)
            {
                if (toil.debugName == "TrySetJobToUseAttackVerb")
                {
                    Action original = toil.initAction;
                    toil.initAction = () =>
                    {
                        Verb selected = RimKataHuntingSession.SelectHuntingVerb(driver.pawn);
                        if (selected == null) original?.Invoke();
                        else
                        {
                            RimKataPreparedWeaponData.Restore(selected);
                            driver.job.verbToUse = selected;
                        }
                    };
                }
                else if (toil.debugName == "CastVerb")
                {
                    session = new RimKataHuntingSession(driver);
                    RimKataHuntingSession castSession = session;
                    toil.AddPreInitAction(castSession.EnterCast);
                    toil.AddPreTickAction(castSession.Tick);
                    toil.AddFinishAction(castSession.LeaveCast);
                    driver.AddFinishAction(_ => castSession.Pause());
                }
                else if (toil.defaultCompleteMode != ToilCompleteMode.Instant)
                {
                    // Capture the eventual session too: the movement toil occurs
                    // before CastVerb in the iterator, but runs again on pursuit.
                    toil.AddPreInitAction(() => session?.Pause());
                }
                yield return toil;
            }
        }
    }

    [HarmonyPatch(typeof(WorkGiver_HunterHunt), "CanFindHuntingPosition")]
    internal static class Patch_HuntingPosition_RimKataPair
    {
        private static Verb SelectVerb(Pawn pawn, Thing target, bool allowManualCastWeapons, bool allowTurrets)
            => RimKataHuntingSession.SelectHuntingVerb(pawn)
                ?? pawn.TryGetAttackVerb(target, allowManualCastWeapons, allowTurrets);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(Pawn), nameof(Pawn.TryGetAttackVerb));
            var replacement = AccessTools.Method(typeof(Patch_HuntingPosition_RimKataPair), nameof(SelectVerb));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }
}
