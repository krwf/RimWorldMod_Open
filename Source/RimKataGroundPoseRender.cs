using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Render-only snapshots. Parallel rendering never advances combat state.
    internal static class RimKataGroundPoseRender
    {
        private sealed class Frame
        {
            internal Matrix4x4 weapons;
            internal Pawn pawn;
            internal RimKataGroundPoseGeometry.WeaponPlacement placement;
            internal Vector3? headCenter;
            internal Vector3 shadowCenter;
            internal float weaponLayerOffset;
            internal bool weaponIndicators;
            internal ThingWithComps firstDrawWeapon, secondDrawWeapon;
            internal Vector3 firstDrawCenter, secondDrawCenter;
            internal int firstDrawFrame = -1, secondDrawFrame = -1;

            internal Vector3 PlaceWeapon(Vector3 center) => headCenter.HasValue
                ? placement.PlaceAtHead(center, headCenter.Value) : placement.Place(center);
        }

        private static readonly ConcurrentDictionary<Pawn, Frame> Frames =
            new ConcurrentDictionary<Pawn, Frame>();
        private static int activeFrameCount;
        [System.ThreadStatic] private static Frame equipmentFrame;
        [System.ThreadStatic] private static Stack<Frame> parents;
        [System.ThreadStatic] private static WeaponScope weaponScope;

        internal struct WeaponScope
        {
            internal ThingWithComps weapon;
            internal float aim;
        }

        internal static void Prepare(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests,
            RimKataVisualSnapshot snapshot)
        {
            // Match the close-ranged dodge pivot in ParallelPreDraw: the pawn
            // render root's local foot point, not the scaled body mesh's edge.
            Vector3 pivot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            bool hasHead = RimKataGroundPoseHead.TryGetHeadMatrix(parms, out Matrix4x4 head);
            float reach = hasHead ? Mathf.Max(0f, head.m23 - pivot.z) * 0.5f : 0f;
            Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 bodyCenter = RimKataGroundPoseHead.TryGetBodyMatrix(parms, out Matrix4x4 body)
                ? body.MultiplyPoint3x4(Vector3.zero) : anchor;
            RimKataGroundPoseGeometry.ObserveBody(parms, pivot, anchor, bodyCenter, reach,
                hasHead ? head.MultiplyPoint3x4(Vector3.zero) : (Vector3?)null);
            Matrix4x4 rotation = Matrix4x4.Translate(pivot)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(snapshot.groundPoseAngle, Vector3.up))
                * Matrix4x4.Translate(-pivot);
            Matrix4x4 transform = Matrix4x4.Translate(snapshot.groundPoseOffset) * rotation;
            Frame frame = new Frame
            {
                pawn = parms.pawn,
                weaponIndicators = !snapshot.groundPoseFallen,
                shadowCenter = transform.MultiplyPoint3x4(bodyCenter),
                placement = RimKataGroundPoseGeometry.Placement(anchor, bodyCenter, pivot,
                    snapshot.groundPoseAngle, snapshot.groundPoseWeaponOffset,
                    RimKataGroundPoseGeometry.NorthLift(reach, snapshot.groundPoseDirection,
                        snapshot.groundPoseProgress), snapshot.groundPoseProgress),
                weapons = Matrix4x4.Translate(snapshot.groundPoseWeaponOffset
                    + new Vector3(0f, 0f, RimKataGroundPoseGeometry.NorthLift(reach,
                        snapshot.groundPoseDirection, snapshot.groundPoseProgress))) * rotation
            };
            float topLayer = anchor.y;
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                request.preDrawnComputedMatrix = transform * request.preDrawnComputedMatrix;
                if (snapshot.groundPoseFallen)
                    topLayer = Mathf.Max(topLayer, request.preDrawnComputedMatrix.m13);
                if (request.node.Props.tagDef == PawnRenderNodeTagDefOf.Body)
                    frame.shadowCenter = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                requests[i] = request;
            }
            // Lift the entire weapon stack above the pawn, including its native
            // rear-facing depth. Keep blade/sheath and slot depth gaps intact.
            if (snapshot.groundPoseFallen)
                frame.weaponLayerOffset = topLayer - anchor.y + PawnRenderUtility.AltitudeForLayer(100f);
            frame.headCenter = RimKataGroundPoseHead.Apply(parms, requests, transform,
                snapshot.groundPoseProgress,
                snapshot.groundPoseWeaponOffset - snapshot.groundPoseOffset);
            if (Frames.TryAdd(parms.pawn, frame)) Interlocked.Increment(ref activeFrameCount);
            else Frames[parms.pawn] = frame;
        }

        internal static bool PushEquipment(Pawn pawn, PawnRenderFlags flags)
        {
            Frame next = null;
            if (Volatile.Read(ref activeFrameCount) != 0
                && (flags & PawnRenderFlags.Portrait) == 0 && pawn != null)
                Frames.TryGetValue(pawn, out next);
            if (next == null && equipmentFrame == null) return false;
            (parents ??= new Stack<Frame>(2)).Push(equipmentFrame);
            equipmentFrame = next;
            return true;
        }

        internal static void PopEquipment(bool pushed)
        {
            if (pushed) equipmentFrame = parents.Pop();
        }

        internal static bool WeaponsAboveBody(Pawn pawn = null)
        {
            if (pawn == null) return equipmentFrame?.weaponLayerOffset > 0f;
            return Volatile.Read(ref activeFrameCount) != 0
                && Frames.TryGetValue(pawn, out Frame frame) && frame.weaponLayerOffset > 0f;
        }

        internal static bool TryGetWeaponCenter(Pawn pawn, ThingWithComps weapon, out Vector3 center)
        {
            center = default;
            if (Volatile.Read(ref activeFrameCount) == 0 || pawn == null || weapon == null
                || !Frames.TryGetValue(pawn, out Frame frame) || !frame.weaponIndicators) return false;
            if (frame.firstDrawWeapon == weapon && frame.firstDrawFrame == Time.frameCount)
                center = frame.firstDrawCenter;
            else if (frame.secondDrawWeapon == weapon && frame.secondDrawFrame == Time.frameCount)
                center = frame.secondDrawCenter;
            else return false;
            return true;
        }

        internal static void PlaceShadow(Pawn pawn, ref Vector3 drawLoc)
        {
            if (Volatile.Read(ref activeFrameCount) == 0 || pawn == null
                || !Frames.TryGetValue(pawn, out Frame frame)) return;
            // The shadow is drawn after the body. Use that body's final center,
            // including rolling, while retaining the shadow's own ground layer.
            drawLoc.x = frame.shadowCenter.x;
            drawLoc.z = frame.shadowCenter.z;
        }

        internal static bool TryGetRangedAimOrigin(Pawn pawn, ThingWithComps weapon, out Vector3 origin)
        {
            origin = default;
            if (Volatile.Read(ref activeFrameCount) == 0 || pawn == null
                || weapon?.def.IsRangedWeapon != true
                || !Frames.TryGetValue(pawn, out Frame frame) || !frame.headCenter.HasValue) return false;
            origin = frame.PlaceWeapon(frame.placement.anchor);
            return true;
        }

        internal static void AdjustWeaponAim(Thing equipment, ref Vector3 drawLoc, ref float aimAngle)
        {
            Frame frame = equipmentFrame;
            if (!(equipment is ThingWithComps weapon)
                || !TryGetRangedAimOrigin(frame?.pawn, weapon, out Vector3 origin)) return;
            Pawn pawn = frame.pawn;
            LocalTargetInfo target = LocalTargetInfo.Invalid;
            RimKataWeaponVisualData cycleVisual = default;
            bool cycleAim = false;
            if (RimKataVisualUtility.TryGetCachedActiveSnapshot(pawn, out var snapshot)
                && snapshot.responsePoseWeapon == weapon
                && RimKataVisualUtility.TryGetLiveResponseFocus(pawn, snapshot, out var response))
                target = response;
            else if (RimKataDualWeaponController.TryGetVisualData(pawn, weapon, out var visual)
                && visual.target.IsValid)
            {
                target = visual.target;
                cycleVisual = visual;
                cycleAim = true;
            }
            else if (pawn.stances?.curStance is Stance_Busy busy
                && !busy.neverAimWeapon && busy.verb?.EquipmentSource == weapon)
                target = busy.focusTarg;
            if (!target.IsValid) return;

            Vector3 position = target.HasThing && target.Thing.Spawned
                ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            Vector3 direction = (position - origin).Yto0();
            if (direction.sqrMagnitude <= 0.001f) return;
            float aimed = cycleAim
                ? RimKataDualWeaponRenderUtility.VisualAimAngle(pawn, weapon, cycleVisual, direction.AngleFlat())
                : direction.AngleFlat();
            // Retarget before mesh/flip/recoil selection, not after submission.
            // Rotate the raw orbital offset by the same continuous angle delta.
            Vector3 radial = drawLoc - frame.placement.anchor;
            float layer = drawLoc.y;
            drawLoc = frame.placement.anchor + radial.RotatedBy(Mathf.DeltaAngle(aimAngle, aimed));
            drawLoc.y = layer;
            aimAngle = aimed;
        }

        internal static Matrix4x4 TransformEquipment(Matrix4x4 matrix)
            => TransformEquipment(equipmentFrame, matrix);

        internal static Matrix4x4 TransformExternalEquipment(Matrix4x4 matrix)
            => TransformEquipment(equipmentFrame, matrix, externalPrimary: true);

        internal static Matrix4x4 TransformEquipment(Pawn pawn, Matrix4x4 matrix)
            => Volatile.Read(ref activeFrameCount) != 0 && pawn != null
                && Frames.TryGetValue(pawn, out Frame frame)
                ? TransformEquipment(frame, matrix) : matrix;

        private static Matrix4x4 TransformEquipment(Frame frame, Matrix4x4 matrix, bool externalPrimary = false)
        {
            if (frame == null)
            {
                RimKataCrawlFireRender.ObserveWeaponCenter(new Vector3(matrix.m03, matrix.m13, matrix.m23));
                return matrix;
            }
            // All weapon renderers share the resolved head pivot and retain
            // their original motion and rotation. Never feed back into the head.
            Vector3 position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
            ThingWithComps weapon = ObserveWeapon(frame, position, externalPrimary);
            position = frame.PlaceWeapon(position);
            matrix.m03 = position.x;
            matrix.m13 = position.y + frame.weaponLayerOffset;
            matrix.m23 = position.z;
            ObserveIndicatorCenter(frame, weapon, new Vector3(matrix.m03, matrix.m13, matrix.m23));
            return matrix;
        }

        internal static void TransformExternalEquipment(ref Vector3 position, ref Quaternion rotation)
        {
            if (equipmentFrame == null)
            {
                RimKataCrawlFireRender.ObserveWeaponCenter(position);
                return;
            }
            ThingWithComps weapon = ObserveWeapon(equipmentFrame, position, externalPrimary: true);
            position = equipmentFrame.PlaceWeapon(position);
            position.y += equipmentFrame.weaponLayerOffset;
            ObserveIndicatorCenter(equipmentFrame, weapon, position);
        }

        private static void ObserveIndicatorCenter(Frame frame, ThingWithComps weapon, Vector3 center)
        {
            // Only final weapon submissions feed the selected-pawn overlays;
            // temporary probes and separate sheath draws must not replace them.
            if (!frame.weaponIndicators || weapon == null || RimKataWeaponRenderProbe.Probing) return;
            if (frame.firstDrawWeapon == null || frame.firstDrawWeapon == weapon)
            {
                frame.firstDrawCenter = center;
                frame.firstDrawFrame = Time.frameCount;
                frame.firstDrawWeapon = weapon;
            }
            else
            {
                frame.secondDrawCenter = center;
                frame.secondDrawFrame = Time.frameCount;
                frame.secondDrawWeapon = weapon;
            }
        }

        private static ThingWithComps ObserveWeapon(Frame frame, Vector3 center, bool externalPrimary = false)
        {
            ThingWithComps weapon = RimKataWeaponRenderProbe.Probing
                ? RimKataWeaponRenderProbe.CurrentDrawWeapon(frame.pawn)
                : weaponScope.weapon ?? RimKataWeaponRenderProbe.CurrentDrawWeapon(frame.pawn);
            if (weapon == null && externalPrimary)
            {
                // The secondary probe has no frame for a single equipped gun.
                // Only discovered external weapon submissions use this fallback;
                // animation hands and accessory submissions retain their own path.
                ref readonly RimKataGunReadyDrawContext context = ref RimKataGunReadyDrawUtility.Current;
                if (context.scoped && context.scopePawn == frame.pawn)
                    weapon = context.active ? context.primary : frame.pawn.equipment?.Primary;
            }
            float aim = weaponScope.weapon == weapon && !RimKataWeaponRenderProbe.Probing ? weaponScope.aim
                : RimKataGroundPoseGeometry.AimAngle(frame.pawn, weapon);
            if (weapon != null)
                RimKataGroundPoseGeometry.ObserveWeapon(frame.pawn, weapon, center, frame.placement.anchor, aim);
            return weapon;
        }

        internal static Matrix4x4 TransformAccessory(Matrix4x4 matrix)
        {
            if (equipmentFrame == null) return matrix;
            Vector3 position = equipmentFrame.weapons.MultiplyPoint3x4(
                new Vector3(matrix.m03, matrix.m13, matrix.m23));
            matrix.m03 = position.x;
            matrix.m13 = position.y + equipmentFrame.weaponLayerOffset;
            matrix.m23 = position.z;
            return matrix;
        }

        internal static WeaponScope BeginWeapon(Thing weapon, float aim)
        {
            WeaponScope previous = weaponScope;
            weaponScope = equipmentFrame == null ? default : new WeaponScope
                { weapon = weapon as ThingWithComps, aim = aim };
            return previous;
        }

        internal static void EndWeapon(WeaponScope previous) => weaponScope = previous;

        internal static void DrawPrimaryMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
            => Graphics.DrawMesh(mesh, TransformEquipment(matrix), material, layer);

        internal static void Clear(Pawn pawn)
        {
            RimKataGroundPoseHead.Clear(pawn);
            if (pawn != null && Frames.TryRemove(pawn, out _))
                Interlocked.Decrement(ref activeFrameCount);
        }

        internal static void ClearMap(Map map)
        {
            RimKataGroundPoseHead.ClearMap(map);
            foreach (Pawn pawn in Frames.Keys)
                if (pawn.Map == map || !pawn.Spawned) Clear(pawn);
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), "DrawShadowInternal")]
    internal static class Patch_PawnRenderer_RimKataGroundPoseShadow
    {
        private static void Prefix(Pawn ___pawn, ref Vector3 drawLoc)
            => RimKataGroundPoseRender.PlaceShadow(___pawn, ref drawLoc);
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    internal static class Patch_PawnRenderUtility_RimKataGroundPoseWeapon
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Thing eq, float aimAngle, out RimKataGroundPoseRender.WeaponScope __state)
            => __state = RimKataGroundPoseRender.BeginWeapon(eq, aimAngle);

        private static void Finalizer(RimKataGroundPoseRender.WeaponScope __state)
            => RimKataGroundPoseRender.EndWeapon(__state);
    }
}
