using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedLoadout
    {
        private delegate void EquipmentStats(ThingWithComps weapon, out float weight, out float bulk);

        private static Type inventoryType;
        private static Type loadoutType;
        private static Type ammoType;
        private static FieldInfo primaryMagazineCount;
        private static FieldInfo forcedSidearm;
        private static FieldInfo sidearms;
        private static FieldInfo sidearmTags;
        private static FieldInfo sidearmMagazineCount;
        private static Action<ThingComp> updateInventory;
        private static EquipmentStats equipmentStats;
        private static Action<object, ThingWithComps> loadMagazine;
        private static Action<object, ThingWithComps, ThingComp, int> generateAmmo;

        internal static void Apply(Harmony harmony)
        {
            Type inventory = AccessTools.TypeByName("CombatExtended.CompInventory");
            Type loadout = AccessTools.TypeByName("CombatExtended.LoadoutPropertiesExtension");
            Type sidearm = AccessTools.TypeByName("CombatExtended.SidearmOption");
            Type ammo = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
            if (inventory == null || loadout == null || sidearm == null || ammo == null)
                return;

            MethodInfo update = AccessTools.Method(inventory, "UpdateInventory", Type.EmptyTypes);
            MethodInfo stats = AccessTools.Method(inventory, "GetEquipmentStats",
                new[] { typeof(ThingWithComps), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
            MethodInfo load = AccessTools.Method(loadout, "LoadWeaponWithRandAmmo", new[] { typeof(ThingWithComps) });
            MethodInfo reserve = AccessTools.Method(loadout, "TryGenerateAmmoFor",
                new[] { typeof(ThingWithComps), inventory, typeof(int) });
            MethodInfo loadoutChanged = AccessTools.Method(typeof(RimKataWeaponSlotUtility), "NotifyLoadoutChanged",
                new[] { typeof(Pawn), typeof(RimKataPawnCombatState) });
            MethodInfo generateSecondary = AccessTools.Method(typeof(Patch_Pawn_RimKataAiSecondaryWeapon),
                "TryGenerateSecondaryWeapon", new[] { typeof(Pawn) });
            FieldInfo weight = AccessTools.Field(inventory, "currentWeightCached");
            FieldInfo bulk = AccessTools.Field(inventory, "currentBulkCached");
            primaryMagazineCount = AccessTools.Field(loadout, "primaryMagazineCount");
            forcedSidearm = AccessTools.Field(loadout, "forcedSidearm");
            sidearms = AccessTools.Field(loadout, "sidearms");
            sidearmTags = AccessTools.Field(sidearm, "weaponTags");
            sidearmMagazineCount = AccessTools.Field(sidearm, "magazineCount");
            if (update?.ReturnType != typeof(void) || stats?.ReturnType != typeof(void) || !stats.IsStatic
                || load?.ReturnType != typeof(void) || reserve?.ReturnType != typeof(void)
                || weight?.FieldType != typeof(float) || bulk?.FieldType != typeof(float)
                || primaryMagazineCount?.FieldType != typeof(FloatRange)
                || sidearmMagazineCount?.FieldType != typeof(FloatRange)
                || sidearmTags?.FieldType != typeof(List<string>)
                || forcedSidearm == null || sidearms == null
                || loadoutChanged == null || generateSecondary == null
                || !typeof(ThingComp).IsAssignableFrom(inventory)
                || !typeof(DefModExtension).IsAssignableFrom(loadout))
            {
                Log.Warning("[RimKata] CE loadout API did not match; secondary loadout integration was not applied.");
                return;
            }

            try
            {
                // This postfix adds only the registered secondary. Refuse a CE
                // version which already enumerates all equipped weapons.
                MethodInfo primary = AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), "Primary");
                MethodInfo all = AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), "AllEquipmentListForReading");
                List<CodeInstruction> codes = PatchProcessor.GetOriginalInstructions(update);
                int primaryReads = 0;
                bool enumeratesEquipment = false;
                foreach (CodeInstruction code in codes)
                {
                    if (Equals(code.operand, primary))
                        primaryReads++;
                    if (Equals(code.operand, all))
                        enumeratesEquipment = true;
                }
                if (primaryReads != 2 || enumeratesEquipment)
                    throw new InvalidOperationException("CE inventory no longer has the supported primary-only equipment calculation.");

                var comp = Expression.Parameter(typeof(ThingComp), "comp");
                updateInventory = Expression.Lambda<Action<ThingComp>>(
                    Expression.Call(Expression.Convert(comp, inventory), update), comp).Compile();
                equipmentStats = (EquipmentStats)Delegate.CreateDelegate(typeof(EquipmentStats), stats);

                var extension = Expression.Parameter(typeof(object), "extension");
                var weapon = Expression.Parameter(typeof(ThingWithComps), "weapon");
                var count = Expression.Parameter(typeof(int), "count");
                loadMagazine = Expression.Lambda<Action<object, ThingWithComps>>(
                    Expression.Call(Expression.Convert(extension, loadout), load, weapon), extension, weapon).Compile();
                generateAmmo = Expression.Lambda<Action<object, ThingWithComps, ThingComp, int>>(
                    Expression.Call(Expression.Convert(extension, loadout), reserve,
                        weapon, Expression.Convert(comp, inventory), count), extension, weapon, comp, count).Compile();

                harmony.Patch(update, postfix: new HarmonyMethod(
                    typeof(RimKataCombatExtendedLoadout), nameof(InventoryPostfix)));
                harmony.Patch(loadoutChanged, postfix: new HarmonyMethod(
                    typeof(RimKataCombatExtendedLoadout), nameof(LoadoutChangedPostfix)));
                harmony.Patch(generateSecondary,
                    prefix: new HarmonyMethod(typeof(RimKataCombatExtendedLoadout), nameof(GenerateSecondaryPrefix)),
                    postfix: new HarmonyMethod(typeof(RimKataCombatExtendedLoadout), nameof(GenerateSecondaryPostfix)));
                inventoryType = inventory;
                loadoutType = loadout;
                ammoType = ammo;
            }
            catch (Exception exception)
            {
                inventoryType = null;
                harmony.Unpatch(update, AccessTools.Method(
                    typeof(RimKataCombatExtendedLoadout), nameof(InventoryPostfix)));
                harmony.Unpatch(loadoutChanged, AccessTools.Method(
                    typeof(RimKataCombatExtendedLoadout), nameof(LoadoutChangedPostfix)));
                harmony.Unpatch(generateSecondary, AccessTools.Method(
                    typeof(RimKataCombatExtendedLoadout), nameof(GenerateSecondaryPrefix)));
                harmony.Unpatch(generateSecondary, AccessTools.Method(
                    typeof(RimKataCombatExtendedLoadout), nameof(GenerateSecondaryPostfix)));
                Log.Warning("[RimKata] CE secondary loadout integration could not be applied: " + exception.Message);
            }
        }

        private static void LoadoutChangedPostfix(Pawn __0)
        {
            ThingComp inventory = RimKataCombatExtendedCompat.FindComp(__0, inventoryType);
            if (inventory != null)
                updateInventory(inventory);
        }

        private static void GenerateSecondaryPrefix(Pawn __0, out ThingWithComps __state)
        {
            __state = __0 == null ? null : RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(__0);
        }

        private static void GenerateSecondaryPostfix(Pawn __0, ThingWithComps __state)
        {
            if (__0 == null)
                return;
            ThingWithComps weapon = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(__0);
            if (weapon != null && weapon != __state)
                InitializeGeneratedSecondary(__0, weapon);
        }

        private static void InitializeGeneratedSecondary(Pawn pawn, ThingWithComps weapon)
        {
            if (inventoryType == null || !RimKataCombatExtendedCompat.IsHeldSecondary(pawn, weapon)
                || RimKataCombatExtendedCompat.FindComp(weapon, ammoType) == null)
                return;
            ThingComp inventory = RimKataCombatExtendedCompat.FindComp(pawn, inventoryType);
            if (inventory == null)
                return;

            try
            {
                object extension = null;
                List<DefModExtension> extensions = pawn.kindDef?.modExtensions;
                if (extensions != null)
                    for (int i = 0; i < extensions.Count; i++)
                        if (loadoutType.IsInstanceOfType(extensions[i]))
                        {
                            extension = extensions[i];
                            break;
                        }
                // CE's default extension fills the gun but requests no reserve
                // magazines. Existing pawn-kind ammunition and capacity rules win.
                if (extension == null)
                    extension = Activator.CreateInstance(loadoutType);
                loadMagazine(extension, weapon);
                updateInventory(inventory);
                FloatRange magazines = MagazineCountFor(extension, weapon.def);
                generateAmmo(extension, weapon, inventory, Mathf.RoundToInt(magazines.RandomInRange));
                updateInventory(inventory);
            }
            catch (Exception exception)
            {
                Log.Warning("[RimKata] CE ammunition for the generated secondary could not be initialized: " + exception.Message);
            }
        }

        private static FloatRange MagazineCountFor(object extension, ThingDef weapon)
        {
            object forced = forcedSidearm.GetValue(extension);
            if (MatchesSidearm(forced, weapon))
                return (FloatRange)sidearmMagazineCount.GetValue(forced);
            if (sidearms.GetValue(extension) is IEnumerable options)
                foreach (object option in options)
                    if (MatchesSidearm(option, weapon))
                        return (FloatRange)sidearmMagazineCount.GetValue(option);
            return (FloatRange)primaryMagazineCount.GetValue(extension);
        }

        private static bool MatchesSidearm(object option, ThingDef weapon)
        {
            if (option == null || weapon.weaponTags == null
                || !(sidearmTags.GetValue(option) is List<string> tags))
                return false;
            for (int i = 0; i < tags.Count; i++)
                if (weapon.weaponTags.Contains(tags[i]))
                    return true;
            return false;
        }

        private static void InventoryPostfix(
            ThingComp __instance, ref float ___currentWeightCached, ref float ___currentBulkCached)
        {
            if (!(__instance.parent is Pawn pawn))
                return;
            ThingWithComps secondary = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn);
            if (!RimKataCombatExtendedCompat.IsHeldSecondary(pawn, secondary))
                return;
            // CE's equipment stats already include its StatPart_LoadedAmmo.
            // UpdateInventory resets both totals before this postfix each time.
            equipmentStats(secondary, out float weight, out float bulk);
            ___currentWeightCached += weight;
            ___currentBulkCached += bulk;
        }
    }
}
