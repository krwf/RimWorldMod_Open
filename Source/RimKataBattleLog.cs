using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.Grammar;

namespace KRWF.RimKata
{
    [HarmonyPatch(typeof(BattleLogEntry_StateTransition), "GenerateGrammarRequest")]
    internal static class Patch_BattleLogEntryStateTransition_RimKataMissingPart
    {
        private static readonly MethodInfo HediffRulesMethod = AccessTools.Method(
            typeof(GrammarUtility), nameof(GrammarUtility.RulesForHediffDef));
        private static readonly MethodInfo SafeHediffRulesMethod = AccessTools.Method(
            typeof(Patch_BattleLogEntryStateTransition_RimKataMissingPart), nameof(HediffRules));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(HediffRulesMethod))
                {
                    // Add this entry to the existing arguments without changing
                    // the rest of vanilla's transition grammar or its saved data.
                    var entry = new CodeInstruction(OpCodes.Ldarg_0);
                    entry.labels.AddRange(instruction.labels);
                    entry.blocks.AddRange(instruction.blocks);
                    instruction.labels.Clear();
                    instruction.blocks.Clear();
                    yield return entry;
                    yield return new CodeInstruction(OpCodes.Call, SafeHediffRulesMethod);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        private static IEnumerable<Rule> HediffRules(
            string prefix, HediffDef def, BodyPartRecord part, BattleLogEntry_StateTransition entry)
        {
            if (part != null || def == null || !InvolvesRimKata(entry))
            {
                return GrammarUtility.RulesForHediffDef(prefix, def, part);
            }

            return RulesWithoutPart(prefix, def);
        }

        private static bool InvolvesRimKata(BattleLogEntry_StateTransition entry)
        {
            foreach (Thing concern in entry.GetConcerns())
            {
                if (concern is Pawn pawn && RimKataEligibility.HasRimKataAccess(pawn))
                {
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<Rule> RulesWithoutPart(string prefix, HediffDef def)
        {
            foreach (Rule rule in GrammarUtility.RulesForDef(prefix, def))
            {
                yield return rule;
            }

            if (!prefix.NullOrEmpty()) prefix += "_";
            string noun = def.labelNoun.NullOrEmpty() ? def.label : def.labelNoun;
            yield return new Rule_String(prefix + "label", def.label);
            yield return new Rule_String(prefix + "labelNoun", noun);
            // Old logs can retain an injury without its body part. Keep the
            // injury and participants, but do not invent a location for it.
            yield return new Rule_String(prefix + "labelNounPretty", noun);
        }
    }
}
