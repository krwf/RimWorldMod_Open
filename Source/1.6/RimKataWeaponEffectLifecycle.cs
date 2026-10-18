using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Preparation only. Recognize a closed target-change cleanup branch, never a whole manager.
    internal static class RimKataWeaponEffectLifecycle
    {
        private static readonly MethodInfo HasValue = AccessTools.PropertyGetter(typeof(LocalTargetInfo?), "HasValue");
        private static readonly MethodInfo Value = AccessTools.PropertyGetter(typeof(LocalTargetInfo?), "Value");
        private static readonly MethodInfo Different = AccessTools.Method(typeof(LocalTargetInfo), "op_Inequality");
        private static readonly MethodInfo IsValid = AccessTools.PropertyGetter(typeof(LocalTargetInfo), "IsValid");
        private static readonly FieldInfo Props = AccessTools.Field(typeof(HediffComp), "props");
        private static readonly FieldInfo Health = AccessTools.Field(typeof(Pawn), "health");
        private static readonly FieldInfo Hediffs = AccessTools.Field(typeof(Pawn_HealthTracker), "hediffSet");

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            MethodBase original, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            try
            {
                if (generator == null || original?.IsStatic != false
                    || !typeof(HediffComp).IsAssignableFrom(original.DeclaringType)
                    || codes.Count > 256 || original.GetMethodBody()?.ExceptionHandlingClauses.Count != 0)
                    return codes;
                // Do not discard labelled nops: unsupported compiler layouts retain their original code.
                if (codes.Exists(c => c.opcode == OpCodes.Nop || c.blocks.Count != 0)) return codes;
                if (!Match(codes, original, out int cleanup, out int pawn, out int weapon, out int target)) return codes;
                MethodInfo begin = AccessTools.Method(typeof(RimKataWeaponEffects), "BeginTargetChange");
                MethodInfo end = begin == null ? null : AccessTools.Method(begin.ReturnType, nameof(IDisposable.Dispose), Type.EmptyTypes);
                if (begin == null || end == null || !begin.ReturnType.IsValueType) return codes;
                LocalBuilder scope = generator.DeclareLocal(begin.ReturnType);
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                first.labels.AddRange(codes[cleanup].labels);
                var call = new CodeInstruction(codes[cleanup]);
                call.labels.Clear();
                codes.RemoveAt(cleanup);
                codes.InsertRange(cleanup, new[]
                {
                    first, LoadLocal(pawn), LoadLocal(weapon),
                    LoadLocal(target), new CodeInstruction(OpCodes.Call, begin),
                    new CodeInstruction(OpCodes.Stloc, scope), call,
                    new CodeInstruction(OpCodes.Ldloca, scope), new CodeInstruction(OpCodes.Call, end)
                });
                // The enclosing effect scope finalizer also restores this marker if the original helper throws.
            }
            catch { /* Unrecognized startup IL must not change the original manager. */ }
            return codes;
        }

        private static bool Match(List<CodeInstruction> c, MethodBase original,
            out int cleanup, out int pawn, out int weapon, out int target)
        {
            cleanup = pawn = weapon = target = -1;
            int reader = -1;
            for (int i = 0; i < c.Count; i++)
                if (IsRole(c[i], RimKataWeaponStateObservers.ObserverRole.Target))
                { if (reader >= 0) return false; reader = i; }
            if (reader < 3 || reader + 1 >= c.Count || c[reader - 3].opcode != OpCodes.Ldarg_0
                || (pawn = Local(c[reader - 2], false, false)) < 0
                || (weapon = Local(c[reader - 1], false, false)) < 0
                || (target = Local(c[reader + 1], true, false)) < 0) return false;
            var locals = original.GetMethodBody().LocalVariables;
            if (!TypeAt(locals, pawn, typeof(Pawn)) || !TypeAt(locals, weapon, typeof(ThingWithComps))
                || !TypeAt(locals, target, typeof(LocalTargetInfo?))) return false;
            int p = reader + 2;
            // Optional cooldown early return, with the same owner and weapon as the target reader.
            if (p + 5 < c.Count && IsRole(c[p + 3], RimKataWeaponStateObservers.ObserverRole.Cooldown))
            {
                if (c[p].opcode != OpCodes.Ldarg_0 || !Load(c[p + 1], pawn) || !Load(c[p + 2], weapon)
                    || !Branch(c, p + 4, p + 6, false) || c[p + 5].opcode != OpCodes.Ret) return false;
                p += 6;
            }
            if (p + 19 >= c.Count || Local(c[p], false, true) != target || !Calls(c[p + 1], HasValue)
                || !Branch(c, p + 2, p + 4, true) || c[p + 3].opcode != OpCodes.Ret) return false;
            p += 4;
            if (c[p].opcode != OpCodes.Ldarg_0 || c[p + 1].opcode != OpCodes.Ldflda
                || !(c[p + 1].operand is FieldInfo previous) || previous.IsStatic
                || previous.FieldType != typeof(LocalTargetInfo?) || !Calls(c[p + 2], HasValue)) return false;
            int previousGuard = p + 3;
            p += 4;
            if (c[p].opcode != OpCodes.Ldarg_0 || c[p + 1].opcode != OpCodes.Ldflda
                || !Equals(c[p + 1].operand, previous) || !Calls(c[p + 2], Value)
                || Local(c[p + 3], false, true) != target || !Calls(c[p + 4], Value)) return false;
            int value = Local(c[p + 5], true, false);
            if (!TypeAt(locals, value, typeof(LocalTargetInfo)) || !Load(c[p + 6], value)
                || !Calls(c[p + 7], Different) || !Branch(c, p + 8, p + 12, true)
                || Local(c[p + 9], false, true) != value || !Calls(c[p + 10], IsValid)) return false;
            int validGuard = p + 11, start = p + 12;
            if (!CleanupArguments(c, start, pawn, out cleanup)
                || !PureCleanup(c[cleanup].operand as MethodInfo)) return false;
            int update = cleanup + 1, unchanged = update + 4;
            if (unchanged + 4 != c.Count || !Update(c, update, target, previous)
                || !Update(c, unchanged, target, previous)
                || !Branch(c, previousGuard, unchanged, false) || !Branch(c, validGuard, unchanged, true)) return false;
            // Other paths must not enter the marked call with a different cleanup reason.
            for (int i = 0; i < c.Count; i++)
            {
                if (!(c[i].operand is Label label))
                { if (c[i].operand is Label[]) return false; continue; }
                int destination = c.FindIndex(code => code.labels.Contains(label));
                if (destination >= start && destination <= cleanup
                    && i != p + 8 && (i < start || i > cleanup)) return false;
            }
            return true;
        }

        private static bool CleanupArguments(List<CodeInstruction> c, int start, int pawn, out int call)
        {
            call = -1;
            if (start + 5 >= c.Count || c[start].opcode != OpCodes.Ldarg_0 || !Load(c[start + 1], pawn)
                || c[start + 2].opcode != OpCodes.Ldarg_0 || !IsCall(c[start + 3])
                || !PropertyReader(c[start + 3].operand as MethodInfo)) return false;
            int field = start + 4;
            if (c[field].opcode == OpCodes.Dup)
            {
                if (start + 10 >= c.Count || !Branch(c, start + 5, start + 9, true)
                    || c[start + 6].opcode != OpCodes.Pop || c[start + 7].opcode != OpCodes.Ldnull
                    || !Jump(c, start + 8, start + 10)) return false;
                field = start + 9;
            }
            if (c[field].opcode != OpCodes.Ldfld || !(c[field].operand is FieldInfo definitions)
                || definitions.FieldType != typeof(List<HediffDef>) || !IsCall(c[field + 1])) return false;
            call = field + 1;
            return true;
        }

        private static bool PropertyReader(MethodInfo method)
        {
            if (method == null || method.IsStatic || method.GetParameters().Length != 0
                || !typeof(HediffCompProperties).IsAssignableFrom(method.ReturnType) || !ScopeHooksOnly(method)) return false;
            List<CodeInstruction> c = PatchProcessor.GetCurrentInstructions(method);
            return c.Count == 4 && c[0].opcode == OpCodes.Ldarg_0
                && c[1].opcode == OpCodes.Ldfld && Equals(c[1].operand, Props)
                && c[2].opcode == OpCodes.Castclass && Equals(c[2].operand, method.ReturnType)
                && c[3].opcode == OpCodes.Ret;
        }

        private static bool PureCleanup(MethodInfo method)
        {
            if (method == null || method.IsStatic || method.ReturnType != typeof(void) || !ScopeHooksOnly(method)) return false;
            var args = method.GetParameters();
            if (args.Length != 2 || args[0].ParameterType != typeof(Pawn)
                || args[1].ParameterType != typeof(List<HediffDef>)) return false;
            int reads = 0, removes = 0;
            List<CodeInstruction> codes = PatchProcessor.GetCurrentInstructions(method);
            if (codes.Count > 128) return false;
            foreach (CodeInstruction code in codes)
            {
                if (IsCall(code))
                {
                    if (!(code.operand is MethodInfo m)) return false;
                    if ((m.DeclaringType == typeof(HediffSet) || m.DeclaringType == typeof(RimKataWeaponEffects))
                        && m.Name == nameof(HediffSet.GetFirstHediffOfDef)) reads++;
                    else if ((m.DeclaringType == typeof(Pawn_HealthTracker) || m.DeclaringType == typeof(RimKataWeaponEffects))
                        && m.Name == nameof(Pawn_HealthTracker.RemoveHediff)) removes++;
                    else if (!CollectionRead(m)) return false;
                }
                else if (code.opcode == OpCodes.Ldfld)
                { if (!Equals(code.operand, Health) && !Equals(code.operand, Hediffs)) return false; }
                else if (code.opcode == OpCodes.Constrained)
                { if (!Equals(code.operand, typeof(List<HediffDef>.Enumerator))) return false; }
                else if (!CleanupInstruction(code)) return false;
            }
            return reads == 1 && removes == 1;
        }

        private static bool CollectionRead(MethodInfo m)
            => (m.DeclaringType == typeof(List<HediffDef>) && m.Name == "GetEnumerator")
                || (m.DeclaringType == typeof(List<HediffDef>.Enumerator)
                    && (m.Name == "get_Current" || m.Name == "MoveNext"))
                || (m.DeclaringType == typeof(IDisposable) && m.Name == nameof(IDisposable.Dispose));
        private static bool ScopeHooksOnly(MethodInfo method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            return patches == null || (ScopeHooksOnly(patches.Prefixes) && ScopeHooksOnly(patches.Postfixes)
                && ScopeHooksOnly(patches.Transpilers) && ScopeHooksOnly(patches.Finalizers)
                && ScopeHooksOnly(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPrefixes"))
                && ScopeHooksOnly(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPostfixes"))
                && ScopeHooksOnly(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerFinalizers")));
        }
        private static bool ScopeHooksOnly(IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches)
                if (patch.PatchMethod?.DeclaringType != typeof(RimKataWeaponEffects)) return false;
            return true;
        }
        private static bool CleanupInstruction(CodeInstruction c)
            => Local(c, false, false) >= 0 || Local(c, true, false) >= 0 || Local(c, false, true) >= 0
                || c.opcode == OpCodes.Ldarg_1 || c.opcode == OpCodes.Ldarg_2
                || c.opcode == OpCodes.Ldnull || c.opcode == OpCodes.Ldc_I4_0
                || c.opcode == OpCodes.Dup || c.opcode == OpCodes.Pop || c.opcode == OpCodes.Ret
                || c.opcode == OpCodes.Endfinally || c.opcode == OpCodes.Leave || c.opcode == OpCodes.Leave_S
                || c.opcode.FlowControl == FlowControl.Branch || c.opcode.FlowControl == FlowControl.Cond_Branch;
        private static bool TypeAt(IList<LocalVariableInfo> locals, int index, Type type)
            => index >= 0 && index < locals.Count && locals[index].LocalType == type;
        private static bool Update(List<CodeInstruction> c, int p, int target, FieldInfo field)
            => c[p].opcode == OpCodes.Ldarg_0 && Load(c[p + 1], target)
                && c[p + 2].opcode == OpCodes.Stfld && Equals(c[p + 2].operand, field) && c[p + 3].opcode == OpCodes.Ret;
        private static bool IsRole(CodeInstruction c, RimKataWeaponStateObservers.ObserverRole role)
            => IsCall(c) && c.operand is MethodInfo m
                && RimKataWeaponStateObservers.Connections.TryGetValue(m, out var actual) && actual == role;
        private static bool IsCall(CodeInstruction c) => c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt;
        private static bool Calls(CodeInstruction c, MethodInfo m) => IsCall(c) && Equals(c.operand, m);
        private static bool Load(CodeInstruction c, int index) => Local(c, false, false) == index;
        private static CodeInstruction LoadLocal(int index) => new CodeInstruction(OpCodes.Ldloc, (short)index);
        private static bool Branch(List<CodeInstruction> c, int from, int to, bool whenTrue)
            => (c[from].opcode == (whenTrue ? OpCodes.Brtrue : OpCodes.Brfalse)
                || c[from].opcode == (whenTrue ? OpCodes.Brtrue_S : OpCodes.Brfalse_S)) && Destination(c, from, to);
        private static bool Jump(List<CodeInstruction> c, int from, int to)
            => (c[from].opcode == OpCodes.Br || c[from].opcode == OpCodes.Br_S) && Destination(c, from, to);
        private static bool Destination(List<CodeInstruction> c, int from, int to)
            => to >= 0 && to < c.Count && c[from].operand is Label label && c[to].labels.Contains(label);
        private static int Local(CodeInstruction c, bool store, bool address)
        {
            OpCode op = c.opcode;
            if (!address)
            {
                if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
                if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
                if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
                if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            }
            if (op != (address ? OpCodes.Ldloca : store ? OpCodes.Stloc : OpCodes.Ldloc)
                && op != (address ? OpCodes.Ldloca_S : store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            return c.operand is LocalBuilder builder ? builder.LocalIndex
                : c.operand is LocalVariableInfo variable ? variable.LocalIndex : Convert.ToInt32(c.operand);
        }
    }
}
