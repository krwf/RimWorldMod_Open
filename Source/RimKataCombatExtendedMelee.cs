using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedMelee
    {
        private static MethodInfo ceDodgeChance;
        private static readonly MethodInfo ResolveDefense = AccessTools.Method(
            typeof(RimKataDefenseUtility), nameof(RimKataDefenseUtility.TryResolveCombatExtendedMeleeDefense));
        private static readonly MethodInfo RandChance = AccessTools.Method(
            typeof(Rand), nameof(Rand.Chance), new[] { typeof(float) });
        private static readonly MethodInfo DodgeSound = AccessTools.Method(
            typeof(Verb_MeleeAttack), "SoundDodge");
        private static readonly MethodInfo NotifyAttack = AccessTools.Method(
            typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.Notify_MeleeAttackOn));
        private static readonly MethodInfo Spawned = AccessTools.PropertyGetter(
            typeof(Thing), nameof(Thing.Spawned));
        private static readonly MethodInfo Stagger = AccessTools.Method(
            typeof(StaggerHandler), nameof(StaggerHandler.StaggerFor), new[] { typeof(int), typeof(float) });

        internal static bool Apply(Harmony harmony)
        {
            Type ceMelee = AccessTools.TypeByName("CombatExtended.Verb_MeleeAttackCE");
            if (ceMelee == null) return true;

            MethodInfo attack = AccessTools.DeclaredMethod(ceMelee, "TryCastShot");
            ceDodgeChance = AccessTools.DeclaredMethod(ceMelee, "GetDodgeChance", new[] { typeof(Pawn) });
            if (attack == null || ceDodgeChance == null
                || !TryFindLayout(PatchProcessor.GetOriginalInstructions(attack, (ILGenerator)null),
                    out _, out _, out _, out _, out _, out _))
            {
                Log.Warning("[RimKata] Combat Extended melee layout was not recognized; keeping its original melee defense.");
                return false;
            }

            // CE's override never calls vanilla TryCastShot. Reuse its context and
            // gun-butt replacement hooks once, without patching CE damage/armor.
            Type context = typeof(Patch_Verb_MeleeAttack_Context);
            harmony.Patch(attack,
                prefix: new HarmonyMethod(context, nameof(Patch_Verb_MeleeAttack_Context.Prefix)),
                postfix: new HarmonyMethod(context, nameof(Patch_Verb_MeleeAttack_Context.Postfix)),
                transpiler: new HarmonyMethod(typeof(RimKataCombatExtendedMelee), nameof(Transpiler)),
                finalizer: new HarmonyMethod(context, nameof(Patch_Verb_MeleeAttack_Context.Finalizer)));
            return true;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (!TryFindLayout(codes, out int defenseStart, out int dodgeStart,
                out int resultStore, out int attackerFollowup, out int staggerGuard, out int afterStagger))
            {
                Log.Warning("[RimKata] Combat Extended melee instructions changed; keeping its original melee defense.");
                return codes;
            }

            LocalBuilder parried = generator.DeclareLocal(typeof(bool));
            Label continueCeDefense = generator.DefineLabel();
            Label dodged = generator.DefineLabel();
            Label followup = generator.DefineLabel();
            Label skipStagger = generator.DefineLabel();
            Label parry = generator.DefineLabel();
            codes[dodgeStart].labels.Add(dodged);
            codes[attackerFollowup].labels.Add(followup);
            codes[afterStagger].labels.Add(skipStagger);
            CodeInstruction storeFailedHit = new CodeInstruction(codes[resultStore].opcode, codes[resultStore].operand);

            // A successful RimKata parry skips defender stagger, while keeping CE's
            // attacker animation, facing, attack notification and result handling.
            CodeInstruction guard = new CodeInstruction(OpCodes.Ldloc, parried);
            MoveEntryMetadata(codes[staggerGuard], guard);
            codes.InsertRange(staggerGuard, new[]
            {
                guard,
                new CodeInstruction(OpCodes.Brtrue, skipStagger)
            });

            CodeInstruction first = new CodeInstruction(OpCodes.Ldarg_0);
            MoveEntryMetadata(codes[defenseStart], first);
            codes[defenseStart].labels.Add(continueCeDefense);
            CodeInstruction markMiss = new CodeInstruction(OpCodes.Ldc_I4_0);
            markMiss.labels.Add(parry);
            codes.InsertRange(defenseStart, new[]
            {
                first,
                new CodeInstruction(OpCodes.Ldloca, parried),
                new CodeInstruction(OpCodes.Call, ResolveDefense),
                new CodeInstruction(OpCodes.Brfalse, continueCeDefense),
                new CodeInstruction(OpCodes.Ldloc, parried),
                new CodeInstruction(OpCodes.Brtrue, parry),
                new CodeInstruction(OpCodes.Br, dodged),
                markMiss,
                storeFailedHit,
                new CodeInstruction(OpCodes.Br, followup)
            });
            return codes;
        }

        private static bool TryFindLayout(List<CodeInstruction> codes,
            out int defenseStart, out int dodgeStart, out int resultStore,
            out int attackerFollowup, out int staggerGuard, out int afterStagger)
        {
            defenseStart = dodgeStart = resultStore = attackerFollowup = staggerGuard = afterStagger = -1;
            int dodge = codes.FindIndex(code => code.Calls(ceDodgeChance));
            int sound = codes.FindIndex(code => code.Calls(DodgeSound));
            int notify = codes.FindLastIndex(code => code.Calls(NotifyAttack));
            if (dodge < 2 || sound < dodge + 4 || notify <= sound
                || codes[dodge - 2].opcode != OpCodes.Ldarg_0
                || !IsLoadLocal(codes[dodge - 1])
                || !codes[dodge + 1].Calls(RandChance)
                || codes[sound - 4].opcode != OpCodes.Ldc_I4_0
                || !IsStoreLocal(codes[sound - 3])) return false;

            int spawned = codes.FindLastIndex(notify - 1, Math.Min(notify, 16), code => code.Calls(Spawned));
            int stagger = codes.FindIndex(notify + 1, code => code.Calls(Stagger));
            int defenderStart = notify + 1;
            while (defenderStart < codes.Count && codes[defenderStart].opcode == OpCodes.Nop)
                defenderStart++;
            if (spawned < 1 || !IsLoadLocal(codes[spawned - 1])
                || stagger <= notify || stagger + 2 >= codes.Count
                || codes[stagger + 1].opcode != OpCodes.Pop
                || defenderStart + 1 >= stagger || !IsLoadLocal(codes[defenderStart])
                || (codes[defenderStart + 1].opcode != OpCodes.Brfalse
                    && codes[defenderStart + 1].opcode != OpCodes.Brfalse_S)) return false;

            defenseStart = dodge - 2;
            dodgeStart = sound - 4;
            resultStore = sound - 3;
            attackerFollowup = spawned - 1;
            staggerGuard = defenderStart;
            afterStagger = stagger + 2;
            return true;
        }

        private static bool IsLoadLocal(CodeInstruction code)
            => code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S
                || code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Ldloc_1
                || code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Ldloc_3;

        private static bool IsStoreLocal(CodeInstruction code)
            => code.opcode == OpCodes.Stloc || code.opcode == OpCodes.Stloc_S
                || code.opcode == OpCodes.Stloc_0 || code.opcode == OpCodes.Stloc_1
                || code.opcode == OpCodes.Stloc_2 || code.opcode == OpCodes.Stloc_3;

        private static void MoveEntryMetadata(CodeInstruction source, CodeInstruction destination)
        {
            destination.labels.AddRange(source.labels);
            source.labels.Clear();
            destination.blocks.AddRange(source.blocks);
            source.blocks.Clear();
        }
    }
}
