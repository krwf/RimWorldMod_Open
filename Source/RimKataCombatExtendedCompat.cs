using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Optional CE integration uses the installed CE components and jobs without
    // requiring a compile-time reference to its assembly.
    internal static class RimKataCombatExtendedCompat
    {
        private static Type ammoCompType;
        private static Type ammoStatusType;
        private static Type reloadCommandType;
        private static Type fireModesType;
        private static MethodInfo toggleFireMode;
        private static MethodInfo toggleAimMode;
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

            RimKataCombatExtendedAmmo.Apply(harmony, ammoCompType);
            RimKataCombatExtendedLoadout.Apply(harmony);

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

            fireModesType = AccessTools.TypeByName("CombatExtended.CompFireModes");
            toggleFireMode = AccessTools.Method(fireModesType, "ToggleFireMode", Type.EmptyTypes);
            toggleAimMode = AccessTools.Method(fireModesType, "ToggleAimMode", Type.EmptyTypes);
            if (fireModesType != null && toggleFireMode?.ReturnType == typeof(void)
                && toggleAimMode?.ReturnType == typeof(void))
                harmony.Patch(AccessTools.Method(typeof(Command), nameof(Command.GroupsWith)),
                    postfix: Patch(nameof(ModeGroupsPostfix)));
            else
                fireModesType = null;
        }

        internal static bool EnsureAmmoReady(Pawn pawn, Verb verb) =>
            RimKataCombatExtendedAmmo.EnsureReady(pawn, verb);

        internal static ThingComp FindComp(ThingWithComps thing, Type type)
        {
            if (thing == null || type == null)
                return null;
            List<ThingComp> comps = thing.AllComps;
            for (int i = 0; i < comps.Count; i++)
                if (type.IsInstanceOfType(comps[i]))
                    return comps[i];
            return null;
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
            ThingComp ammo = FindComp(secondary, ammoCompType);
            if (ammo != null)
                __result = AddAmmoGizmos(pawn, ammo, __result);
            ThingComp modes = FindComp(secondary, fireModesType);
            if (modes != null)
                __result = AddModeGizmos(pawn, modes, __result);
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

            // Only take the two ammo controls; other CE components are handled
            // separately so under-barrel and developer commands are not copied.
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

        private static IEnumerable<Gizmo> AddModeGizmos(
            Pawn pawn, ThingComp modes, IEnumerable<Gizmo> original)
        {
            var all = new List<Gizmo>(original);
            var additions = new List<Command_Action>(2);
            foreach (Gizmo gizmo in modes.CompGetGizmosExtra())
            {
                if (!(gizmo is Command_Action command) || command.action?.Target != modes
                    || (command.action.Method != toggleFireMode && command.action.Method != toggleAimMode))
                    continue;
                command.defaultLabel += " RimKata";
                command.action = new SecondaryModeAction(pawn, modes, command.action).Invoke;
                additions.Add(command);
            }

            foreach (Gizmo gizmo in all)
            {
                yield return gizmo;
                if (!(gizmo is Command_Action primary)
                    || !(primary.action?.Target is ThingComp primaryModes)
                    || primaryModes.parent != pawn.equipment.Primary)
                    continue;
                for (int i = additions.Count - 1; i >= 0; i--)
                {
                    Command_Action secondary = additions[i];
                    var action = (SecondaryModeAction)secondary.action.Target;
                    if (action.mode.Method != primary.action.Method)
                        continue;
                    secondary.Order = primary.Order;
                    yield return secondary;
                    additions.RemoveAt(i);
                }
            }
            foreach (Command_Action command in additions)
                yield return command;
        }

        private sealed class SecondaryModeAction
        {
            private readonly Pawn pawn;
            private readonly ThingComp comp;
            internal readonly Action mode;

            internal SecondaryModeAction(Pawn pawn, ThingComp comp, Action mode)
            {
                this.pawn = pawn;
                this.comp = comp;
                this.mode = mode;
            }

            internal void Invoke()
            {
                if (IsHeldSecondary(pawn, comp.parent))
                    mode();
            }
        }

        private static void ModeGroupsPostfix(Command __instance, Gizmo __0, ref bool __result)
        {
            if (__result
                && ((__instance as Command_Action)?.action?.Target is SecondaryModeAction)
                    != ((__0 as Command_Action)?.action?.Target is SecondaryModeAction))
                __result = false;
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
