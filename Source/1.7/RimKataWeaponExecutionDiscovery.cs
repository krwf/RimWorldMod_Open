using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Bounded, startup-only analysis. It reads instructions and compiles readers;
    // it never invokes a weapon getter, constructs an instance or fires a test shot.
    internal static class RimKataWeaponExecutionDiscovery
    {
        internal const int MaximumMethods = 500;
        internal const int MaximumStoredMethods = MaximumMethods + 2; // Retained lifecycle roots.
        internal const int MaximumInstructions = 72000;
        internal const int MaximumDepth = 30;
        private static readonly Assembly Game = typeof(Verb).Assembly;
        private static readonly Assembly Ours = typeof(RimKataWeaponExecutionDiscovery).Assembly;
        private static readonly Assembly Engine = typeof(UnityEngine.Vector3).Assembly;
        private static readonly MethodInfo NativeBurst = AccessTools.Method(typeof(Verb), "TryCastNextBurstShot");
        private static readonly MethodInfo NativeTick = AccessTools.Method(typeof(Verb), nameof(Verb.VerbTick));
        private static readonly MethodInfo NativeWarmup = AccessTools.Method(typeof(Verb), nameof(Verb.WarmupComplete));
        private static readonly MethodInfo NativeReset = AccessTools.Method(typeof(Verb), nameof(Verb.Reset));
        private static readonly FieldInfo State = AccessTools.Field(typeof(Verb), "state");
        private static readonly FieldInfo ShotsLeft = AccessTools.Field(typeof(Verb), "burstShotsLeft");
        private static readonly FieldInfo NextShot = AccessTools.Field(typeof(Verb), "ticksToNextBurstShot");
        private static readonly FieldInfo Completed = AccessTools.Field(typeof(Verb), "castCompleteCallback");

        internal sealed class Result
        {
            // The unpatched implementation is classification evidence, never a substitute
            // for the current path's completion or burst-control contract.
            internal Result Baseline;
            internal bool HasExternalChanges;
            internal bool OriginalBody;
            internal MethodInfo Warmup, Tick, Count, Spacing;
            internal readonly List<MethodInfo> Methods = new List<MethodInfo>();
            internal readonly List<MethodInfo> Bursts = new List<MethodInfo>();
            internal readonly HashSet<MethodInfo> StatReadMethods = new HashSet<MethodInfo>();
            internal readonly HashSet<StatDef> PawnReadStats = new HashSet<StatDef>();
            internal readonly List<RimKataWeaponStatRead> StatReads = new List<RimKataWeaponStatRead>();
            internal bool StatReadsComplete = true;
            internal string StatReadReason;
            internal readonly HashSet<MethodInfo> NativeStatHelpers = new HashSet<MethodInfo>();
            internal int StatInstructions;
            internal bool HasDynamicPawnStatRead, HasAdditionalPawnStats;
            internal readonly Dictionary<MethodInfo, HashSet<int>> PawnArguments = new Dictionary<MethodInfo, HashSet<int>>();
            internal bool Complete = true;
            internal RimKataWeaponCompletionContract CompletionContract;
            internal bool Custom;
            internal bool CanControlBurst;
            internal string Reason;
            internal Type VerbType;
            internal readonly HashSet<MethodInfo> Visited = new HashSet<MethodInfo>();
            internal readonly HashSet<MethodInfo> FiringEntries = new HashSet<MethodInfo>();
            internal readonly HashSet<MethodInfo> CompletingBursts = new HashSet<MethodInfo>();
            internal bool HasNativeTick;
            internal bool HasShot;
            internal bool HasCompletion;
            internal bool HasCountSource;
            internal bool HasReplacingBurstPrefix;
            internal int Instructions;
        }

        internal static Result AnalyzeBaseline(Type type, bool handledByCompatibility)
            => AnalyzeImplementation(type, handledByCompatibility, true);

        internal static Result Analyze(Type type, bool handledByCompatibility, Result baseline = null)
        {
            baseline ??= AnalyzeBaseline(type, handledByCompatibility);
            if (baseline.VerbType != type)
                throw new ArgumentException("The baseline belongs to a different weapon implementation.", nameof(baseline));
            Result result = AnalyzeImplementation(type, handledByCompatibility, false);
            result.Baseline = baseline;
            // Opaque native helpers retain their live wrapper too. Record their hooks
            // without expanding unrelated helper bodies into the firing graph.
            result.HasExternalChanges = HasExternalHooks(baseline.Methods)
                || HasExternalHooks(baseline.StatReadMethods)
                || HasExternalHooks(result.Methods)
                || HasExternalHooks(result.StatReadMethods);
            return result;
        }

        private static Result AnalyzeImplementation(Type type, bool handledByCompatibility, bool originalBody)
        {
            var result = new Result
            {
                OriginalBody = originalBody,
                VerbType = type,
                Warmup = ResolveSlot(type, NativeWarmup),
                Tick = ResolveSlot(type, NativeTick),
                Count = ResolveSlot(type, AccessTools.PropertyGetter(typeof(Verb), "ShotsPerBurst")),
                Spacing = ResolveSlot(type, AccessTools.PropertyGetter(typeof(Verb), "TicksBetweenBurstShots"))
            };
            if (result.Warmup == null || result.Tick == null)
                throw new InvalidOperationException("The weapon has no native warmup/tick entry.");
            if (handledByCompatibility)
            {
                // Existing explicit adapters own their extra aiming, ammo and mode contracts.
                result.Methods.Add(result.Warmup);
                result.Methods.Add(result.Tick);
                if (result.Count != null) result.Methods.Add(result.Count);
                if (result.Spacing != null) result.Methods.Add(result.Spacing);
                result.CanControlBurst = true;
                result.CompletionContract = RimKataWeaponCompletionContract.Compatibility;
                return result;
            }
            MethodInfo shot = ResolveSlot(type, InstanceMethod(typeof(Verb), "TryCastShot"));
            // Mark every entry before traversing: another entry can call it before its root visit.
            result.FiringEntries.UnionWith(new[]
            {
                result.Warmup, result.Tick, NativeBurst, NativeReset, shot, result.Count, result.Spacing
            });
            Visit(result, result.Warmup, 0, true);
            Visit(result, result.Tick, 0, true);
            Visit(result, NativeBurst, 0, true);
            Visit(result, NativeReset, 0, false);
            Visit(result, shot, 0, false);
            Visit(result, result.Count, 0, false);
            Visit(result, result.Spacing, 0, false);
            // Bounded discovery may stop before reaching a lifecycle root. Registration
            // still needs both entry bodies; retaining them does not complete the analysis.
            if (!result.Methods.Contains(result.Warmup)) result.Methods.Add(result.Warmup);
            if (!result.Methods.Contains(result.Tick)) result.Methods.Add(result.Tick);
            result.CanControlBurst = result.HasCountSource && IsReader(result.Count) && IsReader(result.Spacing);
            if (!result.HasNativeTick) Incomplete(result, "A native owner tick could not be established.");
            if (!result.HasShot) Incomplete(result, "The projectile firing leaf could not be established.");
            if (!result.HasCompletion) Incomplete(result, "The native burst completion could not be established.");
            if (result.HasReplacingBurstPrefix && result.Bursts.Count == 0)
            {
                // The skipped native body's getter is not evidence for its unknown replacement.
                result.CanControlBurst = false;
                Incomplete(result, "A burst prefix has no recognized replacement lifecycle.");
            }
            if (!result.CanControlBurst) Incomplete(result, "The burst count/spacing source could not be established.");
            // Graph coverage cannot establish that a future callback exists. Separately
            // establish the lifecycle entered by this warmup, before taking ownership.
            int budget = MaximumInstructions;
            var lifecycle = new Dictionary<MethodInfo, bool>();
            var visiting = new HashSet<MethodInfo>();
            if (EntersLifecycle(result, result.Warmup, false, lifecycle, visiting, ref budget)
                && EntersLifecycle(result, result.Tick, true,
                    new Dictionary<MethodInfo, bool>(), visiting, ref budget))
                result.CompletionContract = RimKataWeaponCompletionContract.NativeLifecycle;
            RimKataStartupDiagnostics.Count(originalBody ? "weapon_execution_baseline_analyzed" : "weapon_execution_analyzed");
            return result;
        }

        private static List<CodeInstruction> ReadInstructions(Result result, MethodInfo method)
            => result.OriginalBody
                ? PatchProcessor.GetOriginalInstructions(method)
                : PatchProcessor.GetCurrentInstructions(method);

        private static bool HasExternalHooks(IEnumerable<MethodInfo> methods)
        {
            foreach (MethodInfo method in methods)
            {
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches == null) continue;
                if (HasExternalHook(patches.Prefixes) || HasExternalHook(patches.Postfixes)
                    || HasExternalHook(patches.Transpilers) || HasExternalHook(patches.Finalizers)
                    || HasExternalHook(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPrefixes"))
                    || HasExternalHook(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPostfixes"))
                    || HasExternalHook(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerFinalizers"))) return true;
            }
            return false;
        }

        private static bool HasExternalHook(IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches)
                if (patch.PatchMethod?.DeclaringType?.Assembly != Ours) return true;
            return false;
        }

        private static MethodInfo InstanceMethod(Type type, string name)
            => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

        private static bool IsReader(MethodInfo method)
            => method != null && !method.IsStatic && method.ReturnType == typeof(int)
                && method.GetParameters().Length == 0 && !method.ContainsGenericParameters
                && typeof(Verb).IsAssignableFrom(method.DeclaringType);

        private static bool IsNativeLifecycle(MethodInfo method)
        {
            if (method == NativeBurst || method == NativeTick || method == NativeWarmup) return true;
            if (!method.IsVirtual) return false;
            MethodInfo slot = method.GetBaseDefinition();
            return slot == NativeWarmup.GetBaseDefinition() || slot == NativeTick.GetBaseDefinition();
        }

        private static void Incomplete(Result result, string reason)
        {
            result.Complete = false;
            if (result.Reason == null) result.Reason = reason;
        }

        private static void Visit(Result result, MethodInfo method, int depth, bool readNativeBody)
        {
            if (method == null || method.DeclaringType?.Assembly == Ours || result.Visited.Contains(method)) return;
            if (depth > MaximumDepth || result.Visited.Count >= MaximumMethods)
            {
                Incomplete(result, (depth > MaximumDepth ? "The firing graph exceeded " + MaximumDepth + " call levels at "
                    : "The firing graph exceeded " + MaximumMethods + " methods at ") + method.DeclaringType?.FullName + "." + method.Name);
                return;
            }
            result.Visited.Add(method);
            result.Methods.Add(method);
            bool gameMethod = method.DeclaringType?.Assembly == Game;
            if (method == NativeTick) result.HasNativeTick = true;
            // Beam graser and incinerator apply damage through the native beam shot;
            // they have the same burst lifecycle without launching a Projectile.
            if (gameMethod && method.Name == "TryCastShot"
                && (typeof(Verb_LaunchProjectile).IsAssignableFrom(method.DeclaringType)
                    || method.DeclaringType == typeof(Verb_ShootBeam)
                    || method.DeclaringType == typeof(Verb_ArcSprayIncinerator))) result.HasShot = true;
            if (gameMethod && method.Name == nameof(Projectile.Launch)
                && typeof(Projectile).IsAssignableFrom(method.DeclaringType)) result.HasShot = true;

            // A native helper whose body stays opaque also retains its complete original wrapper.
            // Its identity remains above for copying/fingerprints; do not expand only its patches.
            // Actual firing entries and inspected bodies still trace every external hook.
            if (!gameMethod || readNativeBody || result.FiringEntries.Contains(method))
                VisitPatches(result, method, depth);
            if (gameMethod && !readNativeBody)
            {
                // Native helpers remain opaque firing calls. Their stat metadata must not
                // turn an unrelated calculation callback into a failed firing lifecycle.
                if (IsNativeStatHelper(method))
                    InspectNativeStatHelper(result, method, 0);
                return;
            }
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
            {
                Incomplete(result, "A firing method has no inspectable body.");
                return;
            }
            if (!gameMethod) result.Custom = true;
            List<CodeInstruction> codes = ReadInstructions(result, method);
            result.Instructions += codes.Count;
            if (result.Instructions > MaximumInstructions)
            {
                Incomplete(result, "The firing graph exceeded the instruction budget.");
                return;
            }
            TryInspectStatReads(result, method, codes);
            bool writesState = false, writesShots = false, writesNext = false, callsShot = false, callback = false;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                if (code.opcode == OpCodes.Calli)
                {
                    Incomplete(result, "An indirect firing call needs registration.");
                    continue;
                }
                if (code.opcode == OpCodes.Stfld)
                {
                    if (Equals(code.operand, State)) writesState = true;
                    if (Equals(code.operand, ShotsLeft))
                    {
                        writesShots = true;
                        MethodInfo count = StoredValueReader(result, codes, i);
                        if (count != null)
                        {
                            if (PreferReader(result.Count, count)) result.Count = count;
                            result.HasCountSource = true;
                        }
                    }
                    if (Equals(code.operand, NextShot))
                    {
                        writesNext = true;
                        MethodInfo spacing = StoredValueReader(result, codes, i);
                        if (spacing != null && PreferReader(result.Spacing, spacing)) result.Spacing = spacing;
                    }
                }
                if ((code.opcode == OpCodes.Ldfld || code.opcode == OpCodes.Ldflda)
                    && Equals(code.operand, Completed)) callback = true;
                if ((code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt)
                    || !(code.operand is MethodInfo called)) continue;
                if (typeof(Verb).IsAssignableFrom(called.DeclaringType) && called.Name == "TryCastShot") callsShot = true;
                MethodInfo destination = code.opcode == OpCodes.Callvirt ? ResolveVirtual(result, called) : called;
                Type owner = destination.DeclaringType;
                if (owner?.Assembly == Ours) continue;
                if (owner != null && typeof(Delegate).IsAssignableFrom(owner))
                {
                    if (!callback) Incomplete(result, "An independent callback needs registration.");
                    continue;
                }
                if (owner != null && ((typeof(MethodBase).IsAssignableFrom(owner) && destination.Name == "Invoke")
                    || (typeof(Type).IsAssignableFrom(owner) && destination.Name == "InvokeMember")))
                {
                    Incomplete(result, "A reflected firing call needs registration.");
                    continue;
                }
                if (owner?.Assembly == Game)
                {
                    bool lifecycle = IsNativeLifecycle(destination);
                    Visit(result, destination, depth + 1, lifecycle);
                }
                else if (IsExternal(owner)) Visit(result, destination, depth + 1, false);
            }
            if (method == NativeBurst && writesState && callback) result.HasCompletion = true;
            if (!gameMethod && writesState && writesShots && writesNext && callsShot
                && !method.IsStatic && method.ReturnType == typeof(void)
                && method.GetParameters().Length == 0 && typeof(Verb).IsAssignableFrom(method.DeclaringType))
            {
                result.Bursts.Add(method);
                if (callback)
                {
                    result.HasCompletion = true;
                    result.CompletingBursts.Add(method);
                }
                else Incomplete(result, "A replacement burst has no native completion callback.");
            }
        }

        // Inspect only the existing firing graph and these small native calculation helpers.
        // StatWorker/health traversal is prepared separately; never expand the whole game here.
        private static bool IsNativeStatHelper(MethodInfo method)
            => method.DeclaringType == typeof(VerbProperties)
                && (method.Name == nameof(VerbProperties.AdjustedCooldown)
                    || method.Name == nameof(VerbProperties.AdjustedCooldownTicks))
                || method.DeclaringType == typeof(ShotReport)
                    && (method.Name == nameof(ShotReport.HitReportFor)
                        || method.Name == nameof(ShotReport.HitFactorFromShooter));

        private static void IncompleteStats(Result result, string reason)
        { result.StatReadsComplete = false; if (result.StatReadReason == null) result.StatReadReason = reason; }

        private static void InspectNativeStatHelper(Result result, MethodInfo method, int depth)
        {
            if (!result.NativeStatHelpers.Add(method)) return;
            if (depth > MaximumDepth || result.NativeStatHelpers.Count > MaximumMethods)
            { IncompleteStats(result, "The pawn stat helper graph exceeded its preparation limit."); return; }
            result.StatReadMethods.Add(method);
            try
            {
                List<CodeInstruction> codes = ReadInstructions(result, method);
                result.StatInstructions += codes.Count;
                if (result.StatInstructions > MaximumInstructions)
                { IncompleteStats(result, "The pawn stat helper graph exceeded its instruction limit."); return; }
                TryInspectStatReads(result, method, codes);
                foreach (CodeInstruction code in codes)
                    if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                        && code.operand is MethodInfo helper && IsNativeStatHelper(helper))
                        InspectNativeStatHelper(result, helper, depth + 1);
            }
            catch (Exception exception) { IncompleteStats(result, exception.GetType().Name + ": " + exception.Message); }
        }

        private static void TryInspectStatReads(Result result, MethodInfo method, List<CodeInstruction> codes)
        {
            try { InspectStatReads(result, method, codes); }
            catch (Exception exception) { IncompleteStats(result, exception.GetType().Name + ": " + exception.Message); }
        }

        private readonly struct StatInput
        {
            internal readonly bool Pawn, Verb, Request, Caster;
            internal readonly StatDef Stat;
            internal StatInput(bool pawn = false, bool verb = false, bool request = false, StatDef stat = null, bool caster = false)
            { Pawn = pawn; Verb = verb; Request = request; Stat = stat; Caster = caster; }
        }

        private static void InspectStatReads(Result result, MethodInfo method, List<CodeInstruction> codes)
        {
            var stack = new List<StatInput>();
            var locals = new Dictionary<int, StatInput>();
            bool argumentsOverwritten = false;
            StatInput Pop()
            {
                if (stack.Count == 0) return default;
                int last = stack.Count - 1;
                StatInput value = stack[last]; stack.RemoveAt(last); return value;
            }
            foreach (CodeInstruction code in codes)
            {
                // A merge needs full control-flow analysis. Keep its call site but do not
                // turn an uncertain receiver/value into a confirmed pawn stat or grip rule.
                if (code.labels.Count != 0 || code.blocks.Count != 0) { stack.Clear(); locals.Clear(); }
                if (TryLocal(code, true, out int local)) { locals[local] = Pop(); continue; }
                if (TryLocal(code, false, out local))
                { stack.Add(locals.TryGetValue(local, out var value) ? value : default); continue; }
                if (code.opcode == OpCodes.Dup)
                { StatInput value = Pop(); stack.Add(value); stack.Add(value); continue; }
                if (code.opcode == OpCodes.Starg || code.opcode == OpCodes.Starg_S)
                { Pop(); argumentsOverwritten = true; continue; }
                if (code.opcode == OpCodes.Castclass || code.opcode == OpCodes.Isinst)
                {
                    StatInput value = Pop();
                    stack.Add(value.Caster && code.operand is Type cast && typeof(Pawn).IsAssignableFrom(cast)
                        ? new StatInput(pawn: true) : value);
                    continue;
                }
                if (code.opcode == OpCodes.Ldarg_0 && !method.IsStatic)
                { stack.Add(new StatInput(verb: typeof(Verb).IsAssignableFrom(method.DeclaringType))); continue; }
                int argument = code.opcode == OpCodes.Ldarg_0 ? 0 : code.opcode == OpCodes.Ldarg_1 ? 1
                    : code.opcode == OpCodes.Ldarg_2 ? 2 : code.opcode == OpCodes.Ldarg_3 ? 3 : -1;
                if (code.opcode == OpCodes.Ldarg || code.opcode == OpCodes.Ldarg_S)
                    argument = code.operand is ParameterInfo parameter
                        ? parameter.Position + (method.IsStatic ? 0 : 1) : Convert.ToInt32(code.operand);
                if (argument >= 0)
                {
                    int parameterIndex = argument - (method.IsStatic ? 0 : 1);
                    stack.Add(new StatInput(pawn: !argumentsOverwritten && result.PawnArguments.TryGetValue(method, out var known)
                        && known.Contains(parameterIndex)));
                    continue;
                }
                if (code.opcode == OpCodes.Ldsfld && code.operand is FieldInfo field)
                {
                    StatDef stat = null;
                    if (field.FieldType == typeof(StatDef))
                        try { stat = field.GetValue(null) as StatDef; } catch { }
                    stack.Add(new StatInput(stat: stat)); continue;
                }
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Newobj)
                    && code.operand is MethodBase called)
                {
                    ParameterInfo[] parameters = called.GetParameters();
                    var args = new StatInput[parameters.Length];
                    for (int i = args.Length - 1; i >= 0; i--) args[i] = Pop();
                    StatInput receiver = called.IsStatic || code.opcode == OpCodes.Newobj ? default : Pop();
                    StatInput value = default;
                    if (called is MethodInfo target)
                    {
                        MethodInfo destination = code.opcode == OpCodes.Callvirt ? ResolveVirtual(result, target) : target;
                        for (int i = 0; i < args.Length; i++)
                            if (args[i].Pawn || args[i].Caster)
                            {
                                if (!result.PawnArguments.TryGetValue(destination, out var known))
                                    result.PawnArguments[destination] = known = new HashSet<int>();
                                known.Add(i);
                            }
                        bool extension = target.DeclaringType == typeof(StatExtension)
                            && target.Name == nameof(StatExtension.GetStatValue) && parameters.Length == 4;
                        bool worker = typeof(StatWorker).IsAssignableFrom(target.DeclaringType)
                            && target.Name == nameof(StatWorker.GetValue) && parameters.Length == 2
                            && parameters[0].ParameterType == typeof(StatRequest);
                        if (extension || worker)
                        {
                            result.StatReadMethods.Add(method);
                            StatDef stat = extension ? args[1].Stat : receiver.Stat;
                            bool pawn = extension ? args[0].Pawn || args[0].Caster : args[0].Request;
                            result.StatReads.Add(new RimKataWeaponStatRead(method, stat, pawn));
                            // Uncertain receivers are guarded against the actual owning pawn
                            // by the prepared reader. Their fixed StatDef is safe to prepare,
                            // but only a proven pawn read can affect the automatic grip default.
                            if (stat != null) result.PawnReadStats.Add(stat);
                            else result.HasDynamicPawnStatRead = true;
                            if (pawn && stat != null && method.DeclaringType?.Assembly != Game
                                && stat != StatDefOf.AimingDelayFactor && stat != StatDefOf.RangedCooldownFactor
                                && stat != StatDefOf.ShootingAccuracyPawn) result.HasAdditionalPawnStats = true;
                        }
                        if (target.Name == "get_CasterPawn" && typeof(Verb).IsAssignableFrom(target.DeclaringType))
                            value = new StatInput(pawn: receiver.Verb);
                        else if (target.Name == "get_Caster" && typeof(Verb).IsAssignableFrom(target.DeclaringType))
                            value = new StatInput(caster: receiver.Verb);
                        else if (target.DeclaringType == typeof(StatRequest) && target.Name == nameof(StatRequest.For)
                            && args.Length != 0) value = new StatInput(request: args[0].Pawn);
                        else if (target.DeclaringType == typeof(StatDef) && target.Name == "get_Worker")
                            value = new StatInput(stat: receiver.Stat);
                        if (target.ReturnType != typeof(void)) stack.Add(value);
                    }
                    else if (code.opcode == OpCodes.Newobj) stack.Add(value);
                    continue;
                }
                int pops = StackPopCount(code.opcode.StackBehaviourPop);
                if (pops < 0) stack.Clear(); else for (int i = 0; i < pops; i++) Pop();
                int pushes = StackPushCount(code.opcode.StackBehaviourPush);
                if (pushes < 0) stack.Clear(); else for (int i = 0; i < pushes; i++) stack.Add(default);
                if (code.opcode.FlowControl == FlowControl.Branch || code.opcode.FlowControl == FlowControl.Cond_Branch
                    || code.opcode.FlowControl == FlowControl.Return || code.opcode.FlowControl == FlowControl.Throw)
                { stack.Clear(); locals.Clear(); }
            }
        }

        private static int StackPopCount(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                case StackBehaviour.Popref_popi_pop1: return 3;
                default: return -1;
            }
        }

        private static int StackPushCount(StackBehaviour behaviour)
            => behaviour == StackBehaviour.Push0 ? 0 : behaviour == StackBehaviour.Push1_push1 ? 2
                : behaviour == StackBehaviour.Varpush ? -1 : 1;

        private static bool EntersLifecycle(Result result, MethodInfo method, bool tick,
            Dictionary<MethodInfo, bool> known, HashSet<MethodInfo> visiting, ref int budget, bool prefix = false)
        {
            if (method == null || visiting.Contains(method)
                || (!prefix && CanSkipLifecycle(result, method, visiting, ref budget))) return false;
            if (method == NativeBurst) return true;
            if (result.CompletingBursts.Contains(method)) return true;
            if (tick && method == NativeTick) return true;
            if (!tick && method.Name == "TryCastShot" && method.ReturnType == typeof(bool)
                && !method.IsStatic && typeof(Verb).IsAssignableFrom(method.DeclaringType))
                return true; // Its observed synchronous result terminates this invocation.
            if (known.TryGetValue(method, out bool found)) return found;
            if ((!prefix && (method.IsStatic || method.ReturnType != typeof(void)
                    || method.GetParameters().Length != 0
                    || !typeof(Verb).IsAssignableFrom(method.DeclaringType)))
                || (prefix && Harmony.GetPatchInfo(method) != null)
                || visiting.Count >= MaximumDepth || !visiting.Add(method)) return false;
            bool established = false;
            try
            {
                MethodBody body = method.GetMethodBody();
                if (body == null || body.ExceptionHandlingClauses.Count != 0) return false;
                List<CodeInstruction> codes = ReadInstructions(result, method);
                budget -= codes.Count;
                if (budget < 0 || codes.Count == 0) return false;
                var labels = new Dictionary<Label, int>();
                for (int i = 0; i < codes.Count; i++)
                    foreach (Label label in codes[i].labels) labels[label] = i;
                var pending = new Stack<int>();
                var visited = new HashSet<int>();
                pending.Push(0);
                bool reached = false;
                while (pending.Count != 0)
                {
                    int index = pending.Pop();
                    if (index >= codes.Count) return false;
                    if (!visited.Add(index)) continue;
                    CodeInstruction code = codes[index];
                    if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                        && code.operand is MethodInfo called)
                    {
                        MethodInfo destination = code.opcode == OpCodes.Callvirt
                            ? ResolveVirtual(result, called) : called;
                        if (EntersLifecycle(result, destination, tick, known, visiting, ref budget))
                        {
                            reached = true;
                            continue;
                        }
                    }
                    if (code.opcode == OpCodes.Ret)
                    {
                        int value = PreviousValue(codes, index);
                        if (!prefix || value < 0 || codes[value].opcode != OpCodes.Ldc_I4_1) return false;
                        reached = true;
                        continue;
                    }
                    if (code.opcode == OpCodes.Jmp) return false;
                    if (code.opcode.FlowControl == FlowControl.Throw) continue;
                    if (code.opcode.FlowControl == FlowControl.Branch
                        || code.opcode.FlowControl == FlowControl.Cond_Branch)
                    {
                        if (code.operand is Label branch)
                        {
                            if (!labels.TryGetValue(branch, out int target)) return false;
                            pending.Push(target);
                        }
                        else if (code.operand is Label[] branches)
                        {
                            foreach (Label branchTarget in branches)
                            {
                                if (!labels.TryGetValue(branchTarget, out int target)) return false;
                                pending.Push(target);
                            }
                        }
                        else return false;
                        if (code.opcode.FlowControl == FlowControl.Branch) continue;
                    }
                    pending.Push(index + 1);
                }
                established = reached;
                return established;
            }
            finally
            {
                visiting.Remove(method);
                known[method] = established;
            }
        }

        private static bool CanSkipLifecycle(Result result, MethodInfo method,
            HashSet<MethodInfo> visiting, ref int budget)
        {
            if (result.OriginalBody) return false;
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return false;
            visiting.Add(method);
            try
            {
                foreach (Patch patch in RuntimePrefixes(patches))
                {
                    MethodInfo prefix = patch.PatchMethod;
                    if (prefix?.ReturnType != typeof(bool) || prefix.DeclaringType?.Assembly == Ours
                        || ExcludesType(prefix, result.VerbType)) continue;
                    // Every prefix path must either run a recognized replacement or return
                    // true to retain the original body. Finding one burst call is insufficient.
                    if (!EntersLifecycle(result, prefix, false, new Dictionary<MethodInfo, bool>(),
                            visiting, ref budget, prefix: true)) return true;
                }
                return false;
            }
            finally { visiting.Remove(method); }
        }

        private static MethodInfo StoredValueReader(Result result, List<CodeInstruction> codes, int store)
        {
            int value = PreviousValue(codes, store);
            if (value < 0) return null;
            if (TryLocal(codes[value], false, out int local))
            {
                // Accept a local only when its value has one unambiguous assignment.
                // A nearby int-returning method is not sufficient evidence of data flow.
                int assignment = -1;
                for (int i = 0; i < codes.Count; i++)
                {
                    if (!TryLocal(codes[i], true, out int written) || written != local) continue;
                    if (assignment >= 0 || i >= value) return null;
                    assignment = i;
                }
                value = PreviousValue(codes, assignment);
            }
            if (value < 0) return null;
            CodeInstruction code = codes[value];
            if ((code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt)
                || !(code.operand is MethodInfo method)) return null;
            if (code.opcode == OpCodes.Callvirt) method = ResolveVirtual(result, method);
            return IsReader(method) ? method : null;
        }

        private static int PreviousValue(List<CodeInstruction> codes, int before)
        {
            int index = before - 1;
            while (index >= 0 && codes[index].opcode == OpCodes.Nop) index--;
            return index;
        }

        private static bool TryLocal(CodeInstruction code, bool store, out int local)
        {
            local = -1;
            OpCode op = code.opcode;
            if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) local = 0;
            else if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) local = 1;
            else if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) local = 2;
            else if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) local = 3;
            else if (op == (store ? OpCodes.Stloc : OpCodes.Ldloc)
                || op == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S))
            {
                if (code.operand is LocalBuilder builder) local = builder.LocalIndex;
                else if (code.operand is LocalVariableInfo info) local = info.LocalIndex;
                else if (code.operand is int number) local = number;
                else if (code.operand is byte shortNumber) local = shortNumber;
                else if (code.operand is short word) local = word;
            }
            return local >= 0;
        }

        private static bool PreferReader(MethodInfo current, MethodInfo candidate)
            => current == null || candidate.DeclaringType?.Assembly != Game || current.DeclaringType?.Assembly == Game;

        private static MethodInfo ResolveVirtual(Result result, MethodInfo method)
            => ResolveSlot(result.VerbType, method);

        internal static MethodInfo ResolveSlot(Type verbType, MethodInfo method)
        {
            if (method == null || !method.IsVirtual || !typeof(Verb).IsAssignableFrom(method.DeclaringType)
                || !method.DeclaringType.IsAssignableFrom(verbType)) return method;
            ParameterInfo[] parameters = method.GetParameters();
            var arguments = new Type[parameters.Length];
            for (int i = 0; i < arguments.Length; i++) arguments[i] = parameters[i].ParameterType;
            MethodInfo slot = method.GetBaseDefinition();
            for (Type type = verbType; type != null && typeof(Verb).IsAssignableFrom(type); type = type.BaseType)
            {
                MethodInfo candidate = type.GetMethod(method.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, arguments, null);
                if (candidate != null && candidate.GetBaseDefinition() == slot) return candidate;
            }
            return method;
        }

        private static bool IsExternal(Type type)
        {
            if (type == null) return false;
            Assembly assembly = type.Assembly;
            if (assembly == Game || assembly == Ours || assembly == Engine || assembly == typeof(object).Assembly
                || assembly == typeof(Harmony).Assembly) return false;
            string name = assembly.GetName().Name;
            return !name.StartsWith("System", StringComparison.Ordinal)
                && !name.StartsWith("UnityEngine", StringComparison.Ordinal)
                && !name.StartsWith("Microsoft", StringComparison.Ordinal)
                && !name.StartsWith("Waylith", StringComparison.Ordinal)
                && !name.StartsWith("Brutal", StringComparison.Ordinal)
                && name != "netstandard";
        }

        private static void VisitPatches(Result result, MethodInfo method, int depth)
        {
            if (result.OriginalBody) return;
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return;
            foreach (Patch patch in RuntimePrefixes(patches))
            {
                MethodInfo prefix = patch.PatchMethod;
                if (method == NativeBurst && prefix?.ReturnType == typeof(bool)
                    && prefix.DeclaringType?.Assembly != Ours && !ExcludesType(prefix, result.VerbType))
                    result.HasReplacingBurstPrefix = true;
                VisitPatch(result, patch, depth, true, false);
            }
            foreach (Patch patch in patches.Postfixes) VisitPatch(result, patch, depth, false, false);
            foreach (Patch patch in patches.Finalizers) VisitPatch(result, patch, depth, false, false);
            foreach (Patch patch in RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPostfixes"))
                VisitPatch(result, patch, depth, false, false);
            foreach (Patch patch in RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerFinalizers"))
                VisitPatch(result, patch, depth, false, false);
            // The transformed body is read above. A transpiler's own Enumerable machinery
            // is preparation code, not a runtime firing path.
            foreach (Patch patch in patches.Transpilers) VisitPatch(result, patch, depth, false, true);
        }

        private static void VisitPatch(Result result, Patch patch, int depth, bool prefix, bool transpiler)
        {
            MethodInfo method = patch.PatchMethod;
            if (method?.DeclaringType == null || method.DeclaringType.Assembly == Ours) return;
            if (prefix && ExcludesType(method, result.VerbType)) return;
            result.Custom = true;
            // RouteFingerprint already records this preparation callback and its order.
            // Its emitted runtime body is inspected; the transpiler itself never fires.
            if (transpiler) return;
            Visit(result, method, depth + 1, false);
        }

        private static IEnumerable<Patch> RuntimePrefixes(Patches patches)
        {
            foreach (Patch patch in patches.Prefixes) yield return patch;
            foreach (Patch patch in RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPrefixes")) yield return patch;
        }

        internal static bool ExcludesType(MethodInfo method, Type verbType)
        {
            // Recognize only a provable early guard: instance is SomeVerb; unmatched
            // branch returns true without doing work. Other predicates stay conservative.
            if (method == null || verbType == null || Harmony.GetPatchInfo(method)?.Owners.Count > 0) return false;
            ParameterInfo[] parameters = method.GetParameters();
            if (!method.IsStatic || method.ReturnType != typeof(bool) || parameters.Length == 0
                || parameters[0].Name != "__instance"
                || !typeof(Verb).IsAssignableFrom(parameters[0].ParameterType)) return false;
            List<CodeInstruction> codes = PatchProcessor.GetOriginalInstructions(method);
            if (codes.Exists(code => code.blocks.Count != 0)) return false;
            int start = 0;
            while (start < codes.Count && codes[start].opcode == OpCodes.Nop) start++;
            if (start + 1 >= codes.Count || codes[start].opcode != OpCodes.Ldarg_0
                || codes[start + 1].opcode != OpCodes.Isinst || !(codes[start + 1].operand is Type guard)
                || !typeof(Verb).IsAssignableFrom(guard) || guard.IsAssignableFrom(verbType)) return false;
            int branch = start + 2;
            int local = branch < codes.Count ? LocalIndex(codes[branch], true) : -1;
            if (branch + 1 < codes.Count && local >= 0)
            {
                if (LocalIndex(codes[branch + 1], false) != local) return false;
                branch += 2;
            }
            if (branch >= codes.Count || (codes[branch].opcode != OpCodes.Brfalse && codes[branch].opcode != OpCodes.Brfalse_S)
                || !(codes[branch].operand is Label label)) return false;
            int destination = codes.FindIndex(c => c.labels.Contains(label));
            while (destination >= 0 && destination < codes.Count && codes[destination].opcode == OpCodes.Nop) destination++;
            return destination >= 0 && destination + 1 < codes.Count
                && codes[destination].opcode == OpCodes.Ldc_I4_1 && codes[destination + 1].opcode == OpCodes.Ret;
        }

        private static int LocalIndex(CodeInstruction code, bool store)
        {
            if (code.opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (code.opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (code.opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (code.opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (code.opcode != (store ? OpCodes.Stloc : OpCodes.Ldloc)
                && code.opcode != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            return code.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(code.operand);
        }
    }
}
