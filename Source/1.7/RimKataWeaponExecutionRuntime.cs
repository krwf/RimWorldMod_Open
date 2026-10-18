using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Discovery/installation happens at preparation boundaries. Only an already owned
    // weapon Verb is bound here; no pawn discovery is performed by firing hooks.
    internal static class RimKataWeaponExecutionRuntime
    {
        private sealed class Binding
        {
            internal int revision = -1;
            internal ThingDef definition;
            internal RimKataWeaponExecutionPlan plan;
        }
        private sealed class Execution
        {
            internal RimKataWeaponExecutionBundle bundle;
            internal Action<Verb> tick, warmup;
        }
        internal struct TickScope
        {
            internal Verb previous;
            internal Action<Verb> previousBody;
            internal bool previousBodyEntered;
            internal RimKataNativeAttack request;
        }
        private static readonly ConditionalWeakTable<Verb, Binding> Bindings = new ConditionalWeakTable<Verb, Binding>();
        private static readonly ConditionalWeakTable<RimKataWeaponExecutionPlan, Execution> Executions =
            new ConditionalWeakTable<RimKataWeaponExecutionPlan, Execution>();
        private static readonly HashSet<MethodInfo> Ticks = new HashSet<MethodInfo>();
        private static readonly HashSet<MethodInfo> Bursts = new HashSet<MethodInfo>();
        private static readonly HashSet<MethodInfo> Counts = new HashSet<MethodInfo>();
        private static readonly HashSet<MethodInfo> Shots = new HashSet<MethodInfo>();
        private static readonly MethodInfo NativeTick = AccessTools.Method(typeof(Verb), nameof(Verb.VerbTick));
        private static readonly MethodInfo TickDispatcher = AccessTools.Method(typeof(RimKataWeaponExecutionRuntime), nameof(TickTranspiler));
        private static readonly MethodInfo DispatchTick = AccessTools.Method(typeof(RimKataWeaponExecutionRuntime), nameof(TryRunCopiedTick));
        private static Harmony harmony;
        [ThreadStatic] private static Verb sampling;
        [ThreadStatic] private static Verb ticking;
        [ThreadStatic] private static Verb copiedTick;
        [ThreadStatic] private static Action<Verb> tickBody;
        [ThreadStatic] private static bool tickBodyEntered;

        internal static void Initialize(Harmony instance)
        {
            harmony = instance;
            RimKataWeaponExecutionPlans.SetCompatibilityHandler(RimKataCombatExtendedFire.IsVerbType);
            RimKataWeaponExecutionPlans.Initialize(instance);
            PrepareConnections();
        }

        internal static void Refresh(bool force = false)
        {
            if (harmony == null) return;
            RimKataWeaponExecutionPlans.Refresh(force);
            PrepareConnections(force);
        }

        private static void PrepareConnections(bool force = false)
        {
            var definitions = new HashSet<ThingDef>();
            var registrations = new List<RimKataWeaponExecutionPlan>();
            foreach (RimKataWeaponExecutionPlan plan in RimKataWeaponExecutionPlans.Plans)
            {
                definitions.Add(plan.Def);
                plan.FiringReady = false;
                if (plan.HandledByCompatibility) continue;
                if (plan.Mode == RimKataWeaponExecutionMode.Vanilla)
                {
                    plan.FiringReady = true;
                    continue;
                }
                try
                {
                    if (plan.TickMethod != NativeTick)
                        Install(plan.TickMethod, Ticks, nameof(TickPrefix), nameof(TickFinalizer), TickDispatcher);
                    foreach (MethodInfo burst in plan.BurstMethods)
                        Install(burst, Bursts, nameof(BurstPrefix), nameof(BurstFinalizer));
                    if (plan.BurstCountMethod != null && !Counts.Contains(plan.BurstCountMethod))
                    {
                        RimKataStartupPatches.Patch(harmony, plan.BurstCountMethod,
                            postfix: new HarmonyMethod(typeof(RimKataWeaponExecutionRuntime), nameof(CountPostfix)));
                        Counts.Add(plan.BurstCountMethod);
                    }
                    // A callback may enter TryCastShot without the normal warmup/burst wrapper.
                    foreach (MethodInfo method in plan.Methods)
                        if (method.Name == "TryCastShot" && !method.IsStatic && method.ReturnType == typeof(bool)
                            && typeof(Verb).IsAssignableFrom(method.DeclaringType) && method.GetParameters().Length == 0)
                            Install(method, Shots, nameof(ShotPrefix), nameof(ShotFinalizer));
                    if (plan.Mode == RimKataWeaponExecutionMode.Traced) plan.FiringReady = true;
                    if (plan.RegistrationAccepted) registrations.Add(plan);
                }
                catch (Exception exception)
                {
                    RimKataWeaponExecutionPlans.FailRegistration(plan, exception.Message);
                    Log.Warning("[RimKata] Could not connect the firing path for " + plan.Def.defName
                        + ". Original weapon behavior will be used. " + exception.Message);
                }
            }
            RimKataWeaponStateObservers.Prepare(harmony, definitions);
            var copies = new Dictionary<RimKataWeaponExecutionPlan, Execution>();
            // A restored approval still needs a usable session-local firing bundle before effects.
            foreach (var plan in registrations)
            {
                try
                {
                    copies.Add(plan, CreateExecution(plan));
                    plan.FiringReady = true;
                }
                catch (Exception exception)
                {
                    RimKataWeaponExecutionPlans.FailRegistration(plan, exception.Message);
                    Log.Warning("[RimKata] Could not register firing execution for " + plan.Def.defName
                        + ". Original weapon behavior will be used. " + exception.Message);
                }
            }
            RimKataWeaponEffects.Prepare(harmony, force);
            // Only connected effects need recapturing; do not analyze all weapons again.
            foreach (var pair in copies) Register(pair.Key, pair.Value, false);
            RimKataEquipmentMemory.Flush();
            Dialog_RimKataWeaponRegistration.ShowPending();
        }

        private static void Install(MethodInfo method, HashSet<MethodInfo> installed, string prefix, string finalizer,
            MethodInfo transpiler = null)
        {
            if (method == null || installed.Contains(method)) return;
            RimKataStartupPatches.Patch(harmony, method,
                prefix: new HarmonyMethod(typeof(RimKataWeaponExecutionRuntime), prefix) { priority = Priority.First },
                transpiler: transpiler == null ? null : new HarmonyMethod(transpiler) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(RimKataWeaponExecutionRuntime), finalizer));
            installed.Add(method);
        }

        internal static bool Register(RimKataWeaponExecutionPlan plan)
            => Register(plan, null, true);

        private static bool Register(RimKataWeaponExecutionPlan plan, Execution execution, bool prepareEffects)
        {
            try { return RegisterCore(plan, execution, prepareEffects); }
            catch (Exception exception)
            {
                RimKataWeaponExecutionPlans.FailRegistration(plan, exception.Message);
                if (plan != null)
                {
                    Executions.Remove(plan);
                    RimKataWeaponEffects.PrepareWeapon(harmony, plan);
                }
                RimKataEquipmentMemory.Flush();
                Log.Warning("[RimKata] Could not register firing execution for " + plan?.Def?.defName
                    + ". Original weapon behavior will be used. " + exception.Message);
                return false;
            }
        }

        private static bool RegisterCore(RimKataWeaponExecutionPlan plan, Execution execution, bool prepareEffects)
        {
            if (plan == null) return false;
            if (Executions.TryGetValue(plan, out _)) return true;
            execution ??= CreateExecution(plan);
            plan.Mode = RimKataWeaponExecutionMode.Copied;
            plan.FiringReady = true;
            if (prepareEffects) RimKataWeaponEffects.PrepareWeapon(harmony, plan);
            // Effect hooks must be present in the executable snapshot, not merely registered beside it.
            if (plan.EffectStatus == RimKataWeaponEffectStatus.Connected) execution = CreateExecution(plan);
            Executions.Add(plan, execution);
            RimKataWeaponExecutionPlans.Register(plan);
            RimKataEquipmentMemory.Flush();
            Log.Message("[RimKata] Registered firing execution for " + plan.Def.defName
                + ": methods=" + execution.bundle.CopiedMethodCount + ", linked_calls=" + execution.bundle.LinkedCallCount
                + ", original_calls=" + execution.bundle.RetainedCallCount + ".");
            return true;
        }

        private static Execution CreateExecution(RimKataWeaponExecutionPlan plan)
        {
            Install(plan.TickMethod, Ticks, nameof(TickPrefix), nameof(TickFinalizer), TickDispatcher);
            string reason;
            if (RimKataWeaponExecutionBundle.TryCreate(plan.Methods, out var bundle, out reason, plan.TickMethod, TickDispatcher)
                && bundle.TryGetInvoker(plan.TickMethod, out var tick)
                && bundle.TryGetInvoker(plan.WarmupMethod, out var warmup))
            {
                return new Execution { bundle = bundle, tick = tick, warmup = warmup };
            }
            reason = reason ?? "A callable warmup/tick entry was not produced.";
            throw new NotSupportedException(reason);
        }

        internal static bool CanPrepareEffects(RimKataWeaponExecutionPlan plan)
            => plan?.FiringReady == true && !plan.HandledByCompatibility
                && (plan.Mode == RimKataWeaponExecutionMode.Vanilla
                    || plan.Mode == RimKataWeaponExecutionMode.Traced
                    || plan.Mode == RimKataWeaponExecutionMode.Copied);

        internal static void Decline(RimKataWeaponExecutionPlan plan)
        {
            RimKataWeaponExecutionPlans.Decline(plan);
            if (plan != null)
            {
                Executions.Remove(plan);
                RimKataWeaponEffects.PrepareWeapon(harmony, plan);
            }
            RimKataEquipmentMemory.Flush();
        }

        internal static RimKataWeaponExecutionPlan Bind(Verb verb)
        {
            if (verb == null || verb.IsMeleeAttack) return null;
            Binding binding = Bindings.GetOrCreateValue(verb);
            ThingDef definition = verb.EquipmentSource?.def;
            if (binding.revision != RimKataWeaponExecutionPlans.Revision || binding.definition != definition)
            {
                binding.definition = definition;
                binding.revision = RimKataWeaponExecutionPlans.Revision;
                binding.plan = RimKataWeaponExecutionPlans.Get(definition, verb.GetType());
                if (binding.plan == null && definition != null && harmony != null)
                {
                    // A Comp can replace the XML Verb with a runtime-only subclass. Do not
                    // mistake an unprepared type for a verified vanilla conversion route.
                    binding.plan = new RimKataWeaponExecutionPlan
                    {
                        Def = definition, VerbType = verb.GetType(),
                        Mode = RimKataWeaponExecutionMode.Live, AllowConversion = false
                    };
                }
            }
            return binding.plan;
        }

        internal static RimKataWeaponExecutionPlan GetPlan(Verb verb)
            => verb != null && Bindings.TryGetValue(verb, out Binding binding) ? binding.plan : null;
        internal static bool IsSampling(Verb verb) => sampling == verb;
        internal static bool NeedsTimingRefresh(Verb verb)
        {
            RimKataWeaponExecutionPlan plan = GetPlan(verb);
            return plan?.AllowConversion == true && !plan.HandledByCompatibility
                && plan.Mode != RimKataWeaponExecutionMode.Vanilla;
        }
        internal static int ReadBurstCount(RimKataWeaponExecutionPlan plan, Verb verb)
        {
            Verb previous = sampling;
            sampling = verb;
            try { return Math.Max(1, plan?.BurstCountReader != null ? plan.BurstCountReader(verb) : verb.BurstShotCount); }
            finally { sampling = previous; }
        }
        internal static int ReadBurstSpacing(RimKataWeaponExecutionPlan plan, Verb verb)
        {
            Verb previous = sampling;
            sampling = verb;
            try { return Math.Max(0, plan?.BurstSpacingReader != null ? plan.BurstSpacingReader(verb) : verb.TicksBetweenBurstShots); }
            finally { sampling = previous; }
        }
        internal static float ReadWarmup(RimKataWeaponExecutionPlan plan, Verb verb)
        {
            Verb previous = sampling;
            sampling = verb;
            try { return Math.Max(0f, verb.WarmupTime); }
            finally { sampling = previous; }
        }

        private static void CountPostfix(Verb __instance, ref int __result)
        {
            if (sampling == __instance) return;
            RimKataWeaponExecutionPlan plan = GetPlan(__instance);
            if (plan?.AllowConversion == true && RimKataPreparedWeaponData.TryGetPrepared(__instance, out _))
                __result = 1;
        }

        internal static void WarmupComplete(Verb verb)
        {
            RimKataWeaponExecutionPlan plan = GetPlan(verb);
            if (plan != null && plan.Mode == RimKataWeaponExecutionMode.Copied
                && Executions.TryGetValue(plan, out Execution execution)) execution.warmup(verb);
            else verb.WarmupComplete();
        }

        private static void TickPrefix(Verb __instance, out TickScope __state)
        {
            __state = default;
            if (ticking == __instance || copiedTick == __instance) return;
            RimKataNativeAttack request = RimKataNativeAttack.PendingRequest(__instance);
            RimKataWeaponExecutionPlan plan = request?.executionPlan;
            if (plan == null || plan.HandledByCompatibility || request.Executing
                || (plan.Mode != RimKataWeaponExecutionMode.Copied && plan.TickMethod == NativeTick)) return;
            __state.previous = ticking;
            __state.previousBody = tickBody;
            __state.previousBodyEntered = tickBodyEntered;
            __state.request = request;
            ticking = __instance;
            tickBodyEntered = false;
            tickBody = plan.Mode == RimKataWeaponExecutionMode.Copied && Executions.TryGetValue(plan, out Execution execution)
                ? execution.tick : null;
        }

        private static IEnumerable<CodeInstruction> TickTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            // Dispatch inside the original wrapper, after all of its prefixes have run.
            // Branch to a separate nop so an original leading exception block stays intact.
            Label original = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, DispatchTick);
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ret);
            var continuation = new CodeInstruction(OpCodes.Nop);
            continuation.labels.Add(original);
            yield return continuation;
            foreach (CodeInstruction instruction in instructions) yield return instruction;
        }

        private static bool TryRunCopiedTick(Verb verb)
        {
            if (ticking != verb || copiedTick == verb) return false;
            tickBodyEntered = true;
            if (tickBody == null) return false;
            Verb previous = copiedTick;
            copiedTick = verb;
            try { tickBody(verb); }
            finally { copiedTick = previous; }
            return true;
        }

        private static Exception TickFinalizer(Exception __exception, TickScope __state)
        {
            if (__state.request == null) return __exception;
            try
            {
                RimKataNativeAttack request = __state.request;
                // If an override omits base.VerbTick, start after its own maintenance.
                // Never skip the weapon's original tick body or execute it twice.
                if (__exception == null && tickBodyEntered && request.Pending && !request.Started
                    && request.executionPlan?.TickMethod != NativeTick
                    && RimKataNativeAttack.CanBeginNativeTick(request.verb))
                    WarmupComplete(request.verb);
                request.ReconcileNativeTick(__exception);
            }
            finally
            {
                ticking = __state.previous;
                tickBody = __state.previousBody;
                tickBodyEntered = __state.previousBodyEntered;
            }
            return __exception;
        }
        private static bool BurstPrefix(Verb __instance, out RimKataNativeAttack __state)
            => RimKataNativeAttack.TryBeginBurstShot(__instance, out __state);
        private static Exception BurstFinalizer(Exception __exception, RimKataNativeAttack __state)
        {
            __state?.FinishNativeCast(__exception);
            return __exception;
        }
        private static bool ShotPrefix(Verb __instance, out RimKataNativeAttack __state)
        {
            __state = null;
            RimKataNativeAttack request = RimKataNativeAttack.PendingRequest(__instance);
            if (request?.executionPlan == null || request.executionPlan.HandledByCompatibility) return true;
            request.observedShotAttempted = true;
            return RimKataNativeAttack.TryBeginBurstShot(__instance, out __state);
        }
        private static Exception ShotFinalizer(Verb __instance, bool __result, Exception __exception, RimKataNativeAttack __state)
        {
            if (__state == null) return __exception;
            if (__exception == null && __result) RimKataFireContext.ShotFired = true;
            __state.FinishObservedShot(__exception);
            return __exception;
        }
    }
}
