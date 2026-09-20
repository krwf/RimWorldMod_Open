using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Install only alongside CE. Ordinary preparation and timing keep their
    // original instructions when CE is absent, including no adapter calls.
    internal static class RimKataCombatExtendedPrepared
    {
        private static readonly MethodInfo NativeBurstCount = AccessTools.PropertyGetter(
            typeof(Verb), nameof(Verb.BurstShotCount));
        private static readonly MethodInfo CeBurstCount = AccessTools.Method(
            typeof(RimKataCombatExtendedFire), nameof(RimKataCombatExtendedFire.OriginalBurstCount));
        private static readonly FieldInfo NativeWarmup = AccessTools.Field(
            typeof(RimKataPreparedWeaponValues), nameof(RimKataPreparedWeaponValues.OriginalWarmupSeconds));
        private static readonly MethodInfo CeWarmup = AccessTools.Method(
            typeof(RimKataCombatExtendedPrepared), nameof(ReadWarmup));

        internal static void Apply(Harmony harmony)
        {
            if (AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE") == null) return;
            Type data = typeof(RimKataPreparedWeaponData);
            HarmonyMethod burst = new HarmonyMethod(typeof(RimKataCombatExtendedPrepared), nameof(BurstCountTranspiler));
            harmony.Patch(AccessTools.Method(data, nameof(RimKataPreparedWeaponData.Bind)),
                prefix: new HarmonyMethod(typeof(RimKataCombatExtendedPrepared), nameof(BindPrefix)),
                transpiler: burst);
            harmony.Patch(AccessTools.Method(data, nameof(RimKataPreparedWeaponData.CanPrepareSingleShot)), transpiler: burst);
            harmony.Patch(AccessTools.Method(data, nameof(RimKataPreparedWeaponData.IsCurrent)),
                postfix: new HarmonyMethod(typeof(RimKataCombatExtendedPrepared), nameof(IsCurrentPostfix)),
                transpiler: burst);
            harmony.Patch(AccessTools.Method(data, nameof(RimKataPreparedWeaponData.GetOriginalBurstCount)), transpiler: burst);
            harmony.Patch(AccessTools.Method(typeof(RimKataCombatMath), "AdjustedWarmupTicks",
                new[] { typeof(Verb), typeof(float) }),
                transpiler: new HarmonyMethod(typeof(RimKataCombatExtendedPrepared), nameof(WarmupTranspiler)));
        }

        private static void BindPrefix(Verb verb)
        {
            if (RimKataPreparedWeaponData.TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared)
                && !RimKataCombatExtendedFire.PreparedModeIsCurrent(verb, prepared))
            {
                // Restore before ordinary binding samples the new mode. The CE
                // helper retains its existing per-verb/mode burst selection cache.
                RimKataPreparedWeaponData.Restore(verb);
            }
        }

        private static void IsCurrentPostfix(Verb verb, ref bool __result)
        {
            if (__result && RimKataPreparedWeaponData.TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
                __result = RimKataCombatExtendedFire.PreparedModeIsCurrent(verb, prepared);
        }

        private static IEnumerable<CodeInstruction> BurstCountTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            foreach (CodeInstruction code in codes)
            {
                if (!code.Calls(NativeBurstCount)) continue;
                code.opcode = OpCodes.Call;
                code.operand = CeBurstCount;
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata CE preparation could not locate its native burst-count input.");
            return codes;
        }

        private static IEnumerable<CodeInstruction> WarmupTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                if (code.opcode != OpCodes.Ldfld || !Equals(code.operand, NativeWarmup)) continue;
                // The prepared value is already on the stack at this field read.
                // Add the current verb while retaining the original branch labels.
                code.opcode = OpCodes.Ldarg_0;
                code.operand = null;
                codes.Insert(++i, new CodeInstruction(OpCodes.Call, CeWarmup));
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata CE preparation could not locate its original warmup input.");
            return codes;
        }

        private static float ReadWarmup(RimKataPreparedWeaponValues prepared, Verb verb)
            => RimKataCombatExtendedFire.OriginalWarmup(verb, prepared);
    }
}
