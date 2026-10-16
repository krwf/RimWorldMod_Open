using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // The synthetic trait exists only in a character-card list, never in a pawn's TraitSet or DefDatabase.
    [HarmonyPatch]
    internal static class Patch_CharacterCard_RimKataQualification
    {
        private static bool resolved, layoutPatched, drawerPatched, warned;
        private static MethodInfo section, drawer;
        private static FieldInfo displayList;
        private static int displayLocal;
        private static readonly FieldInfo AllTraits = AccessTools.Field(typeof(TraitSet), nameof(TraitSet.allTraits));
        private static readonly MethodInfo SortedTraits = AccessTools.PropertyGetter(typeof(TraitSet), nameof(TraitSet.TraitsSorted));
        private static readonly Trait DisplayTrait = new Trait(new TraitDef
        {
            defName = "RimKata_QualificationDisplay",
            degreeDatas = new List<TraitDegreeData> { new TraitDegreeData { degree = 0, label = "RimKata" } }
        }, 0, false);

        private sealed class DisplayTraits : List<Trait>
        {
            private readonly Pawn pawn;
            private readonly bool unrestricted;
            internal readonly GenUI.StackElementDrawer<Trait> draw;
            internal GenUI.StackElementDrawer<Trait> originalDrawer;

            internal DisplayTraits(List<Trait> originals, Pawn pawn, bool unrestricted)
                : base(originals.Count + 1)
            {
                AddRange(originals);
                Add(DisplayTrait);
                this.pawn = pawn;
                this.unrestricted = unrestricted;
                draw = Draw;
            }

            private void Draw(Rect rect, Trait trait)
            {
                if (!ReferenceEquals(trait, DisplayTrait))
                {
                    originalDrawer(rect, trait);
                    return;
                }

                Color color = GUI.color;
                try
                {
                    GUI.color = CharacterCardUtility.StackElementBackground;
                    GUI.DrawTexture(rect, BaseContent.WhiteTex);
                    GUI.color = color;
                    bool hovered = Mouse.IsOver(rect);
                    if (hovered) Widgets.DrawHighlight(rect);
                    GUI.color = unrestricted ? ColoredText.NameColor : color;
                    Widgets.Label(new Rect(rect.x + 5f, rect.y, rect.width - 10f, rect.height), "RimKata");
                    if (hovered)
                        TooltipHandler.TipRegion(rect,
                            () => RimKataEligibilityCache.GetQualificationTooltip(pawn),
                            pawn.thingIDNumber ^ 183481371);
                }
                finally { GUI.color = color; }
            }
        }

        private static bool Prepare()
        {
            if (resolved) return section != null && drawer != null;
            resolved = true;
            try
            {
                MethodInfo selectedSection = AccessTools.Method(typeof(CharacterCardUtility), "DoLeftSection",
                    new[] { typeof(Rect), typeof(Rect), typeof(Pawn) });
                if (selectedSection == null) throw new MissingMethodException("CharacterCardUtility.DoLeftSection");
                List<CodeInstruction> layout = PatchProcessor.GetOriginalInstructions(selectedSection);
                var candidates = new HashSet<MethodInfo>();
                foreach (CodeInstruction instruction in layout)
                {
                    if (instruction.opcode != OpCodes.Ldftn || !(instruction.operand is MethodInfo method)
                        || method.IsStatic || method.ReturnType != typeof(void)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(Rect)) continue;
                    foreach (CodeInstruction body in PatchProcessor.GetOriginalInstructions(method))
                    {
                        if (body.opcode == OpCodes.Ldstr && body.operand as string == "Traits")
                        {
                            candidates.Add(method);
                            break;
                        }
                    }
                }
                if (candidates.Count != 1)
                    throw new InvalidOperationException("Expected one traits-section drawer, found " + candidates.Count);
                MethodInfo selectedDrawer = null;
                foreach (MethodInfo candidate in candidates) selectedDrawer = candidate;
                FieldInfo selectedList = AccessTools.Field(selectedDrawer.DeclaringType, "traits");
                if (selectedList?.FieldType != typeof(List<Trait>))
                    throw new MissingFieldException("Traits drawer display list");
                int selectedLocal = -1;
                foreach (LocalVariableInfo local in selectedSection.GetMethodBody().LocalVariables)
                {
                    if (local.LocalType != selectedDrawer.DeclaringType) continue;
                    if (selectedLocal >= 0) throw new InvalidOperationException("Multiple traits drawer locals");
                    selectedLocal = local.LocalIndex;
                }
                if (selectedLocal < 0) throw new InvalidOperationException("Traits drawer local not found");
                if (!HasLayout(layout, true)
                    || !HasLayout(PatchProcessor.GetOriginalInstructions(selectedDrawer), false))
                    throw new InvalidOperationException("Character-card trait list layout changed");

                section = selectedSection;
                drawer = selectedDrawer;
                displayList = selectedList;
                displayLocal = selectedLocal;
                return true;
            }
            catch (Exception exception)
            {
                Warn(exception.GetBaseException().Message);
                return false;
            }
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return section;
            yield return drawer;
        }

        private static bool IsTraitStack(CodeInstruction instruction)
            => instruction.operand is MethodInfo method && method.DeclaringType == typeof(GenUI)
                && method.Name == nameof(GenUI.DrawElementStack) && method.IsGenericMethod
                && method.GetGenericArguments()[0] == typeof(Trait);

        private static bool HasLayout(IEnumerable<CodeInstruction> instructions, bool layout)
        {
            int all = 0, sorted = 0, stack = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, AllTraits)) all++;
                if (instruction.Calls(SortedTraits)) sorted++;
                if (IsTraitStack(instruction)) stack++;
            }
            return sorted == 1 && stack == 1 && all == (layout ? 1 : 0);
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            bool layout = Equals(__originalMethod, section);
            if (layout) layoutPatched = false;
            else drawerPatched = false;
            var codes = new List<CodeInstruction>(instructions);
            if (!HasLayout(codes, layout))
            {
                Warn("Another patch changed the character-card trait list layout");
                return codes;
            }

            MethodInfo create = AccessTools.Method(typeof(Patch_CharacterCard_RimKataQualification), nameof(CreateDisplayList));
            MethodInfo sorted = AccessTools.Method(typeof(Patch_CharacterCard_RimKataQualification), nameof(SortedDisplayList));
            MethodInfo draw = AccessTools.Method(typeof(Patch_CharacterCard_RimKataQualification), nameof(DrawDisplayStack));
            var result = new List<CodeInstruction>(codes.Count + 6);
            foreach (CodeInstruction instruction in codes)
            {
                if (layout && instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, AllTraits))
                {
                    CodeInstruction pawn = new CodeInstruction(OpCodes.Ldarg_2);
                    MoveEntryMetadata(instruction, pawn);
                    result.Add(pawn);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = create;
                }
                else if (instruction.Calls(SortedTraits))
                {
                    CodeInstruction owner = layout ? LoadLocal(displayLocal) : new CodeInstruction(OpCodes.Ldarg_0);
                    MoveEntryMetadata(instruction, owner);
                    result.Add(owner);
                    result.Add(new CodeInstruction(OpCodes.Ldfld, displayList));
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = sorted;
                }
                else if (!layout && IsTraitStack(instruction))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = draw;
                }
                result.Add(instruction);
            }
            if (layout) layoutPatched = true;
            else drawerPatched = true;
            return result;
        }

        private static CodeInstruction LoadLocal(int index)
        {
            switch (index)
            {
                case 0: return new CodeInstruction(OpCodes.Ldloc_0);
                case 1: return new CodeInstruction(OpCodes.Ldloc_1);
                case 2: return new CodeInstruction(OpCodes.Ldloc_2);
                case 3: return new CodeInstruction(OpCodes.Ldloc_3);
                default: return index <= byte.MaxValue
                    ? new CodeInstruction(OpCodes.Ldloc_S, (byte)index)
                    : new CodeInstruction(OpCodes.Ldloc, (short)index);
            }
        }

        private static void MoveEntryMetadata(CodeInstruction original, CodeInstruction first)
        {
            first.labels.AddRange(original.labels);
            original.labels.Clear();
            first.blocks.AddRange(original.blocks);
            original.blocks.Clear();
        }

        private static List<Trait> CreateDisplayList(TraitSet traits, Pawn pawn)
        {
            // Only the displayed, already-qualified pawn gets a copied UI list.
            if (!layoutPatched || !drawerPatched
                || !RimKataEligibilityCache.TryGetQualificationDisplay(pawn, out bool unrestricted))
                return traits.allTraits;
            return new DisplayTraits(traits.TraitsSorted, pawn, unrestricted);
        }

        private static List<Trait> SortedDisplayList(TraitSet traits, List<Trait> shown)
            => shown is DisplayTraits ? shown : traits.TraitsSorted;

        private static Rect DrawDisplayStack(Rect rect, float rowHeight, List<Trait> elements,
            GenUI.StackElementDrawer<Trait> originalDrawer, GenUI.StackElementWidthGetter<Trait> widthGetter,
            float rowMargin, float elementMargin, bool allowOrderOptimization)
        {
            GenUI.StackElementDrawer<Trait> selectedDrawer = originalDrawer;
            if (elements is DisplayTraits display)
            {
                display.originalDrawer = originalDrawer;
                selectedDrawer = display.draw;
            }
            return GenUI.DrawElementStack(rect, rowHeight, elements, selectedDrawer, widthGetter,
                rowMargin, elementMargin, allowOrderOptimization);
        }

        private static void Warn(string reason)
        {
            if (warned) return;
            warned = true;
            Log.Warning("[RimKata] The qualification entry could not attach to the character card; "
                + "the original traits display is unchanged. " + reason);
        }
    }
}

