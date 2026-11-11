using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace KRWF.RimKata
{
    // Both the white lantern and weapon codex use this same ability/storage pair.
    internal static class RimKataMonolynCompat
    {
        private static Func<Pawn, ThingComp> findCodex;
        private static AccessTools.FieldRef<object, List<Thing>> storedWeapons;
        private static AccessTools.FieldRef<object, int> storageCapacity;
        private static HediffDef codexDisplay;
        private static EffecterDef codexBlink, weaponSwap;

        internal static void Apply()
        {
            Type ability = RimKataActiveModTypes.Find("ASEL.CompAbilityEffect_WeaponCodex");
            if (ability == null) return;
            var harmony = new Harmony("krwf.rimkata.monolyn");
            try
            {
                Assembly owner = ability.Assembly;
                Type storage = RimKataActiveModTypes.Find(owner, "ASEL.CompWeaponCodexStorage");
                Type properties = RimKataActiveModTypes.Find(owner, "ASEL.CompProperties_WeaponCodexStorage");
                Type utility = RimKataActiveModTypes.Find(owner, "ASEL.MonolynUtility");
                MethodInfo find = AccessTools.DeclaredMethod(utility, "FindCodex", new[] { typeof(Pawn) });
                MethodInfo apply = AccessTools.DeclaredMethod(ability, "Apply",
                    new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) });
                if (!typeof(CompAbilityEffect).IsAssignableFrom(ability)
                    || storage == null || !typeof(ThingComp).IsAssignableFrom(storage)
                    || properties == null || find == null || !find.IsStatic || find.ReturnType != storage
                    || apply == null || apply.IsStatic || apply.ReturnType != typeof(void))
                    throw new MissingMemberException("Monolyn weapon codex API");

                findCodex = (Func<Pawn, ThingComp>)Delegate.CreateDelegate(typeof(Func<Pawn, ThingComp>), find);
                storedWeapons = AccessTools.FieldRefAccess<List<Thing>>(storage, "weapons");
                storageCapacity = AccessTools.FieldRefAccess<int>(properties, "storage");
                codexDisplay = DefDatabase<HediffDef>.GetNamedSilentFail("MNArsenalCodex");
                codexBlink = DefDatabase<EffecterDef>.GetNamedSilentFail("Effecter_MNCodexBlink");
                weaponSwap = DefDatabase<EffecterDef>.GetNamedSilentFail("MNWeaponSwap");
                RimKataStartupPatches.Patch(harmony, apply,
                    transpiler: new HarmonyMethod(typeof(RimKataMonolynCompat), nameof(MenuTranspiler)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] Monolyn secondary weapon menu integration was not applied.\n" + exception);
            }
        }

        private static IEnumerable<CodeInstruction> MenuTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo menu = AccessTools.Constructor(typeof(FloatMenu), new[] { typeof(List<FloatMenuOption>) });
            MethodInfo create = AccessTools.DeclaredMethod(typeof(RimKataMonolynCompat), nameof(CreateMenu));
            int matched = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.opcode == OpCodes.Newobj && Equals(code.operand, menu))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                    yield return new CodeInstruction(OpCodes.Call, create);
                    matched++;
                    continue;
                }
                yield return code;
            }
            if (matched != 1) throw new InvalidOperationException("Monolyn weapon menu creation did not match.");
        }

        private sealed class CodexMenu : FloatMenu
        {
            internal CodexMenu(List<FloatMenuOption> source, int originalCount) : base(source)
            {
                // Vanilla sorts disabled options last. Keep the entire RK group
                // in storage order while preserving the original menu's own order.
                for (int i = originalCount; i < source.Count; i++) options.Remove(source[i]);
                for (int i = originalCount; i < source.Count; i++) options.Add(source[i]);
            }
        }

        private static FloatMenu CreateMenu(List<FloatMenuOption> options, CompAbilityEffect ability)
        {
            int originalCount = options.Count;
            AppendOptions(options, ability);
            return options.Count == originalCount ? new FloatMenu(options) : new CodexMenu(options, originalCount);
        }

        private static List<FloatMenuOption> AppendOptions(List<FloatMenuOption> options, CompAbilityEffect ability)
        {
            Pawn pawn = ability.parent?.pawn;
            if (pawn?.health == null || pawn.Dead || !pawn.IsPlayerControlled
                || !RimKataEligibility.HasRimKataAccess(pawn)) return options;
            ThingComp storage = findCodex(pawn);
            List<Thing> weapons = storage == null ? null : storedWeapons(storage);
            if (weapons == null) return options;
            foreach (Thing thing in weapons)
            {
                if (!(thing is ThingWithComps weapon) || !weapon.def.IsWeapon) continue;
                string label = "Equip".Translate(weapon.LabelShort) + " [RimKata]";
                string reason = RimKataSidearmInventoryEquip.EquipFailureReason(pawn, weapon);
                options.Add(new FloatMenuOption(reason == null ? label : label + ": " + reason,
                    reason == null ? (Action)(() => RequestEquip(pawn, storage, weapon)) : null));
            }
            return options;
        }

        private static bool CanTake(Pawn pawn, ThingComp storage, ThingWithComps weapon)
            => RimKataSidearmInventoryEquip.EquipFailureReason(pawn, weapon) == null
                && !weapon.Spawned && weapon.holdingOwner == null
                && findCodex(pawn) == storage && storedWeapons(storage)?.Contains(weapon) == true;

        private static void RequestEquip(Pawn pawn, ThingComp storage, ThingWithComps weapon)
        {
            if (!CanTake(pawn, storage, weapon)) return;
            string confirmation = EquipmentUtility.GetPersonaWeaponConfirmationText(weapon, pawn);
            if (confirmation.NullOrEmpty()) DispatchEquip(pawn, storage, weapon);
            else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(confirmation,
                () => DispatchEquip(pawn, storage, weapon), false, null, WindowLayer.Dialog));
        }

        private static void DispatchEquip(Pawn pawn, ThingComp storage, ThingWithComps weapon)
        {
            if (!CanTake(pawn, storage, weapon)) return;
            RimKataMultiplayerCommands.EquipStoredSecondary(pawn, storage.parent.GetUniqueLoadID(),
                storedWeapons(storage).IndexOf(weapon), weapon.GetUniqueLoadID());
        }

        internal static void EquipStored(Pawn pawn, string storageId, int weaponIndex, string weaponId)
        {
            ThingComp storage = findCodex?.Invoke(pawn);
            if (storage?.parent?.GetUniqueLoadID() != storageId) return;
            List<Thing> weapons = storedWeapons(storage);
            if (weapons == null || weaponIndex < 0 || weaponIndex >= weapons.Count) return;
            ThingWithComps weapon = weapons[weaponIndex] as ThingWithComps;
            if (weapon != null && weapon.GetUniqueLoadID() == weaponId) Equip(pawn, storage, weapon);
        }

        private static void Equip(Pawn pawn, ThingComp storage, ThingWithComps source)
        {
            // Confirmation and the open menu must not retain authority over a moved weapon/codex.
            if (!CanTake(pawn, storage, source)) return;
            List<Thing> weapons = storedWeapons(storage);
            int index = weapons.IndexOf(source);
            ThingWithComps primary = pawn.equipment.Primary;
            ThingWithComps previous = RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn);
            ThingWithComps weapon = null;
            bool equipped = false, previousRemoved = false;
            try
            {
                if (source.stackCount > 1) weapon = (ThingWithComps)source.SplitOff(1);
                else
                {
                    weapons.RemoveAt(index);
                    weapon = source;
                }
                if (previous != null)
                {
                    pawn.equipment.Remove(previous);
                    previousRemoved = previous.holdingOwner == null;
                    if (!previousRemoved) return;
                }
                equipped = RimKataWeaponSlotUtility.TryEquipSecondary(pawn, weapon);
            }
            finally
            {
                if (!equipped && weapon != null && !weapon.Destroyed && !weapon.Spawned
                    && weapon.holdingOwner == null)
                {
                    if (weapon == source || !source.TryAbsorbStack(weapon, false))
                        weapons.Insert(Math.Min(index, weapons.Count), weapon);
                }
                if (previousRemoved && previous != null && !previous.Destroyed && !previous.Spawned
                    && previous.holdingOwner == null)
                {
                    bool restored = !equipped && pawn.equipment.Primary == primary
                        && RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn) == null
                        && RimKataSidearmInventoryEquip.EquipFailureReason(pawn, previous) == null
                        && RimKataWeaponSlotUtility.TryEquipSecondary(pawn, previous);
                    if (!restored) StorePrevious(pawn, storage, weapons, previous);
                }
            }
            if (!equipped) return;
            Hediff display = codexDisplay == null ? null : pawn.health.hediffSet.GetFirstHediffOfDef(codexDisplay);
            if (display != null) pawn.health.RemoveHediff(display);
            PlayEffect(codexBlink, pawn);
            PlayEffect(weaponSwap, pawn);
            weapon.def.soundInteract?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
        }

        private static void StorePrevious(Pawn pawn, ThingComp storage, List<Thing> weapons, ThingWithComps weapon)
        {
            // Match Monolyn's Dogma exclusion and capacity; retain inventory/drop fallback.
            if (weapon.def.weaponTags?.Contains("Dogma") != true && weapons.Count < storageCapacity(storage.props))
                weapons.Add(weapon);
            else if (pawn.inventory?.innerContainer.TryAddOrTransfer(weapon, false) != true)
                GenPlace.TryPlaceThing(weapon, pawn.Position, pawn.Map, ThingPlaceMode.Near);
        }

        private static void PlayEffect(EffecterDef def, Pawn pawn)
        {
            if (def == null) return;
            Effecter effect = def.Spawn();
            try { effect.Trigger(pawn, pawn); }
            finally { effect.Cleanup(); }
        }
    }
}
