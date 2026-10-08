using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataPocketSandCompat
    {
        private static readonly MethodInfo PrimaryGetter = AccessTools.PropertyGetter(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));
        private static readonly MethodInfo MakeRoom = AccessTools.Method(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.MakeRoomFor),
            new[] { typeof(ThingWithComps) });
        private static readonly MethodInfo AddEquipment = AccessTools.Method(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment));

        private delegate bool FindInventoryWeapon(
            Pawn pawn, ThingDef def, bool includeEquipped, out ThingWithComps weapon);

        private static AccessTools.FieldRef<object, Pawn> singlePawn;
        private static AccessTools.FieldRef<object, ThingWithComps> singleTarget;
        private static AccessTools.FieldRef<object, IList> singleMerged;
        private static AccessTools.FieldRef<object, int> singleInteraction;
        private static AccessTools.FieldRef<object, List<Pawn>> multiPawns;
        private static AccessTools.FieldRef<object, ThingDef> multiTarget;
        private static AccessTools.FieldRef<object, int> multiInteraction;
        private static FindInventoryWeapon findInventoryWeapon;
        private static int singleEquipInteraction, singleNoInteraction;
        private static int multiEquipInteraction, multiNoInteraction;

        internal static void Apply()
        {
            Type driver = RimKataActiveModTypes.Find("PocketSand.JobDriver_Equip");
            if (driver == null) return;
            var harmony = new Harmony("krwf.rimkata.pocketsand");
            try
            {
                MethodInfo exchange = null;
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(driver))
                {
                    if (method.IsStatic || method.ReturnType != typeof(void)
                        || method.GetParameters().Length != 0) continue;
                    int primaryCalls = 0, roomCalls = 0, addCalls = 0;
                    foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
                    {
                        if (instruction.Calls(PrimaryGetter)) primaryCalls++;
                        if (instruction.Calls(MakeRoom)) roomCalls++;
                        if (instruction.Calls(AddEquipment)) addCalls++;
                    }
                    if (primaryCalls != 1 || roomCalls != 1 || addCalls != 1) continue;
                    if (exchange != null)
                        throw new InvalidOperationException("Multiple PocketSand equipment exchanges matched.");
                    exchange = method;
                }
                if (exchange == null || !typeof(JobDriver).IsAssignableFrom(driver))
                    throw new InvalidOperationException("PocketSand equipment exchange API did not match.");
                RimKataStartupPatches.Patch(harmony, exchange, transpiler: new HarmonyMethod(
                    typeof(RimKataPocketSandCompat), nameof(ExchangeTranspiler)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] PocketSand equipment exchange integration was not applied.\n" + exception);
            }
            ApplySecondaryClicks(driver.Assembly);
        }

        private static void ApplySecondaryClicks(Assembly assembly)
        {
            var harmony = new Harmony("krwf.rimkata.pocketsand.secondary");
            try
            {
                Type single = RimKataActiveModTypes.Find(assembly, "PocketSand.Gizmo_WeaponSelector");
                Type multi = RimKataActiveModTypes.Find(assembly, "PocketSand.Gizmo_WeaponSelectorMulti");
                Type extensions = RimKataActiveModTypes.Find(assembly, "PocketSand.PawnExtensions");
                if (single == null || multi == null || extensions == null)
                    throw new InvalidOperationException("PocketSand weapon selector API did not match.");

                singlePawn = AccessTools.FieldRefAccess<Pawn>(single, "m_Pawn");
                singleTarget = AccessTools.FieldRefAccess<ThingWithComps>(single, "m_Target");
                singleMerged = AccessTools.FieldRefAccess<IList>(single, "m_Merged");
                singleInteraction = AccessTools.FieldRefAccess<int>(single, "m_Interaction");
                multiPawns = AccessTools.FieldRefAccess<List<Pawn>>(multi, "m_Pawns");
                multiTarget = AccessTools.FieldRefAccess<ThingDef>(multi, "m_Target");
                multiInteraction = AccessTools.FieldRefAccess<int>(multi, "m_Interaction");
                singleEquipInteraction = InteractionValue(single, "EquipWeapon");
                singleNoInteraction = InteractionValue(single, "None");
                multiEquipInteraction = InteractionValue(multi, "EquipWeapon");
                multiNoInteraction = InteractionValue(multi, "None");

                MethodInfo find = AccessTools.Method(extensions, "TryFindInInventory", new[]
                {
                    typeof(Pawn), typeof(ThingDef), typeof(bool), typeof(ThingWithComps).MakeByRefType()
                });
                if (find == null || !find.IsStatic || find.ReturnType != typeof(bool))
                    throw new InvalidOperationException("PocketSand inventory selection API did not match.");
                findInventoryWeapon = AccessTools.MethodDelegate<FindInventoryWeapon>(find);

                RimKataStartupPatches.Patch(harmony,
                    AccessTools.Method(single, "ProcessInput", new[] { typeof(Event) }),
                    prefix: new HarmonyMethod(typeof(RimKataPocketSandCompat), nameof(SingleInputPrefix)));
                RimKataStartupPatches.Patch(harmony,
                    AccessTools.Method(multi, "ProcessInput", new[] { typeof(Event) }),
                    prefix: new HarmonyMethod(typeof(RimKataPocketSandCompat), nameof(MultiInputPrefix)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] PocketSand secondary weapon click integration was not applied.\n" + exception);
            }
        }

        private static int InteractionValue(Type selector, string name)
        {
            Type interaction = AccessTools.Field(selector, "m_Interaction")?.FieldType;
            if (interaction == null || !interaction.IsEnum
                || Enum.GetUnderlyingType(interaction) != typeof(int)
                || !Enum.IsDefined(interaction, name))
                throw new InvalidOperationException("PocketSand weapon selector interaction API did not match.");
            return Convert.ToInt32(Enum.Parse(interaction, name));
        }

        private static bool SingleInputPrefix(object __instance, Event __0)
        {
            // GUI.Button has already resolved the icon click; the event may be Used.
            // Read eligibility and inventory only for an actual middle-clicked weapon.
            if (__0?.button != 2 || singleInteraction(__instance) != singleEquipInteraction)
                return true;
            ThingWithComps target = singleTarget(__instance);
            if (target == null) return true;

            try
            {
                IList merged = singleMerged(__instance);
                if (merged == null)
                {
                    RimKataSidearmInventoryEquip.TryEquip(singlePawn(__instance), target);
                }
                else
                {
                    // PocketSand includes this selector in its merged list.
                    var seen = new HashSet<Pawn>();
                    for (int i = 0; i < merged.Count; i++)
                    {
                        Pawn pawn = singlePawn(merged[i]);
                        if (pawn != null && seen.Add(pawn)) EquipInventoryWeapon(pawn, target.def);
                    }
                }
            }
            finally
            {
                singleInteraction(__instance) = singleNoInteraction;
                __0.Use();
            }
            return false;
        }

        private static bool MultiInputPrefix(object __instance, Event __0)
        {
            if (__0?.button != 2 || multiInteraction(__instance) != multiEquipInteraction)
                return true;
            ThingDef target = multiTarget(__instance);
            if (target == null) return true;

            try
            {
                List<Pawn> pawns = multiPawns(__instance);
                if (pawns != null)
                {
                    var seen = new HashSet<Pawn>();
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Pawn pawn = pawns[i];
                        if (pawn != null && seen.Add(pawn)) EquipInventoryWeapon(pawn, target);
                    }
                }
            }
            finally
            {
                multiInteraction(__instance) = multiNoInteraction;
                __0.Use();
            }
            return false;
        }

        private static void EquipInventoryWeapon(Pawn pawn, ThingDef def)
        {
            if (pawn.inventory != null
                && findInventoryWeapon(pawn, def, false, out ThingWithComps weapon))
                RimKataSidearmInventoryEquip.TryEquip(pawn, weapon);
        }

        private static IEnumerable<CodeInstruction> ExchangeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo primary = AccessTools.Method(typeof(RimKataPocketSandCompat), nameof(PrimaryAfterStoring));
            MethodInfo room = AccessTools.Method(typeof(RimKataPocketSandCompat), nameof(MakeRoomForReplacement));
            int primaryCalls = 0, roomCalls = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(PrimaryGetter))
                {
                    var driver = new CodeInstruction(OpCodes.Ldarg_0);
                    driver.MoveLabelsFrom(instruction);
                    driver.MoveBlocksFrom(instruction);
                    yield return driver;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = primary;
                    primaryCalls++;
                }
                else if (instruction.Calls(MakeRoom))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = room;
                    roomCalls++;
                }
                yield return instruction;
            }
            if (primaryCalls != 1 || roomCalls != 1)
                throw new InvalidOperationException("PocketSand equipment exchange calls changed.");
        }

        private static ThingWithComps PrimaryAfterStoring(Pawn_EquipmentTracker tracker, JobDriver driver)
        {
            Job job = driver.job;
            ThingWithComps incoming = job?.targetA.Thing as ThingWithComps;
            if (CanCompleteReplacement(tracker, incoming)) return null;

            // PocketSand's fist command stores the old primary with no replacement.
            // Store only the registered offhand promoted by that removal, using the
            // same inventory-first/drop fallback as PocketSand's original exchange.
            if (job != null && !job.targetA.IsValid
                && RimKataSecondaryWeaponRegistry.CurrentRegistry
                    ?.TryTakePrimaryReplacement(tracker.pawn, out ThingWithComps secondary) == true)
            {
                if (tracker.pawn.inventory?.innerContainer.TryAddOrTransfer(secondary, true) != true)
                    tracker.TryDropEquipment(secondary, out _, tracker.pawn.Position, false);
            }
            return tracker.Primary;
        }

        private static void MakeRoomForReplacement(Pawn_EquipmentTracker tracker, ThingWithComps incoming)
        {
            if (!CanCompleteReplacement(tracker, incoming)) tracker.MakeRoomFor(incoming);
        }

        private static bool CanCompleteReplacement(Pawn_EquipmentTracker tracker, ThingWithComps incoming)
        {
            return incoming != null && !incoming.Destroyed
                && incoming.def?.equipmentType == EquipmentType.Primary
                && incoming != tracker.Primary
                && RimKataSecondaryWeaponRegistry.CurrentRegistry
                    ?.HasPendingPrimaryReplacement(tracker.pawn) == true;
        }
    }
}
