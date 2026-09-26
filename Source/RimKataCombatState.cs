using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public enum RimKataVisualState
    {
        None,
        StandardDodge,
        AdditionalDodge,
        Tumble
    }

    public struct RimKataVisualSnapshot
    {
        public bool visualActive;
        public RimKataVisualState visualState;
        public float visualProgress;
        public int visualTotalTicks;
        public bool additionalTumbleActive;
        public float additionalTumbleProgress;
        public int additionalTumbleTotalTicks;
        public IntVec3 dodgeDirection;
        public bool dodgeMovementActive;
        public bool dodgeMovementTumbling;
        public float dodgeMovementProgress;
        public float dodgeTumbleStartProgress;
        public IntVec3 dodgeMovementDirection;
        public int tumbleSign;
        public bool deflectionActive;
        public float deflectionProgress;
        public int deflectionSign;
        public ThingWithComps deflectionWeapon;
        public bool deflectionSpinActive;
        public float deflectionSpinStartAngle;
        public float deflectionSpinProgress;
        public int deflectionSpinSign;
        public bool responsePoseActive;
        public float responsePoseProgress;
        public float responsePoseMaxAngle;
        public int responsePoseSign;
        public ThingWithComps responsePoseWeapon;
        public LocalTargetInfo responsePoseFocus;
        public bool responsePoseLookAtFocus;
        public bool closeDodgeActive;
        public float closeDodgeAngle;
        public bool groundPoseActive;
        public bool groundPoseFallen;
        public float groundPoseAngle;
        public float groundPoseDirection;
        public float groundPoseProgress;
        public Vector3 groundPoseOffset;
        public Vector3 groundPoseWeaponOffset;
        public Rot4 groundPoseFacing;
    }

    public sealed class RimKataTrackedRangedProjectile : IExposable
    {
        public Projectile projectile;
        public Pawn target;
        public bool avoided;
        public bool suppressJobNotification;
        public bool preventDirectHit;

        public RimKataTrackedRangedProjectile()
        {
        }

        public RimKataTrackedRangedProjectile(
            Projectile projectile,
            Pawn target)
        {
            this.projectile = projectile;
            this.target = target;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref projectile, "projectile");
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref avoided, "avoided");
            Scribe_Values.Look(ref preventDirectHit, "preventDirectHit");
            Scribe_Values.Look(
                ref suppressJobNotification,
                "suppressJobNotification");
        }
    }

    internal sealed class RimKataCloseProjectileState : IExposable
    {
        public Projectile projectile;
        public Thing target;
        public Verb attackingVerb;
        public bool meleeResolution;
        public bool meleeHit;
        public RimKataCloseDefensePrecheck precheck;
        // One native impact can apply the main injury and several extra packets.
        internal bool defenseResolved;
        internal bool defenseAvoided;

        public RimKataCloseProjectileState()
        {
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref projectile, "projectile");
            Scribe_References.Look(ref target, "target");
            Scribe_References.Look(ref attackingVerb, "attackingVerb");
            Scribe_Values.Look(ref meleeResolution, "meleeResolution");
            Scribe_Values.Look(ref meleeHit, "meleeHit");
            Scribe_Values.Look(ref precheck, "precheck");
        }
    }

    public sealed class RimKataInterceptionShotLink : IExposable
    {
        public Projectile shot;
        public Thing target;

        public RimKataInterceptionShotLink()
        {
        }

        public RimKataInterceptionShotLink(
            Projectile shot,
            Thing target)
        {
            this.shot = shot;
            this.target = target;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref shot, "shot");
            Scribe_References.Look(ref target, "target");
        }
    }

    internal static class RimKataCombatStatePresenceCache
    {
        private sealed class StateMarker
        {
            public volatile RimKataMapComponent owner;
        }

        private static readonly ConditionalWeakTable<Pawn, StateMarker>
            PawnsWithState = new ConditionalWeakTable<Pawn, StateMarker>();
        private static readonly ConditionalWeakTable<Pawn, StateMarker>
            .CreateValueCallback CreateMarker = delegate { return new StateMarker(); };

        public static bool Contains(Pawn pawn, Map map)
        {
            return pawn != null
                && map != null
                && PawnsWithState.TryGetValue(pawn, out StateMarker marker)
                && marker.owner?.map == map;
        }

        internal static bool TryGetOwner(Pawn pawn, out RimKataMapComponent component)
        {
            component = null;
            if (pawn == null || !PawnsWithState.TryGetValue(pawn, out StateMarker marker))
            {
                return false;
            }

            RimKataMapComponent owner = marker.owner;
            Map ownerMap = owner?.map;
            // Most rendered pawns have no state. Read their map only after a hit.
            if (ownerMap == null || pawn.Map != ownerMap)
            {
                return false;
            }

            component = owner;
            return true;
        }

        internal static void Mark(Pawn pawn, RimKataMapComponent component)
        {
            if (pawn != null && component?.map != null)
            {
                PawnsWithState.GetValue(pawn, CreateMarker).owner = component;
            }
        }

        internal static void Clear(Pawn pawn, Map map)
        {
            if (pawn != null
                && map != null
                && PawnsWithState.TryGetValue(pawn, out StateMarker marker)
                && marker.owner?.map == map)
            {
                PawnsWithState.Remove(pawn);
            }
        }
    }

    internal static class RimKataResponseVisualParticipantCache
    {
        private sealed class Entry
        {
            public Pawn pawn;
            public Map map;
            public ThingWithComps deflectionWeapon;
            public ThingWithComps responsePoseWeapon;
            public ThingWithComps spinPrimaryWeapon;
            public ThingWithComps spinSecondaryWeapon;
        }

        private static readonly object UpdateLock = new object();
        private static readonly ConcurrentDictionary<Pawn, Entry> ByPawn =
            new ConcurrentDictionary<Pawn, Entry>();
        private static readonly ConcurrentDictionary<ThingWithComps, Pawn>
            ByWeapon = new ConcurrentDictionary<ThingWithComps, Pawn>();
        private static readonly ConcurrentDictionary<Pawn, Map>
            BodyVisualByPawn = new ConcurrentDictionary<Pawn, Map>();

        public static bool IsParticipant(Pawn pawn)
        {
            return pawn != null && ByPawn.ContainsKey(pawn);
        }

        public static bool IsBodyVisualParticipant(Pawn pawn)
        {
            return pawn != null && BodyVisualByPawn.ContainsKey(pawn);
        }

        public static bool TryGetParticipantWeapons(
            Pawn pawn,
            out ThingWithComps deflectionWeapon,
            out ThingWithComps responsePoseWeapon,
            out ThingWithComps spinSecondaryWeapon)
        {
            deflectionWeapon = null;
            responsePoseWeapon = null;
            spinSecondaryWeapon = null;
            if (pawn == null || !ByPawn.TryGetValue(pawn, out Entry entry))
            {
                return false;
            }

            deflectionWeapon = entry.deflectionWeapon;
            responsePoseWeapon = entry.responsePoseWeapon;
            spinSecondaryWeapon = entry.spinSecondaryWeapon;
            return true;
        }

        public static bool TryGetWeaponOwner(
            ThingWithComps weapon,
            out Pawn pawn)
        {
            pawn = null;
            return weapon != null
                && ByWeapon.TryGetValue(weapon, out pawn)
                && pawn != null;
        }

        internal static void Refresh(RimKataPawnCombatState state)
        {
            Pawn pawn = state?.pawn;
            if (pawn == null)
            {
                return;
            }

            lock (UpdateLock)
            {
                RemoveEntry(pawn);
                if (!state.DeflectionActive && !state.DeflectionSpinActive && !state.ResponsePoseActive)
                {
                    return;
                }

                Entry entry = new Entry
                {
                    pawn = pawn,
                    map = pawn.Map,
                    deflectionWeapon = state.DeflectionActive
                        ? state.deflectionWeapon
                        : null,
                    responsePoseWeapon = state.ResponsePoseActive
                        ? state.responsePoseWeapon
                        : null,
                    spinPrimaryWeapon = state.DeflectionSpinActive
                        ? pawn.equipment?.Primary
                        : null,
                    spinSecondaryWeapon = state.DeflectionSpinActive
                        ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                        : null
                };
                ByPawn[pawn] = entry;
                AddWeapon(entry.deflectionWeapon, pawn);
                AddWeapon(entry.responsePoseWeapon, pawn);
                AddWeapon(entry.spinPrimaryWeapon, pawn);
                AddWeapon(entry.spinSecondaryWeapon, pawn);
            }
        }

        internal static void RefreshBodyVisual(
            RimKataPawnCombatState state)
        {
            Pawn pawn = state?.pawn;
            Map map = pawn?.Map;
            if (pawn != null
                && map != null
                && (state.VisualActive || state.CloseDodgeActive || state.groundPose?.VisualActive == true))
            {
                BodyVisualByPawn[pawn] = map;
                return;
            }

            if (pawn != null)
            {
                BodyVisualByPawn.TryRemove(pawn, out Map _);
            }
        }

        internal static void Clear(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }

            lock (UpdateLock)
            {
                RemoveEntry(pawn);
            }

            BodyVisualByPawn.TryRemove(pawn, out Map _);
        }

        internal static void ClearForMap(Map map)
        {
            if (map == null)
            {
                return;
            }

            lock (UpdateLock)
            {
                foreach (KeyValuePair<Pawn, Entry> pair in ByPawn)
                {
                    if (pair.Value?.map == map)
                    {
                        RemoveEntry(pair.Key);
                    }
                }
            }

            foreach (KeyValuePair<Pawn, Map> pair in BodyVisualByPawn)
            {
                if (pair.Value == map)
                {
                    BodyVisualByPawn.TryRemove(pair.Key, out Map _);
                }
            }
        }

        private static void AddWeapon(
            ThingWithComps weapon,
            Pawn pawn)
        {
            if (weapon != null)
            {
                ByWeapon[weapon] = pawn;
            }
        }

        private static void RemoveEntry(Pawn pawn)
        {
            if (!ByPawn.TryRemove(pawn, out Entry entry))
            {
                return;
            }

            RemoveWeapon(entry.deflectionWeapon, pawn);
            RemoveWeapon(entry.responsePoseWeapon, pawn);
            RemoveWeapon(entry.spinPrimaryWeapon, pawn);
            RemoveWeapon(entry.spinSecondaryWeapon, pawn);
        }

        private static void RemoveWeapon(
            ThingWithComps weapon,
            Pawn pawn)
        {
            if (weapon != null
                && ByWeapon.TryGetValue(weapon, out Pawn indexedPawn)
                && indexedPawn == pawn)
            {
                ByWeapon.TryRemove(weapon, out Pawn _);
            }
        }
    }

    public sealed class RimKataPawnCombatState : IExposable
    {
        public Pawn pawn;
        internal RimKataMapComponent ownerComponent;
        public RimKataVisualState visualState;
        public int ticksRemaining;
        public int totalTicks;
        public int rangedDodgeDelayTicksRemaining;
        public bool additionalDodgeUsed;
        public int additionalTumbleTicksRemaining;
        public int additionalTumbleTotalTicks;
        public IntVec3 dodgeDirection;
        public bool dodgeMovementActive;
        public bool dodgeMovementStartedInCloseCombat;
        public IntVec3 dodgeMovementOrigin = IntVec3.Invalid;
        public IntVec3 dodgeMovementDestination = IntVec3.Invalid;
        public float dodgeMovementProgress;
        public bool dodgeMovementTumbling;
        public float dodgeTumbleStartProgress;
        public IntVec3 dodgeMovementDirection;
        public int dodgeMovementElapsedTicks;
        public bool dodgeStartInProgress;
        public bool dodgeResumeWasMoving;
        public LocalTargetInfo dodgeResumeDestination = LocalTargetInfo.Invalid;
        public PathEndMode dodgeResumePathEndMode = PathEndMode.OnCell;
        public Job dodgeMovementJob;
        public int dodgeFailureStaggerTicks;
        public float dodgeFailureStaggerSpeedFactor = StaggerHandler.DefaultStaggerMoveSpeedFactor;
        public int tumbleSign = 1;
        public int deflectionTicksRemaining;
        public int deflectionTotalTicks;
        public int deflectionSign = 1;
        public bool deflectionSpin;
        public float deflectionSpinStartAngle;
        public float deflectionSpinStartProgress;
        public int deflectionSpinTicksRemaining;
        public int deflectionSpinTotalTicks;
        public int deflectionSpinSign = 1;
        public int responsePoseTicksRemaining;
        public int responsePoseTotalTicks;
        public float responsePoseMaxAngle;
        public int responsePoseSign = 1;
        public LocalTargetInfo responsePoseFocus = LocalTargetInfo.Invalid;
        public bool responsePoseLookAtFocus;
        public int closeDodgeTicksRemaining;
        public int closeDodgeTotalTicks;
        public float closeDodgeStartAngle;
        public Thing closeCombatTrigger;
        // Retained for old saves; live combat does not maintain a drafted-only latch.
        public bool draftedFireActive;
        public IntVec3 draftedMovementSearchCell = IntVec3.Invalid;
        // Transient edge: newly allowed movement can search before the next cell change.
        public bool draftedMovementSearchAllowed;
        public bool draftedMovementSearchTriggerPending;
        public int draftedWarmupTicksRemaining = -1;
        public int draftedCooldownTicksRemaining;
        public int draftedCooldownLastTick = -1;
        public int draftedCandidateRetryTicks;
        public Thing draftedPlannedTarget;
        public bool draftedPlannedInterception;
        public bool draftedPlannedCloseAttack;
        public bool draftedPlannedCloseContext;
        public RimKataWeaponCycleState primaryWeaponCycle = new RimKataWeaponCycleState();
        public RimKataWeaponCycleState secondaryWeaponCycle = new RimKataWeaponCycleState();
        internal bool weaponBindingsDirty = true;
        internal int weaponConfigurationRevision = -1;
        internal int lastEnemyAttackRecoveryJobId = -1;
        public RimKataSharedTargetSearchState sharedTargetSearch =
            new RimKataSharedTargetSearchState();
        public bool idleProjectileSearchTriggerPending;
        public bool weaponSwapPending;
        public bool dualEngagementActive;
        public int dualLastDrivenTick = -1;
        public bool dualCloseCombatActive;
        public bool candidateSaturationExpansionUsed;
        public Thing dualCloseTarget;
        public ThingWithComps engagementOwnerWeapon;
        public ThingWithComps responsePoseWeapon;
        public ThingWithComps deflectionWeapon;
        public Job loadoutInvalidatedCombatJob;
        public bool dedicatedFollowupJobPending;
        public Thing dedicatedFollowupJobTarget;
        public Job dedicatedFollowupJobSourceJob;
        public Job projectileWakeResumeJob;
        public bool dedicatedFollowupJobPlayerForced;
        public bool dedicatedFollowupJobKillIncappedTarget;
        public int dedicatedFollowupJobRequestedTick = -1;
        public bool dedicatedFollowupJobStartInProgress;
        public int dedicatedFollowupJobLastStartTick = -1;
        public Thing dedicatedContinuityTarget;
        public int dedicatedContinuityUntilTick = -1;
        public int movementFireContinuityUntilTick = -1;
        public int staggerSearchLastCheckTick = -1;
        public Pawn incomingThreatSource;
        public int incomingThreatTicksRemaining;
        public int pendingMeleeThreatClearTick = -1;
        public Thing closeAttackRequestTarget;
        public bool closeAttackRequestFromAttackGizmo;
        internal bool temporaryInactive;
        internal bool temporaryInactivityCleanupPending;
        public RimKataGroundPoseState groundPose;
        // Rebuilt by the active Hunt toil after loading; the weapon timers below
        // are already serialized with their usual slot state.
        internal RimKataHuntingSession huntingSession;

        public bool VisualActive => pawn != null
            && ((visualState != RimKataVisualState.None
                    && (ticksRemaining > 0 || dodgeMovementActive))
                || AdditionalTumbleActive);
        public bool DodgeMovementActive => pawn != null
            && dodgeMovementActive;
        public bool RangedDodgeDelayActive => pawn != null
            && rangedDodgeDelayTicksRemaining > 0;
        public bool AdditionalDodgeAvailable => RangedDodgeDelayActive
            && !additionalDodgeUsed;
        public bool AdditionalTumbleActive => pawn != null
            && additionalTumbleTicksRemaining > 0;
        public bool DodgeMovementStartBlocked => DodgeMovementActive
            || (VisualActive && visualState == RimKataVisualState.Tumble);
        public bool DodgeVisualLocked => DodgeMovementActive
            || AdditionalTumbleActive
            || (VisualActive && visualState == RimKataVisualState.Tumble);
        public bool DodgeMovementWatchdogExpired => DodgeMovementActive
            && dodgeMovementElapsedTicks >= RimKataCombatTuning.AdditionalDodgeWatchdogTicks;
        public bool DodgeMotionBlocksJob => DodgeMovementActive;
        public bool DeflectionActive => pawn != null && deflectionTicksRemaining > 0;
        public bool DeflectionSpinActive => pawn != null && deflectionSpin && deflectionSpinTicksRemaining > 0;
        public bool ResponsePoseActive => pawn != null && responsePoseTicksRemaining > 0;
        public bool CloseDodgeActive => pawn != null && closeDodgeTicksRemaining > 0;
        // Close combat is live state, not timed memory.
        public bool CloseCombatActive => TryGetLiveCloseCombatTrigger(out Thing _);
        public bool DraftedFireActive => pawn != null && draftedFireActive;
        // Keep serialized names, but movement search belongs to every combat entry.
        public bool DraftedMovementSearchTracking => pawn != null
            && draftedMovementSearchCell.IsValid;
        public bool DraftedMovementSearchTriggerPending => pawn != null
            && draftedMovementSearchTriggerPending;
        public bool MovementFireContinuityActive
        {
            get
            {
                int currentTick = CurrentGameTick;
                return pawn != null
                    && currentTick >= 0
                    && movementFireContinuityUntilTick >= currentTick;
            }
        }
        public bool StoredCooldownActive => pawn != null && draftedCooldownTicksRemaining > 0;
        public bool WeaponCyclesActive => (primaryWeaponCycle?.Active == true)
            || (secondaryWeaponCycle?.Active == true)
            || dualEngagementActive
            || sharedTargetSearch?.KeepsCombatAlive == true;
        public bool IncomingThreatActive => IsIncomingThreatActive();
        public bool MeleeThreatClearPending => pendingMeleeThreatClearTick >= 0;
        public bool CloseAttackRequestActive => IsCloseAttackRequestActive();
        public bool DebugIncomingThreatStored => incomingThreatSource != null;
        public bool DebugCloseAttackRequestStored =>
            closeAttackRequestTarget != null;
        public bool Active => VisualActive
            || RangedDodgeDelayActive
            || DeflectionActive
            || DeflectionSpinActive
            || ResponsePoseActive
            || CloseDodgeActive
            || CloseCombatActive
            || (pawn != null
                && !pawn.Dead
                && !pawn.Downed
                && pawn.Awake()
                && pawn.CurJobDef == RimKataDefOf.RimKata_Attack)
            || DraftedFireActive
            || DraftedMovementSearchTracking
            || DraftedMovementSearchTriggerPending
            || idleProjectileSearchTriggerPending
            || MovementFireContinuityActive
            || StoredCooldownActive
            || huntingSession != null
            || WeaponCyclesActive
            || IncomingThreatActive
            || MeleeThreatClearPending
            || CloseAttackRequestActive
            || sharedTargetSearch?.KeepsCombatAlive == true
            || dedicatedFollowupJobPending
            || weaponSwapPending
            // Only decide whether a finished combat state still owns a pose.
            // Active ordinary combat returns above without reading pose state.
            || groundPose != null;
        public float VisualProgress => totalTicks <= 0 ? 1f : 1f - ticksRemaining / (float)totalTicks;
        public float AdditionalTumbleProgress =>
            additionalTumbleTotalTicks <= 0
                ? 1f
                : 1f - additionalTumbleTicksRemaining
                    / (float)additionalTumbleTotalTicks;
        public float DeflectionProgress => deflectionTotalTicks <= 0 ? 1f : 1f - deflectionTicksRemaining / (float)deflectionTotalTicks;
        public float DeflectionSpinProgress => Mathf.Lerp(
            deflectionSpinStartProgress,
            1f,
            deflectionSpinTotalTicks <= 0 ? 1f
                : 1f - deflectionSpinTicksRemaining / (float)deflectionSpinTotalTicks);
        public float ResponsePoseProgress => responsePoseTotalTicks <= 0
            ? 1f
            : 1f - responsePoseTicksRemaining / (float)responsePoseTotalTicks;
        public float CurrentCloseDodgeAngle
        {
            get
            {
                if (!CloseDodgeActive)
                {
                    return 0f;
                }

                float progress = closeDodgeTotalTicks <= 0
                    ? 1f
                    : 1f - closeDodgeTicksRemaining / (float)closeDodgeTotalTicks;
                return closeDodgeStartAngle * (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress)));
            }
        }

        public RimKataPawnCombatState()
        {
        }

        public RimKataPawnCombatState(Pawn pawn)
        {
            this.pawn = pawn;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Deep.Look(ref groundPose, "groundPose");
            Scribe_Values.Look(ref visualState, "visualState", RimKataVisualState.None);
            Scribe_Values.Look(ref ticksRemaining, "ticksRemaining");
            Scribe_Values.Look(ref totalTicks, "totalTicks");
            Scribe_Values.Look(
                ref rangedDodgeDelayTicksRemaining,
                "rangedDodgeDelayTicksRemaining");
            Scribe_Values.Look(ref additionalDodgeUsed, "additionalDodgeUsed");
            Scribe_Values.Look(
                ref additionalTumbleTicksRemaining,
                "additionalTumbleTicksRemaining");
            Scribe_Values.Look(
                ref additionalTumbleTotalTicks,
                "additionalTumbleTotalTicks");
            Scribe_Values.Look(ref dodgeDirection, "dodgeDirection");
            Scribe_Values.Look(ref dodgeMovementActive, "dodgeMovementActive");
            Scribe_Values.Look(
                ref dodgeMovementStartedInCloseCombat,
                "dodgeMovementStartedInCloseCombat");
            Scribe_Values.Look(ref dodgeMovementTumbling, "dodgeMovementTumbling");
            Scribe_Values.Look(ref dodgeMovementOrigin, "dodgeMovementOrigin", IntVec3.Invalid);
            Scribe_Values.Look(ref dodgeMovementDestination, "dodgeMovementDestination", IntVec3.Invalid);
            Scribe_Values.Look(ref dodgeMovementProgress, "dodgeMovementProgress");
            Scribe_Values.Look(ref dodgeTumbleStartProgress, "dodgeTumbleStartProgress");
            Scribe_Values.Look(ref dodgeMovementDirection, "dodgeMovementDirection");
            Scribe_Values.Look(ref dodgeMovementElapsedTicks, "dodgeMovementElapsedTicks");
            Scribe_Values.Look(ref dodgeResumeWasMoving, "dodgeResumeWasMoving");
            Scribe_TargetInfo.Look(ref dodgeResumeDestination, "dodgeResumeDestination");
            Scribe_Values.Look(ref dodgeResumePathEndMode, "dodgeResumePathEndMode", PathEndMode.OnCell);
            Scribe_References.Look(ref dodgeMovementJob, "dodgeMovementJob");
            Scribe_Values.Look(ref dodgeFailureStaggerTicks, "dodgeFailureStaggerTicks");
            Scribe_Values.Look(ref dodgeFailureStaggerSpeedFactor, "dodgeFailureStaggerSpeedFactor", StaggerHandler.DefaultStaggerMoveSpeedFactor);
            Scribe_Values.Look(ref tumbleSign, "tumbleSign", 1);
            Scribe_Values.Look(ref deflectionTicksRemaining, "deflectionTicksRemaining");
            Scribe_Values.Look(ref deflectionTotalTicks, "deflectionTotalTicks");
            Scribe_Values.Look(ref deflectionSign, "deflectionSign", 1);
            Scribe_Values.Look(ref deflectionSpin, "deflectionSpin");
            Scribe_Values.Look(ref deflectionSpinStartAngle, "deflectionSpinStartAngle");
            Scribe_Values.Look(ref deflectionSpinStartProgress, "deflectionSpinStartProgress");
            Scribe_Values.Look(ref deflectionSpinTicksRemaining, "deflectionSpinTicksRemaining");
            Scribe_Values.Look(ref deflectionSpinTotalTicks, "deflectionSpinTotalTicks");
            Scribe_Values.Look(ref deflectionSpinSign, "deflectionSpinSign", 1);
            Scribe_Values.Look(ref responsePoseTicksRemaining, "responsePoseTicksRemaining");
            Scribe_Values.Look(ref responsePoseTotalTicks, "responsePoseTotalTicks");
            Scribe_Values.Look(ref responsePoseMaxAngle, "responsePoseMaxAngle");
            Scribe_Values.Look(ref responsePoseSign, "responsePoseSign", 1);
            Scribe_TargetInfo.Look(ref responsePoseFocus, "responsePoseFocus");
            Scribe_Values.Look(
                ref responsePoseLookAtFocus,
                "responsePoseLookAtFocus");
            Scribe_Values.Look(ref closeDodgeTicksRemaining, "closeDodgeTicksRemaining");
            Scribe_Values.Look(ref closeDodgeTotalTicks, "closeDodgeTotalTicks");
            Scribe_Values.Look(ref closeDodgeStartAngle, "closeDodgeStartAngle");
            Scribe_References.Look(ref closeCombatTrigger, "closeCombatTrigger");
            Scribe_Values.Look(ref draftedFireActive, "draftedFireActive");
            Scribe_Values.Look(
                ref draftedMovementSearchTriggerPending,
                "draftedMovementSearchTriggerPending");
            Scribe_Values.Look(ref draftedWarmupTicksRemaining, "draftedWarmupTicksRemaining", -1);
            Scribe_Values.Look(ref draftedCooldownTicksRemaining, "draftedCooldownTicksRemaining");
            Scribe_Values.Look(ref draftedCooldownLastTick, "draftedCooldownLastTick", -1);
            Scribe_Values.Look(ref draftedCandidateRetryTicks, "draftedCandidateRetryTicks");
            Scribe_References.Look(ref draftedPlannedTarget, "draftedPlannedTarget");
            Scribe_Values.Look(ref draftedPlannedInterception, "draftedPlannedInterception");
            Scribe_Values.Look(ref draftedPlannedCloseAttack, "draftedPlannedCloseAttack");
            Scribe_Values.Look(ref draftedPlannedCloseContext, "draftedPlannedCloseContext");
            Scribe_Deep.Look(ref primaryWeaponCycle, "primaryWeaponCycle");
            Scribe_Deep.Look(ref secondaryWeaponCycle, "secondaryWeaponCycle");
            Scribe_Deep.Look(ref sharedTargetSearch, "sharedTargetSearch");
            Scribe_Values.Look(
                ref idleProjectileSearchTriggerPending,
                "idleProjectileSearchTriggerPending");
            Scribe_Values.Look(ref weaponSwapPending, "weaponSwapPending");
            Scribe_Values.Look(ref dualEngagementActive, "dualEngagementActive");
            Scribe_Values.Look(ref dualLastDrivenTick, "dualLastDrivenTick", -1);
            Scribe_Values.Look(ref dualCloseCombatActive, "dualCloseCombatActive");
            Scribe_Values.Look(
                ref candidateSaturationExpansionUsed,
                "candidateSaturationExpansionUsed");
            Scribe_References.Look(ref dualCloseTarget, "dualCloseTarget");
            Scribe_References.Look(ref engagementOwnerWeapon, "engagementOwnerWeapon");
            Scribe_References.Look(ref responsePoseWeapon, "responsePoseWeapon");
            Scribe_References.Look(ref deflectionWeapon, "deflectionWeapon");
            Scribe_References.Look(
                ref projectileWakeResumeJob,
                "projectileWakeResumeJob");
            Scribe_References.Look(ref incomingThreatSource, "incomingThreatSource");
            Scribe_Values.Look(ref incomingThreatTicksRemaining, "incomingThreatTicksRemaining");
            Scribe_Values.Look(ref pendingMeleeThreatClearTick, "pendingMeleeThreatClearTick", -1);
            Scribe_References.Look(ref closeAttackRequestTarget, "closeAttackRequestTarget");
            Scribe_Values.Look(
                ref closeAttackRequestFromAttackGizmo,
                "closeAttackRequestFromAttackGizmo");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (deflectionSpin && deflectionSpinTotalTicks <= 0 && deflectionTicksRemaining > 0)
                {
                    // Older saves shared the weapon-deflection timer; retain the displayed angle.
                    deflectionSpinStartProgress = Mathf.Lerp(deflectionSpinStartProgress, 1f, DeflectionProgress);
                    deflectionSpinTicksRemaining = Mathf.Clamp(deflectionTicksRemaining, 8, 24);
                    deflectionSpinTotalTicks = deflectionSpinTicksRemaining;
                    deflectionSpinSign = deflectionSign < 0 ? -1 : 1;
                }
                if (deflectionSpin)
                {
                    deflectionSpinTotalTicks = Mathf.Clamp(deflectionSpinTotalTicks, 8, 24);
                    deflectionSpinTicksRemaining = Mathf.Clamp(deflectionSpinTicksRemaining, 0, deflectionSpinTotalTicks);
                    deflectionSpinStartProgress = Mathf.Clamp01(deflectionSpinStartProgress);
                    deflectionSpinSign = deflectionSpinSign < 0 ? -1 : 1;
                }
                if (!deflectionSpin || deflectionSpinTicksRemaining <= 0)
                    ClearDeflectionSpin();
                weaponBindingsDirty = true;
                primaryWeaponCycle ??= new RimKataWeaponCycleState();
                secondaryWeaponCycle ??= new RimKataWeaponCycleState();
                sharedTargetSearch ??= new RimKataSharedTargetSearchState();
                movementFireContinuityUntilTick = -1;
                if (weaponSwapPending)
                {
                    primaryWeaponCycle.Reset();
                    secondaryWeaponCycle.Reset();
                }
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit && dodgeMovementActive)
            {
                if (visualState == RimKataVisualState.AdditionalDodge)
                {
                    dodgeMovementTumbling = true;
                }

                if (dodgeMovementDirection == IntVec3.Zero && dodgeMovementOrigin.IsValid && dodgeMovementDestination.IsValid)
                {
                    dodgeMovementDirection = dodgeMovementDestination - dodgeMovementOrigin;
                }
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (additionalTumbleTicksRemaining <= 0
                    && visualState == RimKataVisualState.AdditionalDodge)
                {
                    additionalTumbleTicksRemaining = Mathf.Max(0, ticksRemaining);
                    additionalTumbleTotalTicks = Mathf.Max(
                        additionalTumbleTicksRemaining,
                        totalTicks);
                    visualState = dodgeMovementActive
                        ? RimKataVisualState.StandardDodge
                        : RimKataVisualState.None;
                    ticksRemaining = 0;
                    totalTicks = 0;
                }

                additionalDodgeUsed = rangedDodgeDelayTicksRemaining > 0
                    && additionalDodgeUsed;
                dodgeMovementTumbling = dodgeMovementActive
                    && additionalTumbleTicksRemaining > 0;
            }
        }

        public void Tick()
        {
            UpdateDraftedCooldown();
            TickDraftedMeleeThreatClear();
            if (rangedDodgeDelayTicksRemaining > 0)
            {
                rangedDodgeDelayTicksRemaining--;
                if (rangedDodgeDelayTicksRemaining <= 0)
                {
                    rangedDodgeDelayTicksRemaining = 0;
                    additionalDodgeUsed = false;
                }
            }

            if (additionalTumbleTicksRemaining > 0)
            {
                additionalTumbleTicksRemaining--;
                if (additionalTumbleTicksRemaining <= 0)
                {
                    additionalTumbleTicksRemaining = 0;
                    additionalTumbleTotalTicks = 0;
                    dodgeMovementTumbling = false;
                    RimKataResponseVisualParticipantCache
                        .RefreshBodyVisual(this);
                }
            }

            if (incomingThreatSource != null && !IsIncomingThreatActive())
            {
                incomingThreatSource = null;
            }

            if (closeAttackRequestTarget != null && !IsCloseAttackRequestActive())
            {
                ClearCloseAttackRequest();
            }

            if (visualState == RimKataVisualState.Tumble
                && dodgeResumeWasMoving
                && dodgeMovementJob != null
                && dodgeMovementJob != pawn.CurJob)
            {
                DetachDodgeMovementPreservingVisual();
            }

            if (DodgeMovementActive)
            {
                bool pathWasReplaced = pawn.pather?.Destination.IsValid == true && pawn.pather.Destination.Cell != dodgeMovementDestination;
                if (dodgeMovementJob != pawn.CurJob || pathWasReplaced)
                {
                    DetachDodgeMovementPreservingVisual();
                }
                else
                {
                    if (pawn.Position != dodgeMovementDestination
                        && (pawn.pather == null || !pawn.pather.Moving || !pawn.pather.Destination.IsValid))
                    {
                        pawn.pather?.StartPath(dodgeMovementDestination, PathEndMode.OnCell);
                        if (!DodgeMovementActive)
                        {
                            return;
                        }
                    }

                    float pathProgress = pawn.pather?.MovePercentage ?? 0f;
                    dodgeMovementProgress = Mathf.Max(dodgeMovementProgress, Mathf.Clamp01(pathProgress));
                    dodgeMovementElapsedTicks++;
                    ticksRemaining = Mathf.Max(0, ticksRemaining - 1);
                }
            }
            else if (ticksRemaining > 0)
            {
                ticksRemaining--;
            }

            if (deflectionTicksRemaining > 0)
            {
                deflectionTicksRemaining--;
                if (deflectionTicksRemaining <= 0)
                {
                    EndDeflection();
                }
            }

            if (deflectionSpinTicksRemaining > 0)
            {
                deflectionSpinTicksRemaining--;
                if (deflectionSpinTicksRemaining <= 0)
                    CancelDeflectionSpin();
            }

            if (responsePoseTicksRemaining > 0)
            {
                responsePoseTicksRemaining--;
                if (responsePoseTicksRemaining <= 0)
                {
                    bool restoreBodyAim = responsePoseLookAtFocus;
                    CancelResponsePose();
                    if (restoreBodyAim)
                    {
                        RimKataDualWeaponController.UpdateBodyAimStance(
                            pawn,
                            this);
                    }
                }

            }

            if (closeDodgeTicksRemaining > 0)
            {
                closeDodgeTicksRemaining--;
                if (closeDodgeTicksRemaining <= 0)
                {
                    CancelCloseDodge();
                }
            }

            if (closeCombatTrigger != null
                && !TryGetLiveCloseCombatTrigger(out Thing _))
            {
                CancelCloseCombat();
            }

            if (!DodgeMovementActive && ticksRemaining <= 0)
            {
                bool visualEnded = visualState != RimKataVisualState.None;
                visualState = RimKataVisualState.None;
                dodgeDirection = IntVec3.Zero;
                totalTicks = 0;
                if (!AdditionalTumbleActive)
                {
                    tumbleSign = 1;
                }
                ClearDodgeMovementFields();
                if (visualEnded)
                {
                    RimKataResponseVisualParticipantCache
                        .RefreshBodyVisual(this);
                }
            }
        }

        public void ScheduleDraftedMeleeThreatClear()
        {
            int currentTick = CurrentGameTick;
            if (currentTick >= 0)
            {
                pendingMeleeThreatClearTick = currentTick + 1;
            }
        }

        private void TickDraftedMeleeThreatClear()
        {
            int currentTick = CurrentGameTick;
            if (!MeleeThreatClearPending
                || currentTick < pendingMeleeThreatClearTick)
            {
                return;
            }

            pendingMeleeThreatClearTick = -1;

            // The queued safety cleanup survives temporary inactivity and
            // weapon loss; neither condition makes a friendly threat hostile.
            if (pawn?.Drafted == true
                && RimKataEligibility.HasRimKataAccess(pawn)
                && pawn.mindState?.meleeThreat != null)
            {
                pawn.mindState.meleeThreat = null;
            }
        }

        public void CancelVisual()
        {
            visualState = RimKataVisualState.None;
            ticksRemaining = 0;
            totalTicks = 0;
            additionalTumbleTicksRemaining = 0;
            additionalTumbleTotalTicks = 0;
            dodgeDirection = IntVec3.Zero;
            ClearDodgeMovementFields();
            tumbleSign = 1;
            RimKataResponseVisualParticipantCache
                .RefreshBodyVisual(this);
        }

        private void DetachDodgeMovementPreservingVisual()
        {
            bool baseVisualActive = visualState != RimKataVisualState.None
                && ticksRemaining > 0;
            ClearDodgeMovementFields();

            if (!baseVisualActive)
            {
                visualState = RimKataVisualState.None;
                ticksRemaining = 0;
                totalTicks = 0;
                dodgeDirection = IntVec3.Zero;
                if (!AdditionalTumbleActive)
                {
                    tumbleSign = 1;
                }
            }

            RimKataResponseVisualParticipantCache
                .RefreshBodyVisual(this);
        }

        public void CancelFailedDodgeMovementStart()
        {
            visualState = RimKataVisualState.None;
            ticksRemaining = 0;
            totalTicks = 0;
            dodgeDirection = IntVec3.Zero;
            ClearDodgeMovementFields();
            if (!AdditionalTumbleActive)
            {
                tumbleSign = 1;
            }

            RimKataResponseVisualParticipantCache
                .RefreshBodyVisual(this);
        }

        public void HoldDodgeLanding()
        {
            int remainingVisualTicks = ticksRemaining;
            int visualTotalTicks = totalTicks;
            ClearDodgeMovementFields();
            dodgeMovementProgress = 1f;

            if (remainingVisualTicks > 0)
            {
                visualState = RimKataVisualState.StandardDodge;
                ticksRemaining = remainingVisualTicks;
                totalTicks = Mathf.Max(remainingVisualTicks, visualTotalTicks);
            }
            else if (!AdditionalTumbleActive)
            {
                visualState = RimKataVisualState.None;
                ticksRemaining = 0;
                totalTicks = 0;
            }
        }

        public void HoldStandardDodgeLanding()
        {
            int remainingDodgeTicks = ticksRemaining;
            int dodgeTotalTicks = totalTicks;
            visualState = RimKataVisualState.StandardDodge;
            ClearDodgeMovementFields();
            dodgeMovementProgress = 1f;

            if (remainingDodgeTicks > 0)
            {
                ticksRemaining = remainingDodgeTicks;
                totalTicks = Mathf.Max(remainingDodgeTicks, dodgeTotalTicks);
            }
            else
            {
                ticksRemaining = RimKataCombatTuning.AdditionalDodgeLandingTicks;
                totalTicks = 0;
            }
        }

        public void BeginAdditionalTumble()
        {
            tumbleSign = Rand.Bool ? 1 : -1;
            additionalTumbleTicksRemaining =
                RimKataCombatTuning.AdditionalDodgeTumbleDurationTicks;
            additionalTumbleTotalTicks = additionalTumbleTicksRemaining;
            if (DodgeMovementActive)
            {
                dodgeMovementTumbling = true;
                dodgeTumbleStartProgress = Mathf.Clamp01(
                    dodgeMovementProgress);
            }
        }

        public void ConvertDodgeMovementToCloseDodge()
        {
            int closeDodgeDurationTicks = Mathf.Max(1, totalTicks);
            ClearDodgeMovementCoreFields();
            visualState = RimKataVisualState.None;
            ticksRemaining = 0;
            totalTicks = 0;
            dodgeDirection = IntVec3.Zero;
            BeginCloseDodge(closeDodgeDurationTicks);
        }

        private void ClearDodgeMovementFields()
        {
            ClearDodgeMovementCoreFields();
            dodgeResumeWasMoving = false;
            dodgeResumeDestination = LocalTargetInfo.Invalid;
            dodgeResumePathEndMode = PathEndMode.OnCell;
            dodgeMovementJob = null;
        }

        private void ClearDodgeMovementCoreFields()
        {
            dodgeMovementActive = false;
            dodgeMovementStartedInCloseCombat = false;
            dodgeMovementTumbling = false;
            dodgeMovementOrigin = IntVec3.Invalid;
            dodgeMovementDestination = IntVec3.Invalid;
            dodgeMovementProgress = 0f;
            dodgeTumbleStartProgress = 0f;
            dodgeMovementDirection = IntVec3.Zero;
            dodgeMovementElapsedTicks = 0;
            dodgeStartInProgress = false;
            dodgeFailureStaggerTicks = 0;
            dodgeFailureStaggerSpeedFactor = StaggerHandler.DefaultStaggerMoveSpeedFactor;
        }

        public void CancelDeflection()
        {
            ClearDeflectionSpin();
            EndDeflection();
        }

        private void EndDeflection()
        {
            deflectionTicksRemaining = 0;
            deflectionTotalTicks = 0;
            deflectionSign = 1;
            deflectionWeapon = null;
            RimKataResponseVisualParticipantCache.Refresh(this);
        }

        public void CancelDeflectionSpin()
        {
            ClearDeflectionSpin();
            RimKataResponseVisualParticipantCache.Refresh(this);
        }

        private void ClearDeflectionSpin()
        {
            deflectionSpin = false;
            deflectionSpinStartAngle = 0f;
            deflectionSpinStartProgress = 0f;
            deflectionSpinTicksRemaining = 0;
            deflectionSpinTotalTicks = 0;
            deflectionSpinSign = 1;
        }

        public void CancelResponsePose()
        {
            responsePoseTicksRemaining = 0;
            responsePoseTotalTicks = 0;
            responsePoseMaxAngle = 0f;
            responsePoseSign = 1;
            responsePoseFocus = LocalTargetInfo.Invalid;
            responsePoseWeapon = null;
            responsePoseLookAtFocus = false;
            RimKataResponseVisualParticipantCache.Refresh(this);
        }

        public bool TryGetLiveResponsePoseFocus(
            out LocalTargetInfo focus)
        {
            focus = LocalTargetInfo.Invalid;
            if (!ResponsePoseActive
                || !responsePoseFocus.HasThing
                || pawn?.Map == null)
            {
                return false;
            }

            Thing target = responsePoseFocus.Thing;
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

            focus = responsePoseFocus;
            return true;
        }

        public void BeginCloseDodge(int durationTicks)
        {
            float currentAngle = CurrentCloseDodgeAngle;
            float addedAngle = Rand.Bool ? 10f : -10f;
            closeDodgeStartAngle = Mathf.Clamp(currentAngle + addedAngle, -30f, 30f);
            closeDodgeTicksRemaining = Mathf.Max(1, durationTicks);
            closeDodgeTotalTicks = closeDodgeTicksRemaining;
            RimKataResponseVisualParticipantCache
                .RefreshBodyVisual(this);
        }

        public void CancelCloseDodge()
        {
            closeDodgeTicksRemaining = 0;
            closeDodgeTotalTicks = 0;
            closeDodgeStartAngle = 0f;
            RimKataResponseVisualParticipantCache
                .RefreshBodyVisual(this);
        }

        public void EnterCloseCombat(Thing trigger)
        {
            closeCombatTrigger = trigger;
        }

        public bool TryGetLiveCloseCombatTrigger(out Thing trigger)
        {
            trigger = closeCombatTrigger;
            if (pawn?.Map == null
                || trigger == null
                || trigger.Destroyed
                || !trigger.Spawned
                || trigger.Map != pawn.Map
                || !pawn.CanReachImmediate(trigger, PathEndMode.Touch))
            {
                return false;
            }

            bool playerForcedTarget = TryGetForcedAttackRequestContext(
                trigger,
                out _,
                out bool killIncappedTarget);
            if (!playerForcedTarget
                && !RimKataTargeting.IsAutomaticEnemy(pawn, trigger))
            {
                return false;
            }

            if (trigger is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    playerForcedTarget && killIncappedTarget))
            {
                return false;
            }

            return true;
        }

        public void CancelCloseCombat()
        {
            closeCombatTrigger = null;
        }

        public RimKataVisualSnapshot VisualSnapshot()
        {
            return new RimKataVisualSnapshot
            {
                visualActive = VisualActive,
                visualState = visualState,
                visualProgress = Mathf.Clamp01(VisualProgress),
                visualTotalTicks = totalTicks,
                additionalTumbleActive = AdditionalTumbleActive,
                additionalTumbleProgress = Mathf.Clamp01(
                    AdditionalTumbleProgress),
                additionalTumbleTotalTicks = additionalTumbleTotalTicks,
                dodgeDirection = dodgeDirection,
                dodgeMovementActive = DodgeMovementActive,
                dodgeMovementProgress = Mathf.Clamp01(dodgeMovementProgress),
                dodgeMovementTumbling = dodgeMovementTumbling,
                dodgeTumbleStartProgress = Mathf.Clamp01(dodgeTumbleStartProgress),
                dodgeMovementDirection = dodgeMovementDirection,
                tumbleSign = tumbleSign < 0 ? -1 : 1,
                deflectionActive = DeflectionActive,
                deflectionProgress = Mathf.Clamp01(DeflectionProgress),
                deflectionSign = deflectionSign < 0 ? -1 : 1,
                deflectionWeapon = deflectionWeapon,
                deflectionSpinActive = DeflectionSpinActive,
                deflectionSpinStartAngle = deflectionSpinStartAngle,
                deflectionSpinProgress = Mathf.Clamp01(DeflectionSpinProgress),
                deflectionSpinSign = deflectionSpinSign < 0 ? -1 : 1,
                responsePoseActive = ResponsePoseActive,
                responsePoseProgress = Mathf.Clamp01(ResponsePoseProgress),
                responsePoseMaxAngle = Mathf.Max(0f, responsePoseMaxAngle),
                responsePoseSign = responsePoseSign < 0 ? -1 : 1,
                responsePoseWeapon = responsePoseWeapon,
                responsePoseFocus = responsePoseFocus,
                responsePoseLookAtFocus = responsePoseLookAtFocus,
                closeDodgeActive = CloseDodgeActive,
                closeDodgeAngle = CurrentCloseDodgeAngle,
                groundPoseActive = groundPose?.VisualActive == true,
                groundPoseFallen = groundPose != null && (groundPose.phase == RimKataFallPhase.Falling
                    || groundPose.phase == RimKataFallPhase.Fallen
                    || (groundPose.phase == RimKataFallPhase.Rising && !groundPose.risingFromProne)),
                groundPoseAngle = groundPose?.DrawAngle ?? 0f,
                groundPoseDirection = groundPose?.angle ?? 0f,
                groundPoseProgress = groundPose?.DrawProgress ?? 0f,
                groundPoseOffset = groundPose?.DrawOffset ?? Vector3.zero,
                groundPoseWeaponOffset = groundPose?.DrawWeaponOffset ?? Vector3.zero,
                groundPoseFacing = groundPose?.DrawFacing ?? Rot4.Invalid
            };
        }

        public void UpdateDraftedCooldown()
        {
            int currentTick = CurrentGameTick;
            if (currentTick < 0)
            {
                return;
            }

            if (draftedCooldownTicksRemaining <= 0)
            {
                draftedCooldownTicksRemaining = 0;
                draftedCooldownLastTick = currentTick;
                return;
            }

            if (draftedCooldownLastTick < 0 || currentTick < draftedCooldownLastTick)
            {
                draftedCooldownLastTick = currentTick;
                return;
            }

            int elapsed = currentTick - draftedCooldownLastTick;
            if (elapsed > 0)
            {
                draftedCooldownTicksRemaining = Mathf.Max(0, draftedCooldownTicksRemaining - elapsed);
                draftedCooldownLastTick = currentTick;
            }
        }

        public void CancelDraftedFire(bool clearCooldown = true)
        {
            draftedFireActive = false;
            draftedWarmupTicksRemaining = -1;
            if (clearCooldown)
            {
                draftedCooldownTicksRemaining = 0;
                draftedCooldownLastTick = CurrentGameTick;
            }
            else
            {
                UpdateDraftedCooldown();
            }

            draftedCandidateRetryTicks = 0;
            draftedPlannedTarget = null;
            draftedPlannedInterception = false;
            draftedPlannedCloseAttack = false;
            draftedPlannedCloseContext = false;
        }

        public void ClearDraftedMovementSearchTracking()
        {
            draftedMovementSearchCell = IntVec3.Invalid;
            draftedMovementSearchAllowed = false;
            draftedMovementSearchTriggerPending = false;
            idleProjectileSearchTriggerPending = false;
            movementFireContinuityUntilTick = -1;
            staggerSearchLastCheckTick = -1;
        }

        public void QueueDraftedMovementSearchTrigger()
        {
            draftedMovementSearchTriggerPending = true;
        }

        public void ConsumeDraftedMovementSearchTrigger()
        {
            draftedMovementSearchTriggerPending = false;
        }

        public void QueueIdleProjectileSearchTrigger()
        {
            idleProjectileSearchTriggerPending = true;
        }

        public void ConsumeIdleProjectileSearchTrigger()
        {
            idleProjectileSearchTriggerPending = false;
        }

        public void RefreshMovementFireContinuity()
        {
            int currentTick = CurrentGameTick;
            if (currentTick >= 0)
            {
                movementFireContinuityUntilTick = Mathf.Max(
                    movementFireContinuityUntilTick,
                    currentTick + RimKataCombatTuning.MovingFireContinuityTicks);
            }
        }

        public void CancelWeaponCycles()
        {
            weaponBindingsDirty = true;
            primaryWeaponCycle?.Reset();
            secondaryWeaponCycle?.Reset();
            sharedTargetSearch?.Reset();
            idleProjectileSearchTriggerPending = false;
            dualEngagementActive = false;
            dualLastDrivenTick = -1;
            dualCloseCombatActive = false;
            dualCloseTarget = null;
            engagementOwnerWeapon = null;
            ClearCloseAttackRequest();
            ClearDedicatedFollowupJobRequest();
            dedicatedContinuityTarget = null;
            dedicatedContinuityUntilTick = -1;
            movementFireContinuityUntilTick = -1;
            candidateSaturationExpansionUsed = false;
        }

        public void ResetCandidateSaturationExpansion(bool clearOverrides)
        {
            candidateSaturationExpansionUsed = false;
            if (!clearOverrides)
            {
                return;
            }

            if (primaryWeaponCycle != null)
            {
                primaryWeaponCycle.pendingCandidateLimitOverride = 0;
                primaryWeaponCycle.activeCandidateLimitOverride = 0;
            }
            if (secondaryWeaponCycle != null)
            {
                secondaryWeaponCycle.pendingCandidateLimitOverride = 0;
                secondaryWeaponCycle.activeCandidateLimitOverride = 0;
            }
        }

        public void QueueDedicatedFollowupJob(Thing target, Job sourceJob)
        {
            if (dedicatedFollowupJobStartInProgress)
            {
                return;
            }

            if (!dedicatedFollowupJobPending)
            {
                dedicatedFollowupJobPending = true;
                dedicatedFollowupJobRequestedTick = CurrentGameTick;
                dedicatedFollowupJobTarget = target;
                dedicatedFollowupJobSourceJob = sourceJob;
                dedicatedFollowupJobPlayerForced = sourceJob?.playerForced == true
                    && sourceJob.def == RimKataDefOf.RimKata_Attack;
                dedicatedFollowupJobKillIncappedTarget =
                    sourceJob?.killIncappedTarget == true;
                return;
            }

            if (dedicatedFollowupJobTarget == null && target != null)
            {
                dedicatedFollowupJobTarget = target;
            }
        }

        public void ClearDedicatedFollowupJobRequest()
        {
            dedicatedFollowupJobPending = false;
            dedicatedFollowupJobTarget = null;
            dedicatedFollowupJobSourceJob = null;
            dedicatedFollowupJobPlayerForced = false;
            dedicatedFollowupJobKillIncappedTarget = false;
            dedicatedFollowupJobRequestedTick = -1;
        }

        public void NotifyIncomingThreat(Pawn attacker)
        {
            incomingThreatSource = attacker;
            incomingThreatTicksRemaining = RimKataCombatTuning.CombatRequestGraceTicks;
        }

        public void RequestCloseAttack(
            Thing target,
            bool fromAttackGizmo = false)
        {
            bool preserveAttackGizmoOrigin =
                closeAttackRequestTarget == target
                && closeAttackRequestFromAttackGizmo;
            closeAttackRequestTarget = target;
            closeAttackRequestFromAttackGizmo =
                fromAttackGizmo || preserveAttackGizmoOrigin;
            EnterCloseCombat(target);
        }

        public void RequestPlayerRush(Thing target)
        {
            closeAttackRequestTarget = target;
            closeAttackRequestFromAttackGizmo = true;
        }

        public bool IsPlayerRushRequestFor(Thing target)
        {
            Job job = pawn?.CurJob;
            return pawn?.IsPlayerControlled == true
                && closeAttackRequestFromAttackGizmo
                && closeAttackRequestTarget == target
                && job?.def == RimKataDefOf.RimKata_Attack
                && job.playerForced
                && job.targetA.Thing == target;
        }

        public bool TryGetForcedAttackRequestContext(
            Thing target,
            out bool playerForced,
            out bool killIncappedTarget)
        {
            playerForced = false;
            killIncappedTarget = false;

            Job job = pawn?.CurJob;
            bool attackJob = job?.def == JobDefOf.AttackMelee
                || job?.def == JobDefOf.AttackStatic
                || job?.def == RimKataDefOf.RimKata_Attack;
            if (target == null
                || !attackJob
                || job.playerForced != true
                || job.targetA.Thing != target)
            {
                return false;
            }

            playerForced = true;
            killIncappedTarget = job.killIncappedTarget;
            return true;
        }

        public void ClearCloseAttackRequest()
        {
            closeAttackRequestTarget = null;
            closeAttackRequestFromAttackGizmo = false;
        }

        private bool IsIncomingThreatActive()
        {
            Pawn attacker = incomingThreatSource;
            if (pawn == null
                || attacker == null
                || pawn.Dead
                || !RimKataTargeting.IsPawnTargetStateValid(attacker)
                || !pawn.Spawned
                || !attacker.Spawned
                || pawn.Map != attacker.Map
                || !RimKataTargeting.IsAutomaticEnemy(pawn, attacker))
            {
                return false;
            }

            if (incomingThreatTicksRemaining > 0)
            {
                return true;
            }

            if ((attacker.stances?.curStance as Stance_Busy)?.focusTarg.Pawn == pawn)
            {
                return true;
            }

            Job job = attacker.CurJob;
            bool attackJob = job?.def == JobDefOf.AttackMelee
                || job?.def == JobDefOf.AttackStatic
                || job?.def == RimKataDefOf.RimKata_Attack;

            return attackJob && job.targetA.Thing == pawn;
        }

        private bool IsCloseAttackRequestActive()
        {
            Thing target = closeAttackRequestTarget;
            bool playerRushRequest = IsPlayerRushRequestFor(target);
            bool forcedAttackRequest = TryGetForcedAttackRequestContext(
                target,
                out _,
                out bool killIncappedTarget);

            if (target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    forcedAttackRequest && killIncappedTarget))
            {
                return false;
            }

            return pawn != null
                && target != null
                && !pawn.Dead
                && !target.Destroyed
                && pawn.Spawned
                && target.Spawned
                && pawn.Map == target.Map
                && (playerRushRequest
                    || ((RimKataTargeting.IsAutomaticEnemy(pawn, target)
                            || forcedAttackRequest)
                        && pawn.CanReachImmediate(target, PathEndMode.Touch)));
        }

        public void CancelOffenseForFire()
        {
            dodgeResumeWasMoving = false;
            dodgeResumeDestination = LocalTargetInfo.Invalid;
            dodgeResumePathEndMode = PathEndMode.OnCell;
            CancelDraftedFire();
            CancelWeaponCycles();

            // A completed defense result keeps its remaining visual pose.  Fire
            // blocks new RimKata work, but does not retroactively cancel an
            // already-started parry or dodge presentation.
            if (pawn?.stances?.curStance is Stance_RimKataAim)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }
        }

        private static int CurrentGameTick => Find.TickManager?.TicksGame ?? -1;
    }

    public sealed class RimKataMapComponent : MapComponent
    {
        private sealed class PendingProjectileValidation : IExposable
        {
            public int dueTick;
            public Pawn dodgeTarget;
            public bool avoided;

            public PendingProjectileValidation()
            {
            }

            public void ExposeData()
            {
                Scribe_Values.Look(ref dueTick, "dueTick");
                Scribe_References.Look(ref dodgeTarget, "dodgeTarget");
                Scribe_Values.Look(ref avoided, "avoided");
            }
        }

        private readonly object statesLock = new object();
        private List<RimKataPawnCombatState> states = new List<RimKataPawnCombatState>();
        private readonly List<RimKataPawnCombatState> groundPoseParticipants =
            new List<RimKataPawnCombatState>();
        // A re-prone deadline must not keep an otherwise idle combat state ticking.
        internal Dictionary<Pawn, long> groundPoseResumeTicks = new Dictionary<Pawn, long>();
        private List<Pawn> groundPoseResumeKeys;
        private List<long> groundPoseResumeValues;
        private readonly Dictionary<Pawn, RimKataPawnCombatState> statesByPawn =
            new Dictionary<Pawn, RimKataPawnCombatState>();
        private List<RimKataTrackedRangedProjectile>
            trackedRangedProjectiles =
                new List<RimKataTrackedRangedProjectile>();
        private readonly Dictionary<Projectile, RimKataTrackedRangedProjectile>
            trackedRangedProjectilesByProjectile =
                new Dictionary<Projectile, RimKataTrackedRangedProjectile>();
        private List<RimKataCloseProjectileState> closeProjectiles =
            new List<RimKataCloseProjectileState>();
        private readonly Dictionary<Projectile, RimKataCloseProjectileState>
            closeProjectilesByProjectile =
                new Dictionary<Projectile, RimKataCloseProjectileState>();
        private List<RimKataInterceptionShotLink> interceptionShotLinks =
            new List<RimKataInterceptionShotLink>();
        private readonly Dictionary<Projectile, RimKataInterceptionShotLink>
            interceptionShotLinksByShot =
                new Dictionary<Projectile, RimKataInterceptionShotLink>();
        private readonly Dictionary<Thing, List<RimKataInterceptionShotLink>>
            interceptionShotLinksByTarget =
                new Dictionary<Thing, List<RimKataInterceptionShotLink>>();
        private Dictionary<Projectile, PendingProjectileValidation>
            pendingProjectileValidations =
                new Dictionary<Projectile, PendingProjectileValidation>();
        private List<Projectile> pendingValidationKeys;
        private List<PendingProjectileValidation> pendingValidationValues;
        private readonly List<Projectile> pendingProjectileScratch =
            new List<Projectile>();
        private readonly HashSet<Projectile> activeExplosiveProjectiles =
            new HashSet<Projectile>();
        private readonly Dictionary<Projectile, IntVec3>
            activeExplosiveProjectileCells =
                new Dictionary<Projectile, IntVec3>();
        private readonly List<Pawn> projectileWakeTraversal =
            new List<Pawn>();
        private bool projectileEventsSubscribed;
        private bool projectileInitialRefreshPending;
        private bool projectileWakeTraversalActive;
        private bool projectileWakeDirty;
        private bool projectileSchedulerSuspended;
        private int projectileWakeTraversalIndex;
        private bool weatherRangeCapInitialized;
        private float observedWeatherMaxRangeCap;
        private int weatherRangeRevision;
        private int lastWeatherRangeCheckTick = int.MinValue;

        internal bool HasActiveExplosiveProjectiles =>
            activeExplosiveProjectiles.Count > 0;

        internal void NotifyCEProjectileChanged()
        {
            if (RimKataTargetAccess.AnyExplosiveInterceptionEnabled) RequestProjectileWakeTraversal();
        }

        internal int WeatherRangeRevision
        {
            get
            {
                RefreshWeatherRangeRevision();
                return weatherRangeRevision;
            }
        }

        public RimKataMapComponent(Map map) : base(map)
        {
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RimKataEligibilityCache.InitializeMap(map);
            RefreshWeatherRangeRevision(true);
            lock (statesLock)
            {
                RebuildStateIndex();
                RebuildTrackedRangedProjectileIndex(true);
                RebuildCloseProjectileIndex(true);
                RebuildInterceptionShotLinkIndex(true);
                for (int i = 0; i < states.Count; i++)
                {
                    RimKataPawnCombatState state = states[i];
                    if (state?.pawn != null)
                    {
                        state.temporaryInactive =
                            RimKataTemporaryInactivity.IsInactive(state.pawn);
                        state.temporaryInactivityCleanupPending = state.temporaryInactive;
                    }
                }
            }
            SubscribeProjectileEvents();
            projectileInitialRefreshPending = true;
        }

        public override void MapRemoved()
        {
            RimKataEligibilityCache.ForgetMap(map);
            RimKataDormantHostileMovementRegistry.NotifyMapRemoved(map);
            RimKataGroundPoseUtility.ClearMap(map);
            lock (statesLock)
            {
                for (int i = 0; i < states.Count; i++)
                {
                    RimKataCombatStatePresenceCache.Clear(
                        states[i]?.pawn,
                        map);
                    if (states[i] != null) states[i].ownerComponent = null;
                }
            }
            UnsubscribeProjectileEvents();
            ClearProjectileScheduler();
            trackedRangedProjectiles.Clear();
            trackedRangedProjectilesByProjectile.Clear();
            closeProjectiles.Clear();
            closeProjectilesByProjectile.Clear();
            interceptionShotLinks.Clear();
            interceptionShotLinksByShot.Clear();
            interceptionShotLinksByTarget.Clear();
            RimKataResponseVisualParticipantCache.ClearForMap(map);
            groundPoseResumeTicks.Clear();
            base.MapRemoved();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            lock (statesLock)
            {
                if (Scribe.mode == LoadSaveMode.Saving && groundPoseResumeTicks.Count != 0)
                {
                    // Lazy cleanup at saving, never a per-tick pawn traversal.
                    var expired = new List<Pawn>();
                    foreach (var pair in groundPoseResumeTicks)
                        if (pair.Key.Destroyed || pair.Key.Dead || pair.Key.Map != map
                            || pair.Value <= (long)Find.TickManager.TicksGame) expired.Add(pair.Key);
                    foreach (Pawn pawn in expired) groundPoseResumeTicks.Remove(pawn);
                }
                Scribe_Collections.Look(ref states, "rimKataPawnStates", LookMode.Deep);
                Scribe_Collections.Look(ref groundPoseResumeTicks, "rimKataGroundPoseResumeTicks",
                    LookMode.Reference, LookMode.Value, ref groundPoseResumeKeys, ref groundPoseResumeValues);
                Scribe_Collections.Look(
                    ref trackedRangedProjectiles,
                    "rimKataTrackedRangedProjectiles",
                    LookMode.Deep);
                Scribe_Collections.Look(
                    ref closeProjectiles,
                    "rimKataCloseProjectiles",
                    LookMode.Deep);
                Scribe_Collections.Look(
                    ref interceptionShotLinks,
                    "rimKataInterceptionShotLinks",
                    LookMode.Deep);
                Scribe_Collections.Look(
                    ref pendingProjectileValidations,
                    "rimKataPendingProjectileValidations",
                    LookMode.Reference, LookMode.Deep,
                    ref pendingValidationKeys, ref pendingValidationValues);
                if (Scribe.mode == LoadSaveMode.PostLoadInit && states == null)
                {
                    states = new List<RimKataPawnCombatState>();
                }

                if (Scribe.mode == LoadSaveMode.PostLoadInit
                    && trackedRangedProjectiles == null)
                {
                    trackedRangedProjectiles =
                        new List<RimKataTrackedRangedProjectile>();
                }

                if (Scribe.mode == LoadSaveMode.PostLoadInit
                    && interceptionShotLinks == null)
                {
                    interceptionShotLinks =
                        new List<RimKataInterceptionShotLink>();
                }

                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    groundPoseResumeTicks ??= new Dictionary<Pawn, long>();
                    pendingProjectileValidations ??=
                        new Dictionary<Projectile, PendingProjectileValidation>();
                    weatherRangeCapInitialized = false;
                    observedWeatherMaxRangeCap = 0f;
                    weatherRangeRevision = 0;
                    lastWeatherRangeCheckTick = int.MinValue;
                    RebuildStateIndex();
                    RebuildTrackedRangedProjectileIndex(false);
                    RebuildCloseProjectileIndex(false);
                    RebuildInterceptionShotLinkIndex(false);
                }
            }
            RimKataDormantHostileMovementRegistry.ExposeData(map);
        }

        public override void MapComponentTick()
        {
            TickProjectileScheduler();
            bool actualCombatActive = false;
            lock (statesLock)
            {
                for (int i = states.Count - 1; i >= 0; i--)
                {
                    RimKataPawnCombatState state = states[i];
                    if (state?.pawn == null || state.pawn.Destroyed || state.pawn.Map != map)
                    {
                        RemoveStateAt(i);
                        continue;
                    }

                    if (state.pawn.Dead)
                    {
                        if (state.DodgeMotionBlocksJob)
                        {
                            state.pawn.pather?.StopDead();
                        }

                        state.CancelVisual();
                        state.CancelDeflection();
                        state.CancelResponsePose();
                        state.CancelCloseDodge();
                        state.CancelCloseCombat();
                        state.CancelDraftedFire();
                        state.ClearDraftedMovementSearchTracking();
                        state.CancelWeaponCycles();
                    }
                    else if (state.pawn.Downed || !state.pawn.Awake())
                    {
                        if (state.DodgeMotionBlocksJob)
                        {
                            state.pawn.pather?.StopDead();
                        }

                        state.CancelVisual();
                        state.CancelDeflection();
                        state.CancelResponsePose();
                        state.CancelCloseDodge();
                        state.CancelCloseCombat();
                        state.CancelDraftedFire(false);
                        state.ClearDraftedMovementSearchTracking();
                        state.CancelWeaponCycles();
                    }
                    else if (state.temporaryInactivityCleanupPending)
                    {
                        state.temporaryInactivityCleanupPending = false;
                        RimKataDualWeaponController
                            .CancelOffenseForMentalState(state.pawn, state);
                    }

                    if (!state.pawn.Dead
                        && !state.pawn.Downed
                        && state.pawn.Awake()
                        && state.pawn.IsBurning())
                    {
                        state.CancelOffenseForFire();
                    }

                    RimKataDualWeaponController.TickIdleCycleTimers(state.pawn);
                    state.Tick();
                    if (state.incomingThreatTicksRemaining > 0)
                    {
                        state.incomingThreatTicksRemaining--;
                    }
                    if (state.weaponSwapPending)
                    {
                        ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(state.pawn);

                        ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(state.pawn);

                        if (primary == null || secondary == null)
                        {
                            state.weaponSwapPending = false;
                        }
                        else if (state.primaryWeaponCycle?.Active != true
                            && state.secondaryWeaponCycle?.Active != true)
                        {
                            if (RimKataWeaponSlotUtility.TrySwapPrimarySecondary(state.pawn))
                            {
                                state.weaponSwapPending = false;
                            }
                        }
                    }

                    if (state.DodgeMovementWatchdogExpired)
                    {
                        TryFinishDodgeMovement(state.pawn, true, true);
                    }

                    if (!state.Active)
                    {
                        RemoveStateAt(i);
                    }
                    else if (!actualCombatActive)
                    {
                        actualCombatActive = RimKataDualWeaponController.IsActualCombatActive(
                            state.pawn, state.pawn.CurJobDef, state);
                    }
                }
                // Only event-registered poses are visited. Ordinary combat states
                // never enter the pose scheduler, even to test an active flag.
                for (int i = groundPoseParticipants.Count - 1; i >= 0; i--)
                    RimKataGroundPoseUtility.Tick(groundPoseParticipants[i]);
            }
            RimKataDormantHostileMovementRegistry.ProcessPending(map, actualCombatActive);
        }

        internal void RegisterGroundPose(RimKataPawnCombatState state)
        {
            if (!groundPoseParticipants.Contains(state)) groundPoseParticipants.Add(state);
        }

        internal void UnregisterGroundPose(RimKataPawnCombatState state)
            => groundPoseParticipants.Remove(state);

        internal void RegisterCloseProjectile(RimKataCloseProjectileState shot)
        {
            if (shot?.projectile == null
                || shot.target == null
                || shot.projectile.Destroyed
                || shot.projectile.Map != map)
            {
                return;
            }

            lock (statesLock)
            {
                if (closeProjectilesByProjectile.ContainsKey(shot.projectile))
                {
                    return;
                }

                closeProjectiles.Add(shot);
                closeProjectilesByProjectile.Add(shot.projectile, shot);
            }
        }

        internal RimKataCloseProjectileState CloseShotFor(Projectile projectile)
        {
            if (projectile == null)
            {
                return null;
            }

            lock (statesLock)
            {
                closeProjectilesByProjectile.TryGetValue(projectile, out RimKataCloseProjectileState shot);
                return shot;
            }
        }

        private void RebuildCloseProjectileIndex(bool pruneInvalid)
        {
            closeProjectilesByProjectile.Clear();
            closeProjectiles ??= new List<RimKataCloseProjectileState>();
            for (int i = closeProjectiles.Count - 1; i >= 0; i--)
            {
                RimKataCloseProjectileState shot = closeProjectiles[i];
                if (shot?.projectile == null
                    || shot.target == null
                    || (pruneInvalid && (shot.projectile.Destroyed
                        || !shot.projectile.Spawned
                        || shot.projectile.Map != map))
                    || closeProjectilesByProjectile.ContainsKey(shot.projectile))
                {
                    closeProjectiles.RemoveAt(i);
                    continue;
                }

                closeProjectilesByProjectile.Add(shot.projectile, shot);
            }
        }

        internal void RegisterLaunchedRangedProjectile(
            Projectile projectile,
            Pawn target,
            bool preventDirectHit = false)
        {
            if (projectile == null
                || target == null
                || map == null
                || map.Disposed
                || projectile.Map != map
                || target.Map != map)
            {
                return;
            }

            lock (statesLock)
            {
                if (trackedRangedProjectilesByProjectile.TryGetValue(
                        projectile,
                        out RimKataTrackedRangedProjectile existing))
                {
                    existing.target = target;
                    existing.preventDirectHit |= preventDirectHit;
                    return;
                }

                RimKataTrackedRangedProjectile tracked =
                    new RimKataTrackedRangedProjectile(projectile, target)
                    {
                        preventDirectHit = preventDirectHit
                    };
                trackedRangedProjectiles.Add(tracked);
                trackedRangedProjectilesByProjectile[projectile] = tracked;
            }
        }

        internal bool PreventsDirectProjectileHit(Projectile projectile, Pawn target)
        {
            lock (statesLock)
            {
                return trackedRangedProjectilesByProjectile.TryGetValue(projectile, out var tracked)
                    && tracked.preventDirectHit && tracked.target == target;
            }
        }

        internal bool MarkCurrentRangedProjectilesAvoided(Pawn target)
        {
            if (target == null)
            {
                return false;
            }

            bool marked = false;
            bool suppressJobNotification =
                RimKataEligibility.IsWorkMovementDefenseException(target);
            lock (statesLock)
            {
                Projectile current =
                    RimKataProjectileImpactContext.CurrentProjectile;
                // Additional dodge covers rounds already in this queue, not later launches.
                foreach (var pair in pendingProjectileValidations)
                {
                    if (pair.Value?.dodgeTarget == target
                        && pair.Key.Spawned && !pair.Key.Destroyed && pair.Key.Map == map)
                    {
                        pair.Value.avoided = true;
                        marked = true;
                    }
                }
                for (int i = trackedRangedProjectiles.Count - 1;
                    i >= 0;
                    i--)
                {
                    RimKataTrackedRangedProjectile tracked =
                        trackedRangedProjectiles[i];
                    Projectile projectile = tracked?.projectile;
                    bool live = projectile != null
                        && (projectile == current
                            || (!projectile.Destroyed
                                && projectile.Spawned
                                && projectile.Map == map));
                    if (!live
                        || tracked.target == null
                        || tracked.target.Destroyed
                        || tracked.target.Map != map)
                    {
                        RemoveTrackedRangedProjectileAt(i);
                        continue;
                    }

                    if (tracked.target == target)
                    {
                        if (!tracked.avoided)
                        {
                            tracked.avoided = true;
                            tracked.suppressJobNotification =
                                suppressJobNotification;
                        }

                        marked = true;
                    }
                }
            }

            return marked;
        }

        internal bool WasRangedProjectileAvoided(Projectile projectile, Pawn target)
        {
            if (projectile == null || target == null) return false;
            lock (statesLock)
            {
                return trackedRangedProjectilesByProjectile.TryGetValue(projectile, out var tracked)
                    && tracked.target == target && tracked.avoided;
            }
        }

        internal bool TryConsumeAvoidedRangedProjectile(
            Projectile projectile,
            Pawn target,
            out bool suppressJobNotification)
        {
            suppressJobNotification = false;
            if (projectile == null || target == null)
            {
                return false;
            }

            lock (statesLock)
            {
                if (!trackedRangedProjectilesByProjectile.TryGetValue(
                        projectile,
                        out RimKataTrackedRangedProjectile tracked)
                    || tracked.target != target
                    || !tracked.avoided)
                {
                    return false;
                }

                suppressJobNotification = tracked.suppressJobNotification;
                RemoveTrackedRangedProjectile(tracked);
                return true;
            }
        }

        internal void NotifyRangedProjectileImpactFinished(
            Projectile projectile)
        {
            if (projectile == null)
            {
                return;
            }

            lock (statesLock)
            {
                if (closeProjectilesByProjectile.TryGetValue(
                        projectile,
                        out RimKataCloseProjectileState closeShot))
                {
                    closeProjectilesByProjectile.Remove(projectile);
                    closeProjectiles.Remove(closeShot);
                }

                if (trackedRangedProjectilesByProjectile.TryGetValue(
                        projectile,
                        out RimKataTrackedRangedProjectile tracked))
                {
                    // A successful early dodge deferred Impact and resumed flight.
                    if (tracked.preventDirectHit && projectile.Spawned && !projectile.Destroyed)
                    {
                        return;
                    }
                    RemoveTrackedRangedProjectile(tracked);
                }
            }
        }

        private void RebuildTrackedRangedProjectileIndex(bool pruneInvalid)
        {
            trackedRangedProjectilesByProjectile.Clear();
            if (trackedRangedProjectiles == null)
            {
                trackedRangedProjectiles =
                    new List<RimKataTrackedRangedProjectile>();
                return;
            }

            for (int i = trackedRangedProjectiles.Count - 1;
                i >= 0;
                i--)
            {
                RimKataTrackedRangedProjectile tracked =
                    trackedRangedProjectiles[i];
                Projectile projectile = tracked?.projectile;
                Pawn target = tracked?.target;
                if (projectile == null
                    || target == null
                    || (pruneInvalid
                        && (projectile.Destroyed
                            || !projectile.Spawned
                            || projectile.Map != map
                            || target.Destroyed
                            || target.Map != map))
                    || trackedRangedProjectilesByProjectile.ContainsKey(
                        projectile))
                {
                    trackedRangedProjectiles.RemoveAt(i);
                    continue;
                }

                trackedRangedProjectilesByProjectile[projectile] = tracked;
            }
        }

        private void RemoveTrackedRangedProjectile(
            RimKataTrackedRangedProjectile tracked)
        {
            if (tracked == null)
            {
                return;
            }

            trackedRangedProjectilesByProjectile.Remove(tracked.projectile);
            trackedRangedProjectiles.Remove(tracked);
        }

        private void RemoveTrackedRangedProjectileAt(int index)
        {
            RimKataTrackedRangedProjectile tracked =
                trackedRangedProjectiles[index];
            trackedRangedProjectiles.RemoveAt(index);
            if (tracked?.projectile != null)
            {
                trackedRangedProjectilesByProjectile.Remove(
                    tracked.projectile);
            }
        }

        internal void RegisterInterceptionShot(
            Projectile shot,
            Thing target)
        {
            if (shot == null
                || target == null
                || shot.Destroyed
                || target.Destroyed
                || !shot.Spawned
                || !target.Spawned
                || shot.Map != map
                || target.Map != map)
            {
                return;
            }

            lock (statesLock)
            {
                if (interceptionShotLinksByShot.TryGetValue(
                        shot,
                        out RimKataInterceptionShotLink existing))
                {
                    RemoveInterceptionShotLink(existing);
                }

                RimKataInterceptionShotLink link =
                    new RimKataInterceptionShotLink(shot, target);
                interceptionShotLinks.Add(link);
                interceptionShotLinksByShot[shot] = link;
                AddInterceptionTargetIndex(link);
            }
        }

        internal bool TryTakeInterceptionTarget(
            Projectile shot,
            out Thing target)
        {
            target = null;
            if (shot == null)
            {
                return false;
            }

            lock (statesLock)
            {
                if (!interceptionShotLinksByShot.TryGetValue(
                        shot,
                        out RimKataInterceptionShotLink link))
                {
                    return false;
                }

                target = link?.target;
                RemoveInterceptionShotLink(link);
                return target != null;
            }
        }

        private void RebuildInterceptionShotLinkIndex(bool pruneInvalid)
        {
            interceptionShotLinksByShot.Clear();
            interceptionShotLinksByTarget.Clear();
            if (interceptionShotLinks == null)
            {
                interceptionShotLinks =
                    new List<RimKataInterceptionShotLink>();
                return;
            }

            for (int i = interceptionShotLinks.Count - 1;
                i >= 0;
                i--)
            {
                RimKataInterceptionShotLink link =
                    interceptionShotLinks[i];
                Projectile shot = link?.shot;
                Thing target = link?.target;
                if (shot == null
                    || target == null
                    || (pruneInvalid
                        && (shot.Destroyed
                            || target.Destroyed
                            || !shot.Spawned
                            || !target.Spawned
                            || shot.Map != map
                            || target.Map != map))
                    || interceptionShotLinksByShot.ContainsKey(shot))
                {
                    interceptionShotLinks.RemoveAt(i);
                    continue;
                }

                interceptionShotLinksByShot[shot] = link;
                AddInterceptionTargetIndex(link);
            }
        }

        private void RemoveInterceptionShotLink(Projectile shot)
        {
            if (shot == null
                || !interceptionShotLinksByShot.TryGetValue(
                    shot,
                    out RimKataInterceptionShotLink link))
            {
                return;
            }

            RemoveInterceptionShotLink(link);
        }

        private void RemoveInterceptionShotLink(
            RimKataInterceptionShotLink link)
        {
            if (link == null)
            {
                return;
            }

            interceptionShotLinksByShot.Remove(link.shot);
            interceptionShotLinks.Remove(link);
            Thing target = link.target;
            if (target != null
                && interceptionShotLinksByTarget.TryGetValue(
                    target,
                    out List<RimKataInterceptionShotLink> targetLinks))
            {
                targetLinks.Remove(link);
                if (targetLinks.Count == 0)
                {
                    interceptionShotLinksByTarget.Remove(target);
                }
            }
        }

        private void RemoveInterceptionLinksForTarget(Thing target)
        {
            if (target == null
                || !interceptionShotLinksByTarget.TryGetValue(
                    target,
                    out List<RimKataInterceptionShotLink> targetLinks))
            {
                return;
            }

            while (targetLinks.Count > 0)
            {
                RemoveInterceptionShotLink(
                    targetLinks[targetLinks.Count - 1]);
            }
        }

        private void AddInterceptionTargetIndex(
            RimKataInterceptionShotLink link)
        {
            Thing target = link?.target;
            if (target == null)
            {
                return;
            }

            if (!interceptionShotLinksByTarget.TryGetValue(
                    target,
                    out List<RimKataInterceptionShotLink> targetLinks))
            {
                targetLinks = new List<RimKataInterceptionShotLink>();
                interceptionShotLinksByTarget[target] = targetLinks;
            }

            targetLinks.Add(link);
        }

        private void SubscribeProjectileEvents()
        {
            if (projectileEventsSubscribed || map?.events == null)
            {
                return;
            }

            map.events.ThingSpawned += NotifyThingSpawned;
            map.events.ThingDespawned += NotifyThingDespawned;
            projectileEventsSubscribed = true;
        }

        private void UnsubscribeProjectileEvents()
        {
            if (!projectileEventsSubscribed || map?.events == null)
            {
                return;
            }

            map.events.ThingSpawned -= NotifyThingSpawned;
            map.events.ThingDespawned -= NotifyThingDespawned;
            projectileEventsSubscribed = false;
        }

        private void NotifyThingSpawned(Thing thing)
        {
            Projectile projectile = thing as Projectile;
            if (!RimKataTargetAccess.AnyExplosiveInterceptionEnabled
                || projectile == null
                || projectile.def?.projectile?.explosionRadius <= 0f
                || pendingProjectileValidations.ContainsKey(projectile)
                || activeExplosiveProjectiles.Contains(projectile))
            {
                return;
            }

            pendingProjectileValidations[projectile] =
                new PendingProjectileValidation
                {
                    dueTick = (Find.TickManager?.TicksGame ?? 0) + 1
                };
        }

        private void NotifyThingDespawned(Thing thing)
        {
            Projectile projectile = thing as Projectile;
            if (projectile == null)
            {
                return;
            }

            lock (statesLock)
            {
                RemoveInterceptionShotLink(projectile);
                RemoveInterceptionLinksForTarget(projectile);
            }

            pendingProjectileValidations.Remove(projectile);
            activeExplosiveProjectiles.Remove(projectile);
            activeExplosiveProjectileCells.Remove(projectile);
            if (RimKataProjectileImpactContext.CurrentProjectile != projectile)
            {
                NotifyRangedProjectileImpactFinished(projectile);
            }
        }

        internal void RegisterLaunchedExplosiveProjectile(
            Projectile projectile)
        {
            if (!RimKataTargetAccess.AnyExplosiveInterceptionEnabled
                || map == null
                || map.Disposed
                || projectile?.Launcher == null
                || !RimKataTargeting.IsPotentialExplosiveProjectile(
                    projectile,
                    map))
            {
                return;
            }

            pendingProjectileValidations.Remove(projectile);
            if (activeExplosiveProjectiles.Add(projectile))
            {
                activeExplosiveProjectileCells[projectile] =
                    projectile.Position;
                RequestProjectileWakeTraversal();
            }
        }

        internal void RegisterExplosiveDodge(Projectile projectile)
        {
            Pawn defender = RimKataDefenseUtility.ExplosiveDodgeTarget(projectile);
            if (defender == null || map == null || map.Disposed || projectile.Map != map)
            {
                return;
            }

            pendingProjectileValidations[projectile] = new PendingProjectileValidation
            {
                dueTick = (Find.TickManager?.TicksGame ?? 0) + 1,
                dodgeTarget = defender
            };
        }

        internal bool DeferImpactForExplosiveDodge(
            Projectile projectile, Thing hitThing, bool blockedByShield)
        {
            if (!pendingProjectileValidations.TryGetValue(projectile, out var pending)
                || pending?.dodgeTarget == null)
            {
                return false;
            }

            // An earlier wall, shield, interception, or bystander collision
            // belongs to the original impact; never turn it into a new flight.
            if (blockedByShield
                || (hitThing != pending.dodgeTarget
                    && (hitThing != null || !RimKataProjectileMissUtility.IsAtDestination(projectile))))
            {
                pendingProjectileValidations.Remove(projectile);
                return false;
            }

            // Short flights can impact before the map's next tick. Keep the same
            // projectile alive until the scheduled roll, without absorbing damage.
            if ((Find.TickManager?.TicksGame ?? 0) < pending.dueTick)
            {
                return true;
            }

            pendingProjectileValidations.Remove(projectile);
            return RimKataDefenseUtility.TryDodgeExplosiveProjectile(
                projectile, pending.dodgeTarget, pending.avoided);
        }

        private void TickProjectileScheduler()
        {
            if (map == null || map.Disposed)
            {
                return;
            }

            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (currentTick < 0)
            {
                return;
            }

            // Dodge validation is independent of the interception feature toggle.
            ValidatePendingProjectiles(currentTick);
            if (!RimKataTargetAccess.AnyExplosiveInterceptionEnabled)
            {
                if (!projectileSchedulerSuspended)
                {
                    ClearProjectileScheduler(clearPending: false);
                    projectileSchedulerSuspended = true;
                }

                return;
            }

            if (projectileSchedulerSuspended)
            {
                projectileSchedulerSuspended = false;
                projectileInitialRefreshPending = true;
            }

            if (projectileInitialRefreshPending)
            {
                projectileInitialRefreshPending = false;
                RefreshActiveExplosiveProjectiles();
                if (activeExplosiveProjectiles.Count > 0)
                {
                    RequestProjectileWakeTraversal();
                }
            }

            DetectExplosiveProjectileCellChanges();

            if (!projectileWakeTraversalActive)
            {
                return;
            }

            if (projectileWakeTraversalIndex < projectileWakeTraversal.Count)
            {
                Pawn pawn = projectileWakeTraversal[
                    projectileWakeTraversalIndex++];
                RimKataDualWeaponController.QueueIdleProjectileSearch(pawn);
            }

            if (projectileWakeTraversalIndex
                < projectileWakeTraversal.Count)
            {
                return;
            }

            projectileWakeTraversalActive = false;
            projectileWakeTraversal.Clear();
            projectileWakeTraversalIndex = 0;
            if (projectileWakeDirty)
            {
                projectileWakeDirty = false;
                StartProjectileWakeTraversal();
            }
        }

        private void ValidatePendingProjectiles(int currentTick)
        {
            if (pendingProjectileValidations.Count == 0)
            {
                return;
            }

            pendingProjectileScratch.Clear();
            foreach (KeyValuePair<Projectile, PendingProjectileValidation>
                pair in pendingProjectileValidations)
            {
                if (pair.Value == null || pair.Value.dueTick <= currentTick)
                {
                    pendingProjectileScratch.Add(pair.Key);
                }
            }

            bool addedProjectile = false;
            for (int i = 0; i < pendingProjectileScratch.Count; i++)
            {
                Projectile projectile = pendingProjectileScratch[i];
                if (!pendingProjectileValidations.TryGetValue(projectile, out var pending))
                {
                    continue;
                }

                pendingProjectileValidations.Remove(projectile);
                if (pending?.dodgeTarget != null)
                {
                    RimKataDefenseUtility.TryDodgeExplosiveProjectile(
                        projectile, pending.dodgeTarget, pending.avoided);
                }

                if (!RimKataTargetAccess.AnyExplosiveInterceptionEnabled
                    || !RimKataTargeting.IsPotentialExplosiveProjectile(
                    projectile,
                    map))
                {
                    continue;
                }

                if (projectile.Launcher != null
                    && activeExplosiveProjectiles.Add(projectile))
                {
                    activeExplosiveProjectileCells[projectile] =
                        projectile.Position;
                    addedProjectile = true;
                }
            }

            if (addedProjectile)
            {
                RequestProjectileWakeTraversal();
            }
        }

        private void RefreshActiveExplosiveProjectiles()
        {
            activeExplosiveProjectiles.Clear();
            activeExplosiveProjectileCells.Clear();
            List<Thing> projectiles = map.listerThings.ThingsInGroup(
                ThingRequestGroup.Projectile);
            for (int i = 0; i < projectiles.Count; i++)
            {
                Projectile projectile = projectiles[i] as Projectile;
                if (projectile?.Launcher != null
                    && RimKataTargeting.IsPotentialExplosiveProjectile(
                        projectile,
                        map))
                {
                    activeExplosiveProjectiles.Add(projectile);
                    activeExplosiveProjectileCells[projectile] =
                        projectile.Position;
                }
            }
        }

        private void DetectExplosiveProjectileCellChanges()
        {
            if (activeExplosiveProjectiles.Count == 0)
            {
                return;
            }

            bool changed = false;
            foreach (Projectile projectile in activeExplosiveProjectiles)
            {
                if (!RimKataTargeting.IsPotentialExplosiveProjectile(
                        projectile,
                        map))
                {
                    continue;
                }

                IntVec3 position = projectile.Position;
                if (!activeExplosiveProjectileCells.TryGetValue(
                        projectile,
                        out IntVec3 previous)
                    || previous != position)
                {
                    activeExplosiveProjectileCells[projectile] = position;
                    changed = true;
                }
            }

            if (changed)
            {
                RequestProjectileWakeTraversal();
            }
        }

        private void RequestProjectileWakeTraversal()
        {
            if (projectileWakeTraversalActive)
            {
                projectileWakeDirty = true;
                return;
            }

            StartProjectileWakeTraversal();
        }

        private void StartProjectileWakeTraversal()
        {
            projectileWakeTraversal.Clear();
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (CanPotentiallyWakeForProjectile(pawn))
                {
                    projectileWakeTraversal.Add(pawn);
                }
            }

            projectileWakeTraversalIndex = 0;
            projectileWakeTraversalActive =
                projectileWakeTraversal.Count > 0;
        }

        private bool CanPotentiallyWakeForProjectile(Pawn pawn)
        {
            if (pawn?.Map != map
                || !RimKataDualWeaponController
                    .CanReceiveIdleProjectileWakeNow(pawn))
            {
                return false;
            }

            ThingWithComps primary =
                RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = null;
            if (RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    pawn,
                    primary,
                    true))
            {
                secondary = RimKataWeaponSlotUtility
                    .SecondaryWeaponWithVerifiedAccess(pawn);
            }

            Verb primaryVerb =
                RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            Verb secondaryVerb =
                RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            if (primaryVerb?.Bursting == true
                || secondaryVerb?.Bursting == true)
            {
                return false;
            }

            float maximumRange = Mathf.Max(
                PotentialProjectileWakeRange(pawn, primary, primaryVerb),
                PotentialProjectileWakeRange(pawn, secondary, secondaryVerb));
            if (maximumRange <= 0f)
            {
                return false;
            }

            float rangeSquared = maximumRange * maximumRange;
            return HasHostileProjectileInRange(pawn, rangeSquared);
        }

        internal bool HasHostileProjectileInRange(Pawn pawn, float rangeSquared)
        {
            foreach (Projectile projectile in activeExplosiveProjectiles)
            {
                if (RimKataTargeting.IsPotentialExplosiveProjectile(
                        projectile,
                        map)
                    && RimKataTargeting.IsEnemyProjectileLauncher(
                        pawn,
                        projectile)
                    && pawn.Position.DistanceToSquared(projectile.Position)
                        <= rangeSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private static float PotentialProjectileWakeRange(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb)
        {
            return RimKataDualWeaponController.ProjectileWakeRange(
                pawn,
                weapon,
                verb);
        }

        internal bool TryGetValidHostileProjectile(
            Pawn pawn,
            Verb verb,
            float rangeSquared,
            out Thing candidate)
        {
            candidate = null;
            if (pawn?.Map != map
                || verb == null
                || verb.IsMeleeAttack
                || rangeSquared <= 0f
                || RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled == false)
            {
                return false;
            }

            foreach (Projectile projectile in activeExplosiveProjectiles)
            {
                if (RimKataTargeting.IsValidExplosiveProjectileForVerb(
                        pawn,
                        verb,
                        projectile,
                        rangeSquared)
                    && RimKataInterceptionTrajectory.CanIntercept(
                        pawn,
                        verb,
                        projectile,
                        0,
                        rangeSquared))
                {
                    candidate = projectile;
                    return true;
                }
            }

            return false;
        }

        internal void AppendValidHostileProjectiles(
            Pawn pawn,
            Verb verb,
            float rangeSquared,
            List<Thing> destination)
        {
            if (pawn?.Map != map
                || verb == null
                || verb.IsMeleeAttack
                || rangeSquared <= 0f
                || destination == null
                || RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled == false)
            {
                return;
            }

            foreach (Projectile projectile in activeExplosiveProjectiles)
            {
                if (RimKataTargeting.IsValidExplosiveProjectileForVerb(
                        pawn,
                        verb,
                        projectile,
                        rangeSquared)
                    && !destination.Contains(projectile)
                    && RimKataInterceptionTrajectory.CanIntercept(
                        pawn, verb, projectile, 0, rangeSquared))
                {
                    destination.Add(projectile);
                }
            }
        }

        private void ClearProjectileScheduler(bool clearPending = true)
        {
            if (clearPending) pendingProjectileValidations.Clear();
            pendingProjectileScratch.Clear();
            activeExplosiveProjectiles.Clear();
            activeExplosiveProjectileCells.Clear();
            projectileWakeTraversal.Clear();
            projectileInitialRefreshPending = false;
            projectileWakeTraversalActive = false;
            projectileWakeDirty = false;
            projectileWakeTraversalIndex = 0;
        }

        internal void RefreshWeatherRangeRevision(bool force = false)
        {
            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (!force && lastWeatherRangeCheckTick == currentTick)
            {
                return;
            }

            lastWeatherRangeCheckTick = currentTick;
            float currentCap =
                map?.weatherManager?.CurWeatherMaxRangeCap ?? float.MaxValue;
            if (!weatherRangeCapInitialized)
            {
                observedWeatherMaxRangeCap = currentCap;
                weatherRangeCapInitialized = true;
                return;
            }

            if (observedWeatherMaxRangeCap.Equals(currentCap))
            {
                return;
            }

            observedWeatherMaxRangeCap = currentCap;
            unchecked
            {
                weatherRangeRevision++;
            }

            if (weatherRangeRevision == 0)
            {
                weatherRangeRevision = 1;
            }
        }

        public void ScheduleDraftedMeleeThreatClear(Pawn pawn)
        {
            if (pawn?.Map != map)
            {
                return;
            }

            GetState(pawn, true)?.ScheduleDraftedMeleeThreatClear();
        }

        internal void RequestTemporaryInactivityUpdate(Pawn pawn, bool inactive)
        {
            if (pawn?.Map != map)
            {
                return;
            }

            lock (statesLock)
            {
                if (!statesByPawn.TryGetValue(pawn, out RimKataPawnCombatState state))
                {
                    return;
                }

                if (inactive && !state.temporaryInactive)
                {
                    state.temporaryInactivityCleanupPending = true;
                }
                state.temporaryInactive = inactive;
            }
        }

        public RimKataPawnCombatState GetState(Pawn pawn, bool createIfMissing)
        {
            lock (statesLock)
            {
                if (pawn == null)
                {
                    return null;
                }

                if (statesByPawn.TryGetValue(
                        pawn,
                        out RimKataPawnCombatState existing))
                {
                    return existing;
                }

                if (!createIfMissing)
                {
                    return null;
                }

                RimKataPawnCombatState state = new RimKataPawnCombatState(pawn);
                state.ownerComponent = this;
                RimKataCombatStatePresenceCache.Mark(pawn, this);
                states.Add(state);
                statesByPawn[pawn] = state;
                state.temporaryInactive = RimKataTemporaryInactivity.IsInactive(pawn);
                state.temporaryInactivityCleanupPending = state.temporaryInactive;
                return state;
            }
        }

        internal bool IsDualEngagementActive(Pawn pawn)
        {
            if (pawn?.Map != map)
            {
                return false;
            }

            lock (statesLock)
            {
                return statesByPawn.TryGetValue(
                        pawn,
                        out RimKataPawnCombatState state)
                    && state.dualEngagementActive;
            }
        }

        private void RebuildStateIndex()
        {
            RimKataResponseVisualParticipantCache.ClearForMap(map);
            RimKataGroundPoseUtility.ClearMap(map);
            foreach (Pawn indexedPawn in statesByPawn.Keys)
            {
                RimKataCombatStatePresenceCache.Clear(indexedPawn, map);
            }
            statesByPawn.Clear();
            if (states == null)
            {
                return;
            }

            for (int i = 0; i < states.Count; i++)
            {
                RimKataPawnCombatState state = states[i];
                if (state?.pawn != null)
                {
                    state.ownerComponent = this;
                    RimKataCombatStatePresenceCache.Mark(state.pawn, this);
                    statesByPawn[state.pawn] = state;
                    RimKataGroundPoseUtility.Rebuild(state);
                    RimKataResponseVisualParticipantCache.Refresh(state);
                    RimKataResponseVisualParticipantCache
                        .RefreshBodyVisual(state);
                }
            }
        }

        private void RemoveStateAt(int index)
        {
            RimKataPawnCombatState state = states[index];
            RimKataGroundPoseUtility.Clear(state);
            if (state != null) state.ownerComponent = null;
            states.RemoveAt(index);
            RimKataResponseVisualParticipantCache.Clear(state?.pawn);
            if (state?.pawn != null
                && statesByPawn.TryGetValue(
                    state.pawn,
                    out RimKataPawnCombatState indexed)
                && indexed == state)
            {
                statesByPawn.Remove(state.pawn);
            }
            if (state?.pawn != null
                && !statesByPawn.ContainsKey(state.pawn))
            {
                RimKataCombatStatePresenceCache.Clear(state.pawn, map);
            }
        }

        public void BeginRangedDodgeWindow(Pawn pawn, int durationTicks)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                state.rangedDodgeDelayTicksRemaining = Mathf.Max(
                    1,
                    durationTicks);
                state.additionalDodgeUsed = false;
            }
        }

        public bool BeginOrRestartStandardDodgeVisual(
            Pawn pawn,
            int durationTicks,
            IntVec3 dodgeDirection)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                state.visualState = RimKataVisualState.StandardDodge;
                state.ticksRemaining = Mathf.Max(1, durationTicks);
                state.totalTicks = state.ticksRemaining;
                state.dodgeDirection = dodgeDirection;
                RimKataResponseVisualParticipantCache
                    .RefreshBodyVisual(state);
                return true;
            }
        }

        public bool BeginDodgeMovement(
            Pawn pawn,
            IntVec3 destination,
            IntVec3 combatDirection,
            bool resumeWasMoving,
            LocalTargetInfo resumeDestination,
            PathEndMode resumePathEndMode,
            Job movementJob,
            int failureStaggerTicks,
            float failureStaggerSpeedFactor,
            int dodgeWindowDurationTicks)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                if (state.DodgeMovementStartBlocked)
                {
                    return false;
                }

                state.visualState = RimKataVisualState.StandardDodge;
                state.ticksRemaining = Mathf.Max(1, dodgeWindowDurationTicks);
                state.totalTicks = state.ticksRemaining;
                state.dodgeDirection = combatDirection;
                state.dodgeMovementActive = true;
                state.dodgeMovementStartedInCloseCombat = state.CloseCombatActive;
                state.dodgeMovementTumbling = false;
                state.dodgeMovementOrigin = pawn.Position;
                state.dodgeMovementDestination = destination;
                state.dodgeMovementProgress = 0f;
                state.dodgeTumbleStartProgress = 0f;
                state.dodgeMovementDirection = destination - pawn.Position;
                state.dodgeMovementElapsedTicks = 0;
                state.dodgeStartInProgress = true;
                state.dodgeResumeWasMoving = resumeWasMoving;
                state.dodgeResumeDestination = resumeDestination;
                state.dodgeResumePathEndMode = resumePathEndMode;
                state.dodgeMovementJob = movementJob;
                state.dodgeFailureStaggerTicks = Mathf.Max(0, failureStaggerTicks);
                state.dodgeFailureStaggerSpeedFactor = Mathf.Max(0f, failureStaggerSpeedFactor);
                if (!state.AdditionalTumbleActive)
                {
                    state.tumbleSign = Rand.Bool ? 1 : -1;
                }
                RimKataResponseVisualParticipantCache
                    .RefreshBodyVisual(state);
            }

            try
            {
                pawn.pather.StartPath(destination, PathEndMode.OnCell);
            }
            finally
            {
                lock (statesLock)
                {
                    RimKataPawnCombatState state = GetState(pawn, false);
                    if (state != null)
                    {
                        state.dodgeStartInProgress = false;
                    }
                }
            }

            return IsDodgeMovementActive(pawn);
        }

        public bool TryFinishDodgeMovement(Pawn pawn, bool failed, bool force = false)
        {
            bool resumePath;
            LocalTargetInfo resumeDestination;
            PathEndMode resumePathEndMode;
            bool applyFailureStagger;
            int failureStaggerTicks;
            float failureStaggerSpeedFactor;
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, false);
                if (state?.DodgeMovementActive != true)
                {
                    return false;
                }

                bool sameJob = state.dodgeMovementJob == pawn.CurJob;
                bool ownsDestination = pawn.pather?.Destination.IsValid == true && pawn.pather.Destination.Cell == state.dodgeMovementDestination;
                bool synchronousStartFailure = failed && state.dodgeStartInProgress;
                applyFailureStagger = failed && !synchronousStartFailure && state.dodgeFailureStaggerTicks > 0;
                failureStaggerTicks = state.dodgeFailureStaggerTicks;
                failureStaggerSpeedFactor = state.dodgeFailureStaggerSpeedFactor;
                if (!sameJob
                    || (!force && !ownsDestination && !synchronousStartFailure)
                    || (!force && !failed && pawn.Position != state.dodgeMovementDestination))
                {
                    return false;
                }

                resumePath = !failed
                    && state.dodgeResumeWasMoving
                    && state.dodgeResumeDestination.IsValid
                    && (!state.dodgeResumeDestination.HasThing || !state.dodgeResumeDestination.ThingDestroyed);
                resumeDestination = state.dodgeResumeDestination;
                resumePathEndMode = state.dodgeResumePathEndMode;
                bool wasTumbling = state.dodgeMovementTumbling;
                if (failed)
                {
                    resumePath = !synchronousStartFailure
                        && state.dodgeResumeWasMoving
                        && state.dodgeResumeDestination.IsValid
                        && (!state.dodgeResumeDestination.HasThing
                            || !state.dodgeResumeDestination.ThingDestroyed);
                    resumeDestination = state.dodgeResumeDestination;
                    resumePathEndMode = state.dodgeResumePathEndMode;
                    if (!synchronousStartFailure
                        && state.dodgeMovementStartedInCloseCombat)
                    {
                        state.ConvertDodgeMovementToCloseDodge();
                    }
                    else if (wasTumbling)
                    {
                        state.HoldDodgeLanding();
                    }
                    else
                    {
                        state.HoldStandardDodgeLanding();
                    }
                    state.dodgeResumeWasMoving = false;
                    state.dodgeResumeDestination = LocalTargetInfo.Invalid;
                    state.dodgeMovementJob = null;
                }
                else if (wasTumbling)
                {
                    state.HoldDodgeLanding();
                }
                else
                {
                    state.HoldStandardDodgeLanding();
                }
            }

            pawn.pather.StopDead();
            if (applyFailureStagger)
            {
                pawn.stances?.stagger?.StaggerFor(failureStaggerTicks, failureStaggerSpeedFactor);
            }

            if (resumePath && pawn.Spawned)
            {
                pawn.pather.StartPath(resumeDestination, resumePathEndMode);
            }

            return true;
        }

        public void CancelFailedDodgeMovementStart(Pawn pawn)
        {
            lock (statesLock)
            {
                GetState(pawn, false)?.CancelFailedDodgeMovementStart();
            }
        }

        public bool IsDodgeMovementActive(Pawn pawn)
        {
            lock (statesLock)
            {
                if (pawn == null)
                {
                    return false;
                }

                statesByPawn.TryGetValue(
                    pawn,
                    out RimKataPawnCombatState state);
                return RimKataDodgeMovementUtility.CalculateIsActive(
                    pawn,
                    state);
            }
        }

        internal void GetDodgeMovementStatus(
            Pawn pawn,
            out bool blocksJob,
            out bool isActive)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = null;
                if (pawn != null)
                {
                    statesByPawn.TryGetValue(pawn, out state);
                }

                blocksJob = state?.DodgeMotionBlocksJob == true;
                isActive = blocksJob
                    && RimKataDodgeMovementUtility.CalculateIsActive(
                        pawn,
                        state);
            }
        }

        public bool IsDodgeMotionBlocking(Pawn pawn)
        {
            lock (statesLock)
            {
                return GetState(pawn, false)?.DodgeMotionBlocksJob == true;
            }
        }

        public bool IsDodgeVisualLocked(Pawn pawn)
        {
            lock (statesLock)
            {
                return GetState(pawn, false)?.DodgeVisualLocked == true;
            }
        }

        public bool IsDodgeMovementStartBlocked(Pawn pawn)
        {
            lock (statesLock)
            {
                return GetState(pawn, false)?.DodgeMovementStartBlocked == true;
            }
        }

        public bool IsRangedDodgeDelayActive(Pawn pawn)
        {
            lock (statesLock)
            {
                return GetState(pawn, false)?.RangedDodgeDelayActive == true;
            }
        }

        public bool CanTryAdditionalDodge(Pawn pawn)
        {
            lock (statesLock)
            {
                return GetState(pawn, false)?.AdditionalDodgeAvailable == true;
            }
        }

        public bool TryBeginAdditionalDodge(Pawn pawn, bool playTumble)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, false);
                if (state?.AdditionalDodgeAvailable != true)
                {
                    return false;
                }

                state.additionalDodgeUsed = true;
                if (playTumble)
                {
                    state.BeginAdditionalTumble();
                    RimKataResponseVisualParticipantCache
                        .RefreshBodyVisual(state);
                }

                return true;
            }
        }

        public void BeginImmediateTumble(Pawn pawn)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                state.BeginAdditionalTumble();
                RimKataResponseVisualParticipantCache.RefreshBodyVisual(state);
            }
        }

        public RimKataVisualSnapshot GetVisualSnapshot(Pawn pawn)
        {
            if (pawn == null)
            {
                return default(RimKataVisualSnapshot);
            }

            lock (statesLock)
            {
                statesByPawn.TryGetValue(
                    pawn,
                    out RimKataPawnCombatState state);
                return state != null ? state.VisualSnapshot() : default(RimKataVisualSnapshot);
            }
        }

        public bool TryGetActiveVisualSnapshot(
            Pawn pawn,
            out RimKataVisualSnapshot snapshot)
        {
            snapshot = default(RimKataVisualSnapshot);
            lock (statesLock)
            {
                if (pawn == null
                    || !statesByPawn.TryGetValue(
                        pawn,
                        out RimKataPawnCombatState state)
                    || (!state.VisualActive
                        && !state.DeflectionActive
                        && !state.DeflectionSpinActive
                        && !state.ResponsePoseActive
                        && !state.CloseDodgeActive
                        && state.groundPose?.VisualActive != true))
                {
                    return false;
                }

                snapshot = state.VisualSnapshot();
                return true;
            }
        }

        public bool TryGetGunReadyTarget(Pawn pawn, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            Job currentJob = pawn?.CurJob;
            bool combatJob = currentJob?.def == RimKataDefOf.RimKata_Attack;

            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, false);
                if (state == null)
                {
                    return false;
                }

                // A newly assigned Job/follow-up can exist while paused. Only
                // an aim already accepted by a weapon cycle makes it gun-ready.
                bool ready = (combatJob || state.dedicatedFollowupJobPending)
                    && RimKataDualWeaponController.TryGetNextAim(
                        pawn, state, out RimKataWeaponCycleState _, out target);
                if (ready && combatJob && target.HasThing
                    && !IsLiveRimKataJobTarget(pawn, currentJob, target.Thing))
                {
                    ready = false;
                    target = LocalTargetInfo.Invalid;
                }

                bool responseLookActive = state.responsePoseLookAtFocus
                    && state.TryGetLiveResponsePoseFocus(
                        out LocalTargetInfo _);

                if (state.CloseCombatActive)
                {
                    if (state.TryGetLiveCloseCombatTrigger(
                        out Thing closeCombatTrigger))
                    {
                        ThingWithComps primary =
                            RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
                        Verb primaryVerb =
                            RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
                        if (RimKataTargetAccess.SettingsFor(pawn)?.closeFireEnabled == false
                            && primaryVerb?.IsMeleeAttack == false)
                        {
                            ready = false;
                            target = LocalTargetInfo.Invalid;
                        }
                        else
                        {
                            ready = true;
                            target = new LocalTargetInfo(closeCombatTrigger);
                        }
                    }
                    else
                    {
                        state.CancelCloseCombat();
                    }
                }
                else if (!state.dedicatedFollowupJobPending
                    && state.DraftedFireActive
                    && pawn?.pather?.MovingNow == true
                    && ((state.draftedPlannedTarget != null
                    && !state.draftedPlannedTarget.Destroyed) || state.StoredCooldownActive))
                {
                    ready = true;
                    if (state.draftedPlannedTarget != null && !state.draftedPlannedTarget.Destroyed)
                    {
                        target = new LocalTargetInfo(state.draftedPlannedTarget);
                    }
                }

                if (!responseLookActive
                    && !state.CloseCombatActive
                    && pawn?.pather?.MovingNow == true
                    && RimKataDualWeaponController.TryGetNextAim(
                        pawn,
                        state,
                        out RimKataWeaponCycleState _,
                        out LocalTargetInfo movingAimTarget))
                {
                    ready = true;
                    target = movingAimTarget;
                }

                if (!responseLookActive
                    && !state.CloseCombatActive
                    && pawn?.pather?.MovingNow == true
                    && ready
                    && !WithinAutomaticMovingFireRange(pawn, target))
                {
                    ready = false;
                    target = LocalTargetInfo.Invalid;
                }

                return ready;
            }
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (Prefs.DevMode
                && RimKataDebugHUD.SearchRangeEnabled
                && Find.CurrentMap == map)
            {
                RimKataDebugHUD.DrawSearchPulses(map);
            }
        }

        private static bool IsLiveRimKataJobTarget(
            Pawn pawn,
            Job job,
            Thing target)
        {
            if (pawn?.Map == null
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || (!job.playerForced
                    && !RimKataTargeting.IsAutomaticEnemy(pawn, target)))
            {
                return false;
            }

            return !(target is Pawn targetPawn)
                || RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    job.playerForced && job.killIncappedTarget);
        }

        private static bool WithinAutomaticMovingFireRange(
            Pawn pawn,
            LocalTargetInfo target)
        {
            if (pawn?.Map == null
                || !target.IsValid
                || !target.HasThing
                || target.Thing == null
                || target.Thing.Destroyed
                || !target.Thing.Spawned
                || target.Thing.Map != pawn.Map)
            {
                return false;
            }

            float candidateCellRadius =
                RimKataTargeting.MaximumAutomaticCandidateCellRadius(pawn);
            return candidateCellRadius > 0f
                && pawn.Position.DistanceToSquared(target.Cell)
                    <= candidateCellRadius * candidateCellRadius;
        }

        public void BeginDeflection(
            Pawn pawn,
            int durationTicks,
            int sign,
            ThingWithComps weapon,
            bool spin = false,
            int spinDurationTicks = 8)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                if (state.DeflectionSpinActive)
                {
                    // A renewed deflection keeps the current angle and only
                    // stretches the remaining part of the same full turn.
                    state.deflectionSpinStartProgress = state.DeflectionSpinProgress;
                    state.deflectionSpinTicksRemaining = Mathf.Clamp(spinDurationTicks, 8, 24);
                    state.deflectionSpinTotalTicks = state.deflectionSpinTicksRemaining;
                }
                else if (spin)
                {
                    state.deflectionSpinStartAngle = pawn.Rotation.AsAngle;
                    state.deflectionSpinStartProgress = 0f;
                    state.deflectionSpin = true;
                    state.deflectionSpinSign = state.DeflectionActive ? state.deflectionSign : (sign < 0 ? -1 : 1);
                    state.deflectionSpinTicksRemaining = Mathf.Clamp(spinDurationTicks, 8, 24);
                    state.deflectionSpinTotalTicks = state.deflectionSpinTicksRemaining;
                }
                if (state.DeflectionActive)
                {
                    state.deflectionTicksRemaining = Mathf.Max(1, durationTicks);
                    state.deflectionTotalTicks = state.deflectionTicksRemaining;
                    state.deflectionWeapon = weapon;
                    RimKataResponseVisualParticipantCache.Refresh(state);
                    return;
                }

                state.deflectionTicksRemaining = Mathf.Max(1, durationTicks);
                state.deflectionTotalTicks = state.deflectionTicksRemaining;
                state.deflectionWeapon = weapon;
                state.deflectionSign = sign < 0 ? -1 : 1;
                RimKataResponseVisualParticipantCache.Refresh(state);
            }
        }

        public void BeginResponsePose(
            Pawn pawn,
            int durationTicks,
            float maximumAngle,
            int sign,
            LocalTargetInfo focus,
            ThingWithComps weapon,
            bool lookAtFocus)
        {
            if (pawn != null && pawn.IsBurning())
            {
                lock (statesLock)
                {
                    GetState(pawn, false)?.CancelOffenseForFire();
                }

                if (pawn.stances?.curStance is Stance_Cooldown
                    || pawn.stances?.curStance is Stance_RimKataAim)
                {
                    pawn.stances.SetStance(new Stance_Mobile());
                }

                return;
            }

            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                state.responsePoseTicksRemaining = Mathf.Max(1, durationTicks);
                state.responsePoseTotalTicks = state.responsePoseTicksRemaining;
                state.responsePoseMaxAngle = Mathf.Clamp(maximumAngle, 0f, 30f);
                state.responsePoseSign = sign < 0 ? -1 : 1;
                state.responsePoseFocus = focus;
                state.responsePoseWeapon = weapon;
                state.responsePoseLookAtFocus = lookAtFocus;
                RimKataResponseVisualParticipantCache.Refresh(state);
            }

            if (Prefs.DevMode && RimKataDebugHUD.Enabled)
            {
                RimKataDebugHUD.RecordResponseIndicator(pawn, weapon);
            }
        }

        public void BeginCloseCombatDodge(Pawn pawn, int durationTicks)
        {
            lock (statesLock)
            {
                GetState(pawn, true)?.BeginCloseDodge(durationTicks);
            }
        }

        public void EnterCloseCombat(Pawn pawn, Thing trigger)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, true);
                state?.EnterCloseCombat(trigger);
            }
        }

        public void CancelDeflectionSpin(Pawn pawn)
        {
            lock (statesLock)
            {
                RimKataPawnCombatState state = GetState(pawn, false);
                if (state?.deflectionSpin != true)
                {
                    return;
                }

                state.CancelDeflectionSpin();
            }
        }

        public bool IsCloseCombatActive(Pawn pawn)
        {
            lock (statesLock)
            {
                return GetState(pawn, false)?.CloseCombatActive == true;
            }
        }
    }
}
