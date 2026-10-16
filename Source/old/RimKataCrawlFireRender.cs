using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCrawlFireRender
    {
        internal struct Scope
        {
            internal Pawn pawn;
            internal ThingWithComps weapon;
            internal LocalTargetInfo target;
            internal Vector3 origin;
            internal float fallbackAngle;
            internal bool drawn;
            internal bool drawing;
        }

        [ThreadStatic] private static Scope current;

        private sealed class WeaponCenter
        {
            internal Pawn pawn;
            internal Vector3 offset;
            internal int frame;
        }

        private static readonly ConditionalWeakTable<ThingWithComps, WeaponCenter> Centers =
            new ConditionalWeakTable<ThingWithComps, WeaponCenter>();

        internal static bool Drawing => current.drawing;

        internal static void ObserveWeaponCenter(Vector3 center)
        {
            if (!current.drawing || current.weapon == null) return;
            WeaponCenter sample = Centers.GetValue(current.weapon, _ => new WeaponCenter());
            sample.pawn = current.pawn;
            sample.offset = center - current.pawn.DrawPos;
            sample.frame = Time.frameCount;
        }

        internal static bool TryGetWeaponCenter(Pawn pawn, ThingWithComps weapon, out Vector3 center)
        {
            center = default;
            if (weapon == null || !RimKataWorldRenderContext.TryCrawl(pawn, out var aimed, out _)
                || aimed != weapon || !Centers.TryGetValue(weapon, out WeaponCenter sample)
                || sample.pawn != pawn || sample.frame != Time.frameCount) return false;
            center = pawn.DrawPos + sample.offset;
            return true;
        }

        internal static void AdjustFacing(Pawn pawn, Vector3 rootLoc, PawnRenderFlags flags,
            ref float angle, ref Rot4 facing)
        {
            if ((flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache
                    | PawnRenderFlags.Invisible | PawnRenderFlags.NeverAimWeapon)) != 0
                || !RimKataWorldRenderContext.TryCrawl(pawn, out _, out LocalTargetInfo target))
                return;

            Vector3 targetPosition = target.HasThing && target.Thing.Spawned
                ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            Vector3 direction = (targetPosition - rootLoc).Yto0();
            if (direction.sqrMagnitude <= 0.001f) return;

            facing = Rot4.FromAngleFlat(direction.AngleFlat());
            angle = PawnRenderUtility.CrawlingBodyAngle(facing);
        }

        internal static Scope Begin(PawnDrawParms parms)
        {
            Scope previous = current;
            current = default;
            if ((parms.flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache
                    | PawnRenderFlags.Invisible | PawnRenderFlags.NeverAimWeapon)) != 0
                || !RimKataWorldRenderContext.TryCrawl(parms.pawn, out ThingWithComps weapon,
                    out LocalTargetInfo target)
                || !RimKataGroundPoseHead.TryGetHeadMatrix(parms, out Matrix4x4 head))
                return previous;

            current = new Scope
            {
                pawn = parms.pawn,
                weapon = weapon,
                target = target,
                origin = new Vector3(head.m03,
                    parms.matrix.m13 + PawnRenderUtility.AltitudeForLayer(
                        parms.facing == Rot4.North ? -10f : 90f), head.m23),
                fallbackAngle = parms.facing.AsAngle
            };
            return previous;
        }

        internal static void End(Scope previous) => current = previous;

        internal static bool TryHandleEquipment(Thing equipment, out bool drawOriginal)
        {
            drawOriginal = true;
            if (current.weapon == null || equipment != current.weapon) return false;
            if (current.drawing) return true;

            Draw();
            drawOriginal = false;
            return true;
        }

        internal static void Draw()
        {
            if (current.weapon == null || current.drawn) return;
            current.drawn = true;

            Vector3 targetPosition = current.target.HasThing && current.target.Thing.Spawned
                ? current.target.Thing.DrawPos : current.target.Cell.ToVector3Shifted();
            Vector3 direction = (targetPosition - current.origin).Yto0();
            float aim = direction.sqrMagnitude > 0.001f
                ? direction.AngleFlat() : current.fallbackAngle;
            float distanceFactor = current.pawn.ageTracker?.CurLifeStage
                ?.equipmentDrawDistanceFactor ?? 1f;
            Vector3 drawLocation = current.origin
                + new Vector3(0f, 0f, 0.4f + current.weapon.def.equippedDistanceOffset)
                    .RotatedBy(aim) * distanceFactor;

            bool groundPoseScope = RimKataGroundPoseRender.PushEquipment(null, PawnRenderFlags.Portrait);
            current.drawing = true;
            try
            {
                PawnRenderUtility.DrawEquipmentAiming(current.weapon, drawLocation, aim);
            }
            finally
            {
                current.drawing = false;
                RimKataGroundPoseRender.PopEquipment(groundPoseScope);
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderNodeWorker_Carried), nameof(PawnRenderNodeWorker_Carried.PostDraw))]
    internal static class Patch_PawnRenderNodeWorkerCarried_RimKataCrawlFire
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            LocalBuilder scope = generator.DeclareLocal(typeof(RimKataCrawlFireRender.Scope));
            return RimKataRenderHookIL.LivingOnly(instructions, generator, 2,
                new[] {
                    new CodeInstruction(OpCodes.Ldarg_2),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataCrawlFireRender), nameof(RimKataCrawlFireRender.Begin))),
                    new CodeInstruction(OpCodes.Stloc, scope)
                }, new[] {
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataCrawlFireRender), nameof(RimKataCrawlFireRender.Draw)))
                }, new[] {
                    new CodeInstruction(OpCodes.Ldloc, scope),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataCrawlFireRender), nameof(RimKataCrawlFireRender.End)))
                });
        }
    }
}
