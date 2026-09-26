using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Only a retained-gun pawn's path-start event creates an entry. Native VerbTick
    // owns every burst; this scheduler starts the next native aiming cycle only.
    internal sealed class RimKataCrawlFireEntry : IAttackTargetSearcher
    {
        internal RimKataDownedWeaponState record;
        internal RimKataCrawlFireMapComponent owner;
        internal Verb verb;
        internal Thing target;
        internal bool casting;
        internal bool starting;
        internal bool fired;
        internal bool removed;
        internal int nextSearchTick;
        internal int cooldownTicks;

        public Thing Thing => record.pawn;
        public Verb CurrentEffectiveVerb => verb;
        public LocalTargetInfo LastAttackedTarget => record.pawn.LastAttackedTarget;
        public int LastAttackTargetTick => record.pawn.LastAttackTargetTick;
    }

    public sealed class RimKataCrawlFireMapComponent : MapComponent
    {
        private readonly List<RimKataCrawlFireEntry> moving = new List<RimKataCrawlFireEntry>();

        public RimKataCrawlFireMapComponent(Map map) : base(map) { }

        internal void Add(RimKataCrawlFireEntry entry) => moving.Add(entry);

        public override void MapComponentTick()
        {
            // Empty maps do not query a pawn, a setting or a combat condition.
            for (int i = moving.Count - 1; i >= 0; i--)
            {
                RimKataCrawlFireEntry entry = moving[i];
                if (!entry.removed) RimKataCrawlFireUtility.Tick(entry);
                if (entry.removed) moving.RemoveAt(i);
            }
        }

        public override void MapRemoved()
        {
            for (int i = moving.Count - 1; i >= 0; i--)
                RimKataCrawlFireUtility.NotifyPathStopped(moving[i].record.pawn);
            moving.Clear();
            RimKataCrawlFireUtility.ForgetMap(map);
            base.MapRemoved();
        }
    }

    public sealed class Stance_RimKataCrawlWarmup : Stance_Warmup
    {
        public Stance_RimKataCrawlWarmup() { }
        public Stance_RimKataCrawlWarmup(int ticks, LocalTargetInfo target, Verb verb)
            : base(ticks, target, verb) { }

        public override bool StanceBusy => false;

        public override void StanceTick()
        {
            if (!RimKataCrawlFireUtility.CanContinue(verb))
            {
                RimKataCrawlFireUtility.CancelCast(verb);
                if (stanceTracker.curStance == this) Interrupt();
                return;
            }
            base.StanceTick();
        }
    }

    internal static class RimKataCrawlFireUtility
    {
        internal static Func<Verb, RimKataPreparedWeaponValues, float> ResolveOriginalWarmup;
        private sealed class Aim
        {
            internal ThingWithComps weapon;
            internal LocalTargetInfo target;
        }

        private static readonly Dictionary<Pawn, RimKataDownedWeaponState> Retained =
            new Dictionary<Pawn, RimKataDownedWeaponState>();
        private static readonly Dictionary<Pawn, RimKataCrawlFireEntry> Moving =
            new Dictionary<Pawn, RimKataCrawlFireEntry>();
        private static readonly Dictionary<Verb, RimKataCrawlFireEntry> Verbs =
            new Dictionary<Verb, RimKataCrawlFireEntry>(RimKataReferenceComparer<Verb>.Instance);
        private static readonly HashSet<ThingWithComps> Weapons = new HashSet<ThingWithComps>();
        private static readonly ConcurrentDictionary<Pawn, Aim> Aims = new ConcurrentDictionary<Pawn, Aim>();
        private static volatile bool hasAims;
        private static readonly Dictionary<Map, HashSet<Pawn>> FireOverrides = new Dictionary<Map, HashSet<Pawn>>();

        internal static void ClearRuntime()
        {
            RimKataCrawlFireHits.ClearRuntime();
            foreach (RimKataCrawlFireEntry entry in Moving.Values) entry.removed = true;
            Retained.Clear();
            Moving.Clear();
            Verbs.Clear();
            Weapons.Clear();
            Aims.Clear();
            hasAims = false;
            FireOverrides.Clear();
        }

        internal static bool IsCrawlVerb(Verb verb) =>
            Verbs.Count != 0 && verb != null && Verbs.ContainsKey(verb);

        internal static bool CanStartCast(Verb verb) =>
            verb != null && Verbs.TryGetValue(verb, out var entry) && entry.starting;

        internal static bool IsCrawlWeapon(ThingWithComps weapon) =>
            Weapons.Count != 0 && weapon != null && Weapons.Contains(weapon);

        internal static bool TryGetAim(Pawn pawn, out ThingWithComps weapon, out LocalTargetInfo target)
        {
            weapon = null;
            target = LocalTargetInfo.Invalid;
            if (!hasAims || pawn == null || !Aims.TryGetValue(pawn, out Aim aim)) return false;
            weapon = aim.weapon;
            target = aim.target;
            return true;
        }

        internal static bool TryGetCooldownIndicator(Pawn pawn, out Verb verb,
            out LocalTargetInfo target, out int ticks)
        {
            verb = null;
            target = LocalTargetInfo.Invalid;
            ticks = 0;
            if (!hasAims || pawn == null || !Moving.TryGetValue(pawn, out var entry)
                || entry.removed || entry.casting || !entry.fired || entry.verb.Bursting
                || !Aims.TryGetValue(pawn, out Aim aim)
                || pawn.stances?.curStance is Stance_Cooldown) return false;
            ticks = entry.record.nextFireTick - Find.TickManager.TicksGame;
            if (ticks <= 0) return false;
            verb = entry.verb;
            target = aim.target;
            return true;
        }

        internal static void NotifyRetained(Pawn pawn)
        {
            if (!RimKataDownedWeaponUtility.TryGet(pawn, out RimKataDownedWeaponState record)) return;
            NotifyReleased(pawn);
            // Finish the upright controller's ownership before binding the same
            // native Verb here. Its ordinary downed rejection remains unchanged.
            if (RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner))
            {
                RimKataPawnCombatState state = owner.GetState(pawn, false);
                state?.CancelDraftedFire(false);
                state?.CancelWeaponCycles();
            }
            Verb verb = record.weapon.TryGetComp<CompEquippable>()?.PrimaryVerb;
            if (verb != null)
            {
                // A saved native burst cannot resume without its movement gate.
                if (verb.state == VerbState.Bursting)
                    record.nextFireTick = Math.Max(record.nextFireTick,
                        Find.TickManager.TicksGame + NativeCooldown(pawn, verb));
                if (pawn.stances?.curStance is Stance_Warmup warmup && warmup.verb == verb)
                    warmup.Interrupt();
                verb.Reset();
                RimKataPreparedWeaponData.Restore(verb);
            }
            Retained[pawn] = record;
            Weapons.Add(record.weapon);
            TrackFireOverride(record);
            NotifyPathStarted(pawn);
        }

        internal static void NotifyReleased(Pawn pawn)
        {
            if (pawn == null) return;
            NotifyPathStopped(pawn);
            if (Retained.TryGetValue(pawn, out RimKataDownedWeaponState record))
            {
                Weapons.Remove(record.weapon);
                Retained.Remove(pawn);
            }
            foreach (HashSet<Pawn> pawns in FireOverrides.Values) pawns.Remove(pawn);
        }

        internal static void NotifySettingsChanged()
        {
            // Settings/profile edits are events, not a reason to watch every pawn.
            foreach (Pawn pawn in new List<Pawn>(Retained.Keys))
            {
                if (Retained.TryGetValue(pawn, out var record)) TrackFireOverride(record);
                NotifyPathStopped(pawn);
                NotifyPathStarted(pawn);
            }
        }

        internal static void NotifyPathStarted(Pawn pawn)
        {
            if (Retained.Count == 0 || pawn == null || Moving.ContainsKey(pawn)
                || !Retained.TryGetValue(pawn, out RimKataDownedWeaponState record)
                || !record.activeDowned || !record.fireAllowed || !pawn.Spawned || !pawn.Downed
                || pawn.pather?.Moving != true || !pawn.Crawling
                || RimKataTargetAccess.SettingsFor(pawn)?.crawlFireEnabled != true
                || !RimKataGroundPoseHead.Supports(pawn))
                return;
            Verb verb = record.weapon.TryGetComp<CompEquippable>()?.PrimaryVerb;
            if (verb == null || verb.IsMeleeAttack) return;
            var entry = new RimKataCrawlFireEntry
            {
                record = record,
                verb = verb,
                owner = pawn.Map.GetComponent<RimKataCrawlFireMapComponent>()
            };
            Moving[pawn] = entry;
            Verbs[verb] = entry;
            entry.owner.Add(entry);
        }

        internal static void NotifyPathStopped(Pawn pawn)
        {
            if (pawn == null || Moving.Count == 0 || !Moving.TryGetValue(pawn, out var entry)) return;
            Cancel(entry);
            entry.removed = true;
            Moving.Remove(pawn);
            Verbs.Remove(entry.verb);
            RimKataPreparedWeaponData.Restore(entry.verb);
        }

        private static bool ActuallyMoving(Pawn pawn, bool firing = false) =>
            pawn.Spawned && !pawn.Dead && pawn.Downed && pawn.Crawling
            && pawn.pather?.MovingNow == true
            && pawn.pather.LastMovedTick >= Find.TickManager.TicksGame - (firing ? 0 : 1)
            && pawn.stances?.stunner.Stunned != true;

        internal static bool CanContinue(Verb verb) =>
            verb != null && Verbs.TryGetValue(verb, out var entry)
            && !entry.removed && entry.casting && entry.record.activeDowned && entry.record.fireAllowed
            && !entry.record.weapon.Destroyed && ActuallyMoving(entry.record.pawn, firing: true)
            && ValidTarget(entry, entry.target);

        private static bool ValidTarget(RimKataCrawlFireEntry entry, Thing target) =>
            target != null && target.Spawned && !target.Destroyed
            && target.Map == entry.record.pawn.Map
            && RimKataTargeting.IsAutomaticEnemy(entry.record.pawn, target)
            && (!(target is Pawn targetPawn) || RimKataTargeting.IsPawnTargetStateValid(targetPawn));

        internal static void Tick(RimKataCrawlFireEntry entry)
        {
            Pawn pawn = entry.record.pawn;
            if (!entry.record.activeDowned || !pawn.Spawned || !pawn.Downed || pawn.Dead
                || entry.record.weapon.Destroyed || pawn.pather?.Moving != true || !pawn.Crawling)
            {
                NotifyPathStopped(pawn);
                return;
            }
            if (!entry.record.fireAllowed || !ActuallyMoving(pawn, firing: true)
                || (pawn.stances.curStance.StanceBusy
                    && !(pawn.stances.curStance is Stance_RimKataCrawlWarmup)))
            {
                Cancel(entry);
                return;
            }
            if (entry.casting)
            {
                if (!ValidTarget(entry, entry.target)) Cancel(entry);
                else if (entry.verb.state == VerbState.Bursting
                    || pawn.stances.curStance is Stance_RimKataCrawlWarmup)
                    return;
                else FinishBurst(entry); // A native warmup was interrupted.
            }
            int now = Find.TickManager.TicksGame;
            if (now < entry.record.nextFireTick || now < entry.nextSearchTick) return;
            if (RimKataTargetAccess.SettingsFor(pawn)?.crawlFireEnabled != true)
            {
                NotifyPathStopped(pawn);
                return;
            }
            RimKataPreparedWeaponData.Bind(entry.verb);
            if (!entry.verb.Available() || entry.verb.ApparelPreventsShooting())
            {
                Cancel(entry);
                entry.nextSearchTick = now + 30;
                return;
            }
            if (!ValidTarget(entry, entry.target) || !entry.verb.CanHitTarget(entry.target))
            {
                IAttackTarget target = AttackTargetFinder.BestShootTargetFromCurrentPosition(entry,
                    TargetScanFlags.NeedLOSToAll | TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable,
                    thing => ValidTarget(entry, thing) && entry.verb.CanHitTarget(thing));
                entry.target = target?.Thing;
            }
            if (entry.target == null)
            {
                ClearAim(pawn);
                entry.nextSearchTick = now + 30;
                return;
            }
            entry.cooldownTicks = NativeCooldown(pawn, entry.verb);
            entry.casting = true;
            entry.fired = false;
            Aims[pawn] = new Aim { weapon = entry.record.weapon, target = entry.target };
            hasAims = true;
            bool started;
            entry.starting = true;
            try
            {
                started = entry.verb.TryStartCastOn(entry.target, LocalTargetInfo.Invalid,
                    false, true, false, true);
            }
            finally { entry.starting = false; }
            if (!started)
            {
                Cancel(entry);
                entry.nextSearchTick = now + 30;
            }
        }

        private static int NativeCooldown(Pawn pawn, Verb verb)
        {
            float cooldown = verb.verbProps.AdjustedCooldownTicks(verb, pawn);
            if (!RimKataPreparedWeaponData.TryGetPrepared(verb, out var prepared))
                return Mathf.Max(1, Mathf.RoundToInt(cooldown));
            // Only the stored single-shot conversion is applied. Armor, response,
            // moving accuracy and other RimKata combat multipliers are absent.
            float warmup = (ResolveOriginalWarmup?.Invoke(verb, prepared) ?? prepared.OriginalWarmupSeconds)
                * pawn.GetStatValue(StatDefOf.AimingDelayFactor) * 60f;
            return ConvertedCooldown(cooldown, warmup, prepared.TotalBurstSpacingTicks,
                prepared.TimingBurstCount);
        }

        internal static int ConvertedCooldown(float cooldown, float warmup, float spacing, int burst)
        {
            burst = Math.Max(1, burst);
            return Mathf.Max(1, Mathf.RoundToInt((warmup + cooldown + spacing) / burst)
                - Mathf.Max(0, Mathf.RoundToInt(warmup / burst)));
        }

        internal static void NotifyShot(Verb verb)
        {
            if (verb == null || Verbs.Count == 0 || !Verbs.TryGetValue(verb, out var entry)) return;
            entry.fired = true;
            // Preserve earned cooldown even if the crawl stops during a burst.
            entry.record.nextFireTick = Math.Max(entry.record.nextFireTick,
                Find.TickManager.TicksGame + Math.Max(1, entry.cooldownTicks));
        }

        internal static void NotifyBurstStep(Verb verb)
        {
            if (verb != null && Verbs.TryGetValue(verb, out var entry)
                && verb.state != VerbState.Bursting) FinishBurst(entry);
        }

        private static void FinishBurst(RimKataCrawlFireEntry entry)
        {
            entry.casting = false;
            if (!entry.fired)
            {
                ClearAim(entry.record.pawn);
                entry.nextSearchTick = Find.TickManager.TicksGame + 30;
            }
        }

        internal static void CancelCast(Verb verb)
        {
            if (verb != null && Verbs.TryGetValue(verb, out var entry)) Cancel(entry);
        }

        private static void Cancel(RimKataCrawlFireEntry entry)
        {
            Pawn pawn = entry.record.pawn;
            ClearAim(pawn);
            if (pawn.stances?.curStance is Stance_RimKataCrawlWarmup warmup && warmup.verb == entry.verb)
                warmup.Interrupt();
            if (entry.casting || entry.verb.state == VerbState.Bursting) entry.verb.Reset();
            entry.casting = false;
            entry.target = null;
        }

        private static void ClearAim(Pawn pawn)
        {
            if (Aims.TryRemove(pawn, out _) && Aims.IsEmpty) hasAims = false;
        }

        internal static void NotifyHit(Thing victim, DamageInfo info, DamageWorker.DamageResult result)
        {
            if (Retained.Count == 0 || !(victim is Pawn) || !(info.Instigator is Pawn shooter)
                || !Retained.TryGetValue(shooter, out var record) || record.hasHitWhileCrawling
                || info.Def?.isRanged != true || info.Weapon != record.weapon.def
                || result == null || (result.totalDamageDealt <= 0f && result.hitThing != victim)
                || !ActuallyMoving(shooter)
                || !RimKataCrawlFireHits.IsTrackedHit(record, victim))
                return;
            record.hasHitWhileCrawling = true;
        }

        internal static bool IsThreateningCrawler(Pawn pawn) =>
            Retained.Count != 0 && Retained.TryGetValue(pawn, out var record)
            && record.activeDowned && record.hasHitWhileCrawling && !record.weapon.Destroyed
            && ActuallyMoving(pawn);

        internal static IEnumerable<Gizmo> AddGizmos(Pawn pawn, IEnumerable<Gizmo> original)
        {
            foreach (Gizmo gizmo in original) yield return gizmo;
            if (Retained.Count == 0 || !Retained.TryGetValue(pawn, out var record)
                || !pawn.Downed || pawn.Faction != Faction.OfPlayer || pawn.IsPrisoner)
                yield break;
            yield return new Command_Toggle
            {
                defaultLabel = "CommandFireAtWillLabel".Translate(),
                defaultDesc = "CommandFireAtWillDesc".Translate(),
                icon = TexCommand.FireAtWill,
                isActive = () => record.fireAllowed,
                toggleAction = () =>
                {
                    record.fireAllowed = !record.fireAllowed;
                    TrackFireOverride(record);
                    if (!record.fireAllowed) NotifyPathStopped(pawn);
                    if (record.fireAllowed) NotifyPathStarted(pawn);
                }
            };
        }

        private static void TrackFireOverride(RimKataDownedWeaponState record)
        {
            Map map = record.pawn.Map;
            if (map == null) return;
            bool overridden = record.fireAllowed != (RimKataTargetAccess.SettingsFor(record.pawn)
                ?.crawlFireDefaultAllowed ?? RimKataSettings.DefaultCrawlFireDefaultAllowed);
            if (!FireOverrides.TryGetValue(map, out HashSet<Pawn> pawns))
            {
                if (!overridden) return;
                FireOverrides[map] = pawns = new HashSet<Pawn>();
            }
            if (overridden) pawns.Add(record.pawn);
            else pawns.Remove(record.pawn);
        }

        internal static void NotifyCombatEnded(Map map)
        {
            if (!FireOverrides.TryGetValue(map, out HashSet<Pawn> pawns)) return;
            FireOverrides.Remove(map);
            foreach (Pawn pawn in pawns)
                if (Retained.TryGetValue(pawn, out var record))
                {
                    record.fireAllowed = RimKataTargetAccess.SettingsFor(pawn)?.crawlFireDefaultAllowed
                        ?? RimKataSettings.DefaultCrawlFireDefaultAllowed;
                    if (record.fireAllowed) NotifyPathStarted(pawn);
                    else NotifyPathStopped(pawn);
                }
        }

        internal static void ForgetMap(Map map) => FireOverrides.Remove(map);
    }

    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    internal static class Patch_Verb_RimKataCrawlBurst
    {
        private static bool Prefix(Verb __instance)
        {
            if (!RimKataCrawlFireUtility.IsCrawlVerb(__instance)
                || RimKataCrawlFireUtility.CanContinue(__instance)) return true;
            RimKataCrawlFireUtility.CancelCast(__instance);
            return false;
        }

        private static void Postfix(Verb __instance) => RimKataCrawlFireUtility.NotifyBurstStep(__instance);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Notify_UsedVerb))]
    internal static class Patch_Pawn_RimKataCrawlShot
    {
        private static void Postfix(Verb __1) => RimKataCrawlFireUtility.NotifyShot(__1);
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class Patch_Thing_RimKataCrawlHit
    {
        private static void Postfix(Thing __instance, DamageInfo dinfo, DamageWorker.DamageResult __result)
            => RimKataCrawlFireUtility.NotifyHit(__instance, dinfo, __result);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.CanAttackWhileCrawling), MethodType.Getter)]
    internal static class Patch_Pawn_RimKataCrawlingThreat
    {
        private static void Postfix(Pawn __instance, ref bool __result)
        {
            if (!__result) __result = RimKataCrawlFireUtility.IsThreateningCrawler(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_Pawn_RimKataCrawlFireGizmo
    {
        private static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
            => __result = RimKataCrawlFireUtility.AddGizmos(__instance, __result);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    internal static class Patch_Pawn_RimKataCrawlFireDespawn
    {
        private static void Prefix(Pawn __instance) => RimKataCrawlFireUtility.NotifyReleased(__instance);
    }
}
