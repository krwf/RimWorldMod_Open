using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataReactiveAttack
    {
        private static readonly Type AmmoType = RimKataActiveModTypes.Find("CombatExtended.CompAmmoUser");
        private static readonly MethodInfo CanFire = AmmoType == null ? null
            : AccessTools.PropertyGetter(AmmoType, "CanBeFiredNow");

        internal static bool Executing
            => RimKataNativeAttack.ReactiveRequest(RimKataFireContext.ActiveVerb)?.Executing == true;
        internal static bool OwnsVerb(Verb verb) => RimKataNativeAttack.ReactiveRequest(verb) != null;
        internal static bool OwnsWeapon(ThingWithComps weapon)
            => weapon != null && RimKataNativeAttack.ReactiveRequest(RimKataFireContext.ActiveVerb)?.weapon == weapon;

        internal static bool Queue(RimKataReactiveMotionState motion, bool secondary, Thing target, int dueTick)
        {
            Pawn pawn = motion.pawn;
            ThingWithComps weapon = secondary ? motion.secondaryWeapon : motion.primaryWeapon;
            RimKataWeaponCycleState cycle = secondary ? motion.combat.secondaryWeaponCycle : motion.combat.primaryWeaponCycle;
            RimKataNativeAttack request = secondary
                ? motion.secondaryAttack ?? (motion.secondaryAttack = new RimKataNativeAttack())
                : motion.primaryAttack ?? (motion.primaryAttack = new RimKataNativeAttack());
            if (request.Pending || !RimKataReactiveMotion.Valid(motion)
                || !ValidOwner(pawn, weapon) || cycle.weapon != weapon
                || !ValidTarget(pawn, target)) return false;
            Verb slotVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, weapon);
            if (slotVerb == null) return false;
            bool close = pawn.CanReachImmediate(target, PathEndMode.Touch);
            bool physicalMelee = slotVerb.IsMeleeAttack
                || RimKataTargetAccess.SettingsFor(pawn)?.closeFireEnabled == false
                || pawn.Drafted && pawn.drafter?.FireAtWill == false;
            if (physicalMelee && !close) return false;
            Verb verb = physicalMelee
                ? RimKataDualWeaponController.ResolveWeaponMeleeVerb(pawn, weapon, target)
                : slotVerb;
            if (verb == null || verb.CasterPawn != pawn || verb.state != VerbState.Idle
                || RimKataNativeAttack.WaitingForNativeTick(verb)
                || !HasAmmo(verb, weapon)
                || !RimKataDualWeaponController.VerbUsable(pawn, verb, close)
                || (!close && (verb.IsMeleeAttack || !verb.CanHitTarget(target)))) return false;

            request.pawn = pawn;
            request.state = motion.combat;
            request.cycle = cycle;
            request.reactiveMotion = motion;
            request.weapon = weapon;
            request.verb = verb;
            request.cycleVerb = slotVerb;
            request.job = pawn.CurJob;
            request.firedTarget = target;
            request.target = target;
            request.closeCombatContext = close;
            request.closeShot = !verb.IsMeleeAttack && close;
            request.closeMeleeResolution = request.closeShot;
            request.closeMeleeHit = false;
            request.closeDefensePrecheck = RimKataCloseDefensePrecheck.None;
            request.notBeforeTick = dueTick;
            try
            {
                if (!request.Queue()) return false;
                if (RimKataReactiveMotion.Valid(motion) && pawn.CurJob == request.job
                    && cycle.weapon == weapon) return true;
                request.Cancel();
                return false;
            }
            finally
            {
                if (!request.Pending) request.ClearCompletedReferences();
            }
        }

        internal static bool CanContinue(RimKataNativeAttack request)
        {
            Pawn pawn = request.pawn;
            Thing target = request.target.Thing;
            return RimKataReactiveMotion.Valid(request.reactiveMotion)
                && ValidOwner(pawn, request.weapon) && pawn.CurJob == request.job
                && request.cycle.weapon == request.weapon
                && request.verb.CasterPawn == pawn
                && RimKataWeaponSlotUtility.CombatVerb(pawn, request.weapon) == request.cycleVerb
                && ValidTarget(pawn, target) && HasAmmo(request.verb, request.weapon)
                && (request.closeCombatContext
                    ? pawn.CanReachImmediate(target, PathEndMode.Touch)
                    : !request.verb.IsMeleeAttack && request.verb.CanHitTarget(target));
        }

        private static bool ValidOwner(Pawn pawn, ThingWithComps weapon) => pawn?.Spawned == true
            && !pawn.Dead && !pawn.Downed && !pawn.InMentalState && !pawn.stances.stunner.Stunned
            && weapon != null && !weapon.Destroyed && pawn.equipment != null
            && weapon.holdingOwner == pawn.equipment.GetDirectlyHeldThings();

        private static bool ValidTarget(Pawn pawn, Thing target) => target?.Spawned == true
            && !target.Destroyed && target != pawn && target.Map == pawn.Map
            && (!(target is Pawn victim) || !victim.Dead && !RimKataTargeting.IsIncapacitatedTarget(victim));

        internal static bool HasAmmo(Verb verb, ThingWithComps weapon)
        {
            if (!RimKataCombatExtendedProjectiles.IsProjectileVerb(verb)) return true;
            ThingComp ammo = AmmoType == null ? null : RimKataCombatExtendedCompat.FindComp(weapon, AmmoType);
            return ammo == null || CanFire != null && (bool)CanFire.Invoke(ammo, null);
        }
    }

    [HarmonyPatch]
    internal static class Patch_CEAmmo_RimKataReactiveAttack
    {
        private static bool Prepare() => RimKataActiveModTypes.Find("CombatExtended.CompAmmoUser") != null;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = RimKataActiveModTypes.Find("CombatExtended.CompAmmoUser");
            MethodBase reload = AccessTools.Method(type, "TryStartReload", Type.EmptyTypes);
            MethodBase empty = AccessTools.Method(type, "DoOutOfAmmoAction", Type.EmptyTypes);
            if (reload != null) yield return reload;
            if (empty != null && empty != reload) yield return empty;
        }
        private static bool Prefix(ThingComp __instance) => !RimKataReactiveAttack.OwnsWeapon(__instance.parent);
    }

    [HarmonyPatch]
    internal static class Patch_CEShots_RimKataReactiveAttack
    {
        private static bool Prepare() => RimKataActiveModTypes.Find("CombatExtended.Verb_ShootCE") != null;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo launcher = AccessTools.PropertyGetter(RimKataActiveModTypes.Find("CombatExtended.Verb_LaunchProjectileCE"), "ShotsPerBurst");
            MethodInfo shooter = AccessTools.PropertyGetter(RimKataActiveModTypes.Find("CombatExtended.Verb_ShootCE"), "ShotsPerBurst");
            if (launcher != null) yield return launcher;
            if (shooter != null && shooter != launcher) yield return shooter;
        }
        private static void Postfix(Verb __instance, ref int __result)
        {
            if (RimKataReactiveAttack.OwnsVerb(__instance)) __result = 1;
        }
    }

    [HarmonyPatch]
    internal static class Patch_CEAim_RimKataReactiveAttack
    {
        private static MethodBase Resolve() => AccessTools.PropertyGetter(
            RimKataActiveModTypes.Find("CombatExtended.Verb_ShootCE"), "ShouldAim");
        private static bool Prepare() => Resolve() != null;
        private static MethodBase TargetMethod() => Resolve();
        private static void Postfix(Verb __instance, ref bool __result)
        {
            if (RimKataReactiveAttack.OwnsVerb(__instance)) __result = false;
        }
    }
}
