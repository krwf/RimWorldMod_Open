using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMeleeAnimationAttackBridge
    {
        private static bool enabled;
        private static bool failed;
        private static Action consumeHitNotification;

        internal static void Apply(Harmony harmony)
        {
            if (enabled || failed) return;
            Type controller = RimKataActiveModTypes.Find("AM.Idle.IdleControllerComp");
            if (controller == null) return;
            try
            {
                MethodInfo notification = AccessTools.Method(controller, "NotifyPawnDidMeleeAttack",
                    new[] { typeof(Thing), typeof(Verb_MeleeAttack) });
                MethodInfo completion = AccessTools.Method(typeof(RimKataDualWeaponController),
                    nameof(RimKataDualWeaponController.CompleteNativeAttack));
                if (notification == null || notification.ReturnType != typeof(void)
                    || !typeof(ThingComp).IsAssignableFrom(controller) || completion == null)
                    throw new MissingMethodException("Melee Animation attack notification did not match.");

                FieldInfo hitTarget = AccessTools.Field(RimKataActiveModTypes.Find(
                    "AM.Patches.Patch_Verb_MeleeAttack_ApplyMeleeDamageToTarget"), "lastTarget");
                if (hitTarget == null || !hitTarget.IsStatic || hitTarget.FieldType != typeof(Thing))
                    throw new MissingFieldException("Melee Animation hit notification did not match.");
                consumeHitNotification = Expression.Lambda<Action>(Expression.Assign(
                    Expression.Field(null, hitTarget), Expression.Constant(null, typeof(Thing)))).Compile();

                RimKataStartupPatches.Patch(harmony, completion, postfix: Hook(nameof(CompleteAttack)));
                RimKataStartupPatches.Patch(harmony, notification, prefix: Hook(nameof(BeforeMeleeNotification)));
                RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.Notify_MeleeAttackOn)),
                    prefix: Hook(nameof(BeforeBodyJitter)));
                enabled = true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        private static HarmonyMethod Hook(string name)
            => new HarmonyMethod(typeof(RimKataMeleeAnimationAttackBridge), name);

        private static bool BeforeMeleeNotification(object __instance, Thing __0, Verb_MeleeAttack __1)
        {
            if (!enabled || failed) return true;
            try
            {
                Pawn pawn = (__instance as ThingComp)?.parent as Pawn;
                Verb verb = RimKataFireContext.ActiveVerb;
                ThingWithComps notifiedWeapon = pawn == RimKataFireContext.Shooter
                    ? verb?.EquipmentSource : __1?.EquipmentSource;
                if (notifiedWeapon?.def.IsRangedWeapon == true
                    && RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn, out ThingWithComps registered)
                    && (notifiedWeapon == pawn.equipment?.Primary || notifiedWeapon == registered))
                {
                    consumeHitNotification();
                    return false;
                }
                if (pawn == null || pawn != RimKataFireContext.Shooter
                    || verb == null || !verb.IsMeleeAttack
                    || !RimKataNativeAttack.OwnsActiveMelee(verb)
                    || !RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn,
                        out ThingWithComps secondary)
                    || secondary == null || verb.EquipmentSource != secondary)
                    return true;

                if (!RimKataMeleeAnimationReplay.CanHandle(pawn, secondary)) return true;
                RimKataMeleeAnimationReplay.NotifyAttack(pawn, secondary, verb.CurrentTarget, int.MaxValue);
                // MA's per-hit marker must be consumed or this hit can mark a later primary miss as a hit.
                consumeHitNotification();
                return false;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
        }

        private static bool BeforeBodyJitter(Pawn ___pawn)
        {
            if (!enabled || failed) return true;
            Pawn pawn = ___pawn;
            Verb verb = RimKataFireContext.ActiveVerb;
            if (pawn != RimKataFireContext.Shooter || verb?.IsMeleeAttack != true
                || !RimKataNativeAttack.OwnsActiveMelee(verb)
                || !RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn, out ThingWithComps secondary)
                || secondary?.def.IsMeleeWeapon != true || verb.EquipmentSource != secondary) return true;
            // The vanilla notification lunges the whole pawn; MA animates the offhand slot independently.
            return !RimKataMeleeAnimationReplay.CanHandle(pawn, secondary);
        }

        private static void CompleteAttack(RimKataNativeAttack attack, bool acted)
        {
            if (!enabled || failed || !acted || attack == null) return;
            try
            {
                Pawn pawn = attack.pawn;
                ThingWithComps weapon = attack.weapon;
                RimKataWeaponCycleState cycle = attack.cycle;
                if (pawn == null || weapon == null || weapon.def.IsRangedWeapon || cycle == null || attack.state == null
                    || cycle != attack.state.secondaryWeaponCycle || cycle.weapon != weapon
                    || attack.verb?.IsMeleeAttack != true || attack.verb.EquipmentSource != weapon
                    || !RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn,
                        out ThingWithComps secondary)
                    || secondary != weapon || !RimKataMeleeAnimationReplay.CanHandle(pawn, weapon))
                    return;

                // acted includes resolved misses, which still need their completed swing animation.
                RimKataMeleeAnimationReplay.NotifyAttack(pawn, weapon, attack.target,
                    cycle.cooldownTicksRemaining);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void Fail(Exception exception)
        {
            enabled = false;
            if (failed) return;
            failed = true;
            Log.Warning("[RimKata] Melee Animation secondary attack notifications disabled after an error: "
                + exception);
        }
    }
}
