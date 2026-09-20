using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Optional Ratkin integration reads RimKata's attack cycle without exposing
    // that cycle through vanilla stances or Verb state.
    internal static class RimKataRatkinCompat
    {
        internal static void Apply(Harmony harmony)
        {
            Type compType = AccessTools.TypeByName("NewRatkin.HediffComp_RatHolicGun");
            if (compType == null)
                return;

            MethodInfo isPawnFiring = FindMethod(compType, "IsPawnFiring", typeof(bool));
            MethodInfo isPawnReloading = FindMethod(compType, "IsPawnReloading", typeof(bool));
            MethodInfo getCurrentAimingTarget = FindMethod(
                compType, "GetCurrentAimingTarget", typeof(LocalTargetInfo?));
            if (isPawnFiring == null || isPawnReloading == null || getCurrentAimingTarget == null)
            {
                Log.Warning("[RimKata] Ratkin RatHolic Gun API did not match; firing-state integration was not applied.");
                return;
            }

            harmony.Patch(isPawnFiring, postfix: Patch(nameof(IsPawnFiringPostfix)));
            harmony.Patch(isPawnReloading, postfix: Patch(nameof(IsPawnReloadingPostfix)));
            harmony.Patch(getCurrentAimingTarget, postfix: Patch(nameof(GetCurrentAimingTargetPostfix)));
        }

        private static MethodInfo FindMethod(Type type, string name, Type returnType)
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name != name || !method.IsPrivate || method.ContainsGenericParameters
                    || method.ReturnType != returnType)
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(Pawn)
                    && parameters[1].ParameterType == typeof(ThingWithComps))
                    return method;
            }
            return null;
        }

        private static HarmonyMethod Patch(string method)
        {
            return new HarmonyMethod(typeof(RimKataRatkinCompat), method);
        }

        private static void IsPawnFiringPostfix(Pawn __0, ThingWithComps __1, ref bool __result)
        {
            // Ratkin treats aiming, burst firing and post-shot cooldown as firing.
            if (!__result && RimKataRangedAttackStatus.TryGetState(__0, __1, out _))
                __result = true;
        }

        private static void IsPawnReloadingPostfix(Pawn __0, ThingWithComps __1, ref bool __result)
        {
            if (!__result
                && RimKataRangedAttackStatus.TryGetState(__0, __1, out RimKataRangedAttackState state)
                && state.Phase == RimKataRangedAttackPhase.Cooldown)
                __result = true;
        }

        private static void GetCurrentAimingTargetPostfix(
            Pawn __0, ThingWithComps __1, ref LocalTargetInfo? __result)
        {
            // Ratkin compares this target to reset aiming stacks, but skips that
            // comparison during cooldown through IsPawnReloading.
            if (!__result.HasValue
                && RimKataRangedAttackStatus.TryGetState(__0, __1, out RimKataRangedAttackState state)
                && state.Phase != RimKataRangedAttackPhase.Cooldown && state.Target.IsValid)
                __result = state.Target;
        }
    }
}
