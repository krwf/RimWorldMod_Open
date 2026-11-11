using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // The authoritative simulation sends existing participant states. Replicas only restore the
    // display indexes; normal save FinalizeInit would restart jobs, attack cycles and held combat.
    internal static class RimKataMultiplayerReplica
    {
        private static readonly ConditionalWeakTable<object, HashSet<Pawn>> published = new();
        private static readonly ConditionalWeakTable<RimKataSubdueState, HeldVisuals> heldVisuals = new();
        private static readonly AccessTools.FieldRef<RimKataMapComponent, List<RimKataPawnCombatState>> states =
            AccessTools.FieldRefAccess<RimKataMapComponent, List<RimKataPawnCombatState>>("states");
        private static readonly AccessTools.FieldRef<RimKataSecondaryWeaponRegistry, List<Pawn>> secondaryPawns =
            AccessTools.FieldRefAccess<RimKataSecondaryWeaponRegistry, List<Pawn>>("pawns");
        private static readonly AccessTools.FieldRef<RimKataSecondaryWeaponRegistry, List<ThingWithComps>> secondaryWeapons =
            AccessTools.FieldRefAccess<RimKataSecondaryWeaponRegistry, List<ThingWithComps>>("weapons");
        private static readonly Action<RimKataPawnCombatState> registerGround =
            AccessTools.MethodDelegate<Action<RimKataPawnCombatState>>(
                AccessTools.DeclaredMethod(typeof(RimKataGroundPoseUtility), "Register"));
        private static readonly Dictionary<Pawn, RimKataPawnCombatState> prone =
            (Dictionary<Pawn, RimKataPawnCombatState>)AccessTools.Field(typeof(RimKataGroundPoseUtility), "Prone").GetValue(null);

        private static Func<bool> sessionActive;
        private static Func<Map, bool> replicaMap;
        private static bool initialized;
        private static readonly Action<RimKataMapComponent> rebuildMap =
            AccessTools.MethodDelegate<Action<RimKataMapComponent>>(
                AccessTools.DeclaredMethod(typeof(RimKataMapComponent), "RebuildStateIndex"));
        private static readonly Action<RimKataPushMotion> publishPush =
            AccessTools.MethodDelegate<Action<RimKataPushMotion>>(AccessTools.DeclaredMethod(typeof(RimKataPushMotion), "Publish"));
        private static readonly Action<RimKataThrownPawn> publishThrow =
            AccessTools.MethodDelegate<Action<RimKataThrownPawn>>(AccessTools.DeclaredMethod(typeof(RimKataThrownPawn), "RefreshVisual"));

        internal static void PublishMap(RimKataMapComponent component)
        {
            if (component != null && ReplicaMap(component.map)) rebuildMap(component);
        }

        internal static void PublishSpecial(Thing thing)
        {
            if (!ReplicaMap(thing?.Map)) return;
            if (thing is RimKataPushMotion push) publishPush(push);
            else if (thing is RimKataThrownPawn thrown) publishThrow(thrown);
        }

        internal static void AdjustVisualTicks(RimKataSubdueState state, int offset)
        {
            if (state == null || !heldVisuals.TryGetValue(state, out HeldVisuals visuals)) return;
            AdjustHeld(visuals.carrier, offset);
            AdjustHeld(visuals.held, offset);
        }

        private static void AdjustHeld(HeldVisual visual, int offset)
        {
            if (visual?.present != true) return;
            visual.value.struggleStartTick = ShiftTick(visual.value.struggleStartTick, offset);
            visual.value.thrustStartTick = ShiftTick(visual.value.thrustStartTick, offset);
        }

        private static int ShiftTick(int tick, int offset) => tick < 0 ? tick
            : (int)Math.Min(int.MaxValue, Math.Max(0L, (long)tick + offset));

        internal static void Initialize(Harmony harmony, Func<bool> active, Func<Map, bool> replica)
        {
            if (initialized) return;
            sessionActive = active;
            replicaMap = replica;
            Patch(harmony, typeof(RimKataMapComponent), "RebuildStateIndex", nameof(BeforeMapRebuild));
            Patch(harmony, typeof(RimKataGroundPoseUtility), "Rebuild", nameof(GroundRebuild));
            Patch(harmony, typeof(RimKataFlyingKick), "Rebuild", nameof(FlyingKickRebuild));
            Patch(harmony, typeof(RimKataKick), "Rebuild", nameof(KickRebuild));
            Patch(harmony, typeof(RimKataSubdueState), "ExposeData", after: nameof(SubdueVisualData));
            Patch(harmony, typeof(RimKataDownedWeaponState), "ExposeData", after: nameof(CrawlVisualData));
            Patch(harmony, typeof(RimKataPushMotion), "ExposeData", after: nameof(PushLoaded));
            Patch(harmony, typeof(RimKataThrownPawn), "ExposeData", after: nameof(ThrowLoaded));
            Patch(harmony, typeof(RimKataPushMotion), "Destroy", nameof(PushDestroy));
            Patch(harmony, typeof(RimKataThrownPawn), "Destroy", nameof(ThrowDestroy));
            initialized = true;
        }

        private static bool ReplicaMap(Map map) => map != null && replicaMap?.Invoke(map) == true;
        private static bool ReplicaPawn(Pawn pawn) => ReplicaMap(pawn?.MapHeld);
        private static bool Readable(Pawn pawn) => pawn?.health != null && !pawn.Dead && !pawn.Destroyed;
        private static void PushLoaded(RimKataPushMotion __instance)
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ReplicaMap(__instance.Map)) publishPush(__instance);
        }
        private static void ThrowLoaded(RimKataThrownPawn __instance)
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ReplicaMap(__instance.Map)) publishThrow(__instance);
        }
        private static void PushDestroy(RimKataPushMotion __instance, ref bool ___finishing)
        {
            if (ReplicaMap(__instance.Map)) ___finishing = true;
        }
        private static void ThrowDestroy(RimKataThrownPawn __instance, ref bool ___resolving)
        {
            if (ReplicaMap(__instance.Map)) ___resolving = true;
        }
        private static void Patch(Harmony harmony, Type type, string name, string before = null, string after = null)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name)
                ?? throw new MissingMethodException(type.FullName, name);
            harmony.Patch(method,
                prefix: before == null ? null : new HarmonyMethod(typeof(RimKataMultiplayerReplica), before),
                postfix: after == null ? null : new HarmonyMethod(typeof(RimKataMultiplayerReplica), after));
        }

        private static void BeforeMapRebuild(RimKataMapComponent __instance)
        {
            if (!ReplicaMap(__instance.map)) return;
            HashSet<Pawn> previous = published.GetOrCreateValue(__instance);
            foreach (Pawn pawn in previous)
            {
                // Only previously received participants are cleaned, never the map's general pawn list.
                var body = RimKataResponseVisualParticipantCache.BodyVisualFor(pawn);
                if (body?.kick is RimKataKickVisual kick) RimKataKickRender.Remove(pawn, kick.frame);
                if (body?.flyingKick is RimKataFlyingKickVisual flying) RimKataFlyingKickRender.Remove(pawn, flying.frame);
            }
            previous.Clear();
            List<RimKataPawnCombatState> current = states(__instance);
            if (current != null)
                foreach (RimKataPawnCombatState state in current)
                    if (Readable(state?.pawn) && ReplicaPawn(state.pawn)) previous.Add(state.pawn);
        }

        private static bool GroundRebuild(RimKataPawnCombatState state)
        {
            if (!ReplicaPawn(state?.pawn)) return true;
            if (Readable(state?.pawn) && state.groundPose?.VisualActive == true)
            {
                if (state.groundPose.prone) prone[state.pawn] = state;
                registerGround(state);
            }
            return false;
        }

        private static bool FlyingKickRebuild(RimKataPawnCombatState combat)
        {
            if (!ReplicaPawn(combat?.pawn)) return true;
            if (Readable(combat?.pawn) && combat.flyingKick != null)
            {
                var flight = combat.flyingKick;
                flight.visual.frame = flight.frame;
                flight.visual.striking = flight.phase == RimKataFlyingKickPhase.Strike && flight.phaseTicks > 0;
                flight.visual.falling = flight.phase == RimKataFlyingKickPhase.Fall;
                flight.visual.aimTarget = flight.visual.falling ? flight.target : null;
                RimKataFlyingKickRender.Publish(combat.pawn, flight.visual);
            }
            return false;
        }

        private static bool KickRebuild(RimKataPawnCombatState combat)
        {
            if (!ReplicaPawn(combat?.pawn)) return true;
            if (Readable(combat?.pawn) && combat.kick?.motionActive == true)
            {
                combat.kick.visual.frame = combat.kick.frame;
                RimKataKickRender.Publish(combat.pawn, combat.kick.visual);
            }
            return false;
        }

        internal static void PublishBreach(RimKataBreachRegistry registry)
        {
            RimKataBreachUtility.SetRegistry(registry);
            HashSet<Pawn> previous = published.GetOrCreateValue(registry);
            foreach (Pawn pawn in previous)
            {
                RimKataResponseVisualParticipantCache.PublishBreach(pawn, null);
                RimKataBreachRender.Clear(pawn);
            }
            previous.Clear();
            foreach (var state in registry.states.Values)
                if (Readable(state?.pawn) && ReplicaPawn(state.pawn))
                {
                    previous.Add(state.pawn);
                    RimKataBreachUtility.Publish(state);
                }
        }

        internal static void PublishReactive(RimKataReactiveRegistry registry)
        {
            RimKataReactiveMotion.SetRegistry(registry);
            HashSet<Pawn> previous = published.GetOrCreateValue(registry);
            foreach (Pawn pawn in previous) RimKataReactiveRender.Remove(pawn);
            previous.Clear();
            foreach (var state in registry.states.Values)
                if (Readable(state?.pawn) && ReplicaPawn(state.pawn))
                {
                    previous.Add(state.pawn);
                    state.combat = RimKataReactiveMotion.ExistingCombat(state.pawn);
                    if (state.combat != null) state.combat.reactiveMotion = state;
                    RimKataReactiveRender.Publish(state);
                }
        }

        internal static void PublishSubdue(RimKataSubdueRegistry registry)
        {
            RimKataSubdueUtility.SetRegistry(registry);
            HashSet<Pawn> previous = published.GetOrCreateValue(registry);
            foreach (Pawn pawn in previous) RimKataResponseVisualParticipantCache.PublishSubdue(pawn, null, null);
            previous.Clear();
            foreach (var state in registry.active)
            {
                if (!Readable(state?.pawn) || !ReplicaPawn(state.pawn) || !Readable(state.target)
                    || !heldVisuals.TryGetValue(state, out HeldVisuals visuals)) continue;
                previous.Add(state.pawn);
                previous.Add(state.target);
                if (visuals.carrier.present)
                    RimKataResponseVisualParticipantCache.PublishSubdue(state.pawn, state.pawn.Map, visuals.carrier.value);
                if (visuals.held.present)
                    RimKataResponseVisualParticipantCache.PublishSubdue(state.target, state.pawn.Map, visuals.held.value);
            }
        }

        internal static void PublishSecondary(RimKataSecondaryWeaponRegistry registry)
        {
            var pawns = secondaryPawns(registry);
            var weapons = secondaryWeapons(registry);
            HashSet<Pawn> previous = published.GetOrCreateValue(registry);
            if (pawns != null) previous.ExceptWith(pawns);
            foreach (Pawn pawn in previous)
                if (Readable(pawn))
                {
                    RimKataEligibilityCache.UpdateRegisteredSecondaryWeapon(pawn, null, accessVerified: true, slotVerified: true);
                }
            previous.Clear();
            if (pawns == null || weapons == null) return;
            for (int i = 0; i < Math.Min(pawns.Count, weapons.Count); i++)
            {
                Pawn pawn = pawns[i];
                if (!Readable(pawn) || !ReplicaPawn(pawn)) continue;
                previous.Add(pawn);
                RimKataEligibilityCache.UpdateRegisteredSecondaryWeapon(pawn, weapons[i], accessVerified: true, slotVerified: true);
            }
        }

        // The ordinary save restores these attack-render inputs by restarting held combat. A replica
        // must instead receive the host's published result without Reset/Begin/health/job mutations.
        private static void SubdueVisualData(RimKataSubdueState __instance)
        {
            if (sessionActive?.Invoke() != true) return;
            HeldVisuals visuals = heldVisuals.GetValue(__instance, _ => new HeldVisuals());
            if (Scribe.mode == LoadSaveMode.Saving && !ReplicaPawn(__instance.pawn))
            {
                visuals.carrier.Read(__instance.pawn);
                visuals.held.Read(__instance.target);
            }
            Scribe_Deep.Look(ref visuals.carrier, "rimKataMultiplayerCarrierVisual");
            Scribe_Deep.Look(ref visuals.held, "rimKataMultiplayerHeldVisual");
            visuals.carrier ??= new HeldVisual();
            visuals.held ??= new HeldVisual();
        }

        private sealed class HeldVisuals
        {
            public HeldVisual carrier = new(), held = new();
        }
        private sealed class HeldVisual : IExposable
        {
            public HeldVisual() { }
            public bool present;
            public RimKataSubdueVisual value;
            public void Read(Pawn pawn)
            {
                var visual = Readable(pawn) ? RimKataResponseVisualParticipantCache.BodyVisualFor(pawn)?.subdue : null;
                present = visual.HasValue;
                value = visual.GetValueOrDefault();
            }
            public void ExposeData()
            {
                Scribe_Values.Look(ref present, "present");
                Scribe_References.Look(ref value.partner, "partner");
                Scribe_References.Look(ref value.weapon, "weapon");
                Scribe_Values.Look(ref value.facing, "facing");
                Scribe_Values.Look(ref value.held, "held");
                Scribe_Values.Look(ref value.attacking, "attacking");
                Scribe_Values.Look(ref value.ranged, "ranged");
                Scribe_Values.Look(ref value.struggleStartTick, "struggleStartTick");
                Scribe_Values.Look(ref value.struggleSign, "struggleSign");
                Scribe_Values.Look(ref value.thrustStartTick, "thrustStartTick");
                Scribe_Values.Look(ref value.thrustDuration, "thrustDuration");
                Scribe_Values.Look(ref value.recoilAngle, "recoilAngle");
                Scribe_Values.Look(ref value.recoilOffset, "recoilOffset");
                Scribe_TargetInfo.Look(ref value.aimTarget, "aimTarget");
                Scribe_TargetInfo.Look(ref value.thrustTarget, "thrustTarget");
            }
        }
        private static readonly ConditionalWeakTable<RimKataDownedWeaponState, CrawlVisual> crawlVisuals = new();
        private static readonly AccessTools.FieldRef<RimKataDownedWeaponRegistry, Dictionary<Pawn, RimKataDownedWeaponState>> downedStates =
            AccessTools.FieldRefAccess<RimKataDownedWeaponRegistry, Dictionary<Pawn, RimKataDownedWeaponState>>("states");
        private static readonly Dictionary<Pawn, RimKataDownedWeaponState> retainedCrawlers =
            (Dictionary<Pawn, RimKataDownedWeaponState>)AccessTools.Field(typeof(RimKataCrawlFireUtility), "Retained").GetValue(null);

        private static void CrawlVisualData(RimKataDownedWeaponState __instance)
        {
            if (sessionActive?.Invoke() != true) return;
            CrawlVisual visual = crawlVisuals.GetValue(__instance, _ => new CrawlVisual());
            if (Scribe.mode == LoadSaveMode.Saving && !ReplicaPawn(__instance.pawn))
            {
                var body = RimKataResponseVisualParticipantCache.BodyVisualFor(__instance.pawn);
                visual.weapon = body?.crawlWeapon;
                visual.target = body?.crawlTarget ?? LocalTargetInfo.Invalid;
            }
            Scribe_References.Look(ref visual.weapon, "rimKataMultiplayerCrawlWeapon");
            Scribe_TargetInfo.Look(ref visual.target, "rimKataMultiplayerCrawlTarget");
        }

        internal static void PublishCrawl(RimKataDownedWeaponRegistry registry)
        {
            HashSet<Pawn> previous = published.GetOrCreateValue(registry);
            foreach (Pawn pawn in previous)
            {
                RimKataResponseVisualParticipantCache.ClearCrawl(pawn);
                retainedCrawlers.Remove(pawn);
            }
            previous.Clear();
            foreach (var record in downedStates(registry).Values)
            {
                if (!Readable(record?.pawn) || !ReplicaPawn(record.pawn) || !record.activeDowned) continue;
                previous.Add(record.pawn);
                retainedCrawlers[record.pawn] = record;
                if (crawlVisuals.TryGetValue(record, out CrawlVisual visual) && visual.weapon != null)
                    RimKataResponseVisualParticipantCache.PublishCrawl(record.pawn, visual.weapon, visual.target);
            }
        }

        private sealed class CrawlVisual
        {
            internal ThingWithComps weapon;
            internal LocalTargetInfo target = LocalTargetInfo.Invalid;
        }

    }
}

