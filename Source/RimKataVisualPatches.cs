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
    public static class RimKataVisualUtility
    {
        public static bool IsCachedWorldVisualUser(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            if (RimKataTargetAccess.IsEnabled(pawn))
            {
                return RimKataEligibility.FactionEffectsEnabled(pawn);
            }

            return RimKataEligibilityCache.IsRegisteredUser(pawn)
                && RimKataEligibility.FactionEffectsEnabled(pawn);
        }

        public static bool TryGetCachedWorldLoadout(
            Pawn pawn,
            out ThingWithComps primary,
            out ThingWithComps secondary)
        {
            return TryGetVisualLoadout(
                pawn,
                false,
                out primary,
                out secondary);
        }

        public static bool TryGetUiLoadout(
            Pawn pawn,
            out ThingWithComps primary,
            out ThingWithComps secondary)
        {
            return TryGetVisualLoadout(
                pawn,
                true,
                out primary,
                out secondary);
        }

        public static bool IsSecondaryUsable(
            Pawn pawn,
            ThingWithComps primary,
            ThingWithComps secondary)
        {
            return pawn != null
                && secondary != null
                && RimKataTargetAccess.SettingsFor(pawn)?.secondaryWeaponEnabled != false
                && RimKataEquipmentUtility.IsWeaponEnabled(primary?.def)
                && RimKataGripUtility.GripTypeFor(primary?.def)
                    == RimKataGripType.OneHand;
        }

        public static bool TryGetResponseParticipantLoadout(
            Pawn pawn,
            out ThingWithComps primary,
            out ThingWithComps secondary)
        {
            primary = null;
            secondary = null;
            if (!RimKataResponseVisualParticipantCache
                .TryGetParticipantWeapons(
                    pawn,
                    out ThingWithComps deflectionWeapon,
                    out ThingWithComps responsePoseWeapon,
                    out ThingWithComps spinSecondaryWeapon))
            {
                return false;
            }

            primary = pawn?.equipment?.Primary;
            if (IsHeldNonPrimary(pawn, primary, spinSecondaryWeapon))
            {
                secondary = spinSecondaryWeapon;
            }
            else if (IsHeldNonPrimary(
                pawn,
                primary,
                responsePoseWeapon))
            {
                secondary = responsePoseWeapon;
            }
            else if (IsHeldNonPrimary(
                pawn,
                primary,
                deflectionWeapon))
            {
                secondary = deflectionWeapon;
            }

            return true;
        }

        public static bool TryGetActiveSnapshot(
            Pawn pawn,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            return RimKataCombatStatePresenceCache.TryGetOwner(
                    pawn,
                    out RimKataMapComponent component)
                && TryGetQualifiedOrResponseSnapshot(pawn, component, out snapshot);
        }

        public static bool TryGetCachedActiveSnapshot(
            Pawn pawn,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            if (!RimKataResponseVisualParticipantCache.IsParticipant(pawn)
                && !RimKataResponseVisualParticipantCache
                    .IsBodyVisualParticipant(pawn))
            {
                return false;
            }

            return TryGetActiveSnapshot(pawn, out snapshot);
        }

        internal static bool TryGetCachedActiveSnapshot(
            Pawn pawn,
            RimKataMapComponent component,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            if (!RimKataResponseVisualParticipantCache.IsParticipant(pawn)
                && !RimKataResponseVisualParticipantCache
                    .IsBodyVisualParticipant(pawn))
            {
                return false;
            }

            return TryGetQualifiedOrResponseSnapshot(pawn, component, out snapshot);
        }

        private static bool TryGetQualifiedOrResponseSnapshot(
            Pawn pawn,
            RimKataMapComponent component,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            bool qualified = RimKataEligibilityCache.IsCachedQualifiedPawn(pawn);
            if ((!qualified && !RimKataResponseVisualParticipantCache.IsParticipant(pawn))
                || component?.TryGetActiveVisualSnapshot(pawn, out snapshot) != true)
            {
                return false;
            }

            if (!qualified)
            {
                // Keep the current response pose without reviving stale dodge visuals.
                snapshot.visualActive = false;
                snapshot.additionalTumbleActive = false;
                snapshot.dodgeMovementActive = false;
                snapshot.closeDodgeActive = false;
                snapshot.groundPoseActive = false;
            }

            return true;
        }

        public static bool TryGetCachedResponseSnapshot(
            Pawn pawn,
            bool participantKnown,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            if (!participantKnown
                && !RimKataResponseVisualParticipantCache.IsParticipant(pawn))
            {
                return false;
            }

            return TryGetActiveSnapshot(pawn, out snapshot);
        }

        internal static bool TryGetCachedResponseSnapshot(
            Pawn pawn,
            RimKataMapComponent component,
            bool participantKnown,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            if (!participantKnown
                && !RimKataResponseVisualParticipantCache.IsParticipant(pawn))
            {
                return false;
            }

            return TryGetQualifiedOrResponseSnapshot(pawn, component, out snapshot);
        }

        public static RimKataVisualSnapshot SnapshotFor(Pawn pawn)
        {
            return TryGetActiveSnapshot(
                    pawn,
                    out RimKataVisualSnapshot snapshot)
                ? snapshot
                : default(RimKataVisualSnapshot);
        }

        public static Vector3 DrawOffset(RimKataVisualSnapshot snapshot)
        {
            if (!snapshot.visualActive)
            {
                return Vector3.zero;
            }

            float progress = Mathf.Clamp01(snapshot.visualProgress);
            Vector3 rawDirection = snapshot.dodgeDirection.ToVector3();
            Vector3 direction = rawDirection;
            if (direction.sqrMagnitude > 0.01f)
            {
                direction.Normalize();
            }

            if (snapshot.dodgeMovementActive)
            {
                float hop = Mathf.Sin(progress * Mathf.PI);
                return new Vector3(0f, 0f, hop * 0.2f);
            }

            // The additional tumble is a rotation overlay.  It must not turn
            // its 24 visual ticks into a second lateral cell-dodge motion.
            if (snapshot.additionalTumbleActive)
            {
                return Vector3.zero;
            }

            switch (snapshot.visualState)
            {
                case RimKataVisualState.StandardDodge:
                    return direction * (Mathf.Sin(progress * Mathf.PI) * 0.45f);
                case RimKataVisualState.Tumble:
                    return new Vector3(0f, 0.02f, Mathf.Sin(progress * Mathf.PI) * 0.65f);
                default:
                    return Vector3.zero;
            }
        }

        public static bool RequiresDynamicBodyRotation(RimKataVisualSnapshot snapshot)
        {
            return snapshot.deflectionSpinActive
                        || snapshot.additionalTumbleActive
                        || (snapshot.visualActive
                            && snapshot.visualState == RimKataVisualState.Tumble)
                        || snapshot.closeDodgeActive
                        || snapshot.groundPoseActive
                        || (snapshot.responsePoseActive
                            && snapshot.responsePoseLookAtFocus);
        }

        public static bool TryGetResponseFacing(
            Pawn pawn,
            RimKataVisualSnapshot snapshot,
            out Rot4 facing)
        {
            facing = Rot4.Invalid;
            if (!snapshot.responsePoseLookAtFocus
                || !TryGetLiveResponseFocus(
                    pawn,
                    snapshot,
                    out LocalTargetInfo focus))
            {
                return false;
            }

            Thing target = focus.Thing;
            Vector3 direction = target.DrawPos - pawn.DrawPos;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.001f)
            {
                return false;
            }

            facing = Rot4.FromAngleFlat(direction.AngleFlat());
            return facing.IsValid;
        }

        public static bool TryGetLiveResponseFocus(
            Pawn pawn,
            RimKataVisualSnapshot snapshot,
            out LocalTargetInfo focus)
        {
            focus = LocalTargetInfo.Invalid;
            if (pawn?.Map == null
                || !snapshot.responsePoseActive
                || !snapshot.responsePoseFocus.HasThing)
            {
                return false;
            }

            Thing target = snapshot.responsePoseFocus.Thing;
            if (target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map)
            {
                return false;
            }

            if (target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(targetPawn))
            {
                return false;
            }

            focus = snapshot.responsePoseFocus;
            return true;
        }

        private static bool TryGetVisualLoadout(
            Pawn pawn,
            bool resolveAccess,
            out ThingWithComps primary,
            out ThingWithComps secondary)
        {
            primary = null;
            secondary = null;
            bool cached = false;
            bool hasAccess;
            if (!resolveAccess
                && !RimKataTargetAccess.IsEnabled(pawn))
            {
                cached = RimKataEligibilityCache
                    .TryGetRegisteredSecondaryWeapon(
                        pawn,
                        out secondary);
                hasAccess = cached
                    && RimKataEligibility.FactionEffectsEnabled(pawn);
            }
            else
            {
                hasAccess = resolveAccess
                    ? RimKataEligibility.HasRimKataAccess(pawn)
                    : IsCachedWorldVisualUser(pawn);
            }
            if (!hasAccess)
            {
                return false;
            }

            primary = pawn?.equipment?.Primary;
            if (!cached)
            {
                cached = RimKataEligibilityCache
                    .TryGetRegisteredSecondaryWeapon(
                        pawn,
                        out secondary);
            }
            if (!cached)
            {
                secondary = RimKataSecondaryWeaponRegistry
                    .CurrentRegistry
                    ?.Get(pawn);
            }

            if (IsHeldSecondary(pawn, primary, secondary))
            {
                return true;
            }

            if (cached && secondary != null)
            {
                secondary = RimKataSecondaryWeaponRegistry
                    .CurrentRegistry
                    ?.Get(pawn);
            }

            if (!IsHeldSecondary(pawn, primary, secondary))
            {
                secondary = null;
            }

            return true;
        }

        private static bool IsHeldSecondary(
            Pawn pawn,
            ThingWithComps primary,
            ThingWithComps secondary)
        {
            return secondary == null
                || IsHeldNonPrimary(pawn, primary, secondary);
        }

        private static bool IsHeldNonPrimary(
            Pawn pawn,
            ThingWithComps primary,
            ThingWithComps weapon)
        {
            return weapon != null
                && !weapon.Destroyed
                && weapon != primary
                && pawn?.equipment?.AllEquipmentListForReading
                    ?.Contains(weapon) == true;
        }

        public static Pawn FindPawnOwner(Thing thing)
        {
            IThingHolder holder = thing?.ParentHolder;
            for (int i = 0; holder != null && i < 5; i++)
            {
                if (holder is Pawn pawn)
                {
                    return pawn;
                }

                holder = holder.ParentHolder;
            }

            return null;
        }
    }

    public struct RimKataCarryDrawContext
    {
        public bool active;
        public Pawn pawn;
        public ThingWithComps primary;
        public ThingWithComps secondary;
        public bool snapshotActive;
        public RimKataVisualSnapshot snapshot;
        public Vector3 drawPos;
    }

    internal static class RimKataCarryDrawUtility
    {
        [ThreadStatic] private static RimKataCarryDrawContext current;
        [ThreadStatic] private static int scopeDepth;
        [ThreadStatic] private static RimKataCarryDrawContext[] nestedContexts;

        public static ref readonly RimKataCarryDrawContext Current => ref current;

        public static int Push(
            ThingWithComps weapon,
            Vector3 drawPos)
        {
            int scopeToken = EnterScope();
            try
            {
                ref readonly RimKataGunReadyDrawContext renderContext =
                    ref RimKataGunReadyDrawUtility.Current;
                if (renderContext.scoped)
                {
                    if (renderContext.active
                        && renderContext.primary == weapon)
                    {
                        current.pawn = renderContext.pawn;
                        current.primary = renderContext.primary;
                        current.secondary = renderContext.secondary;
                        if (renderContext.snapshotActive)
                        {
                            current.snapshot = renderContext.snapshot;
                        }

                        current.snapshotActive =
                            renderContext.snapshotActive;
                        current.drawPos = drawPos;
                        current.active = true;
                    }

                    return scopeToken;
                }

                Pawn pawn = RimKataVisualUtility.FindPawnOwner(weapon);
                bool rimKataUser = RimKataVisualUtility
                    .TryGetCachedWorldLoadout(
                        pawn,
                        out ThingWithComps primary,
                        out ThingWithComps rawSecondary);
                bool responseParticipant = RimKataVisualUtility
                    .TryGetResponseParticipantLoadout(
                        pawn,
                        out ThingWithComps participantPrimary,
                        out ThingWithComps participantSecondary);
                if (!rimKataUser && responseParticipant)
                {
                    primary = participantPrimary;
                }

                if ((!rimKataUser && !responseParticipant)
                    || primary != weapon)
                {
                    return scopeToken;
                }

                ThingWithComps secondary = rimKataUser
                    ? RimKataVisualUtility.IsSecondaryUsable(
                        pawn,
                        primary,
                        rawSecondary)
                            ? rawSecondary
                            : null
                    : participantSecondary;
                bool snapshotActive =
                    (secondary != null || responseParticipant)
                    && RimKataVisualUtility.TryGetCachedActiveSnapshot(
                        pawn,
                        out current.snapshot);
                current.pawn = pawn;
                current.primary = primary;
                current.secondary = secondary;
                current.snapshotActive = snapshotActive;
                current.drawPos = drawPos;
                current.active = true;
                return scopeToken;
            }
            catch
            {
                Pop(scopeToken);
                throw;
            }
        }

        private static int EnterScope()
        {
            int previousDepth = scopeDepth;
            if (previousDepth > 0)
            {
                EnsureNestedContextCapacity(previousDepth);
                nestedContexts[previousDepth - 1] = current;
                current = default(RimKataCarryDrawContext);
            }

            scopeDepth = previousDepth + 1;
            current.active = false;
            return scopeDepth;
        }

        private static void EnsureNestedContextCapacity(int requiredLength)
        {
            if (nestedContexts != null
                && nestedContexts.Length >= requiredLength)
            {
                return;
            }

            int newLength = nestedContexts == null
                ? 2
                : nestedContexts.Length * 2;
            while (newLength < requiredLength)
            {
                newLength *= 2;
            }

            Array.Resize(ref nestedContexts, newLength);
        }

        public static void Pop(int scopeToken)
        {
            if (scopeToken <= 0)
            {
                return;
            }

            if (scopeDepth != scopeToken)
            {
                current = default(RimKataCarryDrawContext);
                scopeDepth = 0;
                if (nestedContexts != null)
                {
                    Array.Clear(
                        nestedContexts,
                        0,
                        nestedContexts.Length);
                }

                return;
            }

            int previousDepth = scopeToken - 1;
            if (previousDepth == 0)
            {
                current = default(RimKataCarryDrawContext);
            }
            else
            {
                int nestedIndex = previousDepth - 1;
                current = nestedContexts[nestedIndex];
                nestedContexts[nestedIndex] =
                    default(RimKataCarryDrawContext);
            }

            scopeDepth = previousDepth;
        }
    }

    [StaticConstructorOnStartup]
    public static class RimKataDualWeaponRenderUtility
    {
        [ThreadStatic] private static bool drawingPair;
        [ThreadStatic] private static bool drawingSecondary;
        [ThreadStatic] private static bool mirroringSecondaryDepth;
        [ThreadStatic] private static bool mirroringRangedCombatWeapon;
        [ThreadStatic] private static Vector3 nativePlacementPivot;
        [ThreadStatic] private static float nativeSecondaryCombatTilt;
        [ThreadStatic] private static float nativeSecondaryReflectionAxis;
        [ThreadStatic] private static Vector3 currentEquipmentPivot;
        private static Mesh plane10VFlip;
        private static Mesh plane10UvFlip;
        internal static readonly float PawnRenderAltitude =
            Altitudes.AltitudeFor(AltitudeLayer.Pawn);

        private const float CombatIndicatorBaseAltitude = 0.2f;
        private const float CombatIndicatorTopAltitude = 0.201f;
        private const float FocusedTargetLineWidth = 0.2f;
        private const float FocusedTargetLineMinimumPixels = 2f;

        private static readonly Material BlackCombatIndicatorMaterial = SolidColorMaterials.SimpleSolidColorMaterial(new Color( 0f, 0f, 0f, 0.3f));
        private static readonly Material BlackTargetLineMaterial =
            MaterialPool.MatFrom(
                GenDraw.LineTexPath,
                ShaderDatabase.Transparent,
                Color.black);

        public static bool DrawingPair => drawingPair;
        internal static bool DrawingSecondary => drawingSecondary;

        internal static float NativeSecondaryAngleForContext(float angle)
        {
            // The native angle already includes its mesh branch and equipment
            // offset. The V-flipped mesh reflects local Z, hence the half turn.
            return drawingSecondary && (!mirroringSecondaryDepth || mirroringRangedCombatWeapon)
                ? 2f * nativeSecondaryReflectionAxis - angle - 180f
                : angle;
        }

        public static Mesh Plane10ForContext()
        {
            return drawingSecondary && (!mirroringSecondaryDepth || mirroringRangedCombatWeapon)
                ? plane10VFlip ??= CreateVFlippedMesh(MeshPool.plane10)
                : MeshPool.plane10;
        }

        public static Mesh Plane10FlipForContext()
        {
            return drawingSecondary && (!mirroringSecondaryDepth || mirroringRangedCombatWeapon)
                ? plane10UvFlip ??= CreateVFlippedMesh(MeshPool.plane10Flip)
                : MeshPool.plane10Flip;
        }

        public static void DrawSecondaryEquipmentMesh(
            Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            // Native and captured secondary draws share the same final placement.
            new RimKataWeaponDrawCapture.DrawCommand(mesh, matrix, material, layer)
                .Submit(mesh, matrix, mirrorSecondaryDepth: mirroringSecondaryDepth,
                    adjustSecondaryHeight: mirroringSecondaryDepth && !mirroringRangedCombatWeapon,
                    pawnPivot: nativePlacementPivot,
                    weaponAngleOffset: nativeSecondaryCombatTilt,
                    lowerSecondaryDepth: drawingSecondary && !mirroringSecondaryDepth);
        }

        public static bool TryDrawPair(
            Thing equipment,
            Vector3 originalDrawLoc,
            float originalAimAngle)
        {
            if (drawingPair)
            {
                return false;
            }

            ref readonly RimKataCarryDrawContext carryContext =
                ref RimKataCarryDrawUtility.Current;
            Pawn pawn;
            ThingWithComps primary;
            ThingWithComps secondary;
            bool snapshotActive;
            RimKataVisualSnapshot snapshot;
            if (carryContext.active)
            {
                pawn = carryContext.pawn;
                primary = carryContext.primary;
                secondary = carryContext.secondary;
                snapshotActive = carryContext.snapshotActive;
                snapshot = carryContext.snapshot;
            }
            else
            {
                ref readonly RimKataGunReadyDrawContext renderContext =
                    ref RimKataGunReadyDrawUtility.Current;
                if (renderContext.scoped)
                {
                    if (!renderContext.active)
                    {
                        return false;
                    }

                    pawn = renderContext.pawn;
                    primary = renderContext.primary;
                    secondary = renderContext.secondary;
                    snapshotActive = renderContext.snapshotActive;
                    snapshot = renderContext.snapshot;
                }
                else
                {
                    pawn = RimKataVisualUtility.FindPawnOwner(equipment);
                    bool rimKataUser = RimKataVisualUtility
                        .TryGetCachedWorldLoadout(
                            pawn,
                            out primary,
                            out ThingWithComps rawSecondary);
                    bool responseParticipant = RimKataVisualUtility
                        .TryGetResponseParticipantLoadout(
                            pawn,
                            out ThingWithComps participantPrimary,
                            out ThingWithComps participantSecondary);
                    if (!rimKataUser && !responseParticipant)
                    {
                        return false;
                    }

                    if (!rimKataUser)
                    {
                        primary = participantPrimary;
                    }

                    secondary = rimKataUser
                        ? RimKataVisualUtility.IsSecondaryUsable(
                            pawn,
                            primary,
                            rawSecondary)
                                ? rawSecondary
                                : null
                        : participantSecondary;
                    if (equipment != primary || secondary == null)
                    {
                        return false;
                    }

                    snapshotActive = RimKataVisualUtility
                        .TryGetCachedActiveSnapshot(
                            pawn,
                            out snapshot);
                }
            }

            if (equipment != primary || secondary == null)
            {
                return false;
            }

            // An idle primary draw does not grant visibility to the secondary.
            // EndFrame evaluates that weapon's own renderer independently.
            if (RimKataWeaponRenderProbe.UsesIndependentIdleVisibility(pawn))
            {
                return false;
            }

            Vector3 equipmentPivot = ResolveEquipmentPivot(
                pawn,
                primary,
                originalDrawLoc,
                originalAimAngle);
            drawingPair = true;
            currentEquipmentPivot = equipmentPivot;
            try
            {
                DrawWeapon(
                    pawn,
                    primary,
                    primary,
                    originalDrawLoc,
                    equipmentPivot,
                    originalAimAngle,
                    false,
                    snapshotActive,
                    snapshot);
                DrawWeapon(
                    pawn,
                    primary,
                    secondary,
                    originalDrawLoc,
                    equipmentPivot,
                    originalAimAngle,
                    true,
                    snapshotActive,
                    snapshot);
            }
            finally
            {
                drawingSecondary = false;
                currentEquipmentPivot = default(Vector3);
                drawingPair = false;
            }

            return true;
        }

        internal static void DrawSecondaryAfterExternalPrimary(
            Pawn pawn, ThingWithComps primary, ThingWithComps secondary, Vector3 root, bool combat = false)
        {
            if (drawingPair || secondary == null) return;
            float aimAngle = pawn.Rotation.AsAngle;
            Vector3 drawLoc;
            if (combat)
            {
                drawLoc = EquipmentCenter(pawn, secondary, root, aimAngle);
            }
            else if (!RimKataWeaponRenderProbe.TryGetVanillaIdlePose(
                pawn, secondary, root, out drawLoc, out aimAngle))
            {
                return;
            }

            ref readonly RimKataGunReadyDrawContext context = ref RimKataGunReadyDrawUtility.Current;
            // External animation renderers can enter here outside the ordinary
            // equipment draw pass. Keep final weapon submission in this pawn's
            // ground-pose scope without changing the external animation itself.
            bool groundPoseScope = RimKataGroundPoseRender.PushEquipment(pawn, PawnRenderFlags.None);
            drawingPair = true;
            currentEquipmentPivot = root;
            try
            {
                DrawWeapon(pawn, primary, secondary, drawLoc, root, aimAngle, true,
                    context.snapshotActive, context.snapshot, nativeCombat: combat);
            }
            finally
            {
                drawingSecondary = false;
                currentEquipmentPivot = default(Vector3);
                drawingPair = false;
                RimKataGroundPoseRender.PopEquipment(groundPoseScope);
            }
        }

        internal static void DrawSecondaryFromOwnIdlePose(
            ThingWithComps secondary, Vector3 root, Rot4 facing,
            Vector3 originalDrawLoc, float originalAimAngle)
        {
            if (drawingPair || secondary == null)
            {
                return;
            }

            Vector3 drawLoc = root + SecondaryOffsetForFacing(originalDrawLoc - root, facing);
            drawingPair = true;
            currentEquipmentPivot = root;
            try
            {
                DrawNativeWeapon(secondary, drawLoc, originalAimAngle, true, true, facing, root);
            }
            finally
            {
                currentEquipmentPivot = default(Vector3);
                drawingPair = false;
            }
        }

        public static void DrawCombatIndicators(Pawn pawn)
        {
            if (pawn?.Spawned != true
                || !Find.Selector.IsSelected(pawn))
            {
                return;
            }

            if (RimKataCrawlFireUtility.TryGetCooldownIndicator(pawn, out Verb crawlVerb,
                    out LocalTargetInfo crawlTarget, out int crawlTicks))
            {
                if (crawlVerb.verbProps?.drawAimPie == true
                    && RimKataTargetAccess.SettingsFor(pawn)?.showRangedWeaponCooldown != false
                    && TryGetIndicatorWeaponCenter(pawn, crawlVerb.EquipmentSource, out Vector3 crawlCenter))
                    DrawBlackAimPie(crawlCenter, crawlTarget, Mathf.Clamp(crawlTicks, 1, 360),
                        CombatIndicatorBaseAltitude);
                return;
            }

            if (!RimKataDualWeaponController
                    .MayNeedCombatIndicatorFrame(pawn)
                || !RimKataVisualUtility.TryGetUiLoadout(
                    pawn,
                    out ThingWithComps primary,
                    out ThingWithComps rawSecondary))
            {
                return;
            }

            ThingWithComps secondary =
                RimKataVisualUtility.IsSecondaryUsable(
                    pawn,
                    primary,
                    rawSecondary)
                        ? rawSecondary
                        : null;

            RimKataCombatIndicatorFrame frame =
                RimKataDualWeaponController.GetCombatIndicatorFrameData(
                    pawn,
                    primary,
                    secondary);
            bool showFocusedLine = RimKataTargetAccess.SettingsFor(pawn)
                ?.showFocusedAttackLine != false;
            if (showFocusedLine && frame.closeTarget != null)
            {
                DrawFocusedCloseTargetLine(
                    pawn,
                    frame.closeTarget);
            }
            else if (showFocusedLine)
            {
                DrawFocusedTargetLine(
                    pawn,
                    frame.primary.focusedTarget,
                    frame.primary.focusedTargetFromAttackGizmo);
                DrawFocusedTargetLine(
                    pawn,
                    frame.secondary.focusedTarget,
                    frame.secondary.focusedTargetFromAttackGizmo);
            }

            if (!frame.primary.visible
                && !frame.secondary.visible)
            {
                return;
            }

            if (frame.primary.visible
                && frame.secondary.visible)
            {
                if (frame.primary.remainingTicks
                    <= frame.secondary.remainingTicks)
                {
                    DrawCombatIndicatorForWeapon(
                        pawn,
                        frame.secondary.visual,
                        frame.secondary.verb,
                        CombatIndicatorBaseAltitude,
                        frame.pauseFireForDodge);
                    DrawCombatIndicatorForWeapon(
                        pawn,
                        frame.primary.visual,
                        frame.primary.verb,
                        CombatIndicatorTopAltitude,
                        frame.pauseFireForDodge);
                }
                else
                {
                    DrawCombatIndicatorForWeapon(
                        pawn,
                        frame.primary.visual,
                        frame.primary.verb,
                        CombatIndicatorBaseAltitude,
                        frame.pauseFireForDodge);
                    DrawCombatIndicatorForWeapon(
                        pawn,
                        frame.secondary.visual,
                        frame.secondary.verb,
                        CombatIndicatorTopAltitude,
                        frame.pauseFireForDodge);
                }

                return;
            }

            if (frame.primary.visible)
            {
                DrawCombatIndicatorForWeapon(
                    pawn,
                    frame.primary.visual,
                    frame.primary.verb,
                    CombatIndicatorBaseAltitude,
                    frame.pauseFireForDodge);

                return;
            }

            DrawCombatIndicatorForWeapon(
                pawn,
                frame.secondary.visual,
                frame.secondary.verb,
                CombatIndicatorBaseAltitude,
                frame.pauseFireForDodge);
        }

        private static void DrawFocusedCloseTargetLine(
            Pawn pawn,
            Thing target)
        {
            Vector3 start = pawn.Position.ToVector3Shifted();
            Vector3 end = new LocalTargetInfo(target).CenterVector3;
            end.y = start.y;
            float altitude = Altitudes.AltitudeFor(
                AltitudeLayer.MetaOverlays);
            GenDraw.DrawLineBetween(
                start,
                end,
                altitude,
                BlackTargetLineMaterial,
                FocusedTargetLineWidthForCamera());
        }

        private static void DrawFocusedTargetLine(
            Pawn pawn,
            Thing target,
            bool fromAttackGizmo)
        {
            if (target == null)
            {
                return;
            }

            Vector3 start = pawn.DrawPos;
            start.y = 0f;
            Vector3 end = new LocalTargetInfo(target).CenterVector3;
            end.y = start.y;
            float altitude = Altitudes.AltitudeFor(
                AltitudeLayer.MetaOverlays);
            if (fromAttackGizmo)
            {
                GenDraw.DrawLineBetween(
                    start,
                    end,
                    altitude,
                    BlackTargetLineMaterial,
                    FocusedTargetLineWidthForCamera());
                return;
            }

            GenDraw.DrawLineBetween(start, end, altitude);
        }

        private static float FocusedTargetLineWidthForCamera()
        {
            Camera camera = Find.Camera;
            if (camera == null
                || !camera.orthographic
                || camera.pixelHeight <= 0
                || camera.orthographicSize <= 0f)
            {
                return FocusedTargetLineWidth;
            }

            float pixelsPerWorldUnit = camera.pixelHeight
                / (camera.orthographicSize * 2f);
            return Mathf.Max(
                FocusedTargetLineWidth,
                FocusedTargetLineMinimumPixels / pixelsPerWorldUnit);
        }

        private static void DrawWeapon(
            Pawn pawn,
            ThingWithComps primary,
            ThingWithComps weapon,
            Vector3 primaryDrawLoc,
            Vector3 equipmentPivot,
            float fallbackAngle,
            bool secondary,
            bool snapshotActive,
            RimKataVisualSnapshot snapshot, bool nativeCombat = false)
        {
            LocalTargetInfo responseFocus = LocalTargetInfo.Invalid;
            bool responseTarget = snapshotActive
                && snapshot.responsePoseWeapon == weapon
                && RimKataVisualUtility.TryGetLiveResponseFocus(
                    pawn, snapshot, out responseFocus);

            // A combat target does not necessarily switch an external renderer
            // to aiming. Follow its actual draw route before applying native aim.
            // A live defensive response still turns this weapon toward its attacker.
            if (secondary && RimKataWeaponRenderProbe.DrawSpecialSecondary(
                pawn, weapon, snapshotActive
                    ? Patch_PawnRenderUtility_RimKataDeflection.GetVisualAngleOffset(weapon, snapshot)
                    : 0f, allowWeaponPose: !responseTarget && !nativeCombat, out _, out _)
                    == RimKataWeaponRenderProbe.SecondaryDrawResult.Custom)
            {
                return;
            }

            float aimAngle = fallbackAngle;
            Vector3 drawLoc = primaryDrawLoc;
            RimKataWeaponVisualData visual = default(RimKataWeaponVisualData);
            bool cycleTarget = RimKataDualWeaponController.TryGetVisualData(
                    pawn,
                    weapon,
                    out visual)
                && visual.target.IsValid;
            bool hasOwnTarget = cycleTarget || responseTarget;
            if (hasOwnTarget)
            {
                LocalTargetInfo target = responseTarget
                    ? responseFocus
                    : visual.target;

                aimAngle = responseTarget
                    ? AngleToTarget(pawn, weapon, target, fallbackAngle)
                    : VisualAimAngle(pawn, weapon, visual, fallbackAngle);
                drawLoc = EquipmentCenter(
                    pawn,
                    weapon,
                    equipmentPivot,
                    aimAngle);
            }
            else if (!secondary
                && RimKataDualWeaponController.TryGetNextAim(pawn, out ThingWithComps activeWeapon, out LocalTargetInfo _)
                && activeWeapon != weapon)
            {
                aimAngle = pawn.Rotation.AsAngle;
                drawLoc = EquipmentCenter(
                    pawn,
                    weapon,
                    equipmentPivot,
                    aimAngle);
            }

            Vector3 placementPivot = equipmentPivot;
            if (secondary)
            {
                if (RimKataWeaponRenderProbe.TryGetDrawPivot(pawn, out Vector3 root, out _))
                {
                    placementPivot = root;
                }
                if (!hasOwnTarget)
                {
                    drawLoc = placementPivot + SecondaryOffsetForFacing(
                        primaryDrawLoc - placementPivot, pawn.Rotation);
                }
            }

            bool secondaryIdle = secondary && !hasOwnTarget && !nativeCombat;
            DrawNativeWeapon(weapon, drawLoc, aimAngle, secondary, secondaryIdle, pawn.Rotation,
                placementPivot);
        }

        private static void DrawNativeWeapon(
            ThingWithComps weapon, Vector3 drawLoc, float aimAngle,
            bool secondary, bool secondaryIdle, Rot4 facing,
            Vector3 placementPivot)
        {
            bool previousSecondary = drawingSecondary;
            bool previousDepthMirroring = mirroringSecondaryDepth;
            bool previousRangedMirroring = mirroringRangedCombatWeapon;
            Vector3 previousPlacementPivot = nativePlacementPivot;
            float previousCombatTilt = nativeSecondaryCombatTilt;
            float previousAxis = nativeSecondaryReflectionAxis;
            drawingSecondary = secondary;
            mirroringSecondaryDepth = secondary && (facing == Rot4.East || facing == Rot4.West);
            mirroringRangedCombatWeapon = mirroringSecondaryDepth && !secondaryIdle
                && weapon.def.IsRangedWeapon;
            nativePlacementPivot = placementPivot;
            nativeSecondaryCombatTilt = secondary && !secondaryIdle && weapon.def.IsMeleeWeapon
                ? (facing == Rot4.East ? 30f : facing == Rot4.West ? -30f : 0f)
                : 0f;
            // Combat mirroring flips the weapon around its actual aim, so a
            // diagonal shot keeps its direction instead of reflecting across E/W.
            nativeSecondaryReflectionAxis = secondaryIdle ? facing.AsAngle : aimAngle;
            try
            {
                PawnRenderUtility.DrawEquipmentAiming(weapon, drawLoc, aimAngle);
                if (secondary)
                {
                    RimKataWeaponRenderProbe.NotifySecondaryDraw(weapon);
                }
            }
            finally
            {
                drawingSecondary = previousSecondary;
                mirroringSecondaryDepth = previousDepthMirroring;
                mirroringRangedCombatWeapon = previousRangedMirroring;
                nativePlacementPivot = previousPlacementPivot;
                nativeSecondaryCombatTilt = previousCombatTilt;
                nativeSecondaryReflectionAxis = previousAxis;
            }
        }

        private static Vector3 SecondaryOffsetForFacing(
            Vector3 offset,
            Rot4 facing)
        {
            // Side-facing draws keep their source pose until final submission,
            // where only the screen-height distance to the pawn is halved.
            if (facing == Rot4.East || facing == Rot4.West)
            {
                return offset;
            }
            float facingAxis = facing.AsAngle;
            Vector3 local = offset.RotatedBy(-facingAxis);
            local.x = -local.x;
            return local.RotatedBy(facingAxis);
        }

        private static Vector3 EquipmentRadial(
            ThingWithComps weapon,
            float angle,
            float distanceFactor)
        {
            return new Vector3(
                0f,
                0f,
                0.4f + weapon.def.equippedDistanceOffset)
                .RotatedBy(angle)
                * distanceFactor;
        }

        private static void DrawCombatIndicatorForWeapon(
            Pawn pawn,
            RimKataWeaponVisualData visual,
            Verb verb,
            float altitudeOffset,
            bool pauseFireForDodge)
        {
            if (pawn == null
                || verb == null)
            {
                return;
            }

            bool warming =
                visual.warming
                && visual.warmupTicksRemaining > 0
                && visual.warmupTotalTicks > 0;

            bool cooling = visual.cooldownTicksRemaining > 0;
            if (warming && pauseFireForDodge)
            {
                return;
            }

            bool weaponCentered = TryGetIndicatorWeaponCenter(pawn, verb.EquipmentSource,
                out Vector3 weaponCenter);

            if (verb.IsMeleeAttack)
            {
                if (warming)
                {
                    if (RimKataTargetAccess.SettingsFor(pawn)
                        ?.showMeleeWeaponAimTime != false)
                    {
                        float radius = Mathf.Min(0.5f, visual.warmupTicksRemaining * 0.002f);
                        DrawBlackCooldownCircle((weaponCentered ? weaponCenter : pawn.Drawer.DrawPos)
                            + new Vector3(0f, altitudeOffset, 0f), radius);
                    }
                }
                else if (cooling)
                {
                    float radius = Mathf.Min(0.5f, visual.cooldownTicksRemaining * 0.002f);
                    GenDraw.DrawCooldownCircle((weaponCentered ? weaponCenter : pawn.Drawer.DrawPos)
                        + new Vector3(0f, altitudeOffset, 0f), radius);
                }

                return;
            }

            if (warming
                && visual.target.IsValid
                && verb.verbProps?.drawAimPie == true)
            {
                int degrees = Mathf.Clamp(visual.warmupTicksRemaining, 1, 360);
                DrawAimPie(weaponCentered ? weaponCenter : pawn.DrawPos,
                    visual.target, degrees, altitudeOffset);

                return;
            }

            if (cooling
                && !verb.Bursting
                && RimKataTargetAccess.SettingsFor(pawn)
                    ?.showRangedWeaponCooldown != false
                && visual.target.IsValid
                && verb.verbProps?.drawAimPie == true)
            {
                int degrees = Mathf.Clamp(visual.cooldownTicksRemaining, 1, 360);
                DrawBlackAimPie(weaponCentered ? weaponCenter : pawn.DrawPos,
                    visual.target, degrees, altitudeOffset);
            }
        }

        private static bool TryGetIndicatorWeaponCenter(Pawn pawn, ThingWithComps weapon,
            out Vector3 center)
            => RimKataGroundPoseRender.TryGetWeaponCenter(pawn, weapon, out center)
                || RimKataCrawlFireRender.TryGetWeaponCenter(pawn, weapon, out center);

        internal static bool TryDrawGroundWarmup(Stance_Warmup warmup, float pieSizeFactor)
        {
            Pawn pawn = warmup?.stanceTracker?.pawn;
            if (pawn?.Spawned != true || !Find.Selector.IsSelected(pawn)
                || !warmup.focusTarg.IsValid || warmup.ticksLeft <= 0
                || !TryGetIndicatorWeaponCenter(pawn, warmup.verb?.EquipmentSource, out Vector3 center))
                return false;
            DrawAimPie(center, warmup.focusTarg, (int)(warmup.ticksLeft * pieSizeFactor),
                CombatIndicatorBaseAltitude);
            return true;
        }

        internal static bool TryDrawGroundCooldown(Stance_Cooldown cooldown)
        {
            Pawn pawn = cooldown?.stanceTracker?.pawn;
            if (pawn?.Spawned != true || !Find.Selector.IsSelected(pawn)
                || cooldown.ticksLeft <= 0
                || !TryGetIndicatorWeaponCenter(pawn, cooldown.verb?.EquipmentSource, out Vector3 center))
                return false;
            GenDraw.DrawCooldownCircle(center + new Vector3(0f, CombatIndicatorBaseAltitude, 0f),
                Mathf.Min(0.5f, cooldown.ticksLeft * 0.002f));
            return true;
        }

        private static void DrawBlackCooldownCircle(
            Vector3 center,
            float radius)
        {
            if (radius <= 0f)
            {
                return;
            }

            Vector3 scale = new Vector3(radius, 1f, radius);
            Matrix4x4 matrix = default(Matrix4x4);
            matrix.SetTRS(center, Quaternion.identity, scale);
            Graphics.DrawMesh(MeshPool.circle, matrix, BlackCombatIndicatorMaterial, 0);
        }

        private static void DrawAimPie(
            Vector3 origin,
            LocalTargetInfo target,
            int degreesWide,
            float altitudeOffset)
        {
            if (!target.IsValid
                || degreesWide <= 0)
            {
                return;
            }

            Vector3 center = origin
                + new Vector3(0f, altitudeOffset, 0f);
            GenDraw.DrawAimPieRaw(
                center,
                AimPieFacing(origin, target),
                Mathf.Min(360, degreesWide));
        }

        private static void DrawBlackAimPie(
            Vector3 origin,
            LocalTargetInfo target,
            int degreesWide,
            float altitudeOffset)
        {
            if (!target.IsValid
                || degreesWide <= 0)
            {
                return;
            }

            degreesWide = Mathf.Min(360, degreesWide);
            float facing = AimPieFacing(origin, target);

            Vector3 center = origin + new Vector3( 0f, altitudeOffset, 0f);
            center += Quaternion.AngleAxis( facing, Vector3.up) * Vector3.forward * 0.8f;
            Quaternion rotation = Quaternion.AngleAxis( facing + degreesWide / 2f - 90f, Vector3.up);
            Graphics.DrawMesh(MeshPool.pies[degreesWide], center, rotation, BlackCombatIndicatorMaterial, 0);
        }

        private static float AimPieFacing(
            Vector3 origin,
            LocalTargetInfo target)
        {
            Vector3 targetPosition = target.HasThing
                && target.Thing.Spawned
                    ? target.Thing.DrawPos
                    : target.Cell.ToVector3Shifted();
            Vector3 direction = targetPosition - origin;
            direction.y = 0f;
            return direction.sqrMagnitude > 0.001f
                ? direction.AngleFlat()
                : 0f;
        }

        public static bool ClaimsVanillaCombatCooldown(
            Stance_Cooldown cooldown)
        {
            Pawn pawn = cooldown?.stanceTracker?.pawn;
            Verb verb = cooldown?.verb;
            ThingWithComps weapon = verb?.EquipmentSource as ThingWithComps;
            if (pawn?.Spawned != true
                || !Find.Selector.IsSelected(pawn)
                || !RimKataAutomaticRangeVisualUtility
                    .CanDrawAutomaticSearchRange(pawn)
                || verb == null
                || cooldown.ticksLeft <= 0
                || weapon == null)
            {
                return false;
            }

            if (!RimKataVisualUtility.TryGetUiLoadout(
                    pawn,
                    out ThingWithComps primary,
                    out ThingWithComps rawSecondary))
            {
                return false;
            }

            ThingWithComps secondary =
                RimKataVisualUtility.IsSecondaryUsable(
                    pawn,
                    primary,
                    rawSecondary)
                        ? rawSecondary
                        : null;
            if (weapon != primary && weapon != secondary)
            {
                return false;
            }

            if (verb.IsMeleeAttack)
            {
                return RimKataDualWeaponController.TryGetVisualData(
                        pawn,
                        weapon,
                        out RimKataWeaponVisualData meleeVisual)
                    && meleeVisual.cooldownTicksRemaining > 0;
            }

            if (verb.verbProps?.drawAimPie != true
                || !cooldown.focusTarg.IsValid)
            {
                return false;
            }

            return RimKataDualWeaponController.TryGetIndicatorVisualData(
                    pawn,
                    weapon,
                    out RimKataWeaponVisualData _,
                    out bool claimed)
                && claimed;
        }

        internal static bool TryGetCurrentEquipmentPivot(
            out Vector3 equipmentPivot)
        {
            equipmentPivot = currentEquipmentPivot;
            return drawingPair;
        }

        internal static Vector3 ResolveEquipmentPivot(
            Pawn pawn,
            ThingWithComps primary,
            Vector3 originalDrawLoc,
            float originalAimAngle)
        {
            ref readonly RimKataCarryDrawContext carryContext =
                ref RimKataCarryDrawUtility.Current;
            if (carryContext.active
                && carryContext.pawn == pawn
                && carryContext.primary == primary)
            {
                return carryContext.drawPos;
            }

            if (RimKataWeaponRenderProbe.TryGetIdlePivot(pawn, out Vector3 root, out _))
            {
                return root;
            }

            if (pawn == null || primary == null)
            {
                return originalDrawLoc;
            }

            float distanceFactor = pawn.ageTracker?.CurLifeStage
                ?.equipmentDrawDistanceFactor ?? 1f;
            return originalDrawLoc
                - EquipmentRadial(
                    primary,
                    originalAimAngle,
                    distanceFactor);
        }

        private static Vector3 EquipmentCenter(
            Pawn pawn,
            ThingWithComps weapon,
            Vector3 equipmentPivot,
            float aimAngle)
        {
            float distanceFactor = pawn.ageTracker?.CurLifeStage
                ?.equipmentDrawDistanceFactor ?? 1f;
            return equipmentPivot
                + EquipmentRadial(weapon, aimAngle, distanceFactor);
        }

        internal static float VisualAimAngle(
            Pawn pawn, ThingWithComps weapon, RimKataWeaponVisualData visual, float fallback)
        {
            if (!visual.turning)
                return AngleToTarget(pawn, weapon, visual.target, fallback);
            float destination = AngleToTarget(pawn, weapon, visual.turnTarget, fallback);
            return Mathf.Repeat(Mathf.LerpAngle(visual.turnStartAngle, destination,
                Mathf.SmoothStep(0f, 1f, visual.turnProgress)), 360f);
        }

        internal static void AdjustCooldownAim(Thing equipment, ref Vector3 drawLoc, ref float aimAngle)
        {
            // Reuse the qualified pawn's existing render scope. Ordinary pawns
            // never look up a combat cycle, and paired draws already resolve each hand.
            ref readonly RimKataGunReadyDrawContext context = ref RimKataGunReadyDrawUtility.Current;
            if (drawingPair || !context.active || context.secondary != null
                || equipment != context.primary || !(equipment is ThingWithComps weapon)
                || (context.snapshotActive && context.snapshot.responsePoseWeapon == weapon
                    && RimKataVisualUtility.TryGetLiveResponseFocus(context.pawn, context.snapshot, out _))
                || !RimKataDualWeaponController.TryGetVisualData(context.pawn, weapon, out var visual)
                || !visual.turning) return;
            float angle = VisualAimAngle(context.pawn, weapon, visual, aimAngle);
            Vector3 pivot = ResolveEquipmentPivot(context.pawn, weapon, drawLoc, aimAngle);
            float layer = drawLoc.y;
            drawLoc = pivot + (drawLoc - pivot).RotatedBy(Mathf.DeltaAngle(aimAngle, angle));
            drawLoc.y = layer;
            aimAngle = angle;
        }

        internal static float AngleToTarget(Pawn pawn, ThingWithComps weapon, LocalTargetInfo target, float fallback)
        {
            if (!target.IsValid) return fallback;
            Vector3 targetPosition = target.HasThing && target.Thing.Spawned
                ? target.Thing.DrawPos
                : target.Cell.ToVector3Shifted();
            Vector3 origin = RimKataGroundPoseRender.TryGetRangedAimOrigin(pawn, weapon, out Vector3 headOrigin)
                ? headOrigin : pawn.DrawPos;
            Vector3 aim = targetPosition - origin;
            return aim.sqrMagnitude > 0.001f ? aim.AngleFlat() : fallback;
        }

        private static Mesh CreateVFlippedMesh(Mesh source)
        {
            Mesh mesh = UnityEngine.Object.Instantiate(source);
            Vector2[] uv = mesh.uv;
            for (int i = 0; i < uv.Length; i++)
            {
                uv[i].y = 1f - uv[i].y;
            }

            mesh.uv = uv;
            return mesh;
        }
    }

    public static class RimKataAutomaticRangeVisualUtility
    {
        private const float RangeRingAltitudeOffset = -0.0001f;
        private const int RangeRingRenderQueue = 2899;
        private static readonly List<IntVec3> RingCells = new List<IntVec3>();

        public static bool CanDrawAutomaticSearchRange(Pawn pawn)
        {
            return RimKataEligibilityCache.IsCachedQualifiedPawn(pawn);
        }

        public static void DrawAutomaticSearchRange(Pawn pawn)
        {
            if (pawn?.Map == null
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                return;
            }

            bool hasAccess = RimKataVisualUtility.TryGetUiLoadout(
                pawn,
                out ThingWithComps primary,
                out ThingWithComps rawSecondary);
            if (!hasAccess)
            {
                primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
                rawSecondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            }

            ThingWithComps usableSecondary = hasAccess
                && RimKataVisualUtility.IsSecondaryUsable(
                    pawn,
                    primary,
                    rawSecondary)
                        ? rawSecondary
                        : null;
            float candidateCellRadius = MaximumAutomaticSearchVisualCellRadius(
                pawn,
                primary,
                usableSecondary);

            if (candidateCellRadius <= 0
                || candidateCellRadius > GenRadial.MaxRadialPatternRadius)
            {
                return;
            }

            RingCells.Clear();

            int cellCount = GenRadial.NumCellsInRadius(candidateCellRadius);

            for (int i = 0;
                 i < cellCount;
                 i++)
            {
                IntVec3 cell = pawn.Position + GenRadial.RadialPattern[i];

                RingCells.Add(cell);
            }

            GenDraw.DrawFieldEdges(RingCells, Color.black, RangeRingAltitudeOffset, null, RangeRingRenderQueue);
        }

        public static void DrawLongestRangedWeaponRange(Pawn pawn)
        {
            Verb verb = LongestRangedWeaponVerb(pawn);
            verb?.verbProps?.DrawRadiusRing(pawn.Position, verb);
        }

        private static Verb LongestRangedWeaponVerb(Pawn pawn)
        {
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || !RimKataVisualUtility.TryGetUiLoadout(
                    pawn,
                    out ThingWithComps primary,
                    out ThingWithComps rawSecondary))
            {
                return FirstVanillaRangedCommandVerb(pawn);
            }

            ThingWithComps secondary =
                RimKataVisualUtility.IsSecondaryUsable(
                    pawn,
                    primary,
                    rawSecondary)
                        ? rawSecondary
                        : null;
            if (secondary == null)
            {
                return FirstVanillaRangedCommandVerb(pawn);
            }

            Verb longest = StandardCommandVerb(
                primary);
            Verb secondaryVerb = StandardCommandVerb(secondary);
            if (secondaryVerb?.IsMeleeAttack == false
                && (longest == null
                    || RimKataRangeUtility.ResolveEffectiveRange(
                            pawn,
                            secondary,
                            secondaryVerb)
                        > RimKataRangeUtility.ResolveEffectiveRange(
                            pawn,
                            primary,
                            longest)))
            {
                longest = secondaryVerb;
            }

            return longest;
        }

        private static Verb FirstVanillaRangedCommandVerb(Pawn pawn)
        {
            List<ThingWithComps> equipment =
                pawn?.equipment?.AllEquipmentListForReading;
            if (equipment == null)
            {
                return null;
            }

            for (int i = 0; i < equipment.Count; i++)
            {
                ThingWithComps weapon = equipment[i];
                if (weapon?.def?.IsRangedWeapon != true)
                {
                    continue;
                }

                return StandardCommandVerb(weapon);
            }

            return null;
        }

        private static Verb StandardCommandVerb(ThingWithComps weapon)
        {
            List<Verb> verbs = weapon?.TryGetComp<CompEquippable>()?.AllVerbs;
            if (verbs == null)
            {
                return null;
            }

            for (int i = 0; i < verbs.Count; i++)
            {
                Verb verb = verbs[i];
                if (verb?.verbProps?.hasStandardCommand == true
                    && !verb.IsMeleeAttack)
                {
                    return verb;
                }
            }

            return null;
        }

        private static float MaximumAutomaticSearchVisualCellRadius(
            Pawn pawn,
            ThingWithComps primary,
            ThingWithComps secondary)
        {
            return Mathf.Max(
                AutomaticSearchVisualCellRadius(
                    pawn,
                    primary,
                    RimKataWeaponSlotUtility.CombatVerb(pawn, primary)),
                AutomaticSearchVisualCellRadius(
                    pawn,
                    secondary,
                    RimKataWeaponSlotUtility.CombatVerb(pawn, secondary)));
        }

        private static float AutomaticSearchVisualCellRadius(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb)
        {
            if (verb == null)
            {
                return 0f;
            }

            return Mathf.Max(
                0f,
                verb.IsMeleeAttack
                    ? RimKataRangeUtility.ResolveEffectiveRange(
                        pawn,
                        weapon,
                        verb)
                    : RimKataRangeUtility.ResolveCandidateCellRadius(
                        pawn,
                        weapon,
                        verb));
        }
    }

    [HarmonyPatch(typeof(PawnAttackGizmoUtility), "GetMeleeAttackGizmo")]
    public static class Patch_PawnAttackGizmoUtility_RimKataMeleeRange
    {
        private static readonly Action<LocalTargetInfo> DrawSelectedMeleeRangesAction = DrawSelectedMeleeRanges;

        public static void Postfix(Pawn pawn, ref Gizmo __result)
        {
            if (!(__result is Command_Target command)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                return;
            }

            Action<LocalTargetInfo> originalAction = command.action;
            if (originalAction != null
                && RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                command.action = target =>
                    RimKataAttackGizmoTargetContext.Invoke(
                        originalAction,
                        target);
            }

            if (command.onUpdate == null)
            {
                command.onUpdate = DrawSelectedMeleeRangesAction;
            }
            else if (command.onUpdate != DrawSelectedMeleeRangesAction)
            {
                command.onUpdate += DrawSelectedMeleeRangesAction;
            }
        }

        private static void DrawSelectedMeleeRanges(LocalTargetInfo _)
        {
            Selector selector = Find.Selector;
            Map map = Find.CurrentMap;
            if (selector == null || map == null)
            {
                return;
            }

            IReadOnlyList<Pawn> qualifiedPawns =
                RimKataEligibilityCache.GetQualifiedPawns(map);
            for (int i = 0; i < qualifiedPawns.Count; i++)
            {
                Pawn pawn = qualifiedPawns[i];
                if (pawn?.Spawned != true
                    || !selector.IsSelected(pawn))
                {
                    continue;
                }

                RimKataAutomaticRangeVisualUtility.DrawAutomaticSearchRange(pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.StanceTrackerDraw))]
    public static class Patch_PawnStanceTracker_RimKataCombatIndicators
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataDualWeaponRenderUtility.DrawCombatIndicators(___pawn);
        }
    }

    [HarmonyPatch(
        typeof(Stance_Warmup),
        nameof(Stance_Warmup.StanceDraw))]
    internal static class Patch_StanceWarmup_RimKataGroundIndicator
    {
        private static bool Prefix(Stance_Warmup __instance, bool ___drawAimPie, float ___pieSizeFactor)
            => !___drawAimPie || !RimKataDualWeaponRenderUtility.TryDrawGroundWarmup(
                __instance, ___pieSizeFactor);
    }

    [HarmonyPatch(
        typeof(Stance_Cooldown),
        nameof(Stance_Cooldown.StanceDraw))]
    public static class Patch_StanceCooldown_RimKataRangedIndicator
    {
        public static bool Prefix(Stance_Cooldown __instance)
        {
            return !RimKataDualWeaponRenderUtility
                    .ClaimsVanillaCombatCooldown(__instance)
                && !RimKataDualWeaponRenderUtility.TryDrawGroundCooldown(__instance);
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.DynamicDrawPhaseAt))]
    public static class Patch_PawnRenderer_RimKataDodgeOffset
    {
        public static void Prefix(
            Pawn ___pawn,
            DrawPhase phase,
            ref Vector3 drawLoc,
            ref Rot4? rotOverride)
        {
            if (phase == DrawPhase.EnsureInitialized)
            {
                return;
            }

            if (!RimKataCombatStatePresenceCache.TryGetOwner(
                    ___pawn,
                    out RimKataMapComponent component))
            {
                return;
            }

            if (RimKataEligibilityCache.IsCachedQualifiedPawn(___pawn)
                && ___pawn?.stances?.curStance
                    is Stance_RimKataAim movingAim
                && movingAim.TryGetCachedMovementDirection(
                    out IntVec3 movementDirection))
            {
                Vector3 forward = movementDirection.ToVector3();
                PawnLeaner leaner = ___pawn.Drawer?.leaner;
                if (forward.sqrMagnitude > 0.001f && leaner != null)
                {
                    forward.Normalize();
                    Vector3 leanOffset = leaner.LeanOffset;
                    drawLoc -= forward
                        * Vector3.Dot(leanOffset, forward);
                }
            }

            if (!RimKataVisualUtility.TryGetCachedActiveSnapshot(
                    ___pawn,
                    component,
                    out RimKataVisualSnapshot snapshot))
            {
                return;
            }

            drawLoc += RimKataVisualUtility.DrawOffset(snapshot);
            if (snapshot.deflectionSpinActive)
            {
                rotOverride = Rot4.FromAngleFlat(
                    snapshot.deflectionSpinStartAngle
                    + 360f * snapshot.deflectionSpinProgress * snapshot.deflectionSpinSign);
            }
            else if (snapshot.dodgeMovementActive
                && !snapshot.additionalTumbleActive
                && snapshot.dodgeMovementDirection != IntVec3.Zero)
            {
                rotOverride = Rot4.FromIntVec3(
                    snapshot.dodgeMovementDirection);
            }
            else if (RimKataVisualUtility.TryGetResponseFacing(
                ___pawn,
                snapshot,
                out Rot4 responseFacing))
            {
                rotOverride = responseFacing;
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    public static class Patch_PawnRenderer_RimKataDynamicRotationCache
    {
        public static void Prefix(Pawn ___pawn, ref bool disableCache)
        {
            if (!RimKataVisualUtility.TryGetCachedActiveSnapshot(
                    ___pawn,
                    out RimKataVisualSnapshot snapshot))
            {
                return;
            }

            if (RimKataVisualUtility.RequiresDynamicBodyRotation(snapshot))
            {
                disableCache = true;
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.ParallelPreDraw))]
    public static class Patch_PawnRenderTree_RimKataTumbleRotation
    {
        public static void Prefix(ref PawnDrawParms parms, List<PawnGraphicDrawRequest> ___drawRequests,
            out RimKataVisualSnapshot __state)
        {
            RimKataGroundPoseHead.Restore(___drawRequests);
            __state = default;
            if (parms.Portrait
                || !RimKataVisualUtility.TryGetCachedActiveSnapshot(
                    parms.pawn,
                    out RimKataVisualSnapshot snapshot))
            {
                return;
            }

            bool additionalTumble = snapshot.additionalTumbleActive;
            if (snapshot.groundPoseActive) __state = snapshot;
            if (snapshot.groundPoseActive && snapshot.groundPoseFacing.IsValid)
                parms.facing = snapshot.groundPoseFacing;
            bool stationaryTumble = snapshot.visualActive  && snapshot.visualState == RimKataVisualState.Tumble;
            if (!additionalTumble && !stationaryTumble && !snapshot.closeDodgeActive)
            {
                return;
            }

            float tumbleAngle = (additionalTumble || stationaryTumble)
                ? (additionalTumble
                    ? snapshot.additionalTumbleProgress
                        * snapshot.additionalTumbleTotalTicks
                    : snapshot.visualProgress
                        * snapshot.visualTotalTicks)
                    * RimKataCombatTuning.TumbleDegreesPerTick
                    * snapshot.tumbleSign
                : 0f;

            Matrix4x4 adjustedMatrix = parms.matrix;
            if (Mathf.Abs(tumbleAngle) > 0.001f)
            {
                adjustedMatrix *= Matrix4x4.Rotate(Quaternion.AngleAxis(tumbleAngle, Vector3.up));
            }

            if (snapshot.closeDodgeActive)
            {
                Vector3 footPivot = new Vector3(0f, 0f, -0.5f);
                adjustedMatrix = adjustedMatrix
                    * Matrix4x4.Translate(footPivot)
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(snapshot.closeDodgeAngle, Vector3.up))
                    * Matrix4x4.Translate(-footPivot);
            }

            parms.matrix = adjustedMatrix;
        }

        public static void Postfix(PawnDrawParms parms, List<PawnGraphicDrawRequest> ___drawRequests,
            RimKataVisualSnapshot __state)
        {
            if (__state.groundPoseActive)
                RimKataGroundPoseRender.Prepare(parms, ___drawRequests, __state);
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    public static class Patch_PawnRenderUtility_RimKataDualWeapons
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Thing eq, ref Vector3 drawLoc, ref float aimAngle)
        {
            if (RimKataCrawlFireRender.TryHandleEquipment(eq, out bool drawOriginal))
                return drawOriginal;
            RimKataDualWeaponRenderUtility.AdjustCooldownAim(eq, ref drawLoc, ref aimAngle);
            RimKataGroundPoseRender.AdjustWeaponAim(eq, ref drawLoc, ref aimAngle);
            if (RimKataWeaponRenderProbe.TryCaptureNativeDraw(eq, drawLoc, aimAngle))
            {
                return false;
            }
            RimKataWeaponRenderProbe.NotifyEquipmentDraw(eq);
            return !RimKataDualWeaponRenderUtility.TryDrawPair(eq, drawLoc, aimAngle);
        }

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            FieldInfo plane10 = AccessTools.Field(typeof(MeshPool), nameof(MeshPool.plane10));
            FieldInfo plane10Flip = AccessTools.Field(typeof(MeshPool), nameof(MeshPool.plane10Flip));
            MethodInfo choosePlane10 = AccessTools.Method(typeof(RimKataDualWeaponRenderUtility), nameof(RimKataDualWeaponRenderUtility.Plane10ForContext));
            MethodInfo choosePlane10Flip = AccessTools.Method(typeof(RimKataDualWeaponRenderUtility), nameof(RimKataDualWeaponRenderUtility.Plane10FlipForContext));
            MethodInfo drawMesh = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
                new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int) });
            MethodInfo drawSecondaryMesh = AccessTools.Method(typeof(RimKataDualWeaponRenderUtility),
                nameof(RimKataDualWeaponRenderUtility.DrawSecondaryEquipmentMesh));
            MethodInfo drawPrimaryMesh = AccessTools.Method(typeof(RimKataGroundPoseRender),
                nameof(RimKataGroundPoseRender.DrawPrimaryMesh));
            FieldInfo drawingSecondary = AccessTools.Field(typeof(RimKataDualWeaponRenderUtility),
                "drawingSecondary");

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, plane10))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = choosePlane10;
                }
                else if (instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, plane10Flip))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = choosePlane10Flip;
                }
                else if (instruction.Calls(drawMesh))
                {
                    // Primary geometry stays intact; the scoped ground-pose
                    // overlay is applied only at the final submission.
                    Label secondaryDraw = generator.DefineLabel();
                    Label drawComplete = generator.DefineLabel();
                    yield return new CodeInstruction(OpCodes.Ldsfld, drawingSecondary)
                        .MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Brtrue, secondaryDraw);
                    yield return new CodeInstruction(OpCodes.Call, drawPrimaryMesh);
                    yield return new CodeInstruction(OpCodes.Br, drawComplete);
                    CodeInstruction secondaryCall = new CodeInstruction(OpCodes.Call, drawSecondaryMesh);
                    secondaryCall.labels.Add(secondaryDraw);
                    yield return secondaryCall;
                    CodeInstruction complete = new CodeInstruction(OpCodes.Nop);
                    complete.labels.Add(drawComplete);
                    yield return complete;
                    continue;
                }

                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    public static class Patch_PawnRenderUtility_RimKataDeflection
    {
        [ThreadStatic] private static float visualAngleOffset;

        [HarmonyPriority(Priority.Last)]
        public static void Prefix(
            Thing eq,
            ref Vector3 drawLoc,
            ref float aimAngle,
            out float __state)
        {
            __state = visualAngleOffset;
            visualAngleOffset = 0f;

            if (RimKataCrawlFireRender.Drawing)
                return;

            ref readonly RimKataGunReadyDrawContext renderContext =
                ref RimKataGunReadyDrawUtility.Current;
            if (renderContext.portrait
                || !(eq is ThingWithComps weapon)
                || !RimKataResponseVisualParticipantCache
                    .TryGetWeaponOwner(weapon, out Pawn owner)
                || owner.equipment?.AllEquipmentListForReading
                    ?.Contains(weapon) != true)
            {
                return;
            }

            RimKataVisualSnapshot snapshot;
            if (renderContext.active
                && renderContext.pawn == owner
                && renderContext.snapshotActive)
            {
                snapshot = renderContext.snapshot;
            }
            else if (!RimKataVisualUtility.TryGetCachedActiveSnapshot(
                owner,
                out snapshot))
            {
                return;
            }

            float totalOffset = GetVisualAngleOffset(weapon, snapshot);
            if (totalOffset == 0f)
            {
                return;
            }

            if (owner != null && Mathf.Abs(totalOffset) > 0.001f)
            {
                Vector3 pivot;
                if (!RimKataDualWeaponRenderUtility
                    .TryGetCurrentEquipmentPivot(out pivot))
                {
                    pivot = RimKataDualWeaponRenderUtility
                        .ResolveEquipmentPivot(
                            owner,
                            weapon,
                            drawLoc,
                            aimAngle);
                }
                float renderHeight = drawLoc.y;
                Vector3 radial = drawLoc - pivot;
                radial.y = 0f;
                radial = radial.RotatedBy(totalOffset);
                drawLoc = new Vector3(pivot.x + radial.x, renderHeight, pivot.z + radial.z);
            }

            visualAngleOffset = totalOffset;
        }

        internal static float GetVisualAngleOffset(ThingWithComps weapon, RimKataVisualSnapshot snapshot)
        {
            float angle = 0f;
            if (snapshot.deflectionSpinActive)
            {
                angle += 360f * snapshot.deflectionSpinProgress * snapshot.deflectionSpinSign;
            }
            if (snapshot.deflectionActive && snapshot.deflectionWeapon == weapon)
            {
                float wave = 1f - Mathf.SmoothStep(0f, 1f, snapshot.deflectionProgress);
                angle += 30f * wave * snapshot.deflectionSign;
            }
            if (snapshot.responsePoseActive && snapshot.responsePoseWeapon == weapon)
            {
                float wave = 1f - Mathf.SmoothStep(0f, 1f, snapshot.responsePoseProgress);
                angle += snapshot.responsePoseMaxAngle * wave * snapshot.responsePoseSign;
            }
            return angle;
        }

        public static void Finalizer(float __state)
        {
            visualAngleOffset = __state;
        }

        public static float ApplyVisualAngleOffset(float angle)
        {
            return RimKataDualWeaponRenderUtility.NativeSecondaryAngleForContext(angle)
                + visualAngleOffset;
        }

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            MethodInfo applyOffset = AccessTools.Method(typeof(Patch_PawnRenderUtility_RimKataDeflection), nameof(ApplyVisualAngleOffset));
            bool patched = false;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instruction = codes[i];
                yield return instruction;
                if (!patched && instruction.opcode == OpCodes.Stloc_1 && i > 0 && codes[i - 1].opcode == OpCodes.Rem)
                {
                    yield return new CodeInstruction(OpCodes.Ldloc_1);
                    yield return new CodeInstruction(OpCodes.Call, applyOffset);
                    yield return new CodeInstruction(OpCodes.Stloc_1);
                    patched = true;
                }
            }

            if (!patched)
            {
                Log.Error("[RimKata] Could not find the final equipment rotation anchor.");
            }
        }
    }

    public struct RimKataGunReadyDrawContext
    {
        public bool scoped;
        public bool portrait;
        public bool active;
        public bool gunReady;
        public Pawn scopePawn;
        public Pawn pawn;
        public ThingWithComps primary;
        public ThingWithComps secondary;
        public bool snapshotActive;
        public RimKataVisualSnapshot snapshot;
        public float aimAngle;
    }

    internal static class RimKataGunReadyDrawUtility
    {
        [ThreadStatic] private static RimKataGunReadyDrawContext current;
        [ThreadStatic] private static int scopeDepth;
        [ThreadStatic] private static RimKataGunReadyDrawContext[] nestedContexts;

        public static ref readonly RimKataGunReadyDrawContext Current => ref current;

        public static bool IsDrawingEquipmentFor(Pawn pawn)
        {
            return pawn != null
                && current.scoped
                && ReferenceEquals(current.scopePawn, pawn);
        }

        public static int Push(Pawn pawn, PawnRenderFlags flags)
        {
            bool portrait = (flags & PawnRenderFlags.Portrait) != 0;
            int scopeToken = EnterScope(portrait);
            try
            {
                current.scopePawn = pawn;
                if (portrait || pawn == null)
                {
                    return scopeToken;
                }

                // Access and response events publish these participants. An ordinary
                // draw must not discover eligibility, equipment, or combat state.
                bool rimKataUser = RimKataEligibilityCache.IsCachedQualifiedPawn(pawn);
                if ((!rimKataUser
                        && !RimKataResponseVisualParticipantCache.IsParticipant(pawn))
                    || !pawn.Spawned)
                {
                    return scopeToken;
                }

                ThingWithComps primary = null;
                ThingWithComps rawSecondary = null;
                rimKataUser = rimKataUser && RimKataVisualUtility
                    .TryGetCachedWorldLoadout(
                        pawn,
                        out primary,
                        out rawSecondary);
                bool statePresent = RimKataCombatStatePresenceCache.TryGetOwner(
                    pawn,
                    out RimKataMapComponent component);
                bool responseParticipant = false;
                ThingWithComps participantPrimary = null;
                ThingWithComps participantSecondary = null;
                ThingWithComps secondary = rimKataUser
                    && RimKataVisualUtility.IsSecondaryUsable(
                        pawn,
                        primary,
                        rawSecondary)
                            ? rawSecondary
                            : null;
                if (statePresent && (!rimKataUser || secondary == null))
                {
                    responseParticipant = RimKataVisualUtility
                        .TryGetResponseParticipantLoadout(
                            pawn,
                            out participantPrimary,
                            out participantSecondary);
                }

                if (!rimKataUser)
                {
                    if (!responseParticipant)
                    {
                        return scopeToken;
                    }

                    primary = participantPrimary;
                    secondary = participantSecondary;
                }

                if (primary == null)
                {
                    return scopeToken;
                }

                bool mayNeedGunReadyTarget = rimKataUser
                    && MayNeedGunReadyTarget(pawn, statePresent);
                bool gunReadyCandidate = mayNeedGunReadyTarget
                    && !pawn.Dead
                    && !pawn.Downed
                    && !pawn.IsBurning()
                    && primary != null
                    && pawn.carryTracker?.CarriedThing == null
                    && (flags & PawnRenderFlags.NeverAimWeapon) == 0
                    && !(pawn.stances?.curStance is Stance_Busy);
                bool needsActiveContext = secondary != null
                    || responseParticipant
                    || (statePresent && pawn.stances?.curStance is Stance_RimKataAim)
                    || gunReadyCandidate;
                if (!needsActiveContext)
                {
                    return scopeToken;
                }

                bool snapshotActive = statePresent
                    && (secondary != null || responseParticipant)
                    && RimKataVisualUtility.TryGetCachedResponseSnapshot(
                        pawn,
                        component,
                        responseParticipant,
                        out current.snapshot);
                current.pawn = pawn;
                current.primary = primary;
                current.secondary = secondary;
                current.snapshotActive = snapshotActive;
                current.active = true;
                if (!gunReadyCandidate)
                {
                    return scopeToken;
                }

                component = component ?? pawn.Map.GetComponent<RimKataMapComponent>();
                if (component?.TryGetGunReadyTarget(pawn, out LocalTargetInfo target) != true)
                {
                    return scopeToken;
                }

                if (!RimKataEligibility.TryGetEnabledCombatVerb(pawn, out Verb _))
                {
                    return scopeToken;
                }

                float aimAngle = pawn.Rotation.AsAngle;
                Vector3 targetPosition;
                if (target.IsValid)
                {
                    targetPosition = target.HasThing && target.Thing.Spawned
                        ? target.Thing.DrawPos
                        : target.Cell.ToVector3Shifted();
                    Vector3 aimVector = targetPosition - pawn.DrawPos;
                    if (aimVector.sqrMagnitude > 0.001f)
                    {
                        aimAngle = aimVector.AngleFlat();
                    }
                }
                else if (RimKataDodgeMovementUtility.TryGetCurrentMovementDirection(pawn, out IntVec3 direction))
                {
                    aimAngle = direction.ToVector3().AngleFlat();
                }

                current.aimAngle = aimAngle;
                current.gunReady = true;
                return scopeToken;
            }
            catch
            {
                Pop(scopeToken);
                throw;
            }
        }

        private static int EnterScope(bool portrait)
        {
            int previousDepth = scopeDepth;
            if (previousDepth > 0)
            {
                EnsureNestedContextCapacity(previousDepth);
                nestedContexts[previousDepth - 1] = current;
                current = default(RimKataGunReadyDrawContext);
            }

            scopeDepth = previousDepth + 1;
            current.scoped = true;
            current.portrait = portrait;
            current.active = false;
            current.gunReady = false;
            return scopeDepth;
        }

        private static void EnsureNestedContextCapacity(int requiredLength)
        {
            if (nestedContexts != null
                && nestedContexts.Length >= requiredLength)
            {
                return;
            }

            int newLength = nestedContexts == null
                ? 2
                : nestedContexts.Length * 2;
            while (newLength < requiredLength)
            {
                newLength *= 2;
            }

            Array.Resize(ref nestedContexts, newLength);
        }

        private static bool MayNeedGunReadyTarget(
            Pawn pawn,
            bool statePresent)
        {
            return pawn?.CurJobDef == RimKataDefOf.RimKata_Attack
                || statePresent;
        }

        public static void Pop(int scopeToken)
        {
            if (scopeToken <= 0)
            {
                return;
            }

            if (scopeDepth != scopeToken)
            {
                current = default(RimKataGunReadyDrawContext);
                scopeDepth = 0;
                if (nestedContexts != null)
                {
                    Array.Clear(
                        nestedContexts,
                        0,
                        nestedContexts.Length);
                }

                return;
            }

            int previousDepth = scopeToken - 1;
            if (previousDepth == 0)
            {
                current = default(RimKataGunReadyDrawContext);
            }
            else
            {
                int nestedIndex = previousDepth - 1;
                current = nestedContexts[nestedIndex];
                nestedContexts[nestedIndex] =
                    default(RimKataGunReadyDrawContext);
            }

            scopeDepth = previousDepth;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
    public static class Patch_PawnRenderUtility_RimKataGunReadyContext
    {
        public struct DrawScope
        {
            internal int gunReady;
            internal int probe;
            internal bool groundPose;
        }

        [HarmonyPriority(Priority.First)]
        public static void Prefix(
            Pawn pawn,
            Vector3 drawPos,
            Rot4 facing,
            PawnRenderFlags flags,
            out DrawScope __state)
        {
            __state = default(DrawScope);
            __state.groundPose = RimKataGroundPoseRender.PushEquipment(pawn, flags);
            __state.gunReady = RimKataGunReadyDrawUtility.Push(pawn, flags);
            __state.probe = RimKataWeaponRenderProbe.BeginFrame(pawn, drawPos, facing, flags);
        }

        public static Exception Finalizer(
            Exception __exception,
            DrawScope __state)
        {
            try
            {
                RimKataWeaponRenderProbe.EndFrame(__state.probe, __exception == null);
            }
            finally
            {
                RimKataGunReadyDrawUtility.Pop(__state.gunReady);
                RimKataGroundPoseRender.PopEquipment(__state.groundPose);
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.CarryWeaponOpenly))]
    public static class Patch_PawnRenderUtility_RimKataCarryGunReady
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            ref readonly RimKataGunReadyDrawContext context =
                ref RimKataGunReadyDrawUtility.Current;
            if (context.gunReady && context.pawn == pawn)
            {
                __result = true;
            }

        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawCarriedWeapon))]
    public static class Patch_PawnRenderUtility_RimKataCarryDrawContext
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(
            ThingWithComps weapon,
            Vector3 drawPos,
            out int __state)
        {
            __state = 0;
            __state = RimKataCarryDrawUtility.Push(weapon, drawPos);
        }

        public static Exception Finalizer(
            Exception __exception,
            int __state)
        {
            RimKataCarryDrawUtility.Pop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawCarriedWeapon))]
    public static class Patch_PawnRenderUtility_RimKataDrawGunReady
    {
        public static bool Prefix(
            ThingWithComps weapon,
            Vector3 drawPos,
            float equipmentDrawDistanceFactor)
        {
            ref readonly RimKataGunReadyDrawContext context =
                ref RimKataGunReadyDrawUtility.Current;
            if (!context.gunReady || context.primary != weapon)
            {
                return true;
            }

            float aimAngle = context.aimAngle;
            Vector3 aimedDrawLoc = drawPos + new Vector3(0f, 0f, 0.4f + weapon.def.equippedDistanceOffset).RotatedBy(aimAngle) * equipmentDrawDistanceFactor;
            PawnRenderUtility.DrawEquipmentAiming(weapon, aimedDrawLoc, aimAngle);
            return false;
        }
    }

    [HarmonyPatch(
    typeof(Command_VerbTarget),
    nameof(Command_VerbTarget.GizmoUpdateOnMouseover))]
    public static class Patch_CommandVerbTarget_RimKataAutomaticRange
    {
        public static void Postfix(
            Command_VerbTarget __instance,
            List<Verb> ___groupedVerbs)
        {
            if (__instance?.drawRadius != true)
            {
                return;
            }

            DrawAutomaticSearchRange(__instance.verb);

            if (___groupedVerbs == null)
            {
                return;
            }

            for (int i = 0; i < ___groupedVerbs.Count; i++)
            {
                DrawAutomaticSearchRange(___groupedVerbs[i]);
            }
        }

        private static void DrawAutomaticSearchRange(Verb verb)
        {
            Pawn pawn = verb?.CasterPawn;
            if (pawn == null
                || !pawn.Spawned
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                return;
            }

            ThingWithComps commandWeapon = verb.EquipmentSource;
            if (commandWeapon == null)
            {
                return;
            }

            if (!RimKataVisualUtility.TryGetUiLoadout(
                    pawn,
                    out ThingWithComps primary,
                    out ThingWithComps rawSecondary))
            {
                return;
            }

            ThingWithComps secondary =
                RimKataVisualUtility.IsSecondaryUsable(
                    pawn,
                    primary,
                    rawSecondary)
                        ? rawSecondary
                        : null;
            if (commandWeapon != primary
                && commandWeapon != secondary)
            {
                return;
            }

            RimKataAutomaticRangeVisualUtility.DrawAutomaticSearchRange(pawn);
        }
    }

    [HarmonyPatch(typeof(PawnAttackGizmoUtility), "GetSquadAttackGizmo")]
    public static class Patch_PawnAttackGizmoUtility_RimKataSquadRange
    {
        private static readonly Action<LocalTargetInfo>
            DrawSelectedAutomaticRangesAction = DrawSelectedAutomaticRanges;

        public static void Postfix(ref Gizmo __result)
        {
            if (!(__result is Command_Target command))
            {
                return;
            }

            RimKataMultiSelectAttackGizmoUtility.SelectedAttackGizmoFacts
                selectedFacts = RimKataMultiSelectAttackGizmoUtility
                    .GetSelectedAttackGizmoFacts();
            if (!selectedFacts.HasAutomaticSearchRange)
            {
                return;
            }

            Action<LocalTargetInfo> originalAction = command.action;
            if (originalAction != null
                && selectedFacts.HasCombatCapableUser)
            {
                command.action = target =>
                    RimKataAttackGizmoTargetContext.InvokeSquad(
                        originalAction,
                        target);
            }

            if (selectedFacts.UseUnifiedAttackGizmo)
            {
                command.onUpdate = DrawSelectedUnifiedRanges;
            }
            else if (command.onUpdate == null)
            {
                command.onUpdate = DrawSelectedAutomaticRangesAction;
            }
            else if (command.onUpdate != DrawSelectedAutomaticRangesAction)
            {
                command.onUpdate += DrawSelectedAutomaticRangesAction;
            }
        }

        private static void DrawSelectedUnifiedRanges(LocalTargetInfo _)
        {
            List<Pawn> selectedPawns = Find.Selector?.SelectedPawns;
            if (selectedPawns == null)
            {
                return;
            }

            for (int i = 0; i < selectedPawns.Count; i++)
            {
                Pawn pawn = selectedPawns[i];
                if (pawn?.Spawned != true || !pawn.IsPlayerControlled)
                {
                    continue;
                }

                RimKataAutomaticRangeVisualUtility
                    .DrawLongestRangedWeaponRange(pawn);
                if (RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
                {
                    RimKataAutomaticRangeVisualUtility
                        .DrawAutomaticSearchRange(pawn);
                }
            }
        }

        private static void DrawSelectedAutomaticRanges(LocalTargetInfo _)
        {
            Selector selector = Find.Selector;
            Map map = Find.CurrentMap;
            if (selector == null || map == null)
            {
                return;
            }

            IReadOnlyList<Pawn> qualifiedPawns =
                RimKataEligibilityCache.GetQualifiedPawns(map);
            for (int i = 0; i < qualifiedPawns.Count; i++)
            {
                Pawn pawn = qualifiedPawns[i];
                if (pawn?.Spawned == true
                    && selector.IsSelected(pawn))
                {
                    RimKataAutomaticRangeVisualUtility
                        .DrawAutomaticSearchRange(pawn);
                }
            }
        }
    }
}
