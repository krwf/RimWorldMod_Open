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
    // One reusable request per weapon cycle. The equipment/body VerbTracker owns
    // execution; the controller only supplies the ready target and consumes the result.
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

        internal Pawn pawn;
        internal RimKataPawnCombatState state;
        internal RimKataWeaponCycleState cycle;
        internal RimKataHuntingSession huntingSession;
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
        private Stance_RimKataAim previousAim;
        private Stance previousHuntingStance;
        // Used only by the optional CE patches; the common firing path never reads it.
        internal int extraAimTicks;

        internal static void Bind(Verb verb)
        {
            if (verb != null)
            {
                VerbBinding binding = bindings.GetOrCreateValue(verb);
                if (binding.request?.Pending == true
                    && !RimKataPreparedWeaponData.IsCurrent(verb))
                {
                    binding.request.Cancel();
                    // A native callback can invalidate settings while the shot is
                    // still reading its properties. Rebind after it unwinds.
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

            // Initialize native cast state once. Nothing executes on this stack,
            // and no original target/flag snapshot has to be restored after firing.
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
            return true;
        }

        internal static bool WaitingForNativeTick(Verb verb)
            => verb != null && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Pending == true && !binding.request.Executing;

        internal static bool CanBeginNativeTick(Verb verb)
        {
            // Registration is event-driven. Ordinary verbs never inspect Pawn state.
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing || request.Started) return false;
            if (!request.CanContinue() || verb.state != VerbState.Idle)
            {
                request.Cancel();
                return false;
            }
            return request.PrepareShot();
        }

        private bool CanContinue()
        {
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
            return true;
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
                // The first shot is already inside its full WarmupComplete scope.
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
            previousHuntingStance = huntingSession != null ? pawn.stances?.curStance : null;
            pawn.rotationTracker.FaceCell(verb.CurrentTarget.Cell);
            movingShot = !verb.IsMeleeAttack && !closeShot && pawn.pather?.MovingNow == true;
            previousContext = RimKataFireContext.Begin(
                verb, pawn, closeShot ? target.Thing : null,
                movingShot, closeShot, interceptionShot,
                interceptionTarget, closeMeleeResolution,
                closeMeleeHit, closeDefensePrecheck);
        }

        internal static bool OwnsActiveMelee(Verb verb)
            => bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Executing == true;

        internal void FinishNativeCast(Exception exception)
        {
            HasFired |= RimKataFireContext.ShotFired;
            RimKataFireContext.End(verb, previousContext);
            Executing = false;
            previousContext = default;
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
            if (pawn.stances?.curStance is Stance_Busy busy && busy.verb == verb)
            {
                if (huntingSession != null)
                {
                    if (pawn.CurJob == job && previousHuntingStance != null)
                        pawn.stances.curStance = previousHuntingStance;
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
            previousAim = null;
            previousHuntingStance = null;
        }

        private void CompleteRequest(bool resetNative)
        {
            Detach();
            // Finish only after the outer native call returns: its effecters and
            // subclass code may still read CurrentTarget and derived verbProps.
            if (resetNative) verb.Reset();
            nonInterruptingSelfCast(verb) = false;
            try
            {
                RimKataDualWeaponController.CompleteNativeAttack(this, HasFired, cancelled);
            }
            finally
            {
                if (cancelled || cycle.boundVerb != verb)
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
            // Damage may reset a cycle while its native attack is still unwinding.
            // Let that cast finish; never reset a live native call from its callback.
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
        }

        private void ReleaseReferences()
        {
            pawn = null;
            state = null;
            cycle = null;
            huntingSession = null;
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
            previousHuntingStance = null;
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

            // The request lookup is the only added work for unregistered verbs.
            // A newly started burst must not advance again on its first tick;
            // continue at vanilla effecter maintenance instead.
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
            // Cast context is transient. Retry unstarted plans after load, but
            // finish partially fired bursts without repeating their earlier shots.
            RimKataNativeAttack.ExposeData(__instance);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.Notify_EquipmentLost))]
    internal static class Patch_VerbEquipmentLost_RimKataNativeAttack
    {
        private static void Prefix(Verb __instance) => RimKataNativeAttack.NotifyEquipmentLost(__instance);
    }
}
