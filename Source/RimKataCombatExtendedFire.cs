using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Keep CE's native shot, modes and ballistic calculation. Only bridge the
    // state/timing inputs owned by a qualified RimKata weapon cycle.
    internal static class RimKataCombatExtendedFire
    {
        private static Type launchType;
        private static Type shootType;
        private static Func<Verb, int> shotsPerBurst;
        private static Func<Verb, int> fireMode;
        private sealed class BurstSelection
        {
            internal VerbProperties properties;
            internal int mode;
            internal int count;
        }
        private static readonly ConditionalWeakTable<Verb, BurstSelection> BurstSelections =
            new ConditionalWeakTable<Verb, BurstSelection>();
        private static Func<Verb, bool> shouldAim;
        private static Func<Verb, bool> isAiming;
        private static Action<Verb, bool> setAiming;
        private static FloatField aimingAccuracy;
        private static FloatField accuracyFactor;
        private static FloatField sway;
        private static FloatField spread;
        private static FloatField circularMiss;
        private static FloatField indirectShift;
        private static FloatField visibilityShift;
        private static FloatField leadDistance;

        internal static void Apply(Harmony harmony)
        {
            Type launcher = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
            Type shooter = AccessTools.TypeByName("CombatExtended.Verb_ShootCE");
            if (launcher == null || shooter == null) return;
            MethodInfo shots = AccessTools.PropertyGetter(launcher, "ShotsPerBurst");
            MethodInfo shootShots = AccessTools.PropertyGetter(shooter, "ShotsPerBurst");
            MethodInfo aim = AccessTools.PropertyGetter(shooter, "ShouldAim");
            FieldInfo aiming = AccessTools.Field(shooter, "_isAiming");
            MethodInfo report = AccessTools.Method(launcher, "ShiftVecReportFor",
                new[] { typeof(LocalTargetInfo), typeof(IntVec3) });
            MethodInfo swayVector = AccessTools.Method(launcher, "GetSwayVec",
                new[] { typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
            Type reportType = AccessTools.TypeByName("CombatExtended.ShiftVecReport");
            if (shots == null || shootShots == null || aim == null || aiming?.FieldType != typeof(bool)
                || report?.ReturnType != reportType || reportType == null || swayVector == null)
                throw new InvalidOperationException("CE firing API does not match the supported shape.");

            shotsPerBurst = Getter<int>(shots);
            MethodInfo modes = AccessTools.PropertyGetter(launcher, "CompFireModes");
            MethodInfo mode = AccessTools.PropertyGetter(modes.ReturnType, "CurrentFireMode");
            MethodInfo setMode = AccessTools.PropertySetter(modes.ReturnType, "CurrentFireMode");
            MethodInfo toggleMode = AccessTools.Method(modes.ReturnType, "ToggleFireMode", Type.EmptyTypes);
            if (setMode == null || toggleMode == null)
                throw new InvalidOperationException("CE fire-mode API does not match.");
            ParameterExpression modeVerb = Expression.Parameter(typeof(Verb), "verb");
            ParameterExpression comp = Expression.Variable(modes.ReturnType, "modes");
            fireMode = Expression.Lambda<Func<Verb, int>>(Expression.Block(new[] { comp },
                Expression.Assign(comp, Expression.Call(Expression.Convert(modeVerb, launcher), modes)),
                Expression.Condition(Expression.Equal(comp, Expression.Constant(null, modes.ReturnType)),
                    Expression.Constant(-1), Expression.Convert(Expression.Call(comp, mode), typeof(int)))), modeVerb).Compile();
            shouldAim = Getter<bool>(aim);
            ParameterExpression verb = Expression.Parameter(typeof(Verb), "verb");
            ParameterExpression value = Expression.Parameter(typeof(bool), "value");
            MemberExpression field = Expression.Field(Expression.Convert(verb, shooter), aiming);
            isAiming = Expression.Lambda<Func<Verb, bool>>(field, verb).Compile();
            setAiming = Expression.Lambda<Action<Verb, bool>>(
                Expression.Assign(field, value), verb, value).Compile();
            aimingAccuracy = new FloatField(reportType, "aimingAccuracy");
            accuracyFactor = new FloatField(reportType, "accuracyFactorInt");
            sway = new FloatField(reportType, "swayDegrees");
            spread = new FloatField(reportType, "spreadDegrees");
            circularMiss = new FloatField(reportType, "circularMissRadius");
            indirectShift = new FloatField(reportType, "indirectFireShift");
            visibilityShift = new FloatField(reportType, "visibilityShiftInt");
            leadDistance = new FloatField(reportType, "leadDistInt");
            launchType = launcher;
            shootType = shooter;

            HarmonyMethod countPatch = new HarmonyMethod(typeof(RimKataCombatExtendedFire), nameof(ShotsPostfix));
            harmony.Patch(shots, postfix: countPatch);
            if (shootShots != shots) harmony.Patch(shootShots, postfix: countPatch);
            harmony.Patch(report, postfix: new HarmonyMethod(
                typeof(RimKataCombatExtendedFire), nameof(ReportPostfix)));
            harmony.Patch(swayVector,
                prefix: new HarmonyMethod(typeof(RimKataCombatExtendedFire), nameof(SwayPrefix)),
                postfix: new HarmonyMethod(typeof(RimKataCombatExtendedFire), nameof(SwayPostfix)));
            var modePatch = new HarmonyMethod(typeof(RimKataCombatExtendedFire), nameof(ModeChangedPostfix));
            harmony.Patch(setMode, postfix: modePatch);
            harmony.Patch(toggleMode, postfix: modePatch);
            RimKataCombatExtendedPrepared.Apply(harmony);
            RimKataCombatExtendedNativeAttack.Apply(harmony);
        }

        private static Func<Verb, T> Getter<T>(MethodInfo method)
        {
            ParameterExpression verb = Expression.Parameter(typeof(Verb), "verb");
            return Expression.Lambda<Func<Verb, T>>(
                Expression.Call(Expression.Convert(verb, method.DeclaringType), method), verb).Compile();
        }

        internal static bool IsVerb(Verb verb) => launchType != null && launchType.IsInstanceOfType(verb);

        internal static int OriginalBurstCount(Verb verb)
        {
            if (!IsVerb(verb)) return Math.Max(1, verb?.BurstShotCount ?? 1);
            VerbProperties previous = verb.verbProps;
            if (RimKataPreparedWeaponData.TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
                verb.verbProps = prepared.OriginalProperties;
            try
            {
                int mode = fireMode(verb);
                BurstSelection selection = BurstSelections.GetOrCreateValue(verb);
                if (selection.properties != verb.verbProps || selection.mode != mode)
                {
                    // CE may roll a weapon trait inside this getter. Sample only
                    // when preparing a weapon/mode, never during validity checks.
                    selection.count = Math.Max(1, shotsPerBurst(verb));
                    selection.properties = verb.verbProps;
                    selection.mode = mode;
                }
                return selection.count;
            }
            finally { verb.verbProps = previous; }
        }

        internal static float OriginalWarmup(Verb verb, RimKataPreparedWeaponValues prepared)
        {
            if (!IsVerb(verb)) return prepared.OriginalWarmupSeconds;
            VerbProperties previous = verb.verbProps;
            verb.verbProps = prepared.OriginalProperties;
            try { return Mathf.Max(0f, verb.WarmupTime); }
            finally { verb.verbProps = previous; }
        }

        internal static bool PreparedModeIsCurrent(Verb verb, RimKataPreparedWeaponValues prepared)
            => !IsVerb(verb) || (BurstSelections.TryGetValue(verb, out BurstSelection selection)
                && selection.properties == prepared.OriginalProperties && selection.mode == fireMode(verb));

        private static void ShotsPostfix(Verb __instance, ref int __result)
        {
            if (RimKataPreparedWeaponData.TryGetPrepared(__instance, out _)
                && RimKataEligibilityCache.IsCachedQualifiedPawn(__instance.CasterPawn))
                __result = 1;
        }

        private static void ModeChangedPostfix(ThingComp __instance)
        {
            if (__instance.parent?.ParentHolder is Pawn_EquipmentTracker equipment
                && RimKataEligibilityCache.IsCachedQualifiedPawn(equipment.pawn))
                RimKataDualWeaponController.InvalidateWeaponBindings(equipment.pawn);
        }

        internal static bool SuspendExtraAim(Verb verb, out int ticks)
        {
            ticks = 0;
            if (shootType == null || !shootType.IsInstanceOfType(verb) || !isAiming(verb)
                || !(verb.CasterPawn?.stances?.curStance is Stance_Warmup warmup)
                || warmup.verb != verb) return false;
            ticks = Math.Max(1, warmup.ticksLeft);
            // CE otherwise cancels this aim because the pawn uses RimKata's
            // shared body stance. The weapon request now owns this exact delay.
            setAiming(verb, false);
            return true;
        }

        internal static bool BeginsExtraAim(Verb verb)
            => shootType != null && shootType.IsInstanceOfType(verb) && !isAiming(verb) && shouldAim(verb);

        internal static bool ContinueExtraAim(Verb verb) => shouldAim(verb);
        internal static void ResumeExtraAim(Verb verb) => setAiming(verb, true);
        internal static void ClearExtraAim(Verb verb)
        {
            if (shootType != null && shootType.IsInstanceOfType(verb)) setAiming(verb, false);
        }

        private static void ReportPostfix(Verb __instance, object __result)
        {
            if (__result == null || RimKataFireContext.ActiveVerb != __instance) return;
            if (RimKataFireContext.CloseMeleeResolution)
            {
                // The close attack has already rolled RimKata melee accuracy.
                // Its miss target is selected before CE starts the native shot.
                aimingAccuracy.Set(__result, 1.5f);
                accuracyFactor.Set(__result, 0f);
                sway.Set(__result, 0f);
                spread.Set(__result, 0f);
                circularMiss.Set(__result, 0f);
                indirectShift.Set(__result, 0f);
                visibilityShift.Set(__result, 0f);
                leadDistance.Set(__result, 0f);
                return;
            }
            float multiplier = Mathf.Max(0f, RimKataFireContext.MovingAccuracyMultiplier
                * RimKataFireContext.InterceptionAccuracyBonusMultiplier
                * RimKataFireContext.SerumInterceptionMultiplier);
            if (Mathf.Approximately(multiplier, 1f)) return;
            // CE models dispersion, not a vanilla hit-percentage roll. Apply
            // the modifier to aiming skill/sway; retain native weapon spread,
            // cover, recoil and physical projectile collision.
            aimingAccuracy.Set(__result, Mathf.Clamp(aimingAccuracy.Get(__result) * multiplier, 0f, 1.5f));
            accuracyFactor.Set(__result, -1f);
            sway.Set(__result, Mathf.Min(180f, sway.Get(__result) / Mathf.Max(0.01f, multiplier)));
        }

        private static void SwayPrefix(float __0, float __1, out Vector2 __state)
            => __state = new Vector2(__0, __1);

        private static void SwayPostfix(Verb __instance, ref float __0, ref float __1, Vector2 __state)
        {
            if (RimKataFireContext.ActiveVerb != __instance) return;
            float multiplier = RimKataFireContext.MovingAccuracyMultiplier
                * RimKataFireContext.InterceptionAccuracyBonusMultiplier
                * RimKataFireContext.SerumInterceptionMultiplier;
            float scale = RimKataFireContext.CloseMeleeResolution ? 0f : 1f / Mathf.Max(0.01f, multiplier);
            __0 = __state.x + (__0 - __state.x) * scale;
            __1 = __state.y + (__1 - __state.y) * scale;
        }

        private sealed class FloatField
        {
            internal readonly Func<object, float> Get;
            internal readonly Action<object, float> Set;
            internal FloatField(Type type, string name)
            {
                FieldInfo info = AccessTools.Field(type, name);
                if (info?.FieldType != typeof(float))
                    throw new InvalidOperationException("CE aiming field changed: " + name);
                ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
                ParameterExpression value = Expression.Parameter(typeof(float), "value");
                MemberExpression field = Expression.Field(Expression.Convert(instance, type), info);
                Get = Expression.Lambda<Func<object, float>>(field, instance).Compile();
                Set = Expression.Lambda<Action<object, float>>(
                    Expression.Assign(field, value), instance, value).Compile();
            }
        }
    }
}
