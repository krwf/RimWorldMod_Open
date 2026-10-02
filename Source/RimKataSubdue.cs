using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed partial class RimKataSubdueState : IExposable
    {
        internal Pawn pawn, target;
        internal Rot4 facing;
        internal bool attackEnabled, hostile;
        internal int struggleStartTick, struggleSign;
        internal ThingWithComps weapon;
        internal Verb attackVerb;
        internal IntVec3 lastPosition;
        internal Job suspendedJob;
        internal bool previousRangedAllowed;
        internal int suspendedJobId = -1;
        internal Action contentsChanged;

        public void ExposeData()
        {
            // The native carryTracker is the sole deep-save owner of this pawn.
            Scribe_References.Look(ref pawn, "carrier");
            Scribe_References.Look(ref target, "heldPawn");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref attackEnabled, "attackEnabled");
            Scribe_Values.Look(ref struggleStartTick, "struggleStartTick");
            Scribe_Values.Look(ref struggleSign, "struggleSign", 1);
            Scribe_Values.Look(ref suspendedJobId, "suspendedJobId", -1);
            Scribe_Values.Look(ref previousRangedAllowed, "previousRangedAllowed");
            ExposeCombat();
        }

        internal void RestoreJobFlag()
        {
            Job original = suspendedJob ?? pawn?.CurJob;
            if (suspendedJobId >= 0 && original?.loadID == suspendedJobId)
                original.canUseRangedWeapon = previousRangedAllowed;
            suspendedJob = null;
            suspendedJobId = -1;
        }

        internal void SuspendAutomaticFire(Job startingJob = null)
        {
            Job current = startingJob ?? pawn.CurJob;
            if (attackAllowed && !attackEnabled && !HasExternalTarget)
            {
                RestoreJobFlag();
                return;
            }
            if (current == suspendedJob) return;
            RestoreJobFlag();
            if (current == null) return;
            suspendedJob = current;
            suspendedJobId = current.loadID;
            previousRangedAllowed = current.canUseRangedWeapon;
            current.canUseRangedWeapon = false;
        }

        internal void Attach()
        {
            if (contentsChanged != null) return;
            contentsChanged = () =>
            {
                if (!RimKataSubdueUtility.IsRelationValid(this)) RimKataSubdueUtility.Remove(this);
            };
            pawn.carryTracker.innerContainer.OnContentsChanged += contentsChanged;
        }

        internal void Detach()
        {
            if (contentsChanged == null) return;
            pawn.carryTracker.innerContainer.OnContentsChanged -= contentsChanged;
            contentsChanged = null;
        }
    }

    public sealed class RimKataSubdueRegistry : GameComponent
    {
        private readonly Game game;
        internal readonly Dictionary<Pawn, RimKataSubdueState> states = new Dictionary<Pawn, RimKataSubdueState>();
        internal readonly List<RimKataSubdueState> active = new List<RimKataSubdueState>();
        private List<RimKataSubdueState> saved;
        internal bool IsCurrent => ReferenceEquals(game, Current.Game);

        public RimKataSubdueRegistry(Game game)
        { this.game = game; RimKataSubdueUtility.SetRegistry(this); }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) saved = new List<RimKataSubdueState>(active);
            Scribe_Collections.Look(ref saved, "rimKataSubdues", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                states.Clear();
                active.Clear();
                if (saved != null) foreach (var state in saved)
                    if (state?.pawn != null && !states.ContainsKey(state.pawn))
                    { states.Add(state.pawn, state); active.Add(state); }
                saved = null;
            }
        }

        public override void FinalizeInit()
        {
            RimKataSubdueUtility.SetRegistry(this);
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (!IsCurrent) return;
                for (int i = active.Count - 1; i >= 0; --i)
                {
                    var state = active[i];
                    state.RestoreJobFlag();
                    if (!RimKataSubdueUtility.IsRelationValid(state))
                        RimKataSubdueUtility.Remove(state);
                    else if (!RimKataEligibility.HasActiveRimKataAccess(state.pawn))
                        RimKataSubdueUtility.Release(state);
                    else
                    {
                        state.lastPosition = state.pawn.Position;
                        state.hostile = state.target.HostileTo(state.pawn);
                        state.Attach();
                        RimKataSubdueCombat.Begin(state, restoring: true);
                        state.SuspendAutomaticFire();
                        RimKataSubdueRender.Publish(state);
                    }
                }
            });
        }

        public override void GameComponentTick()
        {
            for (int i = active.Count - 1; i >= 0; --i)
            {
                if (i >= active.Count) continue;
                var state = active[i];
                if (!RimKataSubdueUtility.IsRelationValid(state))
                { RimKataSubdueUtility.Remove(state); continue; }
                Pawn pawn = state.pawn;
                if (pawn.Position != state.lastPosition)
                {
                    IntVec3 delta = pawn.Position - state.lastPosition;
                    state.facing = Rot4.FromAngleFlat(delta.ToVector3().AngleFlat());
                    state.lastPosition = pawn.Position;
                }
                int now = Find.TickManager.TicksGame;
                if (state.hostile && now - state.struggleStartTick >= 20)
                {
                    state.struggleStartTick = now;
                    state.struggleSign = Rand.Bool ? 1 : -1;
                }
                RimKataSubdueCombat.Tick(state);
                if (RimKataSubdueUtility.Get(pawn) == state)
                    RimKataSubdueRender.Publish(state);
            }
        }
    }

    internal static class RimKataSubdueUtility
    {
        private static RimKataSubdueRegistry registry;
        internal static int RelationVersion { get; private set; }
        internal static RimKataSubdueRegistry Registry => registry?.IsCurrent == true ? registry : null;
        internal static bool Any => Registry?.active.Count > 0;
        internal static RimKataSubdueState Get(Pawn pawn)
        {
            var current = Registry;
            return pawn != null && current != null && current.states.Count != 0
                && current.states.TryGetValue(pawn, out var state) ? state : null;
        }
        internal static bool IsHolding(Pawn pawn) => Get(pawn) != null;
        internal static bool IsRelationValid(RimKataSubdueState state)
            => state?.pawn?.Spawned == true && state.target != null && !state.target.Destroyed
                && state.pawn.carryTracker?.CarriedThing == state.target;

        internal static void SetRegistry(RimKataSubdueRegistry value)
        {
            if (registry == value) return;
            RimKataSubdueRegistry previous = registry;
            registry = value;
            unchecked { RelationVersion++; }
            if (previous != null) foreach (var state in previous.active)
            {
                RimKataSubdueCombat.End(state);
                state.RestoreJobFlag();
                state.Detach();
                RimKataSubdueRender.Remove(state);
            }
        }

        internal static bool CanOrder(Pawn pawn)
            => pawn?.Spawned == true && pawn.IsPlayerControlled && pawn.Drafted
                && pawn.carryTracker != null && pawn.carryTracker.CarriedThing == null
                && RimKataEligibility.HasActiveRimKataAccess(pawn)
                && RimKataTargetAccess.SettingsFor(pawn)?.subdueEnabled == true;

        internal static string Unavailable(Pawn pawn, Pawn target)
        {
            if (!CanOrder(pawn) || target == null || target == pawn || !target.Spawned
                || target.Dead || target.Map != pawn.Map)
                return "KRWF_RimKata_SubdueUnavailable".Translate();
            float maximum = pawn.GetStatValue(StatDefOf.Mass)
                * RimKataTargetAccess.SettingsFor(pawn).GetSubdueMassMultiplier(pawn);
            if (target.GetStatValue(StatDefOf.Mass) > maximum)
                return "KRWF_RimKata_SubdueTooHeavy".Translate(target.LabelShort, maximum.ToString("0.#"));
            if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                return "NoPath".Translate();
            return null;
        }

        internal static bool Take(Pawn pawn, Pawn target)
        {
            var current = Registry;
            if (current == null) return false;
            if (Unavailable(pawn, target) != null || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return false;
            Rot4 facing = pawn.Rotation;
            if (pawn.carryTracker.TryStartCarry(target, 1, true) != 1 || pawn.carryTracker.CarriedThing != target) return false;
            var state = new RimKataSubdueState
            {
                pawn = pawn, target = target, facing = facing, lastPosition = pawn.Position,
                hostile = target.HostileTo(pawn), struggleStartTick = Find.TickManager.TicksGame,
                struggleSign = Rand.Bool ? 1 : -1, attackEnabled = false
            };
            current.states.Add(pawn, state);
            current.active.Add(state);
            unchecked { RelationVersion++; }
            state.Attach();
            RimKataSubdueCombat.Begin(state);
            state.SuspendAutomaticFire();
            RimKataSubdueRender.Publish(state);
            return true;
        }

        internal static void Remove(RimKataSubdueState state)
        {
            if (state == null || Get(state.pawn) != state) return;
            Registry.states.Remove(state.pawn);
            Registry.active.Remove(state);
            unchecked { RelationVersion++; }
            state.Detach();
            state.RestoreJobFlag();
            RimKataSubdueCombat.End(state);
            RimKataSubdueRender.Remove(state);
        }

        internal static void Release(RimKataSubdueState state)
        {
            if (!IsRelationValid(state)) { Remove(state); return; }
            state.pawn.carryTracker.TryDropCarriedThing(state.pawn.Position, ThingPlaceMode.Near, out _);
            if (!IsRelationValid(state)) Remove(state);
        }

        internal static void NotifyEligibilityLost(Pawn pawn)
        {
            var state = Get(pawn);
            if (state == null) return;
            Release(state);
            Remove(state);
        }

        internal static void NotifyEquipmentChanged(Pawn pawn)
        {
            var state = Get(pawn);
            if (state == null) return;
            RimKataSubdueCombat.RefreshWeapon(state);
            RimKataSubdueRender.Publish(state);
        }

        internal static void Throw(RimKataSubdueState state)
        {
            if (!IsRelationValid(state)) { Remove(state); return; }
            RimKataThrownPawn.Launch(state);
        }

        internal static void KillAndDiscard(Pawn target)
        {
            // Kill on a held pawn would first drop it onto the carrier's cell.
            if (!target.Dead) target.Kill(null);
            Corpse corpse = target.Corpse;
            if (corpse != null && !corpse.Destroyed) corpse.Destroy(DestroyMode.Vanish);

            // Death notifications can create new tale records before discard.
            Find.PlayLog.Notify_PawnDiscarded(target, true);
            Find.BattleLog.Notify_PawnDiscarded(target, true);
            ClearPawnTales(Find.TaleManager, target);
            var worldPawns = Find.WorldPawns;
            if (worldPawns.Contains(target)) worldPawns.RemoveAndDiscardPawnViaGC(target);
            else worldPawns.PassToWorld(target, RimWorld.Planet.PawnDiscardDecideMode.Discard);
        }

        internal static void ClearPawnTales(TaleManager manager, Pawn pawn)
        {
            var tales = manager.AllTalesListForReading;
            for (int i = tales.Count - 1; i >= 0; --i)
            {
                Tale tale = tales[i];
                if (!tale.Concerns(pawn)) continue;
                if (tale.Unused)
                {
                    tales.RemoveAt(i);
                    continue;
                }
                // Art retains its TaleReference; only the live pawn reference can be released.
                if (tale is Tale_SinglePawn single) DetachTalePawn(single.pawnData, pawn);
                if (tale is Tale_DoublePawn pair)
                {
                    DetachTalePawn(pair.firstPawnData, pawn);
                    DetachTalePawn(pair.secondPawnData, pawn);
                }
                if (tale is Tale_SinglePawnAndThing withThing
                    && withThing.thingData?.thingID == pawn.thingIDNumber)
                    withThing.thingData.thingID = -1;
            }
        }

        private static void DetachTalePawn(TaleData_Pawn data, Pawn pawn)
        {
            if (data?.pawn == pawn) data.pawn = null;
        }
    }

    public sealed class JobDriver_RimKataSubdue : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            Toil approach = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            approach.FailOn(() => !RimKataSubdueUtility.CanOrder(pawn)
                || job.targetA.Pawn?.Spawned != true || job.targetA.Pawn.Dead);
            yield return approach;
            Toil take = ToilMaker.MakeToil("RimKataSubdueTake");
            take.initAction = () =>
            {
                if (!RimKataSubdueUtility.Take(pawn, job.targetA.Pawn))
                    EndJobWith(JobCondition.Incompletable);
            };
            take.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return take;
        }
    }

    public sealed class FloatMenuOptionProvider_RimKataSubdue : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => false;
        protected override bool Multiselect => false;
        protected override bool AppliesInt(FloatMenuContext context)
            => RimKataSubdueUtility.CanOrder(context.FirstSelectedPawn);
        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            Pawn pawn = context.FirstSelectedPawn;
            if (clickedPawn == pawn) return null;
            string unavailable = RimKataSubdueUtility.Unavailable(pawn, clickedPawn);
            string label = "KRWF_RimKata_Subdue".Translate();
            if (unavailable != null) return new FloatMenuOption(label + ": " + unavailable, null);
            var option = new FloatMenuOption(label, () => pawn.jobs.TryTakeOrderedJob(
                JobMaker.MakeJob(RimKataDefOf.RimKata_Subdue, clickedPawn), JobTag.Misc));
            return FloatMenuUtility.DecoratePrioritizedTask(option, pawn, clickedPawn);
        }
    }
}
