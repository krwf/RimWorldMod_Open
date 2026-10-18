using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Preparation only: discover state readers on components actually linked to weapon definitions.
    internal static class RimKataWeaponStateObservers
    {
        internal enum ObserverRole { None, Firing, Cooldown, Target }

        private static readonly Dictionary<MethodInfo, ObserverRole> installed =
            new Dictionary<MethodInfo, ObserverRole>();
        private static readonly HashSet<MethodInfo> reported = new HashSet<MethodInfo>();
        internal static IReadOnlyDictionary<MethodInfo, ObserverRole> Connections => installed;

        private static readonly FieldInfo PawnStances = AccessTools.Field(typeof(Pawn), "stances");
        private static readonly FieldInfo CurrentStance = AccessTools.Field(typeof(Pawn_StanceTracker), "curStance");
        private static readonly FieldInfo StanceVerb = AccessTools.Field(typeof(Stance_Busy), "verb");
        private static readonly FieldInfo Focus = AccessTools.Field(typeof(Stance_Busy), "focusTarg");
        private static readonly FieldInfo State = AccessTools.Field(typeof(Verb), "state");
        private static readonly MethodInfo Equipment = AccessTools.PropertyGetter(typeof(Verb), "EquipmentSource");
        private static readonly MethodInfo Target = AccessTools.PropertyGetter(typeof(Verb), "CurrentTarget");
        private static readonly ConstructorInfo NullableTarget =
            typeof(LocalTargetInfo?).GetConstructor(new[] { typeof(LocalTargetInfo) });
        private static readonly HashSet<MethodBase> readCalls = new HashSet<MethodBase>
        {
            Equipment, Target, AccessTools.PropertyGetter(typeof(CompEquippable), "AllVerbs"),
            AccessTools.Method(typeof(List<Verb>), "GetEnumerator"),
            AccessTools.PropertyGetter(typeof(List<Verb>.Enumerator), "Current"),
            AccessTools.Method(typeof(List<Verb>.Enumerator), "MoveNext"),
            AccessTools.Method(typeof(IDisposable), "Dispose"),
            AccessTools.PropertyGetter(typeof(LocalTargetInfo), "IsValid")
        };

        internal static int Prepare(Harmony harmony, IEnumerable<ThingDef> definitions)
        {
            if (harmony == null || definitions == null) return 0;
            var types = new HashSet<Type>();
            var hediffs = new HashSet<HediffDef>();
            var pending = new Queue<HediffDef>();
            foreach (ThingDef definition in definitions)
            {
                if (definition?.IsWeapon != true) continue;
                if (definition.comps != null)
                    foreach (CompProperties properties in definition.comps)
                    {
                        AddType(properties?.compClass, types);
                        CollectHediffs(properties, pending, hediffs);
                    }
                if (definition.Verbs != null)
                    foreach (VerbProperties properties in definition.Verbs)
                        CollectHediffs(properties, pending, hediffs);
            }
            while (pending.Count != 0)
            {
                HediffDef definition = pending.Dequeue();
                if (definition.comps == null) continue;
                foreach (HediffCompProperties properties in definition.comps)
                {
                    AddType(properties?.compClass, types);
                    CollectHediffs(properties, pending, hediffs);
                }
            }

            int added = 0;
            foreach (Type type in types)
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (installed.ContainsKey(method) || !HasReaderSignature(method)) continue;
                    try
                    {
                        ObserverRole role = Classify(method);
                        if (role == ObserverRole.None) continue;
                        string hook = role == ObserverRole.Firing ? nameof(FiringPostfix)
                            : role == ObserverRole.Cooldown ? nameof(CooldownPostfix) : nameof(TargetPostfix);
                        RimKataStartupPatches.Patch(harmony, method,
                            postfix: new HarmonyMethod(typeof(RimKataWeaponStateObservers), hook));
                        installed.Add(method, role);
                        added++;
                    }
                    catch (Exception exception)
                    {
                        if (reported.Add(method))
                            Log.Warning("[RimKata] Could not connect weapon state reader "
                                + type.FullName + "." + method.Name + ": " + exception.GetType().Name);
                    }
                }
            return added;
        }

        private static void AddType(Type type, HashSet<Type> types)
        {
            while (type != null && type != typeof(ThingComp) && type != typeof(HediffComp))
            {
                if (!typeof(ThingComp).IsAssignableFrom(type) && !typeof(HediffComp).IsAssignableFrom(type)) return;
                if (type.Assembly != typeof(Pawn).Assembly && type.Assembly != typeof(RimKataWeaponStateObservers).Assembly)
                    types.Add(type);
                type = type.BaseType;
            }
        }

        private static void CollectHediffs(object properties, Queue<HediffDef> pending, HashSet<HediffDef> seen)
        {
            if (properties == null) return;
            for (Type type = properties.GetType(); type != null && type != typeof(object); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    // Read definition references only; never evaluate arbitrary properties or object graphs.
                    if (typeof(HediffDef).IsAssignableFrom(field.FieldType))
                        AddHediff(field.GetValue(properties) as HediffDef, pending, seen);
                    else if (typeof(IEnumerable<HediffDef>).IsAssignableFrom(field.FieldType)
                        && field.GetValue(properties) is IEnumerable<HediffDef> values)
                        foreach (HediffDef value in values) AddHediff(value, pending, seen);
                }
        }

        private static void AddHediff(HediffDef definition, Queue<HediffDef> pending, HashSet<HediffDef> seen)
        {
            if (definition != null && seen.Add(definition)) pending.Enqueue(definition);
        }

        private static bool HasReaderSignature(MethodInfo method)
        {
            if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters
                || method.GetMethodBody() == null
                || (method.ReturnType != typeof(bool) && method.ReturnType != typeof(LocalTargetInfo?))) return false;
            ParameterInfo[] args = method.GetParameters();
            return args.Length == 2 && args[0].ParameterType == typeof(Pawn)
                && args[1].ParameterType == typeof(ThingWithComps);
        }

        internal static ObserverRole Classify(MethodInfo method)
        {
            // A wrapper can deliberately override the reader's result; its body alone cannot prove that meaning.
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches != null)
            {
                if (patches.Prefixes.Count != 0 || patches.Finalizers.Count != 0) return ObserverRole.None;
                foreach (Patch patch in patches.Postfixes)
                {
                    MethodInfo hook = patch.PatchMethod;
                    if (hook?.DeclaringType != typeof(RimKataWeaponStateObservers)
                        || (hook.Name != nameof(FiringPostfix) && hook.Name != nameof(CooldownPostfix)
                            && hook.Name != nameof(TargetPostfix))) return ObserverRole.None;
                }
            }
            List<CodeInstruction> raw = PatchProcessor.GetCurrentInstructions(method);
            if (raw.Count > 256) return ObserverRole.None;
            var codes = raw.FindAll(code => code.opcode != OpCodes.Nop);
            int warmup = 0, cooldown = 0, state = 0, focus = 0, target = 0;
            int equipmentChecks = 0, positiveReturns = 0, targetReturns = 0, comparisons = 0;
            bool stances = false, currentStance = false;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                OpCode op = code.opcode;
                if (IsNotEqual(op)) comparisons++;
                if (op == OpCodes.Isinst)
                {
                    if (Equals(code.operand, typeof(Stance_Warmup))) warmup++;
                    else if (Equals(code.operand, typeof(Stance_Cooldown))) cooldown++;
                    else return ObserverRole.None;
                }
                else if (op == OpCodes.Ldfld)
                {
                    if (Equals(code.operand, PawnStances)) stances = true;
                    else if (Equals(code.operand, CurrentStance)) currentStance = true;
                    else if (Equals(code.operand, StanceVerb)) { }
                    else if (Equals(code.operand, Focus)) focus++;
                    else if (Equals(code.operand, State))
                    {
                        // Only a positive Bursting test; Idle/inverted tests have different meaning.
                        if (i + 2 >= codes.Count || codes[i + 1].opcode != OpCodes.Ldc_I4_1
                            || !IsNotEqual(codes[i + 2].opcode)) return ObserverRole.None;
                        state++;
                    }
                    else return ObserverRole.None;
                }
                else if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    if (!(code.operand is MethodInfo called) || !IsReadCall(called)) return ObserverRole.None;
                    if (called == Equipment)
                    {
                        if (i + 2 >= codes.Count || !LoadsWeapon(codes[i + 1])
                            || !IsNotEqual(codes[i + 2].opcode)) return ObserverRole.None;
                        equipmentChecks++;
                    }
                    else if (called == Target) target++;
                }
                else if (op == OpCodes.Newobj)
                {
                    if (!Equals(code.operand, NullableTarget)) return ObserverRole.None;
                    targetReturns++;
                }
                else if (op == OpCodes.Ldc_I4_1)
                {
                    if (i > 0 && Equals(codes[i - 1].operand, State)) continue;
                    if (i < 1 || !IsNotEqual(codes[i - 1].opcode) || i + 1 >= codes.Count)
                        return ObserverRole.None;
                    // True must immediately follow an accepted state/equipment equality test.
                    if (codes[i + 1].opcode != OpCodes.Ret
                        && !(IsStoreLocal(codes[i + 1].opcode) && i + 2 < codes.Count
                            && IsLeave(codes[i + 2].opcode))) return ObserverRole.None;
                    positiveReturns++;
                }
                else if (op == OpCodes.Initobj)
                {
                    if (!Equals(code.operand, typeof(LocalTargetInfo?))) return ObserverRole.None;
                }
                else if (op == OpCodes.Constrained)
                {
                    if (!Equals(code.operand, typeof(List<Verb>.Enumerator))) return ObserverRole.None;
                }
                else if (!IsReaderInstruction(code)) return ObserverRole.None;
            }
            if (!stances || !currentStance || comparisons != equipmentChecks + state) return ObserverRole.None;
            if (method.ReturnType == typeof(LocalTargetInfo?))
                return warmup == 1 && cooldown == 0 && state == 1 && focus == 1 && target == 1
                    && equipmentChecks == 2 && positiveReturns == 0 && targetReturns == 2
                    ? ObserverRole.Target : ObserverRole.None;
            if (focus != 0 || target != 0 || targetReturns != 0) return ObserverRole.None;
            if (warmup == 1 && cooldown == 1 && state == 1 && equipmentChecks == 2 && positiveReturns == 3)
                return ObserverRole.Firing;
            return warmup == 0 && cooldown == 1 && state == 0 && equipmentChecks == 1 && positiveReturns == 1
                ? ObserverRole.Cooldown : ObserverRole.None;
        }

        private static bool IsReadCall(MethodInfo method)
        {
            if (readCalls.Contains(method)) return true;
            return method.DeclaringType == typeof(ThingWithComps) && method.Name == "GetComp"
                && method.IsGenericMethod && method.GetGenericArguments().Length == 1
                && method.GetGenericArguments()[0] == typeof(CompEquippable);
        }

        private static bool IsNotEqual(OpCode op) => op == OpCodes.Bne_Un || op == OpCodes.Bne_Un_S;
        private static bool IsLeave(OpCode op) => op == OpCodes.Leave || op == OpCodes.Leave_S;
        private static bool IsStoreLocal(OpCode op) => op == OpCodes.Stloc || op == OpCodes.Stloc_S
            || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;

        private static bool LoadsWeapon(CodeInstruction code) => code.opcode == OpCodes.Ldarg_2
            || ((code.opcode == OpCodes.Ldarg || code.opcode == OpCodes.Ldarg_S)
                && Convert.ToInt32(code.operand) == 2);

        private static bool IsReaderInstruction(CodeInstruction code)
        {
            OpCode op = code.opcode;
            if (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S)
            {
                int index = Convert.ToInt32(code.operand);
                return index == 1 || index == 2;
            }
            return op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldnull
                || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ret || op == OpCodes.Endfinally
                || op == OpCodes.Br || op == OpCodes.Br_S || op == OpCodes.Brtrue || op == OpCodes.Brtrue_S
                || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S || IsNotEqual(op) || IsLeave(op)
                || IsStoreLocal(op) || op == OpCodes.Ldloc || op == OpCodes.Ldloc_S
                || op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3
                || op == OpCodes.Ldloca || op == OpCodes.Ldloca_S;
        }

        private static void FiringPostfix(Pawn __0, ThingWithComps __1, ref bool __result)
        {
            if (!__result && ReadState(__0, __1, out _)) __result = true;
        }

        private static void CooldownPostfix(Pawn __0, ThingWithComps __1, ref bool __result)
        {
            if (!__result && ReadState(__0, __1, out RimKataRangedAttackState state)
                && state.Phase == RimKataRangedAttackPhase.Cooldown) __result = true;
        }

        private static void TargetPostfix(Pawn __0, ThingWithComps __1, ref LocalTargetInfo? __result)
        {
            if (!__result.HasValue
                && ReadState(__0, __1, out RimKataRangedAttackState state)
                && state.Phase != RimKataRangedAttackPhase.Cooldown && state.Target.IsValid) __result = state.Target;
        }

        private static bool ReadState(Pawn pawn, ThingWithComps weapon, out RimKataRangedAttackState state)
            => RimKataWeaponEffects.TryReadScopedState(pawn, weapon, out state)
                || RimKataRangedAttackStatus.TryGetState(pawn, weapon, out state);
    }
}

