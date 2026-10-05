using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataGroundPoseRender
    {
        private struct Frame
        {
            internal FrameEntry entry;
            internal int version;
            internal Matrix4x4 weapons;
            internal Pawn pawn;
            internal RimKataGroundPoseGeometry.WeaponPlacement placement;
            internal Vector3? headCenter;
            internal Vector3 shadowCenter;
            internal float weaponLayerOffset;
            internal bool weaponIndicators;
            internal ThingWithComps firstDrawWeapon, secondDrawWeapon;
            internal Vector3 firstDrawCenter, secondDrawCenter;
            internal int firstDrawFrame, secondDrawFrame;

            internal readonly Vector3 PlaceWeapon(Vector3 center) => headCenter.HasValue
                ? placement.PlaceAtHead(center, headCenter.Value) : placement.Place(center);
        }

        private sealed class FrameEntry
        {
            // Readers copy the value under this lock; equipment scopes retain their own version.
            internal Frame snapshot;
            internal volatile bool ready;
        }

        private static readonly ConcurrentDictionary<Pawn, FrameEntry> Frames =
            new ConcurrentDictionary<Pawn, FrameEntry>();
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
            // The pivot is the render root's local foot point, independent of body mesh scale.
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
            if (snapshot.groundPoseFallen)
                frame.weaponLayerOffset = topLayer - anchor.y + PawnRenderUtility.AltitudeForLayer(100f);
            frame.headCenter = RimKataGroundPoseHead.Apply(parms, requests, transform,
                snapshot.groundPoseProgress,
                snapshot.groundPoseWeaponOffset - snapshot.groundPoseOffset);
            FrameEntry entry = Frames.GetOrAdd(parms.pawn, CreateFrameEntry);
            lock (entry)
            {
                frame.entry = entry;
                frame.version = unchecked(entry.snapshot.version + 1);
                entry.snapshot = frame;
                entry.ready = true;
            }
        }

        private static FrameEntry CreateFrameEntry(Pawn pawn) => new FrameEntry();

        internal static bool PushEquipment(Pawn pawn, PawnRenderFlags flags)
        {
            FrameEntry entry = (flags & PawnRenderFlags.Portrait) == 0 ? EntryFor(pawn) : null;
            if (entry == null && equipmentFrame.pawn == null) return false;
            Frame next = entry != null ? ReadFrame(pawn, entry) : default;
            (parents ??= new Stack<Frame>(2)).Push(equipmentFrame);
            equipmentFrame = next;
            return true;
        }

        internal static void PopEquipment(bool pushed)
        {
            if (pushed) equipmentFrame = parents.Pop();
        }

        private static FrameEntry EntryFor(Pawn pawn)
        {
            if (pawn == null) return null;
            var body = RimKataWorldRenderContext.BodyFor(pawn);
            if (body?.kick.HasValue == true || body?.flyingKick.HasValue == true || body?.groundPose != true) return null;
            if (equipmentFrame.pawn == pawn) return equipmentFrame.entry;
            return Frames.TryGetValue(pawn, out FrameEntry entry) && entry.ready ? entry : null;
        }

        private static Frame ReadFrame(Pawn pawn, FrameEntry entry)
        {
            if (equipmentFrame.pawn == pawn) return equipmentFrame;
            lock (entry) return entry.snapshot;
        }

        internal static bool WeaponsAboveBody(Pawn pawn = null)
        {
            if (RimKataKickRender.EquipmentActive || RimKataFlyingKickRender.EquipmentActive) return false;
            if (RimKataReactiveRender.WeaponsAboveBody(pawn)) return true;
            if (RimKataBreachRender.WeaponsAboveBody(pawn)) return true;
            if (pawn == null) return equipmentFrame.weaponLayerOffset > 0f;
            FrameEntry entry = EntryFor(pawn);
            return entry != null && ReadFrame(pawn, entry).weaponLayerOffset > 0f;
        }

        internal static bool TryGetWeaponCenter(Pawn pawn, ThingWithComps weapon, out Vector3 center)
        {
            center = default;
            if (weapon == null) return false;
            FrameEntry entry = EntryFor(pawn);
            if (entry == null) return false;
            Frame frame = ReadFrame(pawn, entry);
            if (!frame.weaponIndicators) return false;
            if (frame.firstDrawWeapon == weapon && frame.firstDrawFrame == Time.frameCount)
                center = frame.firstDrawCenter;
            else if (frame.secondDrawWeapon == weapon && frame.secondDrawFrame == Time.frameCount)
                center = frame.secondDrawCenter;
            else return false;
            return true;
        }

        internal static void PlaceShadow(Pawn pawn, ref Vector3 drawLoc)
        {
            FrameEntry entry = EntryFor(pawn);
            if (entry == null) return;
            Frame frame = ReadFrame(pawn, entry);
            // The shadow is drawn after the body and can reuse its final center.
            drawLoc.x = frame.shadowCenter.x;
            drawLoc.z = frame.shadowCenter.z;
        }

        internal static bool TryGetRangedAimOrigin(Pawn pawn, ThingWithComps weapon, out Vector3 origin)
        {
            origin = default;
            if (pawn == null || weapon?.def.IsRangedWeapon != true) return false;
            // PushEquipment already selected the ground-pose frame for this scope.
            if (equipmentFrame.pawn == pawn)
            {
                if (!equipmentFrame.headCenter.HasValue) return false;
                origin = equipmentFrame.PlaceWeapon(equipmentFrame.placement.anchor);
                return true;
            }
            FrameEntry entry = EntryFor(pawn);
            if (entry == null) return false;
            Frame frame = ReadFrame(pawn, entry);
            if (!frame.headCenter.HasValue) return false;
            origin = frame.PlaceWeapon(frame.placement.anchor);
            return true;
        }

        internal static void AdjustWeaponAim(Thing equipment, ref Vector3 drawLoc, ref float aimAngle,
            in RimKataDualWeaponRenderUtility.AimPreparation prepared)
        {
            ref readonly Frame frame = ref equipmentFrame;
            if (frame.pawn == null || !(equipment is ThingWithComps weapon)
                || !weapon.def.IsRangedWeapon || !frame.headCenter.HasValue) return;
            Pawn pawn = frame.pawn;
            ref readonly RimKataGunReadyDrawContext context = ref RimKataGunReadyDrawUtility.Current;
            LocalTargetInfo target = LocalTargetInfo.Invalid;
            RimKataWeaponVisualData cycleVisual = default;
            bool cycleAim = false;
            bool responseAim = context.pawn == pawn && context.snapshotActive
                ? context.snapshot.responsePoseWeapon == weapon
                    && RimKataVisualUtility.TryGetLiveResponseFocus(pawn, context.snapshot, out target)
                : RimKataVisualUtility.TryGetCachedActiveSnapshot(pawn, out var snapshot)
                    && snapshot.responsePoseWeapon == weapon
                    && RimKataVisualUtility.TryGetLiveResponseFocus(pawn, snapshot, out target);
            if (!responseAim)
            {
                bool sameWeapon = prepared.pawn == pawn && prepared.weapon == weapon;
                if (sameWeapon && prepared.adjusted && prepared.hasVisual && prepared.visual.target.IsValid) return;
                RimKataWeaponVisualData visual = prepared.visual;
                bool hasVisual = sameWeapon ? prepared.hasVisual
                    : RimKataGunReadyDrawUtility.TryGetVisualData(pawn, weapon, out visual);
                if (hasVisual && visual.target.IsValid)
                {
                    target = visual.target;
                    cycleVisual = visual;
                    cycleAim = true;
                }
                else if (pawn.stances?.curStance is Stance_Busy busy
                    && !busy.neverAimWeapon && busy.verb?.EquipmentSource == weapon)
                    target = busy.focusTarg;
            }
            if (!target.IsValid && context.pawn == pawn) target = context.fallAimTarget;
            if (!target.IsValid) return;

            Vector3 origin = frame.PlaceWeapon(frame.placement.anchor);
            Vector3 position = target.HasThing && target.Thing.Spawned
                ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            Vector3 direction = (position - origin).Yto0();
            if (direction.sqrMagnitude <= 0.001f) return;
            float aimed = cycleAim
                ? RimKataDualWeaponRenderUtility.VisualAimAngle(pawn, weapon, cycleVisual, direction.AngleFlat())
                : direction.AngleFlat();
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
        {
            if (RimKataWorldRenderContext.TryKick(pawn, out _))
                return RimKataKickRender.TransformEquipment(pawn, matrix);
            if (RimKataWorldRenderContext.TryFlyingKick(pawn, out _))
                return RimKataFlyingKickRender.TransformEquipment(pawn, matrix);
            FrameEntry entry = EntryFor(pawn);
            if (entry == null)
                return RimKataFlyingKickRender.TransformEquipment(pawn,
                    RimKataReactiveRender.TransformEquipment(pawn, RimKataBreachRender.TransformEquipment(pawn, matrix)));
            if (equipmentFrame.pawn == pawn) return TransformEquipment(in equipmentFrame, matrix);
            Frame frame = ReadFrame(pawn, entry);
            return TransformEquipment(in frame, matrix);
        }

        private static Matrix4x4 TransformEquipment(in Frame frame, Matrix4x4 matrix, bool externalPrimary = false)
        {
            if (RimKataKickRender.EquipmentActive)
                return RimKataKickRender.TransformEquipment(matrix);
            if (RimKataFlyingKickRender.EquipmentActive)
                return RimKataFlyingKickRender.TransformEquipment(matrix);
            if (frame.pawn == null)
            {
                RimKataCrawlFireRender.ObserveWeaponCenter(new Vector3(matrix.m03, matrix.m13, matrix.m23));
                return RimKataFlyingKickRender.TransformEquipment(
                    RimKataReactiveRender.TransformEquipment(RimKataBreachRender.TransformEquipment(matrix)));
            }
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
            if (RimKataKickRender.EquipmentActive)
            {
                RimKataKickRender.TransformEquipment(ref position);
                return;
            }
            if (RimKataFlyingKickRender.EquipmentActive)
            {
                RimKataFlyingKickRender.TransformEquipment(ref position);
                return;
            }
            if (equipmentFrame.pawn == null)
            {
                RimKataCrawlFireRender.ObserveWeaponCenter(position);
                RimKataBreachRender.TransformEquipment(ref position, ref rotation);
                RimKataReactiveRender.TransformEquipment(ref position);
                RimKataFlyingKickRender.TransformEquipment(ref position);
                return;
            }
            ThingWithComps weapon = ObserveWeapon(equipmentFrame, position, externalPrimary: true);
            position = equipmentFrame.PlaceWeapon(position);
            position.y += equipmentFrame.weaponLayerOffset;
            ObserveIndicatorCenter(equipmentFrame, weapon, position);
        }

        private static void ObserveIndicatorCenter(in Frame frame, ThingWithComps weapon, Vector3 center)
        {
            if (!frame.weaponIndicators || weapon == null || RimKataWeaponRenderProbe.Probing) return;
            if (equipmentFrame.entry == frame.entry && equipmentFrame.version == frame.version)
                SetIndicatorCenter(ref equipmentFrame, weapon, center);
            lock (frame.entry)
            {
                if (frame.entry.snapshot.version == frame.version)
                    SetIndicatorCenter(ref frame.entry.snapshot, weapon, center);
            }
        }

        private static void SetIndicatorCenter(ref Frame frame, ThingWithComps weapon, Vector3 center)
        {
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

        private static ThingWithComps ObserveWeapon(in Frame frame, Vector3 center, bool externalPrimary = false)
        {
            ThingWithComps weapon = RimKataWeaponRenderProbe.Probing
                ? RimKataWeaponRenderProbe.CurrentDrawWeapon(frame.pawn)
                : weaponScope.weapon ?? RimKataWeaponRenderProbe.CurrentDrawWeapon(frame.pawn);
            if (weapon == null && externalPrimary)
            {
                // A single equipped gun has no secondary probe frame.
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
            if (RimKataKickRender.EquipmentActive)
                return RimKataKickRender.TransformEquipment(matrix);
            if (RimKataFlyingKickRender.EquipmentActive)
                return RimKataFlyingKickRender.TransformEquipment(matrix);
            if (equipmentFrame.pawn == null)
                return RimKataFlyingKickRender.TransformEquipment(
                    RimKataReactiveRender.TransformEquipment(RimKataBreachRender.TransformEquipment(matrix)));
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
            weaponScope = equipmentFrame.pawn == null ? default : new WeaponScope
                { weapon = weapon as ThingWithComps, aim = aim };
            return previous;
        }

        internal static void EndWeapon(WeaponScope previous) => weaponScope = previous;

        internal static void DrawPrimaryMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
            => Graphics.DrawMesh(mesh, TransformEquipment(matrix), material, layer);

        internal static void Clear(Pawn pawn)
        {
            RimKataGroundPoseHead.Clear(pawn);
            if (pawn != null) Frames.TryRemove(pawn, out _);
        }

        internal static void ClearMap(Map map)
        {
            RimKataGroundPoseHead.ClearMap(map);
            foreach (Pawn pawn in Frames.Keys)
                if (pawn.Map == map || !pawn.Spawned) Clear(pawn);
        }
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
