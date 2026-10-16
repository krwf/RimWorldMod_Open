using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Entry = KRWF.RimKata.RimKataResponseVisualParticipantCache.BodyVisualEntry;

namespace KRWF.RimKata
{
    internal static class RimKataRegisteredPawnGate
    {
        internal static List<CodeInstruction> Branch(ILGenerator generator,
            IEnumerable<CodeInstruction> loadPawn, Label absent, bool qualifiedOnly,
            out LocalBuilder entry)
        {
            LocalBuilder pawn = generator.DeclareLocal(typeof(Pawn));
            LocalBuilder id = generator.DeclareLocal(typeof(int));
            LocalBuilder pages = generator.DeclareLocal(typeof(Entry[][]));
            LocalBuilder page = generator.DeclareLocal(typeof(Entry[]));
            int shift = (int)AccessTools.Field(typeof(RimKataResponseVisualParticipantCache), "BodyPageShift").GetRawConstantValue();
            int mask = (1 << shift) - 1;
            entry = generator.DeclareLocal(typeof(Entry));
            var codes = new List<CodeInstruction>(loadPawn);
            codes.AddRange(new[] {
                new CodeInstruction(OpCodes.Stloc, pawn),
                new CodeInstruction(OpCodes.Ldloc, pawn), new CodeInstruction(OpCodes.Brfalse, absent),
                new CodeInstruction(OpCodes.Ldloc, pawn),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber))),
                new CodeInstruction(OpCodes.Stloc, id),
                new CodeInstruction(OpCodes.Ldloc, id), new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Ble, absent),
                new CodeInstruction(OpCodes.Volatile),
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(RimKataResponseVisualParticipantCache), "bodyVisualPages")),
                new CodeInstruction(OpCodes.Stloc, pages),
                new CodeInstruction(OpCodes.Ldloc, id), new CodeInstruction(OpCodes.Ldc_I4, shift),
                new CodeInstruction(OpCodes.Shr), new CodeInstruction(OpCodes.Ldloc, pages),
                new CodeInstruction(OpCodes.Ldlen), new CodeInstruction(OpCodes.Conv_I4),
                new CodeInstruction(OpCodes.Bge_Un, absent),
                new CodeInstruction(OpCodes.Ldloc, pages), new CodeInstruction(OpCodes.Ldloc, id),
                new CodeInstruction(OpCodes.Ldc_I4, shift), new CodeInstruction(OpCodes.Shr),
                new CodeInstruction(OpCodes.Ldelema, typeof(Entry[])),
                new CodeInstruction(OpCodes.Volatile), new CodeInstruction(OpCodes.Ldind_Ref),
                new CodeInstruction(OpCodes.Stloc, page),
                new CodeInstruction(OpCodes.Ldloc, page), new CodeInstruction(OpCodes.Brfalse, absent),
                new CodeInstruction(OpCodes.Ldloc, page), new CodeInstruction(OpCodes.Ldloc, id),
                new CodeInstruction(OpCodes.Ldc_I4, mask), new CodeInstruction(OpCodes.And),
                new CodeInstruction(OpCodes.Ldelema, typeof(Entry)),
                new CodeInstruction(OpCodes.Volatile), new CodeInstruction(OpCodes.Ldind_Ref),
                new CodeInstruction(OpCodes.Stloc, entry),
                new CodeInstruction(OpCodes.Ldloc, entry), new CodeInstruction(OpCodes.Brfalse, absent),
                new CodeInstruction(OpCodes.Ldloc, entry),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Entry), nameof(Entry.pawn))),
                new CodeInstruction(OpCodes.Ldloc, pawn), new CodeInstruction(OpCodes.Bne_Un, absent)
            });
            if (qualifiedOnly) codes.AddRange(new[] {
                new CodeInstruction(OpCodes.Ldloc, entry),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Entry), nameof(Entry.registeredQualified))),
                new CodeInstruction(OpCodes.Brfalse, absent)
            });
            return codes;
        }
    }
}
