using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal sealed class RimKataNativeAttack
    {
        private sealed class VerbBinding
        {
            internal RimKataNativeAttack request;
            internal bool cancelAfterLoad;
            internal bool interruptedBurstAfterLoad;
            internal bool resettingAfterLoad;
        }

        private static readonly ConditionalWeakTable<Verb, VerbBinding> bindings =
            new ConditionalWeakTable<Verb, VerbBinding>();
        private static int reactiveRequestCount;
        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> currentTarget =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");
        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> currentDestination =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentDestination");
        private static readonly AccessTools.FieldRef<Verb, bool> surpriseAttack =
            AccessTools.FieldRefAccess<Verb, bool>("surpriseAttack");
        private static readonly AccessTools.FieldRef<Verb, bool> canHitNonTargetPawns =
            AccessTools.FieldRefAccess<Verb, bool>("canHitNonTargetPawnsNow");
        private static readonly AccessTools.FieldRef<Verb, bool> preventFriendlyFire =
            AccessTools.FieldRefAccess<Verb, bool>("preventFriendlyFire");
        private static readonly AccessTools.FieldRef<Verb, bool> nonInterruptingSelfCast =
            AccessTools.FieldRefAccess<Verb, bool>("nonInterruptingSelfCast");
        private static readonly AccessTools.FieldRef<Verb, int?> cachedBurstShotCount =
            AccessTools.FieldRefAccess<Verb, int?>("cachedBurstShotCount");
        private static readonly AccessTools.FieldRef<Verb, int> burstShotsLeft =
            AccessTools.FieldRefAccess<Verb, int>("burstShotsLeft");

        internal Pawn pawn;
        internal RimKataPawnCombatState state;
        internal RimKataWeaponCycleState cycle;
        internal RimKataHuntingSession huntingSession;
        internal RimKataReactiveMotionState reactiveMotion;
        internal RimKataSubdueState subdueState;
        internal bool reactiveOpeningAttack;
        internal int notBeforeTick;
        internal ThingWithComps weapon;
        internal Verb verb;
        internal Verb cycleVerb;
        internal Job job;
        internal Thing assignedTarget;
        internal Thing firedTarget;
        internal LocalTargetInfo target;
        internal bool playerForced;
        internal bool killIncappedTarget;
        internal bool closeCombatContext;
        internal bool allowAutomaticRangedFire;
        internal bool randomAttackEnabled;
        internal bool firedFromVanillaOpening;
        internal bool movingShot;
        internal bool closeShot;
        internal bool interceptionShot;
        internal bool closeMeleeResolution;
        internal bool closeMeleeHit;
        internal Thing interceptionTarget;
        internal RimKataCloseDefensePrecheck closeDefensePrecheck;
        internal bool Pending { get; private set; }
        internal bool Executing { get; private set; }
        internal bool Started { get; set; }
        internal bool HasFired { get; private set; }
        internal bool BurstActive => Pending && Started && verb?.state == VerbState.Bursting;
        private bool cancelled;
        internal bool Cancelled => cancelled;
        private Verb bindingVerb;
        private VerbBinding nativeBinding;
        private RimKataFireContext.ScopeState previousContext;
        private RimKataSlidingAttackOrigin.Scope previousSlidingOrigin;
        private Stance_RimKataAim previousAim;
        private Stance previousSpecialStance;
        private RimKataSubdueCombat.ExecutionScope previousSubdueExecution;
        private Rot4 previousSubdueRotation;
        internal int extraAimTicks;
        private int? previousBurstShotCount;
        private bool singleShotOverride;

        internal static void Bind(Verb verb)
        {
            if (verb != null)
            {
                VerbBinding binding = bindings.GetOrCreateValue(verb);
                if (binding.request?.Pending == true
                    && !RimKataPreparedWeaponData.IsCurrent(verb))
                {
                    binding.request.Cancel();
                    // A native callback can invalidate settings while the shot still reads its properties.
                    if (binding.request?.Executing == true) return;
                }
                RimKataPreparedWeaponData.Bind(verb);
            }
        }

        internal bool Queue()
        {
            if (Pending || verb == null || verb.state != VerbState.Idle
                || pawn?.Spawned != true || !target.IsValid)
            {
                return false;
            }
            if (bindingVerb != verb)
            {
                Bind(verb);
                bindingVerb = verb;
                nativeBinding = bindings.GetOrCreateValue(verb);
            }
            else if (!RimKataPreparedWeaponData.IsCurrent(verb))
            {
                RimKataPreparedWeaponData.Bind(verb);
            }
            if (nativeBinding.request != null)
            {
                return false;
            }

            verb.Reset();
            currentTarget(verb) = target;
            currentDestination(verb) = LocalTargetInfo.Invalid;
            surpriseAttack(verb) = false;
            canHitNonTargetPawns(verb) = huntingSession == null;
            preventFriendlyFire(verb) = huntingSession != null && job.preventFriendlyFire;
            nonInterruptingSelfCast(verb) = true;
            cancelled = false;
            Started = false;
            HasFired = false;
            Pending = true;
            nativeBinding.request = this;
            if (reactiveMotion != null || subdueState != null) LimitToSingleShot();
            return true;
        }

        internal void BeginReactiveAttack(RimKataReactiveMotionState motion)
        {
            reactiveMotion = motion;
            reactiveOpeningAttack = true;
            HasFired = false;
            LimitToSingleShot();
            if (verb.state == VerbState.Bursting) burstShotsLeft(verb) = 1;
        }

        private void LimitToSingleShot()
        {
            if (singleShotOverride) return;
            previousBurstShotCount = cachedBurstShotCount(verb);
            cachedBurstShotCount(verb) = 1;
            singleShotOverride = true;
            if (reactiveMotion != null) reactiveRequestCount++;
        }

        internal static RimKataNativeAttack ReactiveRequest(Verb verb)
            => reactiveRequestCount != 0 && verb != null
                && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Pending == true && binding.request.reactiveMotion != null
                    ? binding.request : null;

        internal static bool WaitingForNativeTick(Verb verb)
            => verb != null && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Pending == true && !binding.request.Executing;

        internal static bool CanBeginNativeTick(Verb verb)
        {
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing || request.Started) return false;
            if (request.reactiveMotion != null && Find.TickManager.TicksGame < request.notBeforeTick)
                return false;
            if (!request.CanContinue() || verb.state != VerbState.Idle)
            {
                request.Cancel();
                return false;
            }
            return request.PrepareShot();
        }

        private bool CanContinue()
        {
            if (reactiveMotion != null) return RimKataReactiveAttack.CanContinue(this);
            if (subdueState != null) return RimKataSubdueCombat.CanContinueAttack(this);
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || pawn.InMentalState
                || pawn.stances.stunner.Stunned || pawn.CurJob != job
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || cycle.weapon != weapon || cycle.plannedTarget != firedTarget
                || verb.CasterPawn != pawn
                || !RimKataDualWeaponController.NativeAttackStillAllowed(this))
            {
                return false;
            }
            Thing targetThing = target.Thing;
            if (targetThing != null && (!targetThing.Spawned || targetThing.Map != pawn.Map
                || (targetThing is Pawn victim && (victim.Dead
                    || (huntingSession == null && RimKataTargeting.IsIncapacitatedTarget(victim)
                        && !(playerForced && killIncappedTarget))))))
            {
                return false;
            }

            return (!closeShot || pawn.CanReachImmediate(target, PathEndMode.Touch))
                && (!interceptionShot || RimKataTargeting.IsInterceptionTargetActive(interceptionTarget));
        }

        internal bool PrepareShot()
        {
            if (subdueState != null)
            {
                try
                {
                    if (!RimKataSubdueCombat.AttackStarting(this))
                    {
                        Cancel();
                        return false;
                    }
                }
                catch
                {
                    Cancel();
                    throw;
                }
            }
            if (closeShot)
            {
                currentTarget(verb) = target;
                Executing = true;
                try
                {
                    closeMeleeHit = RimKataCombatMath.RollCloseRangedNonMiss(pawn, verb, target);
                    closeDefensePrecheck = RimKataDefenseUtility.PrecheckCloseGunfire(
                        pawn, target.Thing, verb, closeMeleeHit);
                }
                catch
                {
                    Executing = false;
                    Cancel();
                    throw;
                }
                finally { Executing = false; }
                if (closeDefensePrecheck == RimKataCloseDefensePrecheck.ResponseSucceeded)
                {
                    HasFired = true;
                    CompleteRequest(true);
                    return false;
                }
                if (closeDefensePrecheck == RimKataCloseDefensePrecheck.ResponseSucceededWithAccidentalShot)
                    closeMeleeHit = false;
                if (cancelled)
                {
                    CompleteRequest(true);
                    return false;
                }
                if (!closeMeleeHit && !(verb is Verb_LaunchProjectile) && target.Thing != null)
                {
                    IntVec3 cell = RimKataProjectileUtility.FindCloseMissCell(pawn, target.Thing, pawn.Map);
                    if (cell.IsValid) currentTarget(verb) = new LocalTargetInfo(cell);
                }
            }
            RimKataReactiveMotion.AttackStarting(this);
            return Pending && !cancelled;
        }

        internal static RimKataNativeAttack BeginNativeCast(Verb verb)
        {
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing)
            {
                return null;
            }
            request.Started = true;
            request.BeginExecution();
            return request;
        }

        internal static bool TryBeginBurstShot(Verb verb, out RimKataNativeAttack scope)
        {
            scope = null;
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing)
            {
                // The first shot is already inside its WarmupComplete scope.
                return true;
            }

            if (!request.CanContinue())
            {
                request.cancelled = true;
                request.CompleteRequest(true);
                return false;
            }
            if (!request.PrepareShot())
            {
                return false;
            }

            request.Started = true;
            request.BeginExecution();
            scope = request;
            return true;
        }

        private void BeginExecution()
        {
            Executing = true;
            previousAim = pawn.stances?.curStance as Stance_RimKataAim;
            previousSpecialStance = huntingSession != null || reactiveMotion != null || subdueState != null
                ? pawn.stances?.curStance : null;
            if (subdueState != null)
                previousSubdueRotation = pawn.Rotation;
            pawn.rotationTracker.FaceCell(verb.CurrentTarget.Cell);
            movingShot = !verb.IsMeleeAttack && !closeShot && pawn.pather?.MovingNow == true;
            previousContext = RimKataFireContext.Begin(
                verb, pawn, closeShot ? target.Thing : null,
                movingShot, closeShot, interceptionShot,
                interceptionTarget, closeMeleeResolution,
                closeMeleeHit, closeDefensePrecheck);
            previousSlidingOrigin = RimKataSlidingAttackOrigin.Begin(this);
            if (subdueState != null)
                previousSubdueExecution = RimKataSubdueCombat.BeginNativeExecution(subdueState);
        }

        internal static bool OwnsActiveMelee(Verb verb)
            => bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Executing == true;

        internal void FinishNativeCast(Exception exception)
        {
            HasFired |= RimKataFireContext.ShotFired;
            RimKataFireContext.End(verb, previousContext);
            RimKataSlidingAttackOrigin.End(previousSlidingOrigin);
            previousSlidingOrigin = default;
            Executing = false;
            previousContext = default;
            if (subdueState != null)
                RimKataSubdueCombat.EndNativeExecution(previousSubdueExecution);
            previousSubdueExecution = default;
            RestoreAimAfterShot();
            if (exception != null)
            {
                cancelled = true;
            }
            if (cancelled || verb.state != VerbState.Bursting)
            {
                CompleteRequest(cancelled);
            }
        }

        internal void RestoreAimAfterShot()
        {
            bool restoreSubdue = subdueState != null
                && RimKataSubdueUtility.Get(pawn) == subdueState && pawn.CurJob == job;
            if (pawn.stances?.curStance is Stance_Busy busy && busy.verb == verb)
            {
                if (huntingSession != null || reactiveMotion != null || subdueState != null)
                {
                    if (pawn.CurJob == job && previousSpecialStance != null
                        && (subdueState == null || restoreSubdue))
                        pawn.stances.curStance = previousSpecialStance;
                }
                else if (previousAim != null)
                {
                    pawn.stances.curStance = previousAim;
                }
                else
                {
                    RimKataAutomaticCastSuppressionState suppression = RimKataAutomaticCastSuppression.Push(pawn);
                    try { pawn.stances.SetStance(new Stance_Mobile()); }
                    finally { RimKataAutomaticCastSuppression.Pop(suppression); }
                }
            }
            if (restoreSubdue) pawn.Rotation = previousSubdueRotation;
            previousAim = null;
            previousSpecialStance = null;
        }

        private void CompleteRequest(bool resetNative)
        {
            Detach();
            // Native effecters/subclasses may read CurrentTarget and verbProps until the call returns.
            if (resetNative) verb.Reset();
            nonInterruptingSelfCast(verb) = false;
            try
            {
                if (reactiveMotion != null) RimKataReactiveMotion.AttackCompleted(this);
                else if (subdueState != null) RimKataSubdueCombat.AttackCompleted(this);
                else RimKataDualWeaponController.CompleteNativeAttack(this, HasFired, cancelled);
            }
            finally
            {
                if (cancelled || (subdueState != null ? subdueState.weaponVerb != verb : cycle.boundVerb != verb))
                {
                    RimKataPreparedWeaponData.Restore(verb);
                    bindingVerb = null;
                    nativeBinding = null;
                }
                ReleaseReferences();
            }
        }

        internal void Cancel()
        {
            if (!Pending) return;
            cancelled = true;
            // Damage callbacks can cancel the cycle while its native cast is still unwinding.
            if (Executing) return;
            Verb pendingVerb = verb;
            Detach();
            pendingVerb.Reset();
            nonInterruptingSelfCast(pendingVerb) = false;
            ReleaseReferences();
        }

        internal void ForgetBinding()
        {
            Cancel();
            if (Executing) return;
            bindingVerb = null;
            nativeBinding = null;
        }

        internal static void NotifyReset(Verb verb)
        {
            if (!bindings.TryGetValue(verb, out VerbBinding binding)) return;
            if (!binding.resettingAfterLoad)
                binding.interruptedBurstAfterLoad = false;
            if (binding.request is RimKataNativeAttack request)
            {
                request.cancelled = true;
                if (request.Executing) return;
                request.Detach();
                nonInterruptingSelfCast(verb) = false;
                request.ReleaseReferences();
            }
        }

        internal static void NotifyEquipmentLost(Verb verb)
        {
            if (bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request is RimKataNativeAttack request)
            {
                request.Cancel();
                if (request.Executing) return;
            }
            RimKataPreparedWeaponData.Restore(verb);
        }

        internal static void ExposeData(Verb verb)
        {
            bool queued = WaitingForNativeTick(verb);
            bool interruptedBurst = queued
                && bindings.TryGetValue(verb, out VerbBinding current)
                && current.request.Started && current.request.HasFired;
            Scribe_Values.Look(ref queued, "rimKataQueuedCast");
            Scribe_Values.Look(ref interruptedBurst, "rimKataInterruptedNativeBurst");
            if (Scribe.mode == LoadSaveMode.LoadingVars && queued)
            {
                VerbBinding loaded = bindings.GetOrCreateValue(verb);
                loaded.cancelAfterLoad = true;
                loaded.interruptedBurstAfterLoad = interruptedBurst;
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit
                && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.cancelAfterLoad)
            {
                binding.cancelAfterLoad = false;
                binding.resettingAfterLoad = true;
                try { verb.Reset(); }
                finally { binding.resettingAfterLoad = false; }
                nonInterruptingSelfCast(verb) = false;
            }
        }

        internal static bool ConsumeInterruptedBurstAfterLoad(Verb verb)
        {
            if (verb == null || !bindings.TryGetValue(verb, out VerbBinding binding))
            {
                return false;
            }
            bool interrupted = binding.interruptedBurstAfterLoad;
            binding.interruptedBurstAfterLoad = false;
            return interrupted;
        }

        private void Detach()
        {
            if (verb != null && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request == this) binding.request = null;
            Pending = false;
            Executing = false;
            if (singleShotOverride)
            {
                cachedBurstShotCount(verb) = previousBurstShotCount;
                singleShotOverride = false;
                if (reactiveMotion != null) reactiveRequestCount--;
            }
        }

        private void ReleaseReferences()
        {
            if (reactiveMotion != null) RimKataReactiveMotion.ReleaseAttack(this);
            pawn = null;
            state = null;
            cycle = null;
            huntingSession = null;
            reactiveMotion = null;
            subdueState = null;
            reactiveOpeningAttack = false;
            notBeforeTick = 0;
            weapon = null;
            verb = null;
            cycleVerb = null;
            job = null;
            assignedTarget = null;
            firedTarget = null;
            target = LocalTargetInfo.Invalid;
            interceptionTarget = null;
            previousContext = default;
            previousAim = null;
            previousSlidingOrigin = default;
            previousSpecialStance = null;
            previousSubdueExecution = default;
            Started = false;
            HasFired = false;
        }

        internal void ClearCompletedReferences() => ReleaseReferences();
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.VerbTick))]
    internal static class Patch_VerbTick_RimKataNativeAttack
    {
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            MethodInfo warmup = AccessTools.Method(typeof(Verb), nameof(Verb.WarmupComplete));
            MethodInfo ready = AccessTools.Method(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.CanBeginNativeTick));
            Label regular = generator.DefineLabel();
            FieldInfo stateField = AccessTools.Field(typeof(Verb), nameof(Verb.state));
            Label? afterBurst = null;
            for (int i = 0; i + 2 < codes.Count; i++)
            {
                if (codes[i].LoadsField(stateField)
                    && codes[i + 1].opcode == OpCodes.Ldc_I4_1
                    && (codes[i + 2].opcode == OpCodes.Bne_Un
                        || codes[i + 2].opcode == OpCodes.Bne_Un_S)
                    && codes[i + 2].operand is Label target)
                {
                    afterBurst = target;
                    break;
                }
            }
            if (!afterBurst.HasValue)
            {
                Log.Error("[RimKata] Could not locate the native burst tick boundary.");
                foreach (CodeInstruction instruction in codes) yield return instruction;
                yield break;
            }

            // A new burst already fired this tick; resume at native effecter maintenance.
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, ready);
            yield return new CodeInstruction(OpCodes.Brfalse, regular);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Callvirt, warmup);
            yield return new CodeInstruction(OpCodes.Br, afterBurst.Value);
            CodeInstruction originalEntry = new CodeInstruction(OpCodes.Nop);
            originalEntry.labels.Add(regular);
            yield return originalEntry;
            foreach (CodeInstruction instruction in codes) yield return instruction;
        }
    }

    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    internal static class Patch_VerbNextBurstShot_RimKataNativeAttack
    {
        private static bool Prefix(Verb __instance, out RimKataNativeAttack __state)
            => RimKataNativeAttack.TryBeginBurstShot(__instance, out __state);

        private static Exception Finalizer(Exception __exception, RimKataNativeAttack __state)
        {
            __state?.FinishNativeCast(__exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.Reset))]
    internal static class Patch_VerbReset_RimKataNativeAttack
    {
        private static void Prefix(Verb __instance) => RimKataNativeAttack.NotifyReset(__instance);
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.ExposeData))]
    internal static class Patch_VerbExposeData_RimKataNativeAttack
    {
        private static void Postfix(Verb __instance)
        {
            // Cast context is transient; loading must not repeat a partially fired burst.
            RimKataNativeAttack.ExposeData(__instance);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.Notify_EquipmentLost))]
    internal static class Patch_VerbEquipmentLost_RimKataNativeAttack
    {
        private static void Prefix(Verb __instance) => RimKataNativeAttack.NotifyEquipmentLost(__instance);
    }
}
