using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Registry/equipment/access events prepare the pair. GUI code only reads it.
    internal static class RimKataColonistBarWeaponCache
    {
        private sealed class WeaponPair
        {
            internal ThingWithComps primary;
            internal ThingWithComps secondary;
        }

        private static readonly Dictionary<Pawn, WeaponPair> registeredPairs =
            new Dictionary<Pawn, WeaponPair>();
        private static readonly Dictionary<Thing, ThingWithComps> displayedWeapons =
            new Dictionary<Thing, ThingWithComps>();

        internal static ThingWithComps SecondaryFor(Thing primary)
        {
            return primary != null
                && displayedWeapons.TryGetValue(primary, out ThingWithComps secondary)
                    ? secondary
                    : null;
        }

        internal static void Set(Pawn pawn, ThingWithComps secondary,
            bool accessVerified = false, bool slotVerified = false,
            ThingWithComps heldPairPrimary = null)
        {
            if (pawn == null) return;
            if (!registeredPairs.TryGetValue(pawn, out WeaponPair pair))
            {
                if (secondary == null) return;
                pair = new WeaponPair();
                registeredPairs.Add(pawn, pair);
            }

            if (secondary == null)
            {
                Hide(pair);
                registeredPairs.Remove(pawn);
                return;
            }

            pair.secondary = secondary;
            Refresh(pawn, pair, accessVerified, slotVerified, heldPairPrimary);
        }

        internal static void Refresh(Pawn pawn)
        {
            if (pawn != null && registeredPairs.TryGetValue(pawn, out WeaponPair pair))
                Refresh(pawn, pair);
        }

        internal static void RefreshFaction(Faction faction)
        {
            foreach (KeyValuePair<Pawn, WeaponPair> entry in registeredPairs)
            {
                if (entry.Key.Faction == faction) Refresh(entry.Key, entry.Value);
            }
        }

        internal static void RefreshAll()
        {
            foreach (KeyValuePair<Pawn, WeaponPair> entry in registeredPairs)
                Refresh(entry.Key, entry.Value);
        }

        private static void Refresh(Pawn pawn, WeaponPair pair,
            bool accessVerified = false, bool slotVerified = false,
            ThingWithComps heldPairPrimary = null)
        {
            Hide(pair);
            ThingWithComps primary = heldPairPrimary ?? pawn.equipment?.Primary;
            ThingWithComps secondary = pair.secondary;
            // Reuse only facts verified by this event, never store validation flags.
            if (primary == null || secondary == null
                || (heldPairPrimary == null && (secondary.Destroyed
                    || secondary == primary
                    || !pawn.equipment.AllEquipmentListForReading.Contains(secondary)))
                || (!slotVerified && !RimKataVisualUtility.IsSecondaryUsable(pawn, primary, secondary))
                || (!accessVerified && !RimKataEligibility.HasRimKataAccess(pawn)))
            {
                return;
            }

            pair.primary = primary;
            displayedWeapons[primary] = secondary;
        }

        private static void Hide(WeaponPair pair)
        {
            if (pair.primary == null) return;
            displayedWeapons.Remove(pair.primary);
            pair.primary = null;
        }

        internal static void Reset()
        {
            displayedWeapons.Clear();
            registeredPairs.Clear();
        }
    }

    [HarmonyPatch(typeof(Current), nameof(Current.Game), MethodType.Setter)]
    internal static class Patch_CurrentGame_RimKataColonistBarWeapons
    {
        private static void Prefix(Game __0)
        {
            if (!ReferenceEquals(Current.Game, __0))
            {
                RimKataColonistBarWeaponCache.Reset();
                RimKataEligibilityCache.ResetGame();
                RimKataNeutralTargetInvalidation.ResetGame();
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    internal static class Patch_PawnSetFaction_RimKataColonistBarWeapons
    {
        private static void Postfix(Pawn __instance)
        {
            RimKataNeutralTargetInvalidation.Invalidate(__instance.Map);
            RimKataEligibilityCache.NotifyPawnSpawned(__instance);
            RimKataColonistBarWeaponCache.Refresh(__instance);
        }
    }

    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_RelationKindChanged))]
    internal static class Patch_FactionRelation_RimKataColonistBarWeapons
    {
        private static void Postfix(Faction __instance, Faction other)
        {
            RimKataNeutralTargetInvalidation.InvalidateAll();
            if (other == Faction.OfPlayer)
            {
                RimKataEligibilityCache.RefreshFaction(__instance);
                RimKataColonistBarWeaponCache.RefreshFaction(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.ColonistBarOnGUI))]
    public static class Patch_ColonistBar_RimKataDualWeaponIcons
    {
        private const float SecondaryAngle = 45f;

        private static readonly MethodInfo ThingIconMethod = AccessTools.Method(
            typeof(Widgets),
            nameof(Widgets.ThingIcon),
            new[]
            {
                typeof(Rect),
                typeof(Thing),
                typeof(float),
                typeof(Rot4?),
                typeof(bool),
                typeof(float),
                typeof(bool)
            });
        private static readonly MethodInfo DrawSecondaryWeaponIconMethod = AccessTools.Method(
            typeof(Patch_ColonistBar_RimKataDualWeaponIcons),
            nameof(DrawSecondaryWeaponIcon));

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int thingIconIndex = -1;
            int thingIconCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(ThingIconMethod))
                {
                    thingIconIndex = i;
                    thingIconCount++;
                }
            }

            if (thingIconCount != 1 || thingIconIndex < 0)
            {
                Log.Warning(
                    "[RimKata] Expected one colonist-bar weapon icon call for the secondary overlay, but found "
                    + thingIconCount
                    + ".");
                return codes;
            }

            if (DrawSecondaryWeaponIconMethod == null)
            {
                Log.Warning("[RimKata] Could not resolve the colonist-bar secondary weapon icon helper.");
                return codes;
            }

            if (codes[thingIconIndex].blocks.Count != 0)
            {
                Log.Warning(
                    "[RimKata] Kept the vanilla colonist-bar weapon icon call unchanged because its exception metadata was not safe to augment.");
                return codes;
            }

            LocalBuilder rectLocal = generator.DeclareLocal(typeof(Rect));
            LocalBuilder primaryLocal = generator.DeclareLocal(typeof(Thing));
            LocalBuilder alphaLocal = generator.DeclareLocal(typeof(float));
            LocalBuilder rotLocal = generator.DeclareLocal(typeof(Rot4?));
            LocalBuilder stackOfOneLocal = generator.DeclareLocal(typeof(bool));
            LocalBuilder scaleLocal = generator.DeclareLocal(typeof(float));
            LocalBuilder grayscaleLocal = generator.DeclareLocal(typeof(bool));

            CodeInstruction first = new CodeInstruction(
                OpCodes.Stloc,
                grayscaleLocal);
            first.labels.AddRange(codes[thingIconIndex].labels);
            codes[thingIconIndex].labels.Clear();

            codes.InsertRange(
                thingIconIndex,
                new[]
                {
                    first,
                    new CodeInstruction(OpCodes.Stloc, scaleLocal),
                    new CodeInstruction(OpCodes.Stloc, stackOfOneLocal),
                    new CodeInstruction(OpCodes.Stloc, rotLocal),
                    new CodeInstruction(OpCodes.Stloc, alphaLocal),
                    new CodeInstruction(OpCodes.Stloc, primaryLocal),
                    new CodeInstruction(OpCodes.Stloc, rectLocal),
                    new CodeInstruction(OpCodes.Ldloc, rectLocal),
                    new CodeInstruction(OpCodes.Ldloc, primaryLocal),
                    new CodeInstruction(OpCodes.Ldloc, alphaLocal),
                    new CodeInstruction(OpCodes.Ldloc, rotLocal),
                    new CodeInstruction(OpCodes.Ldloc, stackOfOneLocal),
                    new CodeInstruction(OpCodes.Ldloc, scaleLocal),
                    new CodeInstruction(OpCodes.Ldloc, grayscaleLocal),
                    new CodeInstruction(OpCodes.Call, DrawSecondaryWeaponIconMethod),
                    new CodeInstruction(OpCodes.Ldloc, rectLocal),
                    new CodeInstruction(OpCodes.Ldloc, primaryLocal),
                    new CodeInstruction(OpCodes.Ldloc, alphaLocal),
                    new CodeInstruction(OpCodes.Ldloc, rotLocal),
                    new CodeInstruction(OpCodes.Ldloc, stackOfOneLocal),
                    new CodeInstruction(OpCodes.Ldloc, scaleLocal),
                    new CodeInstruction(OpCodes.Ldloc, grayscaleLocal)
                });

            return codes;
        }

        public static void DrawSecondaryWeaponIcon(
            Rect rect,
            Thing primary,
            float alpha,
            Rot4? rot,
            bool stackOfOne,
            float scale,
            bool grayscale)
        {
            ThingWithComps secondary = RimKataColonistBarWeaponCache.SecondaryFor(primary);
            if (secondary != null)
            {
                DrawRotatedIcon(
                    rect,
                    secondary,
                    SecondaryAngle,
                    alpha,
                    rot,
                    stackOfOne,
                    scale,
                    grayscale);
            }
        }

        private static void DrawRotatedIcon(
            Rect rect,
            Thing weapon,
            float angle,
            float alpha,
            Rot4? rot,
            bool stackOfOne,
            float scale,
            bool grayscale)
        {
            Matrix4x4 previousMatrix = GUI.matrix;
            try
            {
                UI.RotateAroundPivot(angle, rect.center);
                Widgets.ThingIcon(rect, weapon, alpha, rot, stackOfOne, scale, grayscale);
            }
            finally
            {
                GUI.matrix = previousMatrix;
            }
        }
    }

    [HarmonyPatch(typeof(ColonistBarColonistDrawer), "DrawIcons")]
    public static class Patch_ColonistBarColonistDrawer_RimKataAttackIcon
    {
        private static readonly FieldInfo AttackStaticField = AccessTools.Field(
            typeof(JobDefOf),
            nameof(JobDefOf.AttackStatic));
        private static readonly MethodInfo IsAttackJobMethod = AccessTools.Method(
            typeof(Patch_ColonistBarColonistDrawer_RimKataAttackIcon),
            nameof(IsAttackJob),
            new[] { typeof(JobDef), typeof(Pawn) });

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int comparisonIndex = -1;
            int comparisonCount = 0;
            for (int i = 0; i < codes.Count - 1; i++)
            {
                if (codes[i].opcode == OpCodes.Ldsfld
                    && Equals(codes[i].operand, AttackStaticField)
                    && (codes[i + 1].opcode == OpCodes.Bne_Un
                        || codes[i + 1].opcode == OpCodes.Bne_Un_S))
                {
                    comparisonIndex = i;
                    comparisonCount++;
                }
            }

            if (comparisonCount != 1
                || comparisonIndex < 0
                || IsAttackJobMethod == null)
            {
                Log.Warning(
                    "[RimKata] Expected one colonist-bar AttackStatic comparison for the RimKata combat icon, but found "
                    + comparisonCount
                    + ".");
                return codes;
            }

            CodeInstruction comparison = codes[comparisonIndex];
            CodeInstruction branch = codes[comparisonIndex + 1];
            comparison.opcode = OpCodes.Ldarg_2;
            comparison.operand = null;
            codes.Insert(
                comparisonIndex + 1,
                new CodeInstruction(OpCodes.Call, IsAttackJobMethod));
            branch.opcode =
                branch.opcode == OpCodes.Bne_Un
                    ? OpCodes.Brfalse
                    : OpCodes.Brfalse_S;
            return codes;
        }

        public static bool IsAttackJob(JobDef jobDef, Pawn pawn)
        {
            return jobDef == JobDefOf.AttackStatic
                || RimKataDualWeaponController
                    .IsCombatActiveForPortrait(pawn, jobDef);
        }
    }
}
