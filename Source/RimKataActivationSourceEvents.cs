using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataActivationSourceEvents
    {
        internal struct ChangeScope
        {
            internal bool opened;
            internal Pawn previousPawn;
            internal bool previousTraits, previousSkills;
        }

        [ThreadStatic] private static Pawn changingPawn;
        [ThreadStatic] private static bool traitsChanged, skillsChanged;

        internal static bool SkillSourcesEnabled
            => RimKataPersonalActivation.EnabledFor(SkillDefOf.Shooting)
                || RimKataPersonalActivation.EnabledFor(SkillDefOf.Melee);

        internal static ChangeScope Begin(Pawn pawn, bool traits, bool skills)
        {
            if (pawn == null) return default;
            if (changingPawn == pawn)
            {
                traitsChanged |= traits;
                skillsChanged |= skills;
                return default;
            }
            var scope = new ChangeScope
            {
                opened = true,
                previousPawn = changingPawn,
                previousTraits = traitsChanged,
                previousSkills = skillsChanged
            };
            changingPawn = pawn;
            traitsChanged = traits;
            skillsChanged = skills;
            return scope;
        }

        internal static void Finish(ref ChangeScope scope, bool refresh)
        {
            if (!scope.opened) return;
            Pawn pawn = changingPawn;
            bool traits = traitsChanged, skills = skillsChanged;
            changingPawn = scope.previousPawn;
            traitsChanged = scope.previousTraits;
            skillsChanged = scope.previousSkills;
            scope.opened = false;
            if (refresh)
                RimKataEligibilityCache.RefreshPersonalSources(pawn, traits, skills);
        }

        // Learn and EnsureMinLevelWithMargin write the field directly, bypassing Level's setter.
        internal static void StoreLevel(SkillRecord record, int value)
        {
            int previous = record.levelInt;
            record.levelInt = value;
            if (previous != value) NotifySkillChanged(record);
        }

        internal static void NotifySkillChanged(SkillRecord record)
        {
            if (!RimKataPersonalActivation.EnabledFor(record.def)) return;
            Pawn pawn = record.Pawn;
            if (pawn == null) return;
            if (changingPawn == pawn) skillsChanged = true;
            else RimKataEligibilityCache.RefreshPersonalSources(pawn, false, true);
        }
    }

    [HarmonyPatch]
    internal static class Patch_SkillRecord_RimKataActivationLevel
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertySetter(typeof(SkillRecord), nameof(SkillRecord.Level));
            yield return AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Learn));
            yield return AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.EnsureMinLevelWithMargin));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo level = AccessTools.Field(typeof(SkillRecord), nameof(SkillRecord.levelInt));
            MethodInfo store = AccessTools.Method(typeof(RimKataActivationSourceEvents),
                nameof(RimKataActivationSourceEvents.StoreLevel));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, level))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = store;
                }
                yield return instruction;
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_TraitSet_RimKataActivationSources
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TraitSet), nameof(TraitSet.GainTrait));
            yield return AccessTools.Method(typeof(TraitSet), nameof(TraitSet.RemoveTrait));
            yield return AccessTools.Method(typeof(TraitSet), nameof(TraitSet.RecalculateSuppression));
        }

        private static void Prefix(Pawn ___pawn, out RimKataActivationSourceEvents.ChangeScope __state)
            => __state = RimKataPersonalActivation.TraitsEnabled || RimKataPersonalActivation.SkillsEnabled
                ? RimKataActivationSourceEvents.Begin(___pawn, true, true) : default;

        private static void Postfix(Pawn ___pawn, MethodBase __originalMethod,
            ref RimKataActivationSourceEvents.ChangeScope __state)
        {
            // Gain/Remove already clear these caches later in the same outer operation.
            if (__state.opened && RimKataPersonalActivation.SkillsEnabled
                && __originalMethod.Name == nameof(TraitSet.RecalculateSuppression))
            {
                ___pawn?.skills?.Notify_SkillDisablesChanged();
                ___pawn?.skills?.DirtyAptitudes();
            }
            RimKataActivationSourceEvents.Finish(ref __state, true);
        }

        private static Exception Finalizer(Exception __exception,
            ref RimKataActivationSourceEvents.ChangeScope __state)
        {
            RimKataActivationSourceEvents.Finish(ref __state, false);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PawnSkillTracker_RimKataActivationSources
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Pawn_SkillTracker), nameof(Pawn_SkillTracker.DirtyAptitudes));
            yield return AccessTools.Method(typeof(Pawn_SkillTracker), nameof(Pawn_SkillTracker.Notify_SkillDisablesChanged));
        }

        private static void Prefix(Pawn ___pawn, out RimKataActivationSourceEvents.ChangeScope __state)
            => __state = RimKataActivationSourceEvents.SkillSourcesEnabled
                ? RimKataActivationSourceEvents.Begin(___pawn, false, true) : default;

        private static void Postfix(ref RimKataActivationSourceEvents.ChangeScope __state)
            => RimKataActivationSourceEvents.Finish(ref __state, true);

        private static Exception Finalizer(Exception __exception,
            ref RimKataActivationSourceEvents.ChangeScope __state)
        {
            RimKataActivationSourceEvents.Finish(ref __state, false);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class Patch_SkillRecord_RimKataActivationSources
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.DirtyAptitudes));
            yield return AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Notify_SkillDisablesChanged));
        }

        private static void Postfix(SkillRecord __instance)
            => RimKataActivationSourceEvents.NotifySkillChanged(__instance);
    }
}
