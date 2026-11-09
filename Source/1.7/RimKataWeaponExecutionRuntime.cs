using System;
using System.Collections.Generic;
using System.Linq;
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
            internal string executionFailure;
        }
        private sealed class Execution
        {
            internal RimKataWeaponExecutionBundle bundle;
            internal Action<Verb> tick, warmup;
        }
        internal readonly struct RegistrationMetrics
        {
            internal readonly int methods, linkedCalls, originalCalls;
            internal RegistrationMetrics(int methods, int linkedCalls, int originalCalls)
            { this.methods = methods; this.linkedCalls = linkedCalls; this.originalCalls = originalCalls; }
        }
        internal struct TickScope
        {
            internal Verb previous;
            internal Action<Verb> previousBody;
            internal bool previousBodyEntered;
            internal RimKataNativeAttack request;
            internal RimKataWeaponEffectStats.Scope stats;
        }
        private static readonly ConditionalWeakTable<Verb, Binding> Bindings = new ConditionalWeakTable<Verb, Binding>();
        private static readonly ConditionalWeakTable<ThingWithComps, List<Verb>> FailedBindings =
            new ConditionalWeakTable<ThingWithComps, List<Verb>>();
        private static readonly ConditionalWeakTable<RimKataWeaponExecutionPlan, Execution> Executions =
            new ConditionalWeakTable<RimKataWeaponExecutionPlan, Execution>();
        private static readonly HashSet<MethodInfo> Ticks = new HashSet<MethodInfo>();
        private static readonly HashSet<MethodInfo> FailedTicks = new HashSet<MethodInfo>();
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
        [ThreadStatic] private static Verb originalCast;

        internal static void Initialize(Harmony instance)
        {
            harmony = instance;
            RimKataWeaponExecutionPlans.SetCompatibilityHandler(RimKataCombatExtendedFire.IsVerbType);
            RimKataWeaponExecutionPlans.Initialize(instance);
            PrepareConnections(RimKataWeaponExecutionPlans.PendingRegistration.ToArray());
        }

        internal static void Refresh(bool force = false)
        {
            if (harmony == null) return;
            if (!force)
            {
                RefreshAllowedDefinitions();
                return;
            }
            FailedTicks.Clear();
            RimKataWeaponRegistrationFailures.ResetForManualRefresh();
            RimKataEquippedWeaponPlans.Suspend();
            List<RimKataWeaponExecutionPlan> prepared;
            try { prepared = RimKataWeaponExecutionPlans.Refresh(true); }
            finally { RimKataEquippedWeaponPlans.Resume(); }
            PrepareConnections(prepared);
        }

        internal static void RefreshAllowedDefinitions()
        {
            if (harmony == null) return;
            HashSet<ThingDef> changed = RimKataWeaponExecutionPlans.ChangedDefinitions();
            if (changed.Count == 0) return;
            RimKataEquippedWeaponPlans.Suspend(changed);
            List<RimKataWeaponExecutionPlan> prepared;
            try { prepared = RimKataWeaponExecutionPlans.Refresh(); }
            finally { RimKataEquippedWeaponPlans.Resume(); }
            PrepareConnections(prepared);
        }

        private static void PrepareConnections(IEnumerable<RimKataWeaponExecutionPlan> prepared)
        {
            if (RimKataEquipmentMemory.Flush()) RimKataEquipmentMemory.CleanupAnalysisFiles();
            Dialog_RimKataWeaponRegistration.ShowPending(prepared);
            ShowPendingFailures();
        }

        internal static void AcquireDefinition(ThingDef definition)
        {
            if (harmony == null) return;
            List<RimKataWeaponExecutionPlan> recovered = null;
            foreach (var plan in RimKataWeaponExecutionPlans.ForDefinition(definition))
            {
                if (++plan.Residents != 1) continue;
                try
                {
                    if (!RimKataWeaponExecutionPlans.Load(plan))
                    {
                        if (plan.Failed && !plan.HandledByCompatibility)
                        {
                            RimKataWeaponExecutionPlans.LoadOriginalEntries(plan);
                            ConnectOriginalTick(plan);
                        }
                        continue;
                    }
                    if (plan.RegistrationAccepted && !plan.HandledByCompatibility
                        && plan.CompletionContract != RimKataWeaponCompletionContract.Original)
                    {
                        RestoreExecution(plan);
                        continue;
                    }
                    string effectFailure = Connect(plan);
                    if (effectFailure != null) RimKataWeaponEffects.RetainOriginalEffects(plan, effectFailure);
                    if (plan.HandledByCompatibility || plan.Mode == RimKataWeaponExecutionMode.Live
                        || plan.Mode == RimKataWeaponExecutionMode.NeedsRegistration) continue;
                    plan.FiringReady = true;
                    if (effectFailure == null) RimKataWeaponEffects.PrepareWeapon(harmony, plan);
                }
                catch (Exception exception)
                {
                    if (!TryRestoreBaselineExecution(plan, exception, false, out Exception failure)) Fail(plan, failure);
                    else
                    {
                        try
                        {
                            // Release/Load must restore this successful route, not retry the rejected one.
                            RimKataWeaponExecutionPlans.Register(plan);
                            (recovered ??= new List<RimKataWeaponExecutionPlan>()).Add(plan);
                        }
                        catch (Exception storage)
                        {
                            plan.CacheStored = false;
                            Log.Message("[RimKata] The recovered firing route remains available for " + plan.Def?.defName
                                + ", but its cache could not be updated: " + storage.Message);
                        }
                    }
                }
                finally { RimKataWeaponExecutionPlans.ClearPreparationScratch(); }
            }
            if (recovered != null)
            {
                try { SaveRegistrationDecisions(recovered); }
                catch (Exception storage)
                {
                    foreach (var plan in recovered) plan.CacheStored = false;
                    Log.Message("[RimKata] Recovered firing routes remain available, but their cache could not be saved: "
                        + storage.Message);
                }
            }
            ShowPendingFailures();
        }

        private static void ShowPendingFailures()
        {
            // Reporting must not turn an already acquired owner reference into a failed acquisition.
            try { RimKataWeaponRegistrationFailures.ShowPending(); }
            catch (Exception exception)
            {
                Log.Warning("[RimKata] Could not queue weapon registration failures: " + exception.Message);
            }
        }

        internal static void ReleaseDefinition(ThingDef definition)
        {
            foreach (var plan in RimKataWeaponExecutionPlans.ForDefinition(definition))
                if (plan.Residents > 0 && --plan.Residents == 0) ReleasePlan(plan);
        }

        internal static void ResetGame()
        {
            RimKataNativeAttack.ResetOriginalExecutions();
            RimKataDualWeaponController.ResetExecutionFailures();
            foreach (var plan in RimKataWeaponExecutionPlans.Plans)
            {
                plan.Residents = 0;
                ReleasePlan(plan);
            }
        }

        private static void ReleasePlan(RimKataWeaponExecutionPlan plan)
        {
            Executions.Remove(plan);
            try
            {
                bool inUse = false;
                foreach (var other in RimKataWeaponExecutionPlans.ForDefinition(plan.Def))
                    if (other.Residents > 0) { inUse = true; break; }
                if (!inUse) RimKataWeaponEffects.ForgetDefinition(plan.Def);
            }
            catch (Exception exception)
            {
                Log.Warning("[RimKata] Weapon effect cleanup failed for " + plan.Def.defName
                    + "; the successful firing registration is retained. " + exception.Message);
            }
            finally { RimKataWeaponExecutionPlans.Unload(plan); }
        }

        private static void Fail(RimKataWeaponExecutionPlan plan, Exception exception)
        {
            RimKataWeaponExecutionPlans.FailRegistration(plan, exception.GetType().Name + ": " + exception.Message);
            Executions.Remove(plan);
            try { RimKataWeaponEffects.ForgetDefinition(plan.Def); }
            catch (Exception cleanup)
            {
                Log.Warning("[RimKata] Could not finish weapon effect cleanup for " + plan.Def.defName + ": " + cleanup.Message);
            }
            finally { RimKataWeaponExecutionPlans.Unload(plan); }
        }

        private static bool TryRestoreBaselineExecution(RimKataWeaponExecutionPlan plan, Exception cause,
            bool prepareEffects, out Exception failure)
        {
            failure = cause;
            if (plan == null || plan.UsingBaseline || plan.HandledByCompatibility) return false;
            try
            {
                if (!RimKataWeaponExecutionPlans.TryUseBaseline(plan, cause.GetType().Name + ": " + cause.Message)) return false;
                // Preparation has not fired a shot. Runtime failures never enter this retry.
                RestoreExecution(plan, prepareEffects);
                return true;
            }
            catch (Exception exception)
            {
                failure = new AggregateException(cause, exception);
                return false;
            }
        }

        private static string Connect(RimKataWeaponExecutionPlan plan)
        {
            if (plan.CompletionContract == RimKataWeaponCompletionContract.Original)
            {
                // Only the original owner tick receives a one-shot submission opportunity.
                // No copied firing/effect hooks or burst conversion are needed for this route.
                ConnectOriginalTick(plan);
                return null;
            }
            // CE owns firing; its Comp/Hediff state readers still need RimKata's weapon state.
            if (!plan.HandledByCompatibility && plan.Mode != RimKataWeaponExecutionMode.Vanilla)
            {
                if (plan.TickMethod != NativeTick) ConnectTick(plan);
                foreach (MethodInfo burst in plan.BurstMethods)
                    Install(burst, Bursts, nameof(BurstPrefix), nameof(BurstFinalizer));
                if (plan.BurstCountMethod != null && !Counts.Contains(plan.BurstCountMethod))
                {
                    RimKataStartupPatches.Patch(harmony, plan.BurstCountMethod,
                        postfix: new HarmonyMethod(typeof(RimKataWeaponExecutionRuntime), nameof(CountPostfix)));
                    Counts.Add(plan.BurstCountMethod);
                }
                foreach (MethodInfo method in plan.Methods)
                    if (method.Name == "TryCastShot" && !method.IsStatic && method.ReturnType == typeof(bool)
                        && typeof(Verb).IsAssignableFrom(method.DeclaringType) && method.GetParameters().Length == 0)
                        Install(method, Shots, nameof(ShotPrefix), nameof(ShotFinalizer));
            }
            if (!RimKataWeaponStateObservers.TryPrepare(harmony, plan.Def, out string reason))
                return reason;
            return null;
        }

        private static void ConnectTick(RimKataWeaponExecutionPlan plan)
        {
            if (plan.TickMethod == null || Ticks.Contains(plan.TickMethod)) return;
            if (FailedTicks.Contains(plan.TickMethod))
                throw new NotSupportedException("The original tick hook already failed in this session. Use manual refresh to retry.");
            try { Install(plan.TickMethod, Ticks, nameof(TickPrefix), nameof(TickFinalizer), TickDispatcher); }
            catch
            {
                if (plan.TickMethod != null) FailedTicks.Add(plan.TickMethod);
                throw;
            }
        }

        private static void ConnectOriginalTick(RimKataWeaponExecutionPlan plan)
        {
            if (plan.TickMethod == null || plan.TickMethod == NativeTick) return;
            if (FailedTicks.Contains(plan.TickMethod))
            {
                plan.TickMethod = NativeTick;
                return;
            }
            try { ConnectTick(plan); }
            catch (Exception exception)
            {
                // VerbTracker still calls the existing native owner tick. A rejected
                // optional subtype hook must not reject an otherwise original cast.
                plan.TickMethod = NativeTick;
                Log.Message("[RimKata] Original firing for " + plan.Def?.defName
                    + " uses the native owner tick: " + exception.Message);
            }
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

        internal static bool Register(RimKataWeaponExecutionPlan plan, out RegistrationMetrics metrics)
        {
            metrics = default;
            bool registered = false;
            try
            {
                if (!RimKataWeaponExecutionPlans.Load(plan)) return false;
                if (plan.CompletionContract == RimKataWeaponCompletionContract.Original) return false;
                try { RestoreExecution(plan, prepareEffects: true); }
                catch (Exception exception)
                {
                    if (!TryRestoreBaselineExecution(plan, exception, true, out Exception failure)) throw failure;
                }
                RimKataWeaponExecutionPlans.Register(plan);
                if (Executions.TryGetValue(plan, out var execution))
                    metrics = new RegistrationMetrics(execution.bundle.CopiedMethodCount,
                        execution.bundle.LinkedCallCount, execution.bundle.RetainedCallCount);
                registered = true;
            }
            catch (Exception exception)
            {
                if (plan != null) Fail(plan, exception);
            }
            finally
            {
                try
                {
                    if (plan != null && plan.Residents == 0) ReleasePlan(plan);
                }
                catch (Exception cleanup)
                { Log.Warning("[RimKata] Could not release temporary firing preparation for " + plan?.Def?.defName + ": " + cleanup); }
                finally { RimKataWeaponExecutionPlans.ClearPreparationScratch(); }
            }
            return registered && !plan.Failed;
        }

        private static void RestoreExecution(RimKataWeaponExecutionPlan plan, bool prepareEffects = false)
        {
            if (Executions.TryGetValue(plan, out _)) return;
            string effectFailure = Connect(plan);
            int effectHooks = RimKataWeaponEffects.HookRevision;
            Execution execution = CreateExecution(plan);
            plan.Mode = RimKataWeaponExecutionMode.Copied;
            plan.FiringReady = true;
            if (prepareEffects) RimKataWeaponEffectCache.Prepare(plan);
            if (effectFailure == null) RimKataWeaponEffects.PrepareWeapon(harmony, plan);
            else RimKataWeaponEffects.RetainOriginalEffects(plan, effectFailure);
            // Effect hooks must be present in the executable snapshot, not merely registered beside it.
            if (effectHooks != RimKataWeaponEffects.HookRevision
                && plan.EffectStatus != RimKataWeaponEffectStatus.Original
                && (plan.EffectStatus == RimKataWeaponEffectStatus.Connected || plan.StatReadMethods.Length != 0))
            {
                try { execution = CreateExecution(plan); }
                catch (Exception exception)
                {
                    // The first firing snapshot was already valid. Optional scoped
                    // effects failing to link must not discard that executable.
                    RimKataWeaponEffects.RetainOriginalEffects(plan,
                        "Could not connect scoped stats to the prepared firing execution: " + exception.Message);
                }
            }
            Executions.Add(plan, execution);
            plan.AllowConversion = plan.CanControlBurst
                && plan.CompletionContract != RimKataWeaponCompletionContract.Original;
        }

        private static Execution CreateExecution(RimKataWeaponExecutionPlan plan)
        {
            ConnectTick(plan);
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
                RimKataWeaponEffects.ForgetDefinition(plan.Def);
                if (plan.Residents == 0) ReleasePlan(plan);
            }
        }

        internal static void SaveRegistrationDecisions(IReadOnlyList<RimKataWeaponExecutionPlan> plans)
        {
            // Publish the completed decisions together. A staged payload is not yet a saved registration.
            bool flushed = RimKataEquipmentMemory.Flush();
            List<string> unsaved = null;
            foreach (RimKataWeaponExecutionPlan plan in plans)
            {
                if (!RimKataWeaponExecutionPlans.IsCurrent(plan)
                    || (!plan.RegistrationAccepted && !plan.RegistrationDeclined)) continue;
                plan.CacheStored = flushed && RimKataWeaponExecutionPlans.IsRegistrationStored(plan);
                if (!plan.CacheStored)
                {
                    if (unsaved == null) unsaved = new List<string>();
                    unsaved.Add(plan.Def.defName);
                }
            }
            if (unsaved != null)
                Log.Warning("[RimKata] Could not save firing decisions for " + string.Join(", ", unsaved)
                    + ". The prepared execution or original-firing decision remains active for this session. Refresh the equipment data to retry saving.");
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
                binding.executionFailure = null;
                binding.plan = RimKataWeaponExecutionPlans.Get(definition, verb.GetType());
                if (binding.plan == null && definition != null && harmony != null)
                {
                    // A Comp can replace the XML Verb with a runtime-only subclass. Keep
                    // recognized CE verbs on their existing compatibility route; other
                    // unprepared types must retain the original execution without conversion.
                    bool handledByCompatibility = RimKataCombatExtendedFire.IsVerbType(verb.GetType());
                    binding.plan = new RimKataWeaponExecutionPlan
                    {
                        Def = definition, VerbType = verb.GetType(),
                        HandledByCompatibility = handledByCompatibility,
                        Mode = handledByCompatibility ? RimKataWeaponExecutionMode.Vanilla : RimKataWeaponExecutionMode.Live,
                        CompletionContract = handledByCompatibility ? RimKataWeaponCompletionContract.Compatibility
                            : RimKataWeaponCompletionContract.Original,
                        AllowConversion = handledByCompatibility,
                        // VerbTracker.VerbsTick calls the non-virtual Verb.VerbTick for
                        // every owned Verb. This entry is already patched at startup;
                        // a runtime subtype needs neither discovery nor another hook.
                        TickMethod = NativeTick
                    };
                }
            }
            return binding.plan;
        }

        internal static RimKataWeaponExecutionPlan GetPlan(Verb verb)
            => verb != null && Bindings.TryGetValue(verb, out Binding binding) ? binding.plan : null;

        internal static bool CanUseWeaponLocalState(ThingWithComps weapon, Verb verb, out string reason)
        {
            reason = null;
            if (weapon == null || verb == null) return true;
            if (Bindings.TryGetValue(verb, out Binding binding) && binding.executionFailure != null)
            { reason = binding.executionFailure; return false; }
            if (verb.IsMeleeAttack) return true;
            RimKataWeaponExecutionPlan plan = RimKataWeaponExecutionPlans.Get(weapon.def, verb.GetType());
            if (plan == null)
            {
                if (RimKataCombatExtendedFire.IsVerbType(verb.GetType())) return true;
                reason = "The equipped weapon's actual firing type is not registered: " + verb.GetType().FullName;
                return false;
            }
            if (plan.Failed || plan.RegistrationDeclined || (!plan.FiringReady && !plan.HandledByCompatibility))
            { reason = plan.Reason ?? "The weapon's firing execution is not registered or ready."; return false; }
            return true;
        }

        // Runtime failures belong to this Verb instance, never the shared Def plan.
        internal static bool MarkExecutionFailed(Verb verb, Exception exception)
        {
            if (verb == null || verb.IsMeleeAttack) return false;
            Binding binding = Bindings.GetOrCreateValue(verb);
            if (binding.executionFailure != null) return false;
            bool fallbackNew = binding.plan?.CompletionContract != RimKataWeaponCompletionContract.Original;
            ThingWithComps weapon = verb.EquipmentSource;
            binding.definition = verb.EquipmentSource?.def;
            binding.revision = RimKataWeaponExecutionPlans.Revision;
            binding.executionFailure = exception.GetType().Name + ": " + exception.Message;
            binding.plan = new RimKataWeaponExecutionPlan
            {
                Def = binding.definition, VerbType = verb.GetType(), TickMethod = NativeTick,
                Mode = RimKataWeaponExecutionMode.Live,
                CompletionContract = RimKataWeaponCompletionContract.Original,
                AllowConversion = false
            };
            if (weapon != null) FailedBindings.GetOrCreateValue(weapon).Add(verb);
            RimKataPreparedWeaponData.Restore(verb);
            RimKataWeaponEffects.NotifyExecutionFailure(verb.CasterPawn, weapon, exception, verb);
            return fallbackNew;
        }

        internal static void ForgetExecutionFailure(ThingWithComps weapon)
        {
            if (weapon == null || !FailedBindings.TryGetValue(weapon, out var verbs)) return;
            foreach (Verb verb in verbs)
                if (Bindings.TryGetValue(verb, out var binding))
                { binding.executionFailure = null; binding.revision = -1; binding.plan = null; }
            FailedBindings.Remove(weapon);
        }
        internal static bool RequiresOriginalCast(Verb verb)
            => verb != null && !verb.IsMeleeAttack
                && GetPlan(verb)?.CompletionContract == RimKataWeaponCompletionContract.Original;
        internal static bool CanSubmitOriginalCast(Verb verb)
        {
            RimKataWeaponExecutionPlan plan = GetPlan(verb);
            // The non-virtual owner tick remains available even if installing an
            // optional subtype hook failed. Never wait for a hook that was rejected.
            return plan?.CompletionContract == RimKataWeaponCompletionContract.Original;
        }
        internal static bool IsStartingOriginalCast(Verb verb) => verb != null && originalCast == verb;

        internal static bool TryStartOriginalCast(Verb verb, LocalTargetInfo target)
        {
            Verb previous = originalCast;
            originalCast = verb;
            try
            {
                RimKataPreparedWeaponData.Restore(verb);
                return verb.TryStartCastOn(target);
            }
            finally { originalCast = previous; }
        }
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
            using var stats = new RimKataWeaponEffectStats.Scope(verb, verb);
            RimKataNativeAttack request = RimKataNativeAttack.PendingRequest(verb);
            if (request?.OriginalHandoff == true)
            {
                request.HandOffOriginalCast();
                return;
            }
            RimKataWeaponExecutionPlan plan = GetPlan(verb);
            if (UsesCopiedExecution(request) && plan != null && plan.Mode == RimKataWeaponExecutionMode.Copied
                && Executions.TryGetValue(plan, out Execution execution)) execution.warmup(verb);
            else verb.WarmupComplete();
        }

        private static bool UsesCopiedExecution(RimKataNativeAttack request)
            => request?.cycle != null && request.cycle == request.state?.secondaryWeaponCycle;

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
            __state.stats = new RimKataWeaponEffectStats.Scope(__instance, __instance);
            ticking = __instance;
            tickBodyEntered = false;
            tickBody = UsesCopiedExecution(request) && !request.OriginalHandoff && plan.Mode == RimKataWeaponExecutionMode.Copied
                && Executions.TryGetValue(plan, out Execution execution)
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
                if (__exception != null)
                    return RimKataNativeAttack.FinishOwnerTick(request.verb, default, __exception);
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
                __state.stats.Dispose();
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
            return __state != null && !__state.Pending && !__state.Executing ? null : __exception;
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
            return !__state.Pending && !__state.Executing ? null : __exception;
        }
    }
}
