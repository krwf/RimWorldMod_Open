using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Optional sleeve-assisted reloads leave the combat Job and the other weapon alone.
    // All hooks, including the active-request ticker and serialization, require CE.
    internal static class RimKataCombatExtendedAssistedReload
    {
        private delegate bool FindAmmo(ThingComp comp, out Thing ammo);
        private static readonly ConditionalWeakTable<RimKataMapComponent, ReloadQueue> Queues = new();
        private static readonly ConditionalWeakTable<ThingWithComps, ReloadState> Weapons = new();
        private static Type ammoType, reloadDriverType;
        private static Func<ThingComp, bool> hasMagazine, useAmmo, fullMagazine;
        private static Func<ThingComp, int> magazineCount, magazineSize;
        private static Func<ThingComp, ThingDef> currentAmmo, selectedAmmo;
        private static FindAmmo findAmmo;
        private static Action<ThingComp, Thing, bool> loadAmmo;
        private static Action<ThingComp, int> dropCasing;
        private static PropertyInfo props;
        private static FieldInfo reloadTime, oneAtATime, equipmentStats;
        private static MethodInfo primaryProperties;
        private static FieldInfo reloadTimeDef, reloadFactorDef, reloadSpeedDef;
        private static StatDef reloadTimeStat, reloadFactorStat, reloadSpeedStat;

        private sealed class ReloadQueue
        {
            internal List<ReloadState> entries = new();
        }

        private sealed class ReloadState : IExposable
        {
            public ReloadState() { }

            internal Pawn pawn;
            internal ThingWithComps weapon;
            internal int ticksLeft, duration;
            internal bool droppedCasing, singleRound;
            internal ThingComp ammo;
            internal Effecter progressBar;
            internal bool active;
            internal int lastTick = -1;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_References.Look(ref weapon, "weapon");
                Scribe_Values.Look(ref ticksLeft, "ticksLeft");
                Scribe_Values.Look(ref duration, "duration");
                Scribe_Values.Look(ref droppedCasing, "droppedCasing");
                Scribe_Values.Look(ref singleRound, "singleRound");
            }
        }

        internal static void Apply(Harmony harmony, Type compType)
        {
            var installed = new List<(MethodBase target, MethodInfo patch)>();
            try
            {
                Bind(compType);
                Install(AccessTools.Method(compType, "TryStartReload", Type.EmptyTypes), nameof(StartPrefix), false);
                Install(AccessTools.Method(compType, "TryPrepareShot", Type.EmptyTypes), nameof(ShotPrefix), false);
                Install(AccessTools.Method(typeof(RimKataCombatExtendedAmmo), nameof(RimKataCombatExtendedAmmo.EnsureReady)), nameof(ReadyPostfix), true);
                Install(AccessTools.Method(typeof(RimKataMapComponent), nameof(RimKataMapComponent.MapComponentTick)), nameof(TickPostfix), true);
                Install(AccessTools.Method(typeof(RimKataMapComponent), nameof(RimKataMapComponent.ExposeData)), nameof(ExposePostfix), true);
            }
            catch (Exception exception)
            {
                foreach (var patch in installed) harmony.Unpatch(patch.target, patch.patch);
                Log.Warning("[RimKata] CE assisted reload integration could not be applied: " + exception.Message);
            }

            void Install(MethodBase target, string name, bool postfix)
            {
                if (target == null) throw new MissingMethodException("CE assisted reload hook was not found: " + name);
                MethodInfo method = AccessTools.Method(typeof(RimKataCombatExtendedAssistedReload), name);
                installed.Add((target, method));
                var patch = new HarmonyMethod(method);
                harmony.Patch(target, prefix: postfix ? null : patch, postfix: postfix ? patch : null);
            }
        }

        private static void Bind(Type compType)
        {
            ammoType = compType;
            reloadDriverType = AccessTools.TypeByName("CombatExtended.JobDriver_Reload");
            hasMagazine = Getter<bool>(compType, "HasMagazine");
            useAmmo = Getter<bool>(compType, "UseAmmo");
            fullMagazine = Getter<bool>(compType, "FullMagazine");
            magazineCount = Getter<int>(compType, "CurMagCount");
            magazineSize = Getter<int>(compType, "MagSize");
            currentAmmo = Getter<ThingDef>(compType, "CurrentAmmo");
            selectedAmmo = Getter<ThingDef>(compType, "SelectedAmmo");
            props = AccessTools.Property(compType, "Props");
            reloadTime = AccessTools.Field(props.PropertyType, "reloadTime");
            oneAtATime = AccessTools.Field(props.PropertyType, "reloadOneAtATime");
            Type ceUtility = AccessTools.TypeByName("CombatExtended.CE_Utility");
            primaryProperties = AccessTools.Method(ceUtility, "GetPrimaryVerbPropsCE", new[] { typeof(Thing) });
            equipmentStats = AccessTools.Field(primaryProperties.ReturnType, "useEquipmentStatValues");
            Type stats = AccessTools.TypeByName("CombatExtended.CE_StatDefOf");
            // DefOf fields are populated after mod constructors install these hooks.
            reloadTimeDef = AccessTools.Field(stats, "ReloadTime");
            reloadFactorDef = AccessTools.Field(stats, "CE_RangedWeapon_ReloadFactor");
            reloadSpeedDef = AccessTools.Field(stats, "ReloadSpeed");
            if (reloadDriverType == null || reloadTimeDef == null || reloadFactorDef == null || reloadSpeedDef == null
                || reloadTime == null || oneAtATime == null || equipmentStats == null)
                throw new InvalidOperationException("CE reload API does not match the supported shape.");
            var comp = Expression.Parameter(typeof(ThingComp), "comp");
            var instance = Expression.Convert(comp, compType);
            var ammo = Expression.Parameter(typeof(Thing).MakeByRefType(), "ammo");
            findAmmo = Expression.Lambda<FindAmmo>(Expression.Call(instance,
                AccessTools.Method(compType, "TryFindAmmoInInventory", new[] { typeof(Thing).MakeByRefType() }), ammo), comp, ammo).Compile();
            var item = Expression.Parameter(typeof(Thing), "item");
            var empty = Expression.Parameter(typeof(bool), "empty");
            loadAmmo = Expression.Lambda<Action<ThingComp, Thing, bool>>(Expression.Call(instance,
                AccessTools.Method(compType, "LoadAmmo", new[] { typeof(Thing), typeof(bool) }), item, empty), comp, item, empty).Compile();
            var count = Expression.Parameter(typeof(int), "count");
            dropCasing = Expression.Lambda<Action<ThingComp, int>>(Expression.Call(instance,
                AccessTools.Method(compType, "DropCasing", new[] { typeof(int) }), count), comp, count).Compile();

        }

        private static Func<ThingComp, T> Getter<T>(Type type, string name)
        {
            var comp = Expression.Parameter(typeof(ThingComp), "comp");
            return Expression.Lambda<Func<ThingComp, T>>(Expression.Call(Expression.Convert(comp, type),
                AccessTools.PropertyGetter(type, name)), comp).Compile();
        }

        private static bool CanAssist(Pawn pawn, ThingWithComps weapon)
        {
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || pawn.InMentalState
                || pawn.IsBurning() || weapon == null || weapon.Destroyed
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || !RimKataEquipmentUtility.HasEnabledArmor(pawn)
                || !RimKataEquipmentUtility.IsWeaponEnabled(weapon.def)
                || pawn.equipment?.Primary == null
                || weapon.holdingOwner != pawn.equipment.GetDirectlyHeldThings()) return false;
            ThingWithComps secondary = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn);
            return secondary != null && RimKataCombatExtendedCompat.IsHeldSecondary(pawn, secondary)
                && (pawn.equipment.Primary == weapon || secondary == weapon);
        }

        internal static bool IsReloading(ThingWithComps weapon)
            => weapon != null && Weapons.TryGetValue(weapon, out ReloadState state) && state.active;

        private static bool StartPrefix(ThingComp __instance)
        {
            ThingWithComps weapon = __instance.parent;
            Pawn pawn = (weapon.ParentHolder as Pawn_EquipmentTracker)?.pawn;
            if (Weapons.TryGetValue(weapon, out ReloadState running) && running.active)
            {
                if (CanAssist(pawn, weapon) && !AmmoChanged(__instance)) return false;
                // A manual change of ammunition must keep CE's normal unloading path.
                running.active = false;
                running.progressBar?.Cleanup();
                running.progressBar = null;
                Weapons.Remove(weapon);
            }
            if (!hasMagazine(__instance) || magazineCount(__instance) > 0
                || !CanAssist(pawn, weapon)
                || (pawn.CurJobDef != RimKataDefOf.RimKata_Attack
                    && !RimKataDraftedFireController.IsAutomaticFireJob(pawn.CurJobDef))
                || (useAmmo(__instance) && !findAmmo(__instance, out _))) return true;

            var state = new ReloadState { pawn = pawn, weapon = weapon, ammo = __instance,
                singleRound = (bool)oneAtATime.GetValue(props.GetValue(__instance)), active = true,
                lastTick = Find.TickManager.TicksGame };
            state.duration = state.ticksLeft = Duration(state);
            Weapons.Add(weapon, state);
            Queues.GetOrCreateValue(pawn.Map.GetComponent<RimKataMapComponent>()).entries.Add(state);
            MoteMaker.ThrowText(pawn.DrawPos, pawn.Map,
                string.Format("CE_ReloadingMote".Translate().ToString(), weapon.LabelCap));
            return false;
        }

        private static int Duration(ReloadState state)
        {
            reloadTimeStat ??= (StatDef)reloadTimeDef.GetValue(null);
            reloadFactorStat ??= (StatDef)reloadFactorDef.GetValue(null);
            reloadSpeedStat ??= (StatDef)reloadSpeedDef.GetValue(null);
            float seconds = (float)reloadTime.GetValue(props.GetValue(state.ammo));
            object verbProps = primaryProperties.Invoke(null, new object[] { state.weapon });
            if (verbProps != null && (bool)equipmentStats.GetValue(verbProps))
                seconds = state.weapon.GetStatValue(reloadTimeStat);
            return Math.Max(1, Mathf.CeilToInt(seconds.SecondsToTicks()
                * state.weapon.GetStatValue(reloadFactorStat)
                / Math.Max(0.01f, state.pawn.GetStatValue(reloadSpeedStat))));
        }

        private static bool AmmoChanged(ThingComp comp)
            => magazineCount(comp) > 0 && useAmmo(comp) && selectedAmmo(comp) != currentAmmo(comp);

        private static bool ShotPrefix(ThingComp __instance, ref bool __result)
        {
            if (!IsReloading(__instance.parent)) return true;
            __result = false;
            return false;
        }

        private static void ReadyPostfix(Verb verb, ref bool __result)
        {
            if (__result && IsReloading(verb?.EquipmentSource)) __result = false;
        }

        private static void TickPostfix(RimKataMapComponent __instance)
        {
            if (!Queues.TryGetValue(__instance, out ReloadQueue queue)) return;
            int tick = Find.TickManager.TicksGame;
            for (int i = queue.entries.Count - 1; i >= 0; i--)
            {
                ReloadState state = queue.entries[i];
                if (!state.active || !CanAssist(state.pawn, state.weapon)
                    || state.pawn.Map != __instance.map || state.ammo == null
                    || fullMagazine(state.ammo) || AmmoChanged(state.ammo)
                    || reloadDriverType.IsInstanceOfType(state.pawn.jobs?.curDriver))
                {
                    Remove(queue, i);
                    continue;
                }
                UpdateProgressBar(state);
                if (state.lastTick == tick || state.pawn.stances.stunner.Stunned) continue;
                state.lastTick = tick;
                state.ticksLeft--;
                if (!state.droppedCasing && (state.ticksLeft == state.duration - 30 || state.ticksLeft <= 1))
                {
                    dropCasing(state.ammo, magazineSize(state.ammo));
                    state.droppedCasing = true;
                }
                if (state.ticksLeft > 0) continue;
                Thing ammo = null;
                if (useAmmo(state.ammo) && !findAmmo(state.ammo, out ammo))
                {
                    Remove(queue, i);
                    continue;
                }
                loadAmmo(state.ammo, ammo, true);
                if (!state.singleRound)
                {
                    // CE fills a magazine from more than one inventory stack.
                    while (!fullMagazine(state.ammo) && useAmmo(state.ammo) && findAmmo(state.ammo, out ammo))
                    {
                        int before = magazineCount(state.ammo);
                        loadAmmo(state.ammo, ammo, false);
                        if (magazineCount(state.ammo) <= before) break;
                    }
                }
                if (state.singleRound && !fullMagazine(state.ammo)
                    && (!useAmmo(state.ammo) || findAmmo(state.ammo, out _)))
                {
                    state.ticksLeft = state.duration;
                    continue;
                }
                Remove(queue, i);
            }
        }

        private static void UpdateProgressBar(ReloadState state)
        {
            // Match CE's normal Toil bar; the visual follows this slot's timer,
            // without replacing the combat Job or blocking the other weapon.
            if (state.pawn.Faction != Faction.OfPlayer)
            {
                state.progressBar?.Cleanup();
                state.progressBar = null;
                return;
            }
            state.progressBar ??= EffecterDefOf.ProgressBar.Spawn();
            state.progressBar.EffectTick(state.pawn, TargetInfo.Invalid);
            MoteProgressBar mote = ((SubEffecter_ProgressBar)state.progressBar.children[0]).mote;
            if (mote == null) return;
            mote.progress = Mathf.Clamp01(1f - (float)state.ticksLeft / Math.Max(1, state.duration));
            mote.offsetZ = state.weapon == state.pawn.equipment.Primary ? -0.5f : -0.75f;
        }

        private static void Remove(ReloadQueue queue, int index)
        {
            ReloadState state = queue.entries[index];
            state.active = false;
            state.progressBar?.Cleanup();
            state.progressBar = null;
            if (state.weapon != null && Weapons.TryGetValue(state.weapon, out ReloadState current) && current == state)
                Weapons.Remove(state.weapon);
            queue.entries.RemoveAt(index);
        }

        private static void ExposePostfix(RimKataMapComponent __instance)
        {
            ReloadQueue queue = Queues.GetOrCreateValue(__instance);
            Scribe_Collections.Look(ref queue.entries, "rimKataCEAssistedReloads", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            queue.entries ??= new List<ReloadState>();
            for (int i = queue.entries.Count - 1; i >= 0; i--)
            {
                ReloadState state = queue.entries[i];
                if (state?.pawn == null || state.weapon == null || state.weapon.Destroyed
                    || Weapons.TryGetValue(state.weapon, out _))
                {
                    queue.entries.RemoveAt(i);
                    continue;
                }
                state.ammo = RimKataCombatExtendedCompat.FindComp(state.weapon, ammoType);
                state.active = true;
                Weapons.Add(state.weapon, state);
            }
        }
    }
}
