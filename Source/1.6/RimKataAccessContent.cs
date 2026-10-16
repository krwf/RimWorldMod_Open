using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Graphic_RimKataStackCount : Graphic_StackCount
    {
        public override Graphic SubGraphicFor(Thing thing)
        {
            if (thing == null || subGraphics == null || subGraphics.Length == 0)
            {
                return base.SubGraphicFor(thing);
            }

            int index = Mathf.Clamp(thing.stackCount - 1, 0, subGraphics.Length - 1);
            return subGraphics[index];
        }
    }

    public sealed class CompProperties_RimKataAccessAbility : CompProperties_AbilityEffect
    {
        public CompProperties_RimKataAccessAbility()
        {
            compClass = typeof(CompAbilityEffect_RimKataAccess);
        }
    }

    public sealed class CompAbilityEffect_RimKataAccess : CompAbilityEffect
    {
        public override bool ShouldHideGizmo => true;
    }

    internal static class RimKataPsylinkAbilityPredicateResolver
    {
        private const string PredicateNamePrefix = "<" + nameof(Hediff_Psylink.TryGiveAbilityOfLevel) + ">b__";
        private const BindingFlags NestedTypeFlags = BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags MethodFlags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        public static bool TryResolve(Type argumentType, out MethodBase target)
        {
            target = null;
            int matchCount = 0;
            Type[] nestedTypes = typeof(Hediff_Psylink).GetNestedTypes(NestedTypeFlags);
            for (int typeIndex = 0; typeIndex < nestedTypes.Length; typeIndex++)
            {
                Type nestedType = nestedTypes[typeIndex];
                if (!nestedType.IsDefined(typeof(CompilerGeneratedAttribute), false))
                {
                    continue;
                }

                MethodInfo[] methods = nestedType.GetMethods(MethodFlags);
                for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    MethodInfo method = methods[methodIndex];
                    ParameterInfo[] parameters = method.GetParameters();
                    if (!method.Name.StartsWith(PredicateNamePrefix, StringComparison.Ordinal)
                        || method.ReturnType != typeof(bool)
                        || parameters.Length != 1
                        || parameters[0].ParameterType != argumentType)
                    {
                        continue;
                    }

                    matchCount++;
                    if (matchCount == 1)
                    {
                        target = method;
                    }
                }
            }

            if (matchCount == 1)
            {
                return true;
            }

            target = null;
            Log.Error(
                $"[RimKata] Expected exactly one {nameof(Hediff_Psylink.TryGiveAbilityOfLevel)} "
                + $"predicate accepting {argumentType.FullName}, but found {matchCount}; this patch will be skipped.");
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_HediffPsylink_RimKataPExistingAbility
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            return target != null
                || RimKataPsylinkAbilityPredicateResolver.TryResolve(typeof(Ability), out target);
        }

        public static MethodBase TargetMethod()
        {
            return target;
        }

        public static void Postfix(Ability __0, ref bool __result)
        {
            if (__0?.def == RimKataDefOf.RimKata_P)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_HediffPsylink_RimKataPNaturalCandidate
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            return target != null
                || RimKataPsylinkAbilityPredicateResolver.TryResolve(typeof(AbilityDef), out target);
        }

        public static MethodBase TargetMethod()
        {
            return target;
        }

        public static void Postfix(AbilityDef __0, ref bool __result)
        {
            if (__0 == RimKataDefOf.RimKata_P)
            {
                __result = false;
            }
        }
    }
}
