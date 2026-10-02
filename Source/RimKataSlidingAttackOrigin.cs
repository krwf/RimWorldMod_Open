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
    internal static class RimKataSlidingAttackOrigin
    {
        internal struct Scope
        {
            internal Verb verb;
            internal Pawn pawn;
            internal LocalTargetInfo target;
            internal Vector3 center;
        }

        [ThreadStatic] private static Scope current;

        internal static Scope Begin(RimKataNativeAttack request)
        {
            Scope previous = current;
            current = default;
            RimKataReactiveMotionState motion = request.reactiveMotion;
            if (motion?.kind == RimKataReactiveKind.Sliding && motion.pawn == request.pawn)
                current = new Scope { verb = request.verb, pawn = request.pawn, target = request.target,
                    center = Center(motion, Find.TickManager.TicksGame) };
            return previous;
        }

        internal static void End(Scope previous) => current = previous;

        internal static Vector3 Center(RimKataReactiveMotionState motion, int tick)
        {
            Vector3 destination = motion.destination.ToVector3Shifted();
            destination.y = motion.startDrawPos.y;
            return Vector3.Lerp(motion.startDrawPos, destination,
                Mathf.Clamp01((tick - motion.startTick) / 12f));
        }

        internal static bool TryGet(Verb verb, out Vector3 center)
        {
            center = current.center;
            return current.verb != null && current.verb == verb
                && RimKataFireContext.ActiveVerb == verb && RimKataFireContext.Shooter == current.pawn
                && (Patch_Verb_TryCastShot_RimKata.CurrentVerb == null
                    || Patch_Verb_TryCastShot_RimKata.CurrentVerb == verb);
        }

        internal static void SetMeleeAngle(ref DamageInfo damage, Vector3 direction)
        {
            if (current.verb?.IsMeleeAttack == true && damage.Instigator == current.pawn
                && current.target.IsValid && TryGet(current.verb, out Vector3 center))
                direction = current.target.CenterVector3 - center;
            damage.SetAngle(direction);
        }

        internal static void DrawShotFlash(IntVec3 cell, Map map, FleckDef fleck, float scale)
        {
            if (TryGet(RimKataFireContext.ActiveVerb, out Vector3 center))
                FleckMaker.Static(center, map, fleck, scale);
            else FleckMaker.Static(cell, map, fleck, scale);
        }
    }

    [HarmonyPatch]
    internal static class Patch_MeleeDamage_RimKataSlidingOrigin
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.EnumeratorMoveNext(
                AccessTools.DeclaredMethod(typeof(Verb_MeleeAttackDamage), "DamageInfosToApply"));
            Type ce = AccessTools.TypeByName("CombatExtended.Verb_MeleeAttackCE");
            MethodInfo damage = ce == null ? null : AccessTools.DeclaredMethod(ce, "DamageInfosToApply");
            if (damage != null)
            {
                MethodInfo moveNext = AccessTools.EnumeratorMoveNext(damage);
                if (moveNext != null) yield return moveNext;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(DamageInfo), nameof(DamageInfo.SetAngle),
                new[] { typeof(Vector3) });
            MethodInfo replacement = AccessTools.Method(typeof(RimKataSlidingAttackOrigin),
                nameof(RimKataSlidingAttackOrigin.SetMeleeAngle));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    internal static class Patch_VerbShotFlash_RimKataSlidingOrigin
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(FleckMaker), nameof(FleckMaker.Static),
                new[] { typeof(IntVec3), typeof(Map), typeof(FleckDef), typeof(float) });
            MethodInfo replacement = AccessTools.Method(typeof(RimKataSlidingAttackOrigin),
                nameof(RimKataSlidingAttackOrigin.DrawShotFlash));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original)) instruction.operand = replacement;
                yield return instruction;
            }
        }
    }
}
