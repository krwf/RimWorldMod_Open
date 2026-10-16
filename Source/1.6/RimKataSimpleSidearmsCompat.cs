using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataSimpleSidearmsCompat
    {
        private static readonly ConditionalWeakTable<Pawn, StoredPair> StoredPairs =
            new ConditionalWeakTable<Pawn, StoredPair>();
        private static Func<Pawn, ThingWithComps, bool, bool, bool> equipWeapon;
        private static Func<ThingComp, Pawn, bool> forcedUnarmed;
        private static object weaponInteraction;
        private static object unmemorisedWeaponInteraction;
        private static bool installed;

        internal static void Apply()
        {
            Type memory = RimKataActiveModTypes.Find("SimpleSidearms.rimworld.CompSidearmMemory");
            if (memory == null) return;
            var harmony = new Harmony("krwf.rimkata.simplesidearms");
            try
            {
                Type assignment = RimKataActiveModTypes.Find(
                    "PeteTimesSix.SimpleSidearms.Utilities.WeaponAssingment");
                MethodInfo equip = AccessTools.Method(assignment, "equipSpecificWeapon",
                    new[] { typeof(Pawn), typeof(ThingWithComps), typeof(bool), typeof(bool) });
                equipWeapon = (Func<Pawn, ThingWithComps, bool, bool, bool>)Delegate.CreateDelegate(
                    typeof(Func<Pawn, ThingWithComps, bool, bool, bool>), equip);
                forcedUnarmed = CompileForceCheck(memory);

                Patch(harmony, memory, "SetUnarmedAsForced", nameof(ForceUnarmedPostfix));
                Patch(harmony, memory, "UnsetUnarmedAsForced", nameof(ReleaseUnarmedPostfix));
                Patch(harmony, memory, "InformOfUndraft", nameof(ReleaseUnarmedPostfix));
                Patch(harmony, memory, "SetWeaponAsForced", nameof(SelectWeaponPrefix), prefix: true);
                Patch(harmony, memory, "SetMeleeWeaponTypeAsPreferred", nameof(SelectWeaponPrefix), prefix: true);
                Patch(harmony, memory, "SetRangedWeaponTypeAsDefault", nameof(SelectWeaponPrefix), prefix: true);
                Patch(harmony, memory, "PostExposeData", nameof(ExposeDataPostfix));
                installed = true;
            }
            catch (Exception exception)
            {
                installed = false;
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] Simple Sidearms forced-unarmed integration was not applied.\n" + exception);
            }

            ApplyWeaponInteraction();
        }

        private static void ApplyWeaponInteraction()
        {
            var harmony = new Harmony("krwf.rimkata.simplesidearms.middleclick");
            try
            {
                Type gizmo = RimKataActiveModTypes.Find("SimpleSidearms.rimworld.Gizmo_SidearmsList");
                MethodInfo target = AccessTools.DeclaredMethod(gizmo, "handleInteraction");
                if (target == null) throw new MissingMethodException(gizmo?.FullName, "handleInteraction");
                ParameterInfo[] parameters = target.GetParameters();
                if (parameters.Length != 2 || !parameters[0].ParameterType.IsEnum
                    || parameters[1].ParameterType != typeof(Event) || target.ReturnType != typeof(void))
                    throw new InvalidOperationException("Simple Sidearms weapon interaction signature is not supported.");
                if (AccessTools.Field(gizmo, "parent")?.FieldType != typeof(Pawn)
                    || AccessTools.Field(gizmo, "interactionWeapon")?.FieldType != typeof(ThingWithComps))
                    throw new InvalidOperationException("Simple Sidearms weapon interaction fields are not supported.");

                Type interaction = parameters[0].ParameterType;
                weaponInteraction = Enum.Parse(interaction, "Weapon");
                unmemorisedWeaponInteraction = Enum.Parse(interaction, "UnmemorisedWeapon");
                if (Equals(weaponInteraction, unmemorisedWeaponInteraction))
                    throw new InvalidOperationException("Simple Sidearms weapon interaction values overlap.");
                RimKataStartupPatches.Patch(harmony, target,
                    prefix: new HarmonyMethod(typeof(RimKataSimpleSidearmsCompat), nameof(WeaponInteractionPrefix)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] Simple Sidearms middle-click secondary equipment integration was not applied.\n" + exception);
            }
        }

        private static bool WeaponInteractionPrefix(object __0, Event __1,
            Pawn ___parent, ThingWithComps ___interactionWeapon)
        {
            // GUI.Button has already consumed the mouse-up event. Its button remains
            // available when the gizmo grid passes the same event to another gizmo.
            if (__1 == null || __1.button != 2
                || (!Equals(__0, weaponInteraction) && !Equals(__0, unmemorisedWeaponInteraction)))
                return true;

            __1.Use();
            RimKataSidearmInventoryEquip.TryEquip(___parent, ___interactionWeapon);
            // ProcessInput still clears its interaction after this method returns.
            return false;
        }

        private static void Patch(Harmony harmony, Type type, string method, string hook, bool prefix = false)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            var patch = new HarmonyMethod(typeof(RimKataSimpleSidearmsCompat), hook);
            RimKataStartupPatches.Patch(harmony, target,
                prefix: prefix ? patch : null, postfix: prefix ? null : patch);
        }

        private static Func<ThingComp, Pawn, bool> CompileForceCheck(Type type)
        {
            var comp = Expression.Parameter(typeof(ThingComp), "comp");
            var pawn = Expression.Parameter(typeof(Pawn), "pawn");
            var memory = Expression.Convert(comp, type);
            var drafted = Expression.Property(pawn, nameof(Pawn.Drafted));
            var forced = Expression.Property(memory, "ForcedUnarmed");
            var forcedDrafted = Expression.Property(memory, "ForcedUnarmedWhileDrafted");
            var weaponDrafted = Expression.Property(memory, "ForcedWeaponWhileDrafted");
            var whenDrafted = Expression.OrElse(forcedDrafted,
                Expression.AndAlso(forced, Expression.Not(Expression.Property(weaponDrafted, "HasValue"))));
            return Expression.Lambda<Func<ThingComp, Pawn, bool>>(
                Expression.Condition(drafted, whenDrafted, forced), comp, pawn).Compile();
        }

        private static void ForceUnarmedPostfix(ThingComp __instance)
        {
            Pawn pawn = __instance.parent as Pawn;
            if (!installed || pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || pawn.equipment == null || pawn.inventory == null
                || !forcedUnarmed(__instance, pawn)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)) return;
            // A second force flag must not replace the original pair with an empty loadout.
            if (StoredPairs.TryGetValue(pawn, out _)) return;
            ThingWithComps primary = pawn.equipment.Primary;
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn);
            if (primary == null || secondary == null || primary == secondary) return;

            var pair = new StoredPair { primary = primary, secondary = secondary, changingEquipment = true };
            StoredPairs.Add(pawn, pair);
            try
            {
                // Remove the offhand first: removing the primary first would promote it
                // and start the ordinary external-primary-replacement workflow.
                if (pawn.inventory.innerContainer.TryAddOrTransfer(secondary, false))
                    equipWeapon(pawn, null, false, false);
                if (pawn.equipment.Primary != null || !InInventory(pawn, primary) || !InInventory(pawn, secondary))
                {
                    StoredPairs.Remove(pawn);
                    if (pawn.equipment.Primary == primary) RestoreSecondary(pawn, secondary);
                }
            }
            finally
            {
                pair.changingEquipment = false;
            }
        }

        private static void ReleaseUnarmedPostfix(ThingComp __instance)
        {
            Pawn pawn = __instance.parent as Pawn;
            if (!installed || pawn == null || !StoredPairs.TryGetValue(pawn, out StoredPair pair)
                || pair.changingEquipment || forcedUnarmed(__instance, pawn)) return;

            // Consume once before equipment callbacks. A failed or superseded restore
            // never becomes a later attempt to force the old offhand onto a new weapon.
            StoredPairs.Remove(pawn);
            if (pawn.Spawned != true || pawn.Dead || pawn.Downed
                || pawn.equipment == null || pawn.equipment.Primary != null
                || !InInventory(pawn, pair.primary)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || !EquipmentUtility.CanEquip(pair.primary, pawn)) return;

            if (equipWeapon(pawn, pair.primary, false, false)
                && pawn.equipment.Primary == pair.primary)
                RestoreSecondary(pawn, pair.secondary);
        }

        private static void RestoreSecondary(Pawn pawn, ThingWithComps secondary)
        {
            if (!InInventory(pawn, secondary)
                || RimKataSecondaryHandRequirement.HasMissingHand(pawn)
                || RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn) != null
                || !EquipmentUtility.CanEquip(secondary, pawn)
                || !RimKataWeaponSlotUtility.CanEquipAsSecondary(pawn, secondary)) return;
            ThingOwner inventory = pawn.inventory.innerContainer;
            if (!inventory.Remove(secondary)) return;
            if (!RimKataWeaponSlotUtility.TryEquipSecondary(pawn, secondary)
                && !inventory.TryAddOrTransfer(secondary, false) && !secondary.Destroyed)
                GenPlace.TryPlaceThing(secondary, pawn.Position, pawn.Map, ThingPlaceMode.Near);
        }

        private static bool InInventory(Pawn pawn, ThingWithComps weapon)
        {
            return weapon != null && !weapon.Destroyed
                && pawn.inventory != null && weapon.holdingOwner == pawn.inventory.innerContainer;
        }

        private static void SelectWeaponPrefix(ThingComp __instance)
        {
            NotifyEquipmentChanged(__instance.parent as Pawn);
        }

        internal static void NotifyEquipmentChanged(Pawn pawn)
        {
            if (installed && pawn != null && StoredPairs.TryGetValue(pawn, out StoredPair pair)
                && !pair.changingEquipment) StoredPairs.Remove(pawn);
        }

        private static void ExposeDataPostfix(ThingComp __instance)
        {
            Pawn pawn = __instance.parent as Pawn;
            if (!installed || pawn == null) return;
            StoredPairs.TryGetValue(pawn, out StoredPair pair);
            Scribe_Deep.Look(ref pair, "rimKataForcedUnarmedPair");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                StoredPairs.Remove(pawn);
                if (pair != null) StoredPairs.Add(pawn, pair);
            }
        }

        private sealed class StoredPair : IExposable
        {
            internal ThingWithComps primary;
            internal ThingWithComps secondary;
            internal bool changingEquipment;

            public StoredPair() { }

            public void ExposeData()
            {
                Scribe_References.Look(ref primary, "primary");
                Scribe_References.Look(ref secondary, "secondary");
            }
        }
    }
}
