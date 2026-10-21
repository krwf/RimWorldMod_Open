using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
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
        private const int MaximumDepth = 10;
        private static readonly Assembly Game = typeof(Verb).Assembly;
        private static readonly Assembly Ours = typeof(RimKataWeaponExecutionDiscovery).Assembly;
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
            internal MethodInfo Warmup, Tick, Count, Spacing;
            internal readonly List<MethodInfo> Methods = new List<MethodInfo>();
            internal readonly List<MethodInfo> Bursts = new List<MethodInfo>();
            internal bool Complete = true;
            internal bool Custom;
            internal bool CanControlBurst;
            internal string Reason;
            internal Type VerbType;
            internal readonly HashSet<MethodInfo> Visited = new HashSet<MethodInfo>();
            internal readonly HashSet<MethodInfo> FiringEntries = new HashSet<MethodInfo>();
            internal bool HasNativeTick;
            internal bool HasShot;
            internal bool HasCompletion;
            internal bool HasCountSource;
            internal bool HasReplacingBurstPrefix;
            internal int Instructions;
        }

        internal static Result Analyze(Type type, bool handledByCompatibility)
        {
            var result = new Result
            {
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
            RimKataStartupDiagnostics.Count("weapon_execution_analyzed");
            return result;
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
                Incomplete(result, "The firing graph exceeded the bounded discovery limit.");
                return;
            }
            result.Visited.Add(method);
            result.Methods.Add(method);
            bool gameMethod = method.DeclaringType?.Assembly == Game;
            if (method == NativeTick) result.HasNativeTick = true;
            if (gameMethod && method.Name == "TryCastShot"
                && typeof(Verb_LaunchProjectile).IsAssignableFrom(method.DeclaringType)) result.HasShot = true;
            if (gameMethod && method.Name == nameof(Projectile.Launch)
                && typeof(Projectile).IsAssignableFrom(method.DeclaringType)) result.HasShot = true;

            // A native helper whose body stays opaque also retains its complete original wrapper.
            // Its identity remains above for copying/fingerprints; do not expand only its patches.
            // Actual firing entries and inspected bodies still trace every external hook.
            if (!gameMethod || readNativeBody || result.FiringEntries.Contains(method))
                VisitPatches(result, method, depth);
            if (gameMethod && !readNativeBody) return;
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
            {
                Incomplete(result, "A firing method has no inspectable body.");
                return;
            }
            if (!gameMethod) result.Custom = true;
            List<CodeInstruction> codes = PatchProcessor.GetCurrentInstructions(method);
            result.Instructions += codes.Count;
            if (result.Instructions > MaximumInstructions)
            {
                Incomplete(result, "The firing graph exceeded the instruction budget.");
                return;
            }
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
                if (callback) result.HasCompletion = true;
                else Incomplete(result, "A replacement burst has no native completion callback.");
            }
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
            if (assembly == Game || assembly == Ours || assembly == typeof(object).Assembly
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
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return;
            foreach (Patch patch in patches.Prefixes)
            {
                MethodInfo prefix = patch.PatchMethod;
                if (method == NativeBurst && prefix?.ReturnType == typeof(bool)
                    && prefix.DeclaringType?.Assembly != Ours && !ExcludesType(prefix, result.VerbType))
                    result.HasReplacingBurstPrefix = true;
                VisitPatch(result, patch, depth, true, false);
            }
            foreach (Patch patch in patches.Postfixes) VisitPatch(result, patch, depth, false, false);
            foreach (Patch patch in patches.Finalizers) VisitPatch(result, patch, depth, false, false);
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
            if (transpiler)
            {
                if (result.Visited.Add(method)) result.Methods.Add(method);
                return;
            }
            Visit(result, method, depth + 1, false);
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
