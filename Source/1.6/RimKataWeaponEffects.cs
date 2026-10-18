using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Only the participating secondary weapon owns a detached health snapshot.
    public sealed class RimKataWeaponEffectState : IExposable
    {
        internal Pawn pawn;
        internal ThingWithComps weapon;
        internal List<Hediff> managers = new List<Hediff>();
        internal List<Hediff> effects = new List<Hediff>();
        internal bool managersInitialized;
        internal bool secondarySnapshot = true;
        internal List<HediffDef> suspendedManagers = new List<HediffDef>();
        internal readonly List<Hediff> baselineHediffs = new List<Hediff>();
        internal RimKataWeaponEffectStats.Snapshot statSnapshot;
        internal ThingWithComps primaryWeapon;
        internal int lastTick = -1;
        internal int intervalDelta;
        internal readonly List<Hediff> tickBuffer = new List<Hediff>();
        internal RimKataWeaponEffectPlan plan;
        internal RimKataWeaponCycleState cycle;
        internal RimKataPawnCombatState combat;
        internal Verb verb;
        internal readonly List<ThingWithComps> equipmentView = new List<ThingWithComps>(1);
        private List<HediffDef> legacyDefinitions;
        private List<float> legacySeverities;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref secondarySnapshot, "secondarySnapshot", false);
            if (secondarySnapshot) Scribe_Collections.Look(ref managers, "secondaryManagers", LookMode.Deep);
            else Scribe_Collections.Look(ref managers, "managers", LookMode.Reference);
            Scribe_Collections.Look(ref suspendedManagers, "suspendedManagers", LookMode.Def);
            Scribe_Collections.Look(ref effects, "effects", LookMode.Deep);
            Scribe_Values.Look(ref managersInitialized, "managersInitialized");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Collections.Look(ref legacyDefinitions, "definitions", LookMode.Def);
                Scribe_Collections.Look(ref legacySeverities, "severities", LookMode.Value);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                managers ??= new List<Hediff>();
                effects ??= new List<Hediff>();
                suspendedManagers ??= new List<HediffDef>();
            }
        }

        internal void Restore()
        {
            equipmentView.Clear();
            equipmentView.Add(weapon);
            effects.RemoveAll(h => h?.def == null || Array.IndexOf(plan.Effects, h.def) < 0);
            foreach (Hediff effect in effects) effect.pawn = pawn;
            foreach (Hediff manager in managers) if (manager != null) manager.pawn = pawn;
            if (legacyDefinitions != null && legacySeverities != null)
                for (int i = 0; i < Math.Min(legacyDefinitions.Count, legacySeverities.Count); i++)
                {
                    HediffDef def = legacyDefinitions[i];
                    float severity = legacySeverities[i];
                    if (def == null || Array.IndexOf(plan.Effects, def) < 0 || severity <= 0f
                        || float.IsNaN(severity) || float.IsInfinity(severity)) continue;
                    Hediff effect = HediffMaker.MakeHediff(def, pawn);
                    effect.Severity = severity;
                    effects.Add(effect);
                }
            legacyDefinitions = null;
            legacySeverities = null;
        }
    }

    // No global Tick or pawn discovery. Existing equipment binding supplies participants.
    public sealed class RimKataWeaponEffectRegistry : GameComponent
    {
        internal readonly Game game;
        internal List<RimKataWeaponEffectState> records = new List<RimKataWeaponEffectState>();
        internal readonly Dictionary<ThingWithComps, RimKataWeaponEffectState> weapons = new Dictionary<ThingWithComps, RimKataWeaponEffectState>();
        internal readonly Dictionary<Verb, RimKataWeaponEffectState> verbs = new Dictionary<Verb, RimKataWeaponEffectState>();
        internal readonly Dictionary<Hediff, RimKataWeaponEffectState> managers = new Dictionary<Hediff, RimKataWeaponEffectState>();
        internal readonly Dictionary<Pawn, List<RimKataWeaponEffectState>> owners = new Dictionary<Pawn, List<RimKataWeaponEffectState>>();

        public RimKataWeaponEffectRegistry(Game game) { this.game = game; }
        public override void LoadedGame() => RimKataWeaponEffects.RestoreLoadedRecords(this);
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref records, "weaponEffects", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            records ??= new List<RimKataWeaponEffectState>();
            weapons.Clear(); verbs.Clear(); managers.Clear(); owners.Clear();
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (record?.pawn == null || record.weapon == null || record.weapon.Destroyed
                    || weapons.ContainsKey(record.weapon)
                    || record.pawn.equipment?.AllEquipmentListForReading.Contains(record.weapon) != true)
                { records.RemoveAt(i); continue; }
                Index(record);
                foreach (Hediff manager in record.managers)
                    if (manager != null && !managers.ContainsKey(manager)) managers.Add(manager, record);
            }
        }

        internal void Index(RimKataWeaponEffectState record)
        {
            weapons[record.weapon] = record;
            if (!owners.TryGetValue(record.pawn, out var list))
                owners.Add(record.pawn, list = new List<RimKataWeaponEffectState>(2));
            list.Add(record);
        }

        internal void Forget(RimKataWeaponEffectState record)
        {
            if (record.verb != null) verbs.Remove(record.verb);
            weapons.Remove(record.weapon);
            records.Remove(record);
            foreach (Hediff manager in record.managers)
                if (manager != null && managers.TryGetValue(manager, out var owner) && owner == record) managers.Remove(manager);
            if (owners.TryGetValue(record.pawn, out var list))
            {
                list.Remove(record);
                if (list.Count == 0) owners.Remove(record.pawn);
            }
            record.managers.Clear(); record.effects.Clear(); record.cycle = null;
            record.baselineHediffs.Clear(); record.statSnapshot = null;
        }
    }

    internal static class RimKataWeaponEffects
    {
        private static readonly Dictionary<ThingDef, RimKataWeaponEffectPlan> plans = new Dictionary<ThingDef, RimKataWeaponEffectPlan>();
        private static readonly HashSet<MethodInfo> installed = new HashSet<MethodInfo>();
        private static readonly Func<Thing, int> MinInterval = AccessTools.MethodDelegate<Func<Thing, int>>(
            AccessTools.PropertyGetter(typeof(Thing), "MinTickIntervalRate"));
        private static readonly Func<Thing, int> MaxInterval = AccessTools.MethodDelegate<Func<Thing, int>>(
            AccessTools.PropertyGetter(typeof(Thing), "MaxTickIntervalRate"));
        private static readonly Func<Thing, int> IntervalOffset = AccessTools.MethodDelegate<Func<Thing, int>>(
            AccessTools.PropertyGetter(typeof(Thing), "UpdateRateTickOffset"));
        private static RimKataWeaponEffectRegistry store;
        [ThreadStatic] private static RimKataWeaponEffectState current;
        [ThreadStatic] private static TargetCleanup targetCleanup;

        internal readonly struct TargetCleanup
        {
            internal readonly object manager;
            internal readonly Pawn pawn;
            internal readonly RimKataWeaponEffectPlan plan;
            internal readonly RimKataWeaponEffectState snapshot;
            internal TargetCleanup(object manager, Pawn pawn, RimKataWeaponEffectPlan plan, RimKataWeaponEffectState snapshot)
            { this.manager = manager; this.pawn = pawn; this.plan = plan; this.snapshot = snapshot; }
        }

        internal readonly struct TargetChangeScope : IDisposable
        {
            private readonly TargetCleanup previous;
            internal TargetChangeScope(TargetCleanup context) { previous = targetCleanup; targetCleanup = context; }
            public void Dispose() { targetCleanup = previous; }
        }

        private readonly struct Scope : IDisposable
        {
            private readonly RimKataWeaponEffectState previous;
            private readonly TargetCleanup previousCleanup;
            private readonly bool entered;
            internal Scope(RimKataWeaponEffectState record, object owner = null)
            {
                previous = current; previousCleanup = targetCleanup; entered = true; current = record;
                // A nested callback belonging to another manager must not inherit this removal reason.
                if (owner != null && owner != targetCleanup.manager) targetCleanup = default;
            }
            public void Dispose()
            { if (entered) { current = previous; targetCleanup = previousCleanup; } }
        }

        internal static void Prepare(Harmony harmony, bool force = false)
        {
            // Refresh is a preparation boundary, never a per-pawn polling path.
            if (store != null)
                while (store.records.Count != 0) Release(store.records[store.records.Count - 1], true);
            plans.Clear();
            foreach (var execution in RimKataWeaponExecutionPlans.Plans)
                PrepareWeapon(harmony, execution, force);
        }

        internal static void PrepareWeapon(Harmony harmony, RimKataWeaponExecutionPlan execution, bool force = false)
        {
            execution.EffectStatus = RimKataWeaponEffectStatus.NotDetected;
            execution.EffectReason = null;
            if (!RimKataWeaponExecutionRuntime.CanPrepareEffects(execution))
            {
                plans.Remove(execution.Def);
                if (store != null)
                    for (int i = store.records.Count - 1; i >= 0; i--)
                        if (store.records[i].weapon.def == execution.Def) Release(store.records[i], true);
                return;
            }
            if (plans.ContainsKey(execution.Def))
            { execution.EffectStatus = RimKataWeaponEffectStatus.Connected; return; }
            try
            {
                if (!RimKataWeaponEffectCache.TryGet(execution, force, out var plan, out string reason))
                { if (reason != null) Original(execution, reason); return; }
                if (!RimKataWeaponEffectHealth.Initialize(out reason)) { Original(execution, reason); return; }
                RimKataWeaponEffectStats.IsSupported(StatDefOf.AimingDelayFactor);
                RimKataWeaponEffectStats.IsSupported(StatDefOf.RangedCooldownFactor);
                RimKataWeaponEffectStats.IsSupported(StatDefOf.ShootingAccuracyPawn);
                foreach (MethodInfo method in plan.AnalyzedMethods) Install(harmony, method);
                plans.Add(execution.Def, plan);
                execution.EffectStatus = RimKataWeaponEffectStatus.Connected;
                RimKataStartupDiagnostics.Count("weapon_effects_connected");
            }
            catch (Exception exception)
            { Original(execution, exception.GetType().Name + ": " + exception.Message); }
        }

        private static void Original(RimKataWeaponExecutionPlan execution, string reason)
        {
            execution.EffectStatus = RimKataWeaponEffectStatus.Original;
            execution.EffectReason = reason;
            RimKataStartupDiagnostics.Count("weapon_effects_original");
            Log.Warning("[RimKata] Weapon effects remain on the original shared path for "
                + execution.Def.defName + ": " + reason
                + " Firing registration does not imply weapon-local effect support.");
        }

        private static void Install(Harmony harmony, MethodInfo method)
        {
            if (installed.Contains(method)) return;
            bool owner = !method.IsStatic && (typeof(Verb).IsAssignableFrom(method.DeclaringType)
                || typeof(ThingComp).IsAssignableFrom(method.DeclaringType)
                || typeof(HediffComp).IsAssignableFrom(method.DeclaringType)
                || typeof(Hediff).IsAssignableFrom(method.DeclaringType));
            RimKataStartupPatches.Patch(harmony, method,
                prefix: owner ? new HarmonyMethod(typeof(RimKataWeaponEffects), nameof(EnterScope)) { priority = Priority.First } : null,
                postfix: !method.IsStatic && typeof(HediffComp).IsAssignableFrom(method.DeclaringType)
                    && method.Name == nameof(HediffComp.CompPostPostRemoved)
                        ? new HarmonyMethod(typeof(RimKataWeaponEffects), nameof(ManagerRemoved)) : null,
                transpiler: new HarmonyMethod(typeof(RimKataWeaponEffects), nameof(RewriteAccess)) { priority = Priority.Last },
                finalizer: owner ? new HarmonyMethod(typeof(RimKataWeaponEffects), nameof(ExitScope)) { priority = Priority.Last } : null);
            installed.Add(method);
        }

        private static void EnterScope(object __instance, out Scope __state)
        {
            RimKataWeaponEffectState record = null;
            if (current != null && (__instance == current.verb
                || (__instance is ThingComp existingComp && existingComp.parent == current.weapon)
                || (__instance is HediffComp existingManager && current.managers.Contains(existingManager.parent))))
            { __state = new Scope(current, __instance); return; }
            if (store != null)
            {
                if (__instance is Verb verb) store.verbs.TryGetValue(verb, out record);
                else if (__instance is ThingComp comp) store.weapons.TryGetValue(comp.parent, out record);
                else
                {
                    Hediff hediff = (__instance as HediffComp)?.parent ?? __instance as Hediff;
                    if (hediff != null && !store.managers.TryGetValue(hediff, out record)
                        && current != null && (current.managers.Contains(hediff) || current.effects.Contains(hediff)))
                        record = current;
                }
            }
            __state = new Scope(record, __instance);
        }

        private static Exception ExitScope(Exception __exception, Scope __state)
        { __state.Dispose(); return __exception; }

        private static void ManagerRemoved(HediffComp __instance)
        {
            Hediff manager = __instance.parent;
            if (store == null || manager == null || !store.managers.TryGetValue(manager, out var record)) return;
            if (record.managers.Contains(manager)) return;
            if (manager is HediffWithComps withComps && withComps.comps.Count != 0
                && withComps.comps[withComps.comps.Count - 1] != __instance) return;
            record.managers.Remove(manager);
            store.managers.Remove(manager);
        }

        private static RimKataWeaponEffectRegistry Store()
        {
            Game game = Current.Game;
            if (game == null) return null;
            if (store == null || store.game != game) store = game.GetComponent<RimKataWeaponEffectRegistry>();
            return store;
        }

        internal static void RestoreLoadedRecords(RimKataWeaponEffectRegistry registry)
        {
            if (registry == null || registry.game != Current.Game) return;
            store = registry;
            for (int i = registry.records.Count - 1; i >= 0; i--)
            {
                var record = registry.records[i];
                // The previous format stored both weapons' managers in real health.
                // Retire that ownership once; never interpret those references as detached copies.
                if (!record.secondarySnapshot) { Release(record, true); continue; }
                Pawn pawn = record.pawn;
                ThingWithComps weapon = record.weapon;
                if (pawn?.Spawned != true || pawn.Dead || pawn.health?.hediffSet == null
                    || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    || weapon == null || weapon.Destroyed
                    || pawn.equipment?.AllEquipmentListForReading.Contains(weapon) != true
                    || !RimKataEquipmentUtility.IsWeaponEnabled(weapon.def)
                    || !plans.TryGetValue(weapon.def, out var plan))
                { Release(record, true); continue; }
                Verb verb = RimKataWeaponSlotUtility.PrimaryVerb(weapon);
                if (verb == null || verb.GetType() != plan.VerbType) { Release(record, true); continue; }
                RimKataPawnCombatState combat = RimKataCombatStatePresenceCache.TryGetOwner(pawn, out var owner)
                    ? owner.GetState(pawn, false) : null;
                if (combat?.dualEngagementActive != true || combat.secondaryWeaponCycle?.weapon != weapon)
                { Release(record, true); continue; }
                record.cycle = combat.secondaryWeaponCycle;
                Connect(record, plan, verb, combat);
            }
        }

        internal static void Sync(Pawn pawn, RimKataPawnCombatState state)
        {
            if (pawn == null || state == null || (plans.Count == 0 && store == null)) return;
            var registry = Store();
            if (registry == null) return;
            var second = state.secondaryWeaponCycle;
            bool active = state.dualEngagementActive && CanBind(second, out _);
            if (registry.owners.TryGetValue(pawn, out var previous))
                for (int i = previous.Count - 1; i >= 0; i--)
                {
                    var old = previous[i];
                    if (!active || old.weapon != second.weapon || old.primaryWeapon != state.primaryWeaponCycle?.weapon)
                        Release(old, true);
                }
            if (active) Bind(registry, pawn, second, state);
        }

        private static void Bind(RimKataWeaponEffectRegistry registry, Pawn pawn,
            RimKataWeaponCycleState cycle, RimKataPawnCombatState combat)
        {
            if (!CanBind(cycle, out var plan)) return;
            if (registry.weapons.TryGetValue(cycle.weapon, out var record) && record.pawn != pawn)
            { Release(record, false); record = null; }
            if (record == null)
            {
                record = new RimKataWeaponEffectState { pawn = pawn, weapon = cycle.weapon };
                registry.records.Add(record);
                registry.Index(record);
            }
            record.cycle = cycle;
            Connect(record, plan, cycle.boundVerb, combat);
        }

        private static void Connect(RimKataWeaponEffectState record, RimKataWeaponEffectPlan plan, Verb verb, RimKataPawnCombatState combat)
        {
            if (record.verb != null && record.verb != verb) store.verbs.Remove(record.verb);
            bool first = record.plan == null;
            record.plan = plan; record.verb = verb; record.combat = combat;
            store.verbs[verb] = record;
            if (first)
            {
                record.Restore();
                record.primaryWeapon = combat?.primaryWeaponCycle?.weapon;
                var shared = new HashSet<HediffDef>();
                if (record.primaryWeapon != null && plans.TryGetValue(record.primaryWeapon.def, out var primaryPlan))
                {
                    shared.UnionWith(primaryPlan.ManagerDefs);
                    shared.UnionWith(primaryPlan.Effects);
                }
                else if (record.primaryWeapon != null)
                {
                    var primaryExecution = RimKataWeaponExecutionPlans.Get(record.primaryWeapon.def,
                        combat?.primaryWeaponCycle?.boundVerb?.GetType());
                    // An unprepared primary is not proof of absent shared effects. Do not
                    // analyze a failed firing route here or remove its native health state.
                    if (!RimKataWeaponExecutionRuntime.CanPrepareEffects(primaryExecution)
                        || primaryExecution.EffectStatus != RimKataWeaponEffectStatus.NotDetected)
                    {
                        shared.UnionWith(plan.ManagerDefs);
                        shared.UnionWith(plan.Effects);
                    }
                }
                SuspendNativeSecondary(record, shared);
                shared.UnionWith(plan.ManagerDefs);
                shared.UnionWith(plan.Effects);
                RimKataWeaponEffectStats.Capture(record, shared);
                EnsureManagers(record);
            }
        }

        private static void SuspendNativeSecondary(RimKataWeaponEffectState record, HashSet<HediffDef> shared)
        {
            var health = record.pawn.health;
            // Removal callbacks operate on an empty local context, so a secondary manager
            // cannot remove either the native primary's stack or a restored secondary stack.
            var cleanup = new RimKataWeaponEffectState
                { pawn = record.pawn, weapon = record.weapon, plan = record.plan, verb = record.verb };
            cleanup.equipmentView.Add(record.weapon);
            foreach (HediffDef def in record.plan.ManagerDefs)
            {
                if (shared.Contains(def)) continue;
                for (int i = health.hediffSet.hediffs.Count - 1; i >= 0; i--)
                {
                    Hediff manager = health.hediffSet.hediffs[i];
                    if (manager.def != def) continue;
                    if (!record.suspendedManagers.Contains(def)) record.suspendedManagers.Add(def);
                    store.managers[manager] = cleanup;
                    try { using (new Scope(cleanup)) health.RemoveHediff(manager); }
                    finally { store.managers.Remove(manager); }
                }
            }
            foreach (HediffDef def in record.plan.Effects)
            {
                if (shared.Contains(def)) continue;
                for (int i = health.hediffSet.hediffs.Count - 1; i >= 0; i--)
                {
                    Hediff effect = health.hediffSet.hediffs[i];
                    if (effect.def != def) continue;
                    if (!record.effects.Exists(h => h.def == def))
                    {
                        Hediff copy = HediffMaker.MakeHediff(def, record.pawn, effect.Part);
                        copy.Severity = effect.Severity;
                        copy.ageTicks = effect.ageTicks;
                        record.effects.Add(copy);
                    }
                    using (new Scope(null)) health.RemoveHediff(effect);
                }
            }
        }

        private static void EnsureManagers(RimKataWeaponEffectState record)
        {
            var health = record.pawn.health;
            for (int i = record.managers.Count - 1; i >= 0; i--)
            {
                Hediff manager = record.managers[i];
                RimKataWeaponEffectState owner = null;
                if (manager != null) store.managers.TryGetValue(manager, out owner);
                if (manager == null || Array.IndexOf(record.plan.ManagerDefs, manager.def) < 0
                    || (owner != null && owner != record))
                {
                    record.managers.RemoveAt(i);
                    if (manager != null && owner == record) store.managers.Remove(manager);
                }
                else store.managers[manager] = record;
            }
            using (new Scope(record))
                foreach (HediffDef def in record.plan.ManagerDefs)
                {
                    if (record.managers.Exists(h => h.def == def)) continue;
                    // Absence after native removal is state, including across saves and refreshes.
                    if (record.managersInitialized) continue;
                    AddHediffDef(health, def, null, null, null);
                }
            record.managersInitialized = true;
        }

        private static bool CanBind(RimKataWeaponCycleState cycle, out RimKataWeaponEffectPlan plan)
        {
            plan = null;
            return cycle?.weapon != null && cycle.boundVerb != null && cycle.ordinaryWeaponEnabled
                && plans.TryGetValue(cycle.weapon.def, out plan) && cycle.boundVerb.GetType() == plan.VerbType;
        }

        internal static void Tick(Pawn pawn, RimKataPawnCombatState combat)
        {
            var cycle = combat?.secondaryWeaponCycle;
            if (store == null || cycle?.weapon == null || !store.weapons.TryGetValue(cycle.weapon, out var record)
                || record.pawn != pawn || record.combat != combat) return;
            int tick = Find.TickManager.TicksGame;
            if (record.lastTick == tick) return;
            record.lastTick = tick;
            using (new Scope(record))
            {
                TickLocalHealth(record, 0);
                int rate = Math.Min(Math.Max(pawn.UpdateRateTicks, MinInterval(pawn)), MaxInterval(pawn));
                int delta = ++record.intervalDelta;
                if (delta >= rate || GenTicks.IsTickInterval(IntervalOffset(pawn), rate))
                {
                    record.intervalDelta = 0;
                    TickLocalHealth(record, delta);
                }
            }
        }

        private static void TickLocalHealth(RimKataWeaponEffectState record, int interval)
        {
            var buffer = record.tickBuffer;
            buffer.Clear();
            buffer.AddRange(record.managers);
            buffer.AddRange(record.effects);
            foreach (Hediff hediff in buffer)
            {
                if (!record.managers.Contains(hediff) && !record.effects.Contains(hediff)) continue;
                if (interval > 0) { hediff.TickInterval(interval); hediff.PostTickInterval(interval); }
                else { hediff.Tick(); hediff.PostTick(); }
            }
            for (int i = buffer.Count - 1; i >= 0; i--)
            {
                Hediff hediff = buffer[i];
                if ((record.managers.Contains(hediff) || record.effects.Contains(hediff)) && hediff.ShouldRemove)
                    RemoveHediff(record.pawn.health, hediff);
            }
            buffer.Clear();
        }

        internal static bool HasEffects(Verb verb) => verb != null && store != null && store.verbs.ContainsKey(verb);
        internal static bool TryGetStatContext(Verb verb, out RimKataWeaponEffectState record)
        {
            record = null;
            return verb != null && store != null && store.verbs.TryGetValue(verb, out record) && record.plan != null;
        }

        internal static bool TryReadScopedState(Pawn pawn, ThingWithComps weapon, out RimKataRangedAttackState state)
        {
            state = default;
            return current != null && current.pawn == pawn && current.weapon == weapon
                && RimKataRangedAttackStatus.TryReadCycle(pawn, current.cycle, current.combat?.dualCloseCombatActive == true, out state);
        }

        // Only a proven target-change cleanup call site enters this scope. Other manager
        // ticks, expiry, unequipping and combat-end cleanup keep their original behavior.
        internal static TargetChangeScope BeginTargetChange(object manager, Pawn pawn, ThingWithComps weapon, LocalTargetInfo? target)
        {
            TargetCleanup context = default;
            if (TryContinueAutomaticAttack(pawn, weapon, target, out var plan))
                context = new TargetCleanup(manager, pawn, plan, current);
            return new TargetChangeScope(context);
        }

        private static bool TryContinueAutomaticAttack(Pawn pawn, ThingWithComps weapon,
            LocalTargetInfo? target, out RimKataWeaponEffectPlan plan)
        {
            plan = null;
            if (pawn == null || weapon == null || !plans.TryGetValue(weapon.def, out plan)
                || !target.HasValue || !target.Value.IsValid) return false;
            RimKataPawnCombatState combat;
            RimKataWeaponCycleState cycle;
            if (current != null)
            {
                if (current.pawn != pawn || current.weapon != weapon) return false;
                combat = current.combat; cycle = current.cycle;
            }
            else
            {
                // Existing participant lookup, only when the original manager actually changes target.
                if (!RimKataCombatStatePresenceCache.TryGetOwner(pawn, out var owner)) return false;
                combat = owner.GetState(pawn, false);
                cycle = combat?.primaryWeaponCycle;
                if (cycle?.weapon != weapon || pawn.equipment?.Primary != weapon) return false;
            }
            var job = pawn.jobs?.curJob;
            return combat?.dualEngagementActive == true && !combat.temporaryInactive
                && pawn.Spawned && !pawn.Dead && !pawn.Downed && !pawn.InMentalState && !weapon.Destroyed
                && cycle?.weapon == weapon && cycle.ordinaryWeaponEnabled
                && cycle.focusedTarget == null && cycle.ContainsAutomaticCandidate(target.Value.Thing)
                && (job?.playerForced != true || job.targetA != target.Value)
                && RimKataRangedAttackStatus.TryReadCycle(pawn, cycle, combat.dualCloseCombatActive, out var attack)
                && attack.Phase != RimKataRangedAttackPhase.Cooldown && attack.Target == target.Value;
        }

        private static bool Owns(RimKataWeaponEffectState record, HediffDef def)
            => record?.plan != null && (Array.IndexOf(record.plan.Effects, def) >= 0 || Array.IndexOf(record.plan.ManagerDefs, def) >= 0);
        private static bool IsOwner(Pawn_HealthTracker health, HediffDef def) => current != null && current.pawn.health == health && Owns(current, def);

        private static Hediff GetFirstHediffOfDef(HediffSet set, HediffDef def, bool mustBeVisible)
        {
            if (current == null || current.pawn.health.hediffSet != set || !Owns(current, def)) return set.GetFirstHediffOfDef(def, mustBeVisible);
            var list = Array.IndexOf(current.plan.Effects, def) >= 0 ? current.effects : current.managers;
            foreach (Hediff hediff in list)
                if (hediff.def == def && (!mustBeVisible || hediff.Visible)) return hediff;
            return null;
        }

        private static bool HasHediff(HediffSet set, HediffDef def, bool mustBeVisible) => GetFirstHediffOfDef(set, def, mustBeVisible) != null;
        private static bool HasHediffAtPart(HediffSet set, HediffDef def, BodyPartRecord part, bool mustBeVisible)
        {
            if (current == null || current.pawn.health.hediffSet != set || !Owns(current, def)) return set.HasHediff(def, part, mustBeVisible);
            var list = Array.IndexOf(current.plan.Effects, def) >= 0 ? current.effects : current.managers;
            foreach (Hediff hediff in list)
                if (hediff.def == def && hediff.Part == part && (!mustBeVisible || hediff.Visible)) return true;
            return false;
        }

        internal static void AddHediff(Pawn_HealthTracker health, Hediff hediff, BodyPartRecord part, DamageInfo? dinfo, DamageWorker.DamageResult result)
        {
            if (!IsOwner(health, hediff.def)) { health.AddHediff(hediff, part, dinfo, result); return; }
            var record = current;
            bool manager = Array.IndexOf(record.plan.ManagerDefs, hediff.def) >= 0;
            if (manager) store.managers[hediff] = record;
            try { RimKataWeaponEffectHealth.Add(health, hediff, part, dinfo, result, manager ? record.managers : record.effects, manager); }
            finally
            {
                if (manager && !record.managers.Contains(hediff)) store.managers.Remove(hediff);
            }
        }

        private static Hediff AddHediffDef(Pawn_HealthTracker health, HediffDef def, BodyPartRecord part, DamageInfo? dinfo, DamageWorker.DamageResult result)
            => IsOwner(health, def) ? RimKataWeaponEffectHealth.CreateAndAdd(health, def, part, dinfo, result) : health.AddHediff(def, part, dinfo, result);

        private static void RemoveHediff(Pawn_HealthTracker health, Hediff hediff)
        {
            if (hediff == null) return;
            if (targetCleanup.pawn?.health == health && current == targetCleanup.snapshot
                && targetCleanup.plan != null && Array.IndexOf(targetCleanup.plan.Effects, hediff.def) >= 0)
                return;
            if (!IsOwner(health, hediff.def)) { health.RemoveHediff(hediff); return; }
            var record = current;
            bool manager = record.managers.Contains(hediff);
            if (!manager && !record.effects.Contains(hediff)) return;
            try { RimKataWeaponEffectHealth.Remove(health, hediff, manager ? record.managers : record.effects); }
            finally
            {
                if (manager && !record.managers.Contains(hediff)) store.managers.Remove(hediff);
            }
        }

        private static List<ThingWithComps> Equipment(Pawn_EquipmentTracker equipment)
            => current != null && current.pawn.equipment == equipment ? current.equipmentView : equipment.AllEquipmentListForReading;
        private static ThingWithComps Primary(Pawn_EquipmentTracker equipment)
            => current != null && current.pawn.equipment == equipment ? current.weapon : equipment.Primary;
        private static float GetStatValue(Thing thing, StatDef stat, bool applyPostProcess, int cacheStaleAfterTicks)
            => current != null && thing == current.pawn
                ? RimKataWeaponEffectStats.GetStatValue(current.verb, current.pawn, stat, applyPostProcess, cacheStaleAfterTicks)
                : thing.GetStatValue(stat, applyPostProcess, cacheStaleAfterTicks);

        private static IEnumerable<CodeInstruction> RewriteAccess(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod, ILGenerator generator)
        {
            foreach (CodeInstruction instruction in RimKataWeaponEffectLifecycle.Rewrite(instructions, __originalMethod, generator))
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo method && RimKataWeaponEffectDiscovery.IsScopedAccess(method))
                {
                    string hook = null;
                    if (method.DeclaringType == typeof(HediffSet))
                        hook = method.Name == nameof(HediffSet.GetFirstHediffOfDef) ? nameof(GetFirstHediffOfDef)
                            : method.GetParameters().Length == 3 ? nameof(HasHediffAtPart) : nameof(HasHediff);
                    else if (method.DeclaringType == typeof(Pawn_HealthTracker))
                        hook = method.Name == nameof(Pawn_HealthTracker.RemoveHediff) ? nameof(RemoveHediff)
                            : method.GetParameters()[0].ParameterType == typeof(HediffDef) ? nameof(AddHediffDef) : nameof(AddHediff);
                    else if (method.DeclaringType == typeof(Pawn_EquipmentTracker)) hook = method.Name == "get_Primary" ? nameof(Primary) : nameof(Equipment);
                    else if (method.DeclaringType == typeof(StatExtension)) hook = nameof(GetStatValue);
                    if (hook == null) throw new NotSupportedException("Unbound weapon effect access: " + method);
                    yield return new CodeInstruction(instruction) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(RimKataWeaponEffects), hook) };
                }
                else yield return instruction;
            }
        }

        private static void Release(RimKataWeaponEffectState record, bool restoreOriginal)
        {
            var plan = record.plan;
            var managerDefs = new HashSet<HediffDef>(record.suspendedManagers);
            if (!record.secondarySnapshot)
            {
                // Old saves used real-health manager references for both slots. Remove only
                // those references, then let the original equipment manager start afresh.
                if (plan == null) plans.TryGetValue(record.weapon.def, out plan);
                record.plan = plan;
                foreach (Hediff manager in record.managers.ToArray())
                {
                    if (manager?.def == null) continue;
                    managerDefs.Add(manager.def);
                    if (record.pawn.health.hediffSet.hediffs.Contains(manager))
                        using (new Scope(record)) record.pawn.health.RemoveHediff(manager);
                }
            }
            else if (plan != null)
                using (new Scope(record))
                {
                    foreach (Hediff manager in record.managers.ToArray())
                        if (manager != null) RemoveHediff(record.pawn.health, manager);
                    foreach (Hediff effect in record.effects.ToArray()) RemoveHediff(record.pawn.health, effect);
                }
            store.Forget(record);
            if (!restoreOriginal || record.pawn.Dead
                || record.pawn.equipment?.AllEquipmentListForReading.Contains(record.weapon) != true) return;
            foreach (HediffDef def in managerDefs)
                using (new Scope(null))
                    if (def != null && record.pawn.health.hediffSet.GetFirstHediffOfDef(def) == null)
                        record.pawn.health.AddHediff(def);
        }

        internal static void ReleaseWeapon(ThingWithComps weapon)
        {
            if (weapon != null && store != null && store.weapons.TryGetValue(weapon, out var record)) Release(record, true);
        }

        internal static void ReleasePawn(Pawn pawn)
        {
            if (pawn == null || store == null || !store.owners.TryGetValue(pawn, out var records)) return;
            while (records.Count != 0) Release(records[records.Count - 1], true);
        }

        internal static void ResetGame() { store = null; current = null; targetCleanup = default; }
    }
}

