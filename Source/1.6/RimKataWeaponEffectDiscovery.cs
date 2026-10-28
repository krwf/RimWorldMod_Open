using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal sealed class RimKataWeaponEffectPlan
    {
        internal ThingDef WeaponDef;
        internal Type VerbType;
        internal HediffDef[] ManagerDefs, Effects;
        internal MethodInfo[] AnalyzedMethods;
    }

    // Discover original execution and its scoped storage accesses. No gain/reset behavior is inferred.
    internal static class RimKataWeaponEffectDiscovery
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Assembly GameAssembly = typeof(Pawn).Assembly;
        private static readonly Assembly OwnAssembly = typeof(RimKataWeaponEffectDiscovery).Assembly;
        private static readonly HashSet<MethodBase> ScopedAccesses = BuildScopedAccesses();
        private static readonly MethodInfo EquipmentDisplayStats = AccessTools.Method(typeof(ThingComp),
            nameof(ThingComp.SpecialDisplayStats), Type.EmptyTypes);

        internal static bool TryAnalyze(RimKataWeaponExecutionPlan execution, out RimKataWeaponEffectPlan plan)
            => TryAnalyze(execution, out plan, out _);

        internal static bool TryAnalyze(RimKataWeaponExecutionPlan execution, out RimKataWeaponEffectPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (execution?.Def == null || execution.VerbType == null || execution.HandledByCompatibility) return false;
            try
            {
                var definitions = new HashSet<HediffDef>();
                var pending = new Queue<HediffDef>();
                var owners = new HashSet<Type> { execution.VerbType };
                foreach (CompProperties properties in execution.Def.comps ?? new List<CompProperties>())
                {
                    Collect(properties, definitions, pending);
                    if (properties?.compClass != null) owners.Add(properties.compClass);
                }
                foreach (VerbProperties properties in execution.Def.Verbs ?? new List<VerbProperties>())
                    if (properties?.verbClass == execution.VerbType) Collect(properties, definitions, pending);
                if (pending.Count == 0) return false;
                if (execution.Def.Verbs.Any(v => v != null && !v.IsMeleeAttack && v.verbClass != execution.VerbType))
                    return Fail("Multiple ranged Verb implementations share the declared effects; ownership is unresolved.", out reason);

                var managers = new List<HediffDef>();
                var effects = new List<HediffDef>();
                while (pending.Count != 0)
                {
                    HediffDef definition = pending.Dequeue();
                    if (definition.comps?.Count > 0)
                    {
                        if (!ManagerOnly(definition))
                            return Fail("A manager also changes shared health or uses a custom Hediff class: " + definition.defName, out reason);
                        managers.Add(definition);
                        foreach (HediffCompProperties properties in definition.comps)
                        {
                            if (properties?.compClass == null || !typeof(HediffComp).IsAssignableFrom(properties.compClass))
                                return Fail("An effect manager has an unresolved component.", out reason);
                            owners.Add(properties.compClass);
                            Collect(properties, definitions, pending);
                        }
                    }
                    else
                    {
                        if (!StatOnly(definition))
                            return Fail("A declared effect is not a supported stat-only Hediff: " + definition.defName, out reason);
                        effects.Add(definition);
                    }
                }
                if (effects.Count == 0)
                    return Fail("Declared managers have no traceable stat-effect definitions; original ownership is retained.", out reason);

                var graph = new Graph(execution.VerbType, owners, definitions);
                foreach (Type owner in owners) graph.AddOwner(owner);
                if (!graph.Inspect(out reason)) return false;
                if (graph.Methods.Count == 0)
                    return Fail("No custom effect execution was available to connect.", out reason);
                plan = new RimKataWeaponEffectPlan
                {
                    WeaponDef = execution.Def, VerbType = execution.VerbType,
                    ManagerDefs = managers.ToArray(), Effects = effects.ToArray(),
                    AnalyzedMethods = graph.Methods.ToArray()
                };
                return true;
            }
            catch (Exception exception)
            {
                reason = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static bool Fail(string message, out string reason) { reason = message; return false; }

        private static void Collect(object properties, HashSet<HediffDef> seen, Queue<HediffDef> pending)
        {
            if (properties == null) return;
            for (Type type = properties.GetType(); type != null && type != typeof(object); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(Fields))
                {
                    if (typeof(HediffDef).IsAssignableFrom(field.FieldType))
                        Add(field.GetValue(properties) as HediffDef);
                    else if (typeof(IEnumerable<HediffDef>).IsAssignableFrom(field.FieldType)
                        && field.GetValue(properties) is IEnumerable<HediffDef> values)
                        foreach (HediffDef value in values) Add(value);
                }
            void Add(HediffDef value) { if (value != null && seen.Add(value)) pending.Enqueue(value); }
        }

        internal static bool IsScopedAccess(MethodBase method) => method != null && ScopedAccesses.Contains(method);
        internal static string AccessFingerprint()
            => RimKataWeaponExecutionPlans.RouteFingerprint(ScopedAccesses.OfType<MethodInfo>().ToArray());

        private static HashSet<MethodBase> BuildScopedAccesses()
        {
            var result = new HashSet<MethodBase>();
            void Add(Type type, string name, params Type[] args)
            {
                MethodInfo method = AccessTools.Method(type, name, args);
                if (method != null) result.Add(method);
            }
            Add(typeof(HediffSet), nameof(HediffSet.GetFirstHediffOfDef), typeof(HediffDef), typeof(bool));
            Add(typeof(HediffSet), nameof(HediffSet.HasHediff), typeof(HediffDef), typeof(bool));
            Add(typeof(HediffSet), nameof(HediffSet.HasHediff), typeof(HediffDef), typeof(BodyPartRecord), typeof(bool));
            Add(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
                typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult));
            Add(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
                typeof(HediffDef), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult));
            Add(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.RemoveHediff), typeof(Hediff));
            result.Add(AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary)));
            result.Add(AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AllEquipmentListForReading)));
            Add(typeof(StatExtension), nameof(StatExtension.GetStatValue), typeof(Thing), typeof(StatDef), typeof(bool), typeof(int));
            result.Remove(null);
            return result;
        }

        private static bool Custom(Type type)
        {
            if (type == null || type.Assembly == GameAssembly || type.Assembly == OwnAssembly
                || type.Assembly == typeof(object).Assembly || type.Assembly == typeof(Harmony).Assembly) return false;
            string name = type.Assembly.GetName().Name;
            return name != "netstandard" && name != "System" && !name.StartsWith("System.", StringComparison.Ordinal)
                && !name.StartsWith("Microsoft.", StringComparison.Ordinal)
                && !name.StartsWith("UnityEngine", StringComparison.Ordinal)
                && !name.StartsWith("Brutal", StringComparison.Ordinal);
        }

        private sealed class Graph
        {
            private readonly Type verbType;
            private readonly HashSet<Type> owners;
            private readonly HashSet<HediffDef> definitions;
            private readonly Queue<MethodBase> pending = new Queue<MethodBase>();
            private readonly HashSet<MethodBase> seen = new HashSet<MethodBase>();
            private readonly HashSet<MethodBase> inspectedPatches = new HashSet<MethodBase>();
            internal readonly HashSet<MethodInfo> Methods = new HashSet<MethodInfo>();
            private string failure;
            private int instructions;

            internal Graph(Type verbType, HashSet<Type> owners, HashSet<HediffDef> definitions)
            { this.verbType = verbType; this.owners = owners; this.definitions = definitions; }
            internal void AddOwner(Type owner)
            {
                // Information-card enumeration is not an independent effect lifecycle entry.
                // A call to it from an actual effect method still follows the normal Add path.
                for (Type type = owner; Custom(type); type = type.BaseType)
                {
                    foreach (MethodInfo method in type.GetMethods(Fields | BindingFlags.Static))
                        if (method.IsVirtual && method.GetBaseDefinition() != EquipmentDisplayStats) Add(method);
                    foreach (ConstructorInfo constructor in type.GetConstructors(Fields)) Add(constructor);
                }
            }

            private void Add(MethodBase method)
            {
                if (method == null) return;
                // These API entries and all of their external hooks execute in the
                // original health snapshots. Health.Initialize validates their mutation sites.
                // The weapon graph must not expand unrelated health/graphics callbacks again.
                if (method.DeclaringType == typeof(Pawn_HealthTracker) && IsScopedAccess(method)) return;
                bool custom = Custom(method.DeclaringType);
                bool rewrittenHelper = false;
                if ((custom || method.DeclaringType?.Assembly == GameAssembly) && inspectedPatches.Add(method))
                {
                    Patches patches = Harmony.GetPatchInfo(method);
                    if (patches != null)
                    {
                        foreach (Patch patch in patches.Prefixes)
                            if (!ExcludesWeapon(patch.PatchMethod, method)) Add(patch.PatchMethod);
                        foreach (Patch patch in patches.Postfixes.Concat(patches.Finalizers))
                            if (patch.PatchMethod?.DeclaringType?.Assembly != OwnAssembly) Add(patch.PatchMethod);
                        foreach (string kind in new[] { "InnerPrefixes", "InnerPostfixes", "InnerFinalizers" })
                            foreach (Patch patch in RimKataWeaponExecutionPlans.ExtraPatches(patches, kind))
                                if (patch.PatchMethod?.DeclaringType?.Assembly != OwnAssembly
                                    && (kind != "InnerPrefixes" || !ExcludesWeapon(patch.PatchMethod, method))) Add(patch.PatchMethod);
                        if (!custom && patches.Transpilers.Any(p => p.PatchMethod?.DeclaringType?.Assembly != OwnAssembly))
                            rewrittenHelper = true;
                    }
                }
                if ((!custom && !rewrittenHelper) || !seen.Add(method)) return;
                if (method.ContainsGenericParameters || method.IsAbstract || method.GetMethodBody() == null)
                { failure = "An original method cannot be inspected: " + method; return; }
                if (rewrittenHelper && !(method is MethodInfo))
                { failure = "A rewritten game constructor cannot be retained as an effect dependency: " + method; return; }
                pending.Enqueue(method);
                if (method is MethodInfo callable) Methods.Add(callable);
                // Transpilers are executed by GetCurrentInstructions; their emitted runtime calls are inspected below.
            }

            private bool ExcludesWeapon(MethodInfo prefix, MethodBase original)
                => prefix?.DeclaringType?.Assembly == OwnAssembly
                    || (!original.IsStatic && typeof(Verb).IsAssignableFrom(original.DeclaringType)
                        && original.DeclaringType.IsAssignableFrom(verbType)
                        && RimKataWeaponExecutionDiscovery.ExcludesType(prefix, verbType));

            internal bool Inspect(out string reason)
            {
                while (failure == null && pending.Count != 0)
                {
                    if (seen.Count > RimKataWeaponExecutionDiscovery.MaximumMethods)
                    { failure = "The connected effect method graph exceeded " + RimKataWeaponExecutionDiscovery.MaximumMethods + " methods."; break; }
                    MethodBase current = pending.Dequeue();
                    List<CodeInstruction> codes = PatchProcessor.GetCurrentInstructions(current);
                    bool originalHelper = current.DeclaringType?.Assembly == GameAssembly;
                    var originalAccesses = originalHelper ? OriginalAccesses(current) : null;
                    instructions += codes.Count;
                    if (instructions > RimKataWeaponExecutionDiscovery.MaximumInstructions)
                    { failure = "The connected effect method graph exceeded " + RimKataWeaponExecutionDiscovery.MaximumInstructions + " instructions."; break; }
                    foreach (CodeInstruction code in codes)
                    {
                        // The native body remains an original call. Inspect accesses added by
                        // transpilers, rather than expanding unrelated native health callbacks.
                        // Opcode and occurrence count matter: a read changed to a write/address
                        // or a repeated call must not disappear into the original access set.
                        if (originalAccesses != null && code.operand is MemberInfo member
                            && originalAccesses.TryGetValue((code.opcode, member), out int remaining) && remaining > 0)
                        { originalAccesses[(code.opcode, member)] = remaining - 1; continue; }
                        if (code.opcode == OpCodes.Calli || code.opcode == OpCodes.Ldvirtftn
                            || (originalHelper && code.opcode == OpCodes.Ldftn))
                        { failure = "An indirect effect call has no fixed target: " + current; break; }
                        if (code.operand is FieldInfo field && UnsafeField(field, code.opcode))
                        { failure = "An effect accesses direct health or unresolved callback storage: " + field; break; }
                        if (!(code.operand is MethodBase called)) continue;
                        if (IsScopedAccess(called))
                        {
                            // Rewriting a common native helper would introduce a global hook.
                            // Keep the weapon on its original effect path if that is required.
                            if (originalHelper)
                            { failure = "A rewritten game helper adds an unscoped health/equipment access: " + called; break; }
                            Add(called);
                            if (!(current is MethodInfo))
                                failure = "A constructor requires a scoped health/equipment rewrite: " + current;
                            continue;
                        }
                        if (UnsafeCall(called))
                        { failure = "An effect call cannot be scoped safely: " + called; break; }
                        if (called is MethodInfo virtualCall && code.opcode == OpCodes.Callvirt && virtualCall.IsVirtual
                            && Custom(virtualCall.DeclaringType))
                        {
                            Type[] matches = owners.Where(t => virtualCall.DeclaringType.IsAssignableFrom(t)).ToArray();
                            if (matches.Length == 0 && !virtualCall.IsFinal && !virtualCall.DeclaringType.IsSealed)
                            { failure = "A virtual effect receiver is unresolved: " + called; break; }
                            foreach (Type owner in matches)
                            {
                                MethodInfo slot = owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                    .FirstOrDefault(m => m.GetBaseDefinition() == virtualCall.GetBaseDefinition());
                                Add(slot);
                            }
                        }
                        Add(called);
                    }
                }
                reason = failure;
                return failure == null;
            }

            private static Dictionary<(OpCode, MemberInfo), int> OriginalAccesses(MethodBase method)
            {
                var result = new Dictionary<(OpCode, MemberInfo), int>();
                foreach (CodeInstruction code in PatchProcessor.GetOriginalInstructions(method))
                    if (code.operand is MemberInfo member)
                    {
                        var key = (code.opcode, member);
                        result.TryGetValue(key, out int count);
                        result[key] = count + 1;
                    }
                return result;
            }

            private bool UnsafeField(FieldInfo field, OpCode op)
            {
                if (typeof(HediffDef).IsAssignableFrom(field.FieldType)
                    || typeof(IEnumerable<HediffDef>).IsAssignableFrom(field.FieldType))
                {
                    if (field.IsStatic)
                    {
                        object value = field.GetValue(null);
                        if (value is HediffDef def) { if (!definitions.Contains(def)) return true; }
                        else if (value is IEnumerable<HediffDef> values)
                        { foreach (HediffDef item in values) if (item != null && !definitions.Contains(item)) return true; }
                        else return true;
                    }
                    else if (!typeof(VerbProperties).IsAssignableFrom(field.DeclaringType)
                        && !typeof(CompProperties).IsAssignableFrom(field.DeclaringType)
                        && !typeof(HediffCompProperties).IsAssignableFrom(field.DeclaringType)) return true;
                }
                if (typeof(Delegate).IsAssignableFrom(field.FieldType)) return true;
                if (field.DeclaringType == typeof(HediffSet) && typeof(IEnumerable<Hediff>).IsAssignableFrom(field.FieldType))
                    return true;
                bool write = op == OpCodes.Stfld || op == OpCodes.Stsfld || op == OpCodes.Ldflda || op == OpCodes.Ldsflda;
                return write && ((field.IsStatic && Custom(field.DeclaringType))
                    || field.DeclaringType == typeof(HediffSet) || field.DeclaringType == typeof(Pawn_HealthTracker)
                    || (field.DeclaringType == typeof(Pawn) && field.FieldType == typeof(Pawn_HealthTracker)));
            }

            private static bool UnsafeCall(MethodBase method)
            {
                Type type = method.DeclaringType;
                if (type == null) return true;
                if (type == typeof(HediffDef) && method is ConstructorInfo) return true;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DefDatabase<>)
                    && type.GetGenericArguments()[0] == typeof(HediffDef)) return true;
                if (type == typeof(List<ThingWithComps>) && (method.Name == "Add" || method.Name == "Remove"
                    || method.Name == "Clear" || method.Name == "set_Item" || method.Name == "RemoveAt")) return true;
                if (method is ConstructorInfo && Custom(type) && typeof(IEnumerator).IsAssignableFrom(type)) return true;
                if (typeof(Delegate).IsAssignableFrom(type) && method.Name == "Invoke") return true;
                if (type == typeof(Type) && method.Name != nameof(Type.GetTypeFromHandle)) return true;
                if (type == typeof(Activator) || type.Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) == true
                    || type.Namespace?.StartsWith("System.Threading", StringComparison.Ordinal) == true) return true;
                if (type == typeof(HediffSet) || type == typeof(Pawn_HealthTracker))
                    return !(method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal));
                if (type == typeof(HealthUtility)) return true;
                return false;
            }
        }

        private static readonly HashSet<string> DefPresentation = new HashSet<string>
        {
            "hediffClass", "comps", "stages", "maxSeverity", "isBad", "descriptionShort", "debugLabelExtra",
            "overrideTooltip", "extraTooltip", "inspectString", "alwaysShowSeverity", "defaultLabelColor",
            "labelNoun", "battleStateLabel", "labelNounPretty", "targetPrefix"
        };
        private static readonly HashSet<string> StagePresentation = new HashSet<string>
        {
            "minSeverity", "label", "overrideLabel", "untranslatedLabel", "becomeVisible",
            "overrideTooltip", "extraTooltip", "statFactors", "statOffsets"
        };

        internal static bool StatOnly(HediffDef effect)
        {
            if (effect == null || effect.GetType() != typeof(HediffDef)
                || (effect.hediffClass != typeof(Hediff) && effect.hediffClass != typeof(HediffWithComps))
                || effect.comps?.Count > 0 || effect.modExtensions?.Count > 0 || effect.stages == null
                || effect.stages.Count == 0 || effect.maxSeverity <= 0f || float.IsNaN(effect.maxSeverity)
                || float.IsInfinity(effect.maxSeverity)
                || !DefaultOtherFields(effect, new HediffDef(), DefPresentation)) return false;
            float previous = float.NegativeInfinity;
            bool any = false;
            foreach (HediffStage stage in effect.stages)
            {
                if (stage == null || stage.GetType() != typeof(HediffStage) || float.IsNaN(stage.minSeverity)
                    || float.IsInfinity(stage.minSeverity) || stage.minSeverity <= previous
                    || !DefaultOtherFields(stage, new HediffStage(), StagePresentation)) return false;
                previous = stage.minSeverity;
                foreach (StatModifier modifier in (stage.statFactors ?? new List<StatModifier>())
                    .Concat(stage.statOffsets ?? new List<StatModifier>()))
                {
                    if (modifier?.stat == null || !RimKataWeaponEffectStats.IsSupported(modifier.stat)
                        || float.IsNaN(modifier.value) || float.IsInfinity(modifier.value)) return false;
                    any = true;
                }
            }
            return any;
        }

        internal static bool ManagerOnly(HediffDef definition)
        {
            if (definition?.GetType() != typeof(HediffDef) || definition.hediffClass != typeof(HediffWithComps)
                || definition.modExtensions?.Count > 0 || !DefaultOtherFields(definition, new HediffDef(), DefPresentation)) return false;
            if (definition.stages != null)
                foreach (HediffStage stage in definition.stages)
                    if (stage == null || stage.GetType() != typeof(HediffStage)
                        || stage.statFactors?.Count > 0 || stage.statOffsets?.Count > 0
                        || !DefaultOtherFields(stage, new HediffStage(), StagePresentation)) return false;
            return true;
        }

        private static bool DefaultOtherFields(object value, object defaults, HashSet<string> ignored)
        {
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (ignored.Contains(field.Name)) continue;
                object a = field.GetValue(value), b = field.GetValue(defaults);
                if (a is ICollection list && list.Count == 0 && (b == null || b is ICollection empty && empty.Count == 0)) continue;
                if (a is SimpleCurve curve && b is SimpleCurve defaultCurve
                    && a.GetType() == typeof(SimpleCurve) && b.GetType() == typeof(SimpleCurve)
                    && curve.Points.SequenceEqual(defaultCurve.Points)) continue;
                if (!Equals(a, b)) return false;
            }
            return true;
        }
    }
}

