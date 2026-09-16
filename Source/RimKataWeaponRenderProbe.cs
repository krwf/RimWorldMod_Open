using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // A probe substitutes only reads made by discovered render methods. Equipment,
    // jobs and combat state remain owned by the live pawn throughout the draw.
    internal static class RimKataWeaponRenderProbe
    {
        internal enum SecondaryDrawResult
        {
            None,
            Custom,
            Native
        }

        private struct Frame
        {
            internal Pawn pawn;
            internal ThingWithComps primary;
            internal ThingWithComps secondary;
            internal Vector3 root;
            internal Rot4 facing;
            internal PawnRenderFlags flags;
            internal bool independentIdle;
            internal bool primaryDrawn;
            internal bool secondaryDrawn;
        }

        private static readonly Dictionary<ThingDef, RimKataWeaponRenderDiscovery.Renderer[]> renderersByDef =
            new Dictionary<ThingDef, RimKataWeaponRenderDiscovery.Renderer[]>();
        private static readonly HashSet<ThingDef> failedProbes = new HashSet<ThingDef>();
        private static readonly HashSet<ThingDef> failedFallbacks = new HashSet<ThingDef>();
        private static readonly RimKataWeaponRenderDiscovery.Renderer[] noRenderers =
            Array.Empty<RimKataWeaponRenderDiscovery.Renderer>();
        [ThreadStatic] private static Frame frame;
        [ThreadStatic] private static Frame[] parents;
        [ThreadStatic] private static int frameDepth;
        [ThreadStatic] private static Pawn probePawn;
        [ThreadStatic] private static ThingWithComps probeWeapon;
        [ThreadStatic] private static int probeDepth;
        [ThreadStatic] private static int extraDrawDepth;
        [ThreadStatic] private static bool nativeDrawSeen;
        [ThreadStatic] private static Vector3 nativeDrawLoc;
        [ThreadStatic] private static float nativeAimAngle;

        internal static bool Probing => probeDepth > 0;

        internal static void Initialize(Harmony harmony)
        {
            try
            {
                RimKataWeaponRenderDiscovery.Initialize(harmony);
                RefreshDefinitions();
            }
            catch (Exception exception)
            {
                Log.Warning("[RimKata] Automatic weapon renderer discovery could not finish: "
                    + exception.GetType().Name);
            }
        }

        internal static void RefreshDefinitions()
        {
            renderersByDef.Clear();
            failedProbes.Clear();
            failedFallbacks.Clear();
            if (!RimKataWeaponRenderDiscovery.HasRenderers)
            {
                return;
            }

            // Definitions are shared by named profiles. Register every weapon's
            // possible routes here; the existing loadout gate decides permission.
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.IsWeapon)
                {
                    Register(def);
                }
            }
        }

        private static RimKataWeaponRenderDiscovery.Renderer[] Register(ThingDef def)
        {
            var matches = new List<RimKataWeaponRenderDiscovery.Renderer>();
            foreach (var renderer in RimKataWeaponRenderDiscovery.Renderers)
            {
                if (renderer.Supports(def))
                {
                    matches.Add(renderer);
                }
            }

            var result = matches.Count == 0 ? noRenderers : matches.ToArray();
            renderersByDef[def] = result;
            return result;
        }

        internal static int BeginFrame(Pawn pawn, Vector3 root, Rot4 facing, PawnRenderFlags flags)
        {
            if (Probing || !RimKataWeaponRenderDiscovery.HasRenderers)
            {
                return 0;
            }

            if (frameDepth > 0)
            {
                if (parents == null || parents.Length < frameDepth)
                {
                    Array.Resize(ref parents, Math.Max(4, frameDepth * 2));
                }
                parents[frameDepth - 1] = frame;
            }
            frame = default(Frame);
            int token = ++frameDepth;
            ref readonly RimKataGunReadyDrawContext context = ref RimKataGunReadyDrawUtility.Current;
            if (!context.active || context.pawn != pawn || context.secondary == null
                || (flags & PawnRenderFlags.Portrait) != 0
                || pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || pawn.carryTracker?.CarriedThing != null)
            {
                return token;
            }

            // Hidden vanilla equipment may still have an external idle renderer.
            // Its visibility belongs to each weapon, not to the primary slot.
            frame.pawn = pawn;
            frame.primary = context.primary;
            frame.secondary = context.secondary;
            frame.root = root;
            frame.facing = facing;
            frame.flags = flags;
            frame.independentIdle = !PawnRenderUtility.CarryWeaponOpenly(pawn) && !IsAiming(pawn);
            return token;
        }

        internal static void EndFrame(int token, bool completed)
        {
            if (token == 0)
            {
                return;
            }
            try
            {
                if (completed && token == frameDepth && frame.pawn != null
                    && !frame.secondaryDrawn
                    && !failedFallbacks.Contains(frame.secondary.def))
                {
                    if (frame.independentIdle)
                    {
                        // Probe the secondary even when the primary was hidden.
                        // No draw from its own renderer means no idle fallback.
                        if (DrawSpecialSecondary(frame.pawn, frame.secondary, 0f, true,
                            out Vector3 drawLoc, out float aimAngle) == SecondaryDrawResult.Native)
                        {
                            RimKataDualWeaponRenderUtility.DrawSecondaryFromOwnIdlePose(
                                frame.secondary, frame.root, frame.facing, drawLoc, aimAngle);
                        }
                    }
                    else if (frame.primaryDrawn)
                    {
                        // An external primary renderer may never enter DrawEquipmentAiming.
                        RimKataDualWeaponRenderUtility.DrawSecondaryAfterExternalPrimary(
                            frame.pawn, frame.primary, frame.secondary, frame.root);
                    }
                }
            }
            catch (Exception exception)
            {
                if (frame.secondary != null && failedFallbacks.Add(frame.secondary.def))
                {
                    Log.Warning("[RimKata] Could not draw the secondary weapon after an external renderer for "
                        + frame.secondary.def.defName + ": " + exception.GetType().Name);
                }
            }
            finally
            {
                if (token == frameDepth && --frameDepth > 0)
                {
                    frame = parents[frameDepth - 1];
                    parents[frameDepth - 1] = default(Frame);
                }
                else
                {
                    frame = default(Frame);
                    frameDepth = 0;
                }
            }
        }

        internal static void NotifyMeshDraw()
        {
            if (!Probing && extraDrawDepth == 0 && frame.pawn != null)
            {
                frame.primaryDrawn = true;
            }
        }

        internal static void NotifyEquipmentDraw(Thing weapon)
        {
            if (!Probing && frame.pawn != null && weapon == frame.primary)
            {
                frame.primaryDrawn = true;
            }
        }

        internal static void NotifySecondaryDraw(Thing weapon)
        {
            if (!Probing && frame.pawn != null && weapon == frame.secondary)
            {
                frame.secondaryDrawn = true;
            }
        }

        private static bool IsAiming(Pawn pawn)
        {
            return pawn?.stances?.curStance is Stance_Busy busy
                && !busy.neverAimWeapon && busy.focusTarg.IsValid;
        }

        internal static bool UsesIndependentIdleVisibility(Pawn pawn)
        {
            return !Probing && pawn != null && frame.pawn == pawn && frame.independentIdle;
        }

        internal static bool TryGetIdlePivot(Pawn pawn, out Vector3 pivot, out float facingAngle)
        {
            pivot = frame.root;
            facingAngle = frame.facing.AsAngle;
            return !Probing && frame.pawn == pawn && pawn != null
                && !IsAiming(pawn) && !RimKataGunReadyDrawUtility.Current.gunReady;
        }

        internal static SecondaryDrawResult DrawSpecialSecondary(
            Pawn pawn, ThingWithComps weapon, float visualAngleOffset, bool allowWeaponPose,
            out Vector3 nativeLoc, out float nativeAngle)
        {
            nativeLoc = default(Vector3);
            nativeAngle = 0f;
            if (Probing || pawn == null || frame.pawn != pawn || weapon == null
                || failedProbes.Contains(weapon.def))
            {
                return SecondaryDrawResult.None;
            }

            if (!renderersByDef.TryGetValue(weapon.def, out var renderers))
            {
                renderers = Register(weapon.def);
            }
            if (renderers.Length == 0)
            {
                return SecondaryDrawResult.None;
            }

            Pawn previousPawn = probePawn;
            ThingWithComps previousWeapon = probeWeapon;
            bool previousNative = nativeDrawSeen;
            probePawn = pawn;
            probeWeapon = weapon;
            probeDepth++;
            try
            {
                foreach (var renderer in renderers)
                {
                    nativeDrawSeen = false;
                    using (var capture = RimKataWeaponDrawCapture.Begin())
                    {
                        bool replaced = renderer.ReplacesOriginal(pawn, frame.root, frame.facing, frame.flags);
                        bool nativeWeapon = nativeDrawSeen;
                        if (!replaced || (!nativeWeapon && (capture.Count == 0 || !allowWeaponPose)))
                        {
                            continue;
                        }

                        // Replay during the capture lifetime. Never cache instance
                        // materials or world-space poses across pawns or frames.
                        // Native aiming is suppressed during the probe, so when
                        // it was seen the captured meshes contain only additions
                        // such as a sheath. Keep those anchored to the pawn.
                        if (capture.Count > 0 && !capture.ReplayMirrored(
                            frame.root, frame.facing.AsAngle, -0.001f,
                            nativeWeapon ? 0f : visualAngleOffset))
                        {
                            continue;
                        }
                        if (nativeWeapon)
                        {
                            // Preserve an actual native draw request separately
                            // from an empty renderer, including native-only routes.
                            nativeLoc = nativeDrawLoc;
                            nativeAngle = nativeAimAngle;
                            return SecondaryDrawResult.Native;
                        }
                        frame.secondaryDrawn = true;
                        return SecondaryDrawResult.Custom;
                    }
                }
            }
            catch (Exception exception)
            {
                if (failedProbes.Add(weapon.def))
                {
                    Log.Warning("[RimKata] Could not inspect weapon rendering for "
                        + weapon.def.defName + "; skipping its custom secondary pose. "
                        + exception.GetType().Name);
                }
            }
            finally
            {
                probeDepth--;
                probePawn = previousPawn;
                probeWeapon = previousWeapon;
                nativeDrawSeen = previousNative;
            }
            return SecondaryDrawResult.None;
        }

        internal static bool TryGetVanillaIdlePose(
            Pawn pawn, ThingWithComps weapon, Vector3 root,
            out Vector3 drawLoc, out float aimAngle)
        {
            Pawn previousPawn = probePawn;
            ThingWithComps previousWeapon = probeWeapon;
            bool previousNative = nativeDrawSeen;
            probePawn = pawn;
            probeWeapon = weapon;
            nativeDrawSeen = false;
            probeDepth++;
            try
            {
                float factor = pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f;
                using (RimKataWeaponDrawCapture.Begin())
                {
                    PawnRenderUtility.DrawCarriedWeapon(weapon, root, frame.facing, factor);
                }
                drawLoc = nativeDrawLoc;
                aimAngle = nativeAimAngle;
                return nativeDrawSeen;
            }
            finally
            {
                probeDepth--;
                probePawn = previousPawn;
                probeWeapon = previousWeapon;
                nativeDrawSeen = previousNative;
            }
        }

        // Signatures intentionally match the original calls for IL substitution.
        internal static ThingWithComps Primary(Pawn_EquipmentTracker tracker)
        {
            return Probing && tracker == probePawn?.equipment ? probeWeapon : tracker.Primary;
        }

        internal static bool TryCaptureNativeDraw(Thing weapon, Vector3 drawLoc, float aimAngle)
        {
            if (!Probing)
            {
                return false;
            }
            if (weapon == probeWeapon)
            {
                nativeDrawSeen = true;
                nativeDrawLoc = drawLoc;
                nativeAimAngle = aimAngle;
            }
            return true;
        }

        internal static void DrawEquipmentAiming(Thing weapon, Vector3 drawLoc, float aimAngle)
        {
            if (!TryCaptureNativeDraw(weapon, drawLoc, aimAngle))
            {
                PawnRenderUtility.DrawEquipmentAiming(weapon, drawLoc, aimAngle);
            }
        }

        internal static void DrawWornExtras(Apparel apparel)
        {
            if (Probing)
            {
                return;
            }
            extraDrawDepth++;
            try
            {
                apparel.DrawWornExtras();
            }
            finally
            {
                extraDrawDepth--;
            }
        }
    }
}
