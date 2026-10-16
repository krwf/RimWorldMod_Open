using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static partial class RimKataWeaponRenderDiscovery
    {
        private const int MaximumDepth = 8;
        private const int MaximumMethodsPerRenderer = 96;
        private const int MaximumInstructionsPerRenderer = 16000;
        private const int MaximumRenderers = 24;

        private static readonly MethodInfo DrawExtras = AccessTools.Method(
            typeof(PawnRenderUtility),
            nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras));
        private static readonly MethodInfo NativeDrawEquipment = AccessTools.Method(
            typeof(PawnRenderUtility),
            nameof(PawnRenderUtility.DrawEquipmentAiming),
            new[] { typeof(Thing), typeof(Vector3), typeof(float) });
        private static readonly MethodInfo NativePrimary = AccessTools.PropertyGetter(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));
        private static readonly MethodInfo NativeDrawWornExtras = AccessTools.Method(
            typeof(Apparel), nameof(Apparel.DrawWornExtras));
        private static readonly MethodInfo ProbeDrawEquipment = AccessTools.Method(
            typeof(RimKataWeaponRenderProbe), nameof(RimKataWeaponRenderProbe.DrawEquipmentAiming));
        private static readonly MethodInfo ProbePrimary = AccessTools.Method(
            typeof(RimKataWeaponRenderProbe), nameof(RimKataWeaponRenderProbe.Primary));
        private static readonly MethodInfo ProbeDrawWornExtras = AccessTools.Method(
            typeof(RimKataWeaponRenderProbe), nameof(RimKataWeaponRenderProbe.DrawWornExtras));
        private static readonly MethodInfo TranspilerMethod = AccessTools.Method(
            typeof(RimKataWeaponRenderDiscovery), nameof(InstrumentRenderer));
        private static readonly MethodInfo NativeSheathMesh = AccessTools.Method(typeof(Graphics),
            nameof(Graphics.DrawMesh), new[] { typeof(Mesh), typeof(Vector3), typeof(Quaternion),
                typeof(Material), typeof(int) });
        private static readonly MethodInfo ProbeSheathMesh = AccessTools.Method(
            typeof(RimKataWeaponDrawCapture), nameof(RimKataWeaponDrawCapture.DrawAccessoryMesh));

        private static IReadOnlyList<Renderer> renderers = Array.Empty<Renderer>();
        private static bool initialized;
        private static Harmony patcher;
        private static readonly HashSet<MethodInfo> instrumented = new HashSet<MethodInfo>();

        internal static bool HasRenderers => renderers.Count != 0;
        internal static IReadOnlyList<Renderer> Renderers => renderers;

        // Discovery runs after external mod constructors register their render patches.
        internal static void Initialize(Harmony harmony)
        {
            if (initialized || harmony == null)
            {
                return;
            }

            initialized = true;
            patcher = harmony;
            var plan = new DiscoveryPlan(RimKataEquipmentMemory.Current);
            using (RimKataStartupDiagnostics.Measure("discovery", "render_analysis"))
                foreach (object unused in plan.Scan()) { }
            Apply(plan.Install());
        }

        private static Candidate CreateSysSheathPostfix(MethodInfo postfix,
            RimKataEquipmentMemory.View memory, string environment)
        {
            Type patchType = postfix.DeclaringType;
            var adapters = AccessTools.Field(patchType, "adaptersByCompType")?.GetValue(null) as IDictionary;
            if (adapters == null || adapters.Count == 0) return null;
            Type state = patchType.GetNestedType("RenderState", BindingFlags.Public | BindingFlags.NonPublic);
            if (state == null) throw new InvalidOperationException("SYS sheath render state was not found.");
            MethodInfo prefix = AccessTools.Method(patchType, "Prefix",
                new[] { typeof(Vector3), state.MakeByRefType() });
            if (prefix == null || postfix.ReturnType != typeof(void)
                || AccessTools.Method(patchType, "Postfix",
                    new[] { typeof(Pawn), typeof(PawnRenderFlags), state }) != postfix)
                throw new InvalidOperationException("Unsupported SYS sheath postfix signature.");

            Candidate candidate = new Candidate(postfix, null) { accessoriesOnly = true };
            var adapterTypes = new List<string>();
            foreach (Type compType in adapters.Keys) adapterTypes.Add(compType.AssemblyQualifiedName);
            adapterTypes.Sort(StringComparer.Ordinal);
            string fingerprint = RimKataEquipmentMemory.Fingerprint(environment + "\n" + string.Join("\n", adapterTypes));
            bool restored = TryRestore(candidate, memory, fingerprint);
            if (!restored)
            {
                candidate.visited.Add(postfix);
                candidate.visited.Add(prefix);
                candidate.methodsToInstrument.Add(postfix);
                foreach (Type compType in adapters.Keys)
                {
                    Type renderer = compType.Assembly.GetType("SYS.DrawEquipment_WeaponBackPatch");
                    MethodInfo draw = AccessTools.Method(renderer, "DrawSheath",
                        new[] { compType, typeof(Pawn), typeof(Vector3), typeof(Graphic) });
                    if (draw == null) throw new InvalidOperationException("SYS sheath renderer was not found.");
                    Candidate helper = new Candidate(draw, null);
                    if (!Visit(helper, draw, 0) || !helper.hasCustomDraw)
                        throw new InvalidOperationException("Unsupported SYS sheath drawing path.");
                    candidate.methodsToInstrument.UnionWith(helper.methodsToInstrument);
                    candidate.visited.UnionWith(helper.visited);
                    candidate.compTypes.Add(compType);
                }
                candidate.hasCustomDraw = true;
            }
            candidate.fingerprint = fingerprint;

            DynamicMethod invoker = new DynamicMethod("RimKataInvokeSysSheathPostfix", typeof(bool),
                new[] { typeof(Pawn), typeof(Vector3), typeof(Rot4), typeof(PawnRenderFlags) },
                typeof(RimKataWeaponRenderDiscovery), true);
            ILGenerator il = invoker.GetILGenerator();
            LocalBuilder capturedState = il.DeclareLocal(state);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca, capturedState);
            il.Emit(OpCodes.Call, prefix);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldloc, capturedState);
            il.Emit(OpCodes.Call, postfix);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            candidate.invoke = (Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool>)invoker.CreateDelegate(
                typeof(Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool>));
            return candidate;
        }

        private static bool Visit(Candidate candidate, MethodInfo method, int depth)
        {
            if (candidate.visited.Contains(method))
            {
                return true;
            }

            if (depth > MaximumDepth
                || candidate.visited.Count >= MaximumMethodsPerRenderer
                || method.ContainsGenericParameters
                || method.IsGenericMethod
                || method.GetMethodBody() == null)
            {
                return false;
            }

            candidate.visited.Add(method);
            List<CodeInstruction> instructions = PatchProcessor.GetCurrentInstructions(method);
            candidate.instructionCount += instructions.Count;
            if (candidate.instructionCount > MaximumInstructionsPerRenderer)
            {
                return false;
            }

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Calli)
                {
                    return false;
                }

                if ((instruction.opcode != OpCodes.Call
                        && instruction.opcode != OpCodes.Callvirt)
                    || !(instruction.operand is MethodInfo called))
                {
                    continue;
                }

                called = OriginalForProbe(called);
                CollectCompType(candidate, called);
                MethodInfo replacement = ReplacementFor(called);
                if (replacement != null)
                {
                    candidate.methodsToInstrument.Add(method);
                    if (called.DeclaringType == typeof(Graphics))
                    {
                        candidate.hasCustomDraw = true;
                    }

                    continue;
                }

                if (IsUnsupportedDrawingLeaf(candidate, called))
                {
                    return false;
                }

                if (called.DeclaringType != null
                    && typeof(Delegate).IsAssignableFrom(called.DeclaringType)
                    && (called.Name == "Invoke" || called.Name == "DynamicInvoke"))
                {
                    return false;
                }

                if (called.DeclaringType != null
                    && ((typeof(MethodBase).IsAssignableFrom(called.DeclaringType)
                            && called.Name == "Invoke")
                        || (typeof(Type).IsAssignableFrom(called.DeclaringType)
                            && called.Name == "InvokeMember")))
                {
                    return false;
                }

                if (called.DeclaringType?.Assembly == candidate.assembly
                    && !Visit(candidate, called, depth + 1))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUnsupportedDrawingLeaf(Candidate candidate, MethodInfo called)
        {
            Type declaringType = called.DeclaringType;
            if (declaringType == typeof(Graphics)
                || declaringType?.FullName == "UnityEngine.GL"
                || declaringType?.FullName == "UnityEngine.Rendering.CommandBuffer")
            {
                return true;
            }

            if (declaringType?.Assembly == candidate.assembly)
            {
                return false;
            }

            return called.Name.StartsWith("Draw", StringComparison.OrdinalIgnoreCase)
                || called.Name.StartsWith("Render", StringComparison.OrdinalIgnoreCase)
                || called.Name.StartsWith("Blit", StringComparison.OrdinalIgnoreCase)
                || called.Name.StartsWith("Submit", StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectCompType(Candidate candidate, MethodInfo called)
        {
            if (!called.IsGenericMethod
                || (called.Name != "GetComp" && called.Name != "TryGetComp")
                || (called.DeclaringType != typeof(ThingWithComps)
                    && called.DeclaringType != typeof(ThingCompUtility)))
            {
                return;
            }

            Type[] arguments = called.GetGenericArguments();
            if (arguments.Length == 1 && typeof(ThingComp).IsAssignableFrom(arguments[0]))
            {
                candidate.compTypes.Add(arguments[0]);
            }
        }

        private static MethodInfo ReplacementFor(MethodInfo called)
        {
            if (called == NativeDrawEquipment)
            {
                return ProbeDrawEquipment;
            }

            if (called == NativePrimary)
            {
                return ProbePrimary;
            }

            if (called == NativeDrawWornExtras)
            {
                return ProbeDrawWornExtras;
            }

            return called.DeclaringType == typeof(Graphics)
                ? RimKataWeaponDrawCapture.ReplacementFor(called)
                : null;
        }

        private static IEnumerable<CodeInstruction> InstrumentRenderer(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            string owner = __originalMethod.DeclaringType?.FullName;
            bool sheath = (owner == "SYS.DrawEquipment_WeaponBackPatch" && __originalMethod.Name == "DrawSheath")
                || (owner == "MihoLib.DrawEquipment_WeaponBackPatch" && __originalMethod.Name == "DrawSheathLogic");
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo called)
                {
                    MethodInfo replacement = sheath && called == NativeSheathMesh
                        ? ProbeSheathMesh : ReplacementFor(called);
                    if (replacement != null)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                }

                yield return instruction;
            }
        }

        private static bool TryMapArguments(MethodInfo method, out int[] argumentMap)
        {
            argumentMap = null;
            if (!method.IsStatic
                || method.ContainsGenericParameters
                || method.ReturnType != typeof(bool))
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            int[] mapping = new int[parameters.Length];
            bool[] used = new bool[4];
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                // Harmony bookkeeping/ref arguments depend on their original patch invocation.
                if (parameter.ParameterType.IsByRef
                    || parameter.Name?.StartsWith("__", StringComparison.Ordinal) == true)
                {
                    return false;
                }

                int source = parameter.ParameterType == typeof(Pawn) ? 0
                    : parameter.ParameterType == typeof(Vector3) ? 1
                    : parameter.ParameterType == typeof(Rot4) ? 2
                    : parameter.ParameterType == typeof(PawnRenderFlags) ? 3
                    : -1;
                if (source < 0 || used[source])
                {
                    return false;
                }

                used[source] = true;
                mapping[i] = source;
            }

            if (!used[0] || !used[1])
            {
                return false;
            }

            argumentMap = mapping;
            return true;
        }

        private static Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool> CompileInvoker(
            MethodInfo method,
            int[] argumentMap)
        {
            DynamicMethod invoker = new DynamicMethod(
                "RimKataInvokeWeaponRenderer",
                typeof(bool),
                new[] { typeof(Pawn), typeof(Vector3), typeof(Rot4), typeof(PawnRenderFlags) },
                typeof(RimKataWeaponRenderDiscovery),
                true);
            ILGenerator il = invoker.GetILGenerator();
            foreach (int argument in argumentMap)
            {
                il.Emit(OpCodes.Ldarg, (short)argument);
            }

            il.Emit(OpCodes.Call, method);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Ret);
            return (Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool>)invoker.CreateDelegate(
                typeof(Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool>));
        }

        private static bool IsExternalAssembly(Assembly assembly)
        {
            return assembly != null
                && assembly != typeof(RimKataWeaponRenderDiscovery).Assembly
                && assembly != typeof(PawnRenderUtility).Assembly
                && assembly != typeof(Harmony).Assembly
                && assembly != typeof(Graphics).Assembly
                && assembly != typeof(object).Assembly;
        }

        private static void ReportUnsupported(MethodInfo method, Exception exception)
        {
            Log.Warning("[RimKata] Skipped automatic weapon renderer "
                + method.DeclaringType?.FullName + "." + method.Name
                + ": " + exception.GetType().Name);
        }

        private sealed class Candidate
        {
            internal readonly MethodInfo prefix;
            internal readonly Assembly assembly;
            internal readonly int[] arguments;
            internal readonly HashSet<MethodInfo> visited = new HashSet<MethodInfo>();
            internal readonly HashSet<MethodInfo> methodsToInstrument = new HashSet<MethodInfo>();
            internal readonly HashSet<Type> compTypes = new HashSet<Type>();
            internal int instructionCount;
            internal bool hasCustomDraw;
            internal bool accessoriesOnly;
            internal Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool> invoke;
            internal string fingerprint;
            internal string restoredKey;
            internal Renderer renderer;

            internal Candidate(MethodInfo prefix, int[] arguments)
            {
                this.prefix = prefix;
                assembly = prefix.DeclaringType.Assembly;
                this.arguments = arguments;
            }
        }

        internal sealed class Renderer
        {
            private readonly Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool> invoke;
            private readonly Type[] compTypes;
            internal bool AccessoriesOnly { get; }
            internal string Key { get; }

            internal Renderer(
                Func<Pawn, Vector3, Rot4, PawnRenderFlags, bool> invoke,
                HashSet<Type> compTypes, bool accessoriesOnly = false, string key = null)
            {
                this.invoke = invoke;
                Key = key;
                AccessoriesOnly = accessoriesOnly;
                this.compTypes = new Type[compTypes.Count];
                compTypes.CopyTo(this.compTypes);
            }

            internal bool Supports(ThingDef weaponDef)
            {
                if (weaponDef == null)
                {
                    return false;
                }

                if (compTypes.Length == 0)
                {
                    return true;
                }

                if (weaponDef.comps != null)
                {
                    foreach (CompProperties properties in weaponDef.comps)
                    {
                        if (properties?.compClass == null)
                        {
                            continue;
                        }

                        foreach (Type compType in compTypes)
                        {
                            if (compType.IsAssignableFrom(properties.compClass))
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }

            internal bool ReplacesOriginal(Pawn pawn, Vector3 rootLoc, Rot4 facing, PawnRenderFlags flags)
            {
                return invoke(pawn, rootLoc, facing, flags);
            }
        }
    }
}
