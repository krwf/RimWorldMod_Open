using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Optional CE integration: reuse its ammo UI and reload jobs. No CE assembly
    // reference is required, and no firing or projectile path is patched here.
    internal static class RimKataCombatExtendedCompat
    {
        private static Type ammoCompType;
        private static Type ammoStatusType;
        private static Type reloadCommandType;
        private static FieldInfo ammoStatusComp;
        private static FieldInfo reloadCommandComp;
        private static FieldInfo duplicatePawnGizmo;
        private static PropertyInfo hasMagazine;
        private static PropertyInfo useAmmo;
        private static PropertyInfo hasAmmo;

        internal static void Apply(Harmony harmony)
        {
            ammoCompType = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
            if (ammoCompType == null)
                return;

            Type pawnGizmoType = AccessTools.TypeByName("CombatExtended.CompPawnGizmo");
            ammoStatusType = AccessTools.TypeByName("CombatExtended.GizmoAmmoStatus");
            reloadCommandType = AccessTools.TypeByName("CombatExtended.Command_Reload");
            Type reloadDriverType = AccessTools.TypeByName("CombatExtended.JobDriver_Reload");
            if (pawnGizmoType == null || ammoStatusType == null
                || reloadCommandType == null || reloadDriverType == null)
            {
                Log.Warning("[RimKata] CE ammo UI types were not found; secondary ammo integration was not applied.");
                return;
            }

            ammoStatusComp = AccessTools.Field(ammoStatusType, "compAmmo");
            reloadCommandComp = AccessTools.Field(reloadCommandType, "compAmmo");
            duplicatePawnGizmo = AccessTools.Field(pawnGizmoType, "duplicate");
            hasMagazine = AccessTools.Property(ammoCompType, "HasMagazine");
            useAmmo = AccessTools.Property(ammoCompType, "UseAmmo");
            hasAmmo = AccessTools.Property(ammoCompType, "HasAmmo");
            MethodInfo pawnGizmos = AccessTools.Method(pawnGizmoType, "CompGetGizmosExtra");
            MethodInfo title = AccessTools.PropertyGetter(ammoStatusType, "Title");
            MethodInfo groupsWith = AccessTools.Method(reloadCommandType, "GroupsWith", new[] { typeof(Gizmo) });
            if (ammoStatusComp == null || reloadCommandComp == null || duplicatePawnGizmo == null
                || hasMagazine?.PropertyType != typeof(bool) || useAmmo?.PropertyType != typeof(bool)
                || hasAmmo?.PropertyType != typeof(bool)
                || pawnGizmos == null || title == null || groupsWith == null
                || !RimKataCombatExtendedReload.Apply(harmony, reloadDriverType))
            {
                Log.Warning("[RimKata] CE ammo UI or reload API did not match; secondary ammo integration was not applied.");
                return;
            }

            harmony.Patch(pawnGizmos, postfix: Patch(nameof(PawnGizmosPostfix)));
            harmony.Patch(title, postfix: Patch(nameof(AmmoTitlePostfix)));
            harmony.Patch(groupsWith, postfix: Patch(nameof(ReloadGroupsPostfix)));
        }

        private static HarmonyMethod Patch(string method)
        {
            return new HarmonyMethod(typeof(RimKataCombatExtendedCompat), method);
        }

        internal static bool IsHeldSecondary(Pawn pawn, ThingWithComps weapon)
        {
            return pawn?.equipment != null && weapon != null && !weapon.Destroyed
                && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                && pawn.equipment.Primary != weapon
                && RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn) == weapon
                && weapon.holdingOwner == pawn.equipment.GetDirectlyHeldThings()
                && pawn.equipment.AllEquipmentListForReading.Contains(weapon);
        }

        private static void PawnGizmosPostfix(ThingComp __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__result == null || !(__instance.parent is Pawn pawn)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || (bool)duplicatePawnGizmo.GetValue(__instance))
                return;

            ThingWithComps secondary = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn);
            if (!IsHeldSecondary(pawn, secondary))
                return;
            List<ThingComp> comps = secondary.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (ammoCompType.IsInstanceOfType(comps[i]))
                {
                    __result = AddAmmoGizmos(pawn, comps[i], __result);
                    return;
                }
            }
        }

        private static IEnumerable<Gizmo> AddAmmoGizmos(
            Pawn pawn, ThingComp secondaryAmmo, IEnumerable<Gizmo> original)
        {
            var all = new List<Gizmo>(original);
            Gizmo secondaryStatus = null;
            Command secondaryReload = null;
            foreach (Gizmo gizmo in all)
            {
                if (AmmoComp(gizmo, ammoStatusType, ammoStatusComp) == secondaryAmmo)
                    secondaryStatus = gizmo;
                else if (AmmoComp(gizmo, reloadCommandType, reloadCommandComp) == secondaryAmmo)
                    secondaryReload = gizmo as Command;
            }

            // Enumerate only the ammo component, never fire modes, under-barrel
            // controls, attack commands or CE's additional developer commands.
            if (secondaryStatus == null || secondaryReload == null)
            {
                foreach (Gizmo gizmo in secondaryAmmo.CompGetGizmosExtra())
                {
                    if (secondaryStatus == null && ammoStatusType.IsInstanceOfType(gizmo))
                        secondaryStatus = gizmo;
                    else if (secondaryReload == null && reloadCommandType.IsInstanceOfType(gizmo))
                        secondaryReload = gizmo as Command;
                    if (secondaryStatus != null && secondaryReload != null)
                        break;
                }
            }
            if (secondaryReload != null && !secondaryReload.defaultLabel.NullOrEmpty()
                && !secondaryReload.defaultLabel.EndsWith(" RimKata", StringComparison.Ordinal))
                secondaryReload.defaultLabel += " RimKata";
            else if (secondaryReload != null && secondaryReload.defaultLabel.NullOrEmpty())
                secondaryReload.defaultLabel = "CE_ReloadLabel".Translate() + " RimKata";
            if (secondaryReload is Command_Action action && action.action != null
                && !(action.action.Target is SecondaryReloadAction))
                action.action = new SecondaryReloadAction(pawn, secondaryAmmo, action.action).Invoke;

            bool statusInserted = false;
            bool reloadInserted = false;
            ThingWithComps primary = pawn.equipment.Primary;
            foreach (Gizmo gizmo in all)
            {
                if (gizmo == secondaryStatus || gizmo == secondaryReload)
                    continue;
                yield return gizmo;
                if (!statusInserted && secondaryStatus != null
                    && AmmoComp(gizmo, ammoStatusType, ammoStatusComp)?.parent == primary)
                {
                    secondaryStatus.Order = gizmo.Order;
                    yield return secondaryStatus;
                    statusInserted = true;
                }
                if (!reloadInserted && secondaryReload != null
                    && AmmoComp(gizmo, reloadCommandType, reloadCommandComp)?.parent == primary)
                {
                    secondaryReload.Order = gizmo.Order;
                    yield return secondaryReload;
                    reloadInserted = true;
                }
            }
            if (!statusInserted && secondaryStatus != null)
                yield return secondaryStatus;
            if (!reloadInserted && secondaryReload != null)
                yield return secondaryReload;
        }

        private static ThingComp AmmoComp(Gizmo gizmo, Type type, FieldInfo field)
        {
            return gizmo != null && type.IsInstanceOfType(gizmo)
                ? field.GetValue(gizmo) as ThingComp : null;
        }

        private sealed class SecondaryReloadAction
        {
            private readonly Pawn pawn;
            private readonly ThingComp ammo;
            private readonly Action reload;

            internal SecondaryReloadAction(Pawn pawn, ThingComp ammo, Action reload)
            {
                this.pawn = pawn;
                this.ammo = ammo;
                this.reload = reload;
            }

            internal void Invoke()
            {
                if (!IsHeldSecondary(pawn, ammo.parent) || !(bool)hasMagazine.GetValue(ammo))
                    return;
                // CE's out-of-ammo fallback can switch the primary weapon. A
                // manual secondary reload must not invoke that fallback.
                if ((bool)useAmmo.GetValue(ammo) && !(bool)hasAmmo.GetValue(ammo))
                {
                    Messages.Message("CE_OutOfAmmo".Translate(), pawn, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                reload();
            }
        }

        private static bool IsSecondaryAmmo(ThingComp ammo)
        {
            ThingWithComps weapon = ammo?.parent;
            return weapon?.ParentHolder is Pawn_EquipmentTracker equipment
                && IsHeldSecondary(equipment.pawn, weapon);
        }

        private static void AmmoTitlePostfix(Gizmo __instance, ref string __result)
        {
            if (IsSecondaryAmmo(AmmoComp(__instance, ammoStatusType, ammoStatusComp)))
                __result += "[RimKata]";
        }

        private static void ReloadGroupsPostfix(Gizmo __instance, Gizmo __0, ref bool __result)
        {
            if (__result && IsSecondaryAmmo(AmmoComp(__instance, reloadCommandType, reloadCommandComp))
                != IsSecondaryAmmo(AmmoComp(__0, reloadCommandType, reloadCommandComp)))
                __result = false;
        }
    }
}
