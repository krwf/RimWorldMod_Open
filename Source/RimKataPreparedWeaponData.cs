using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Metadata is separate from the native properties object so the actual
    // VerbProperties subclass and all of its private fields remain intact.
    internal sealed class RimKataPreparedWeaponValues
    {
        internal readonly VerbProperties OriginalProperties;
        internal readonly VerbProperties Properties;
        internal readonly ThingDef OriginalDefinition;
        internal readonly ThingDef Definition;
        internal readonly int VerbIndex;
        internal readonly int ConfigurationRevision;
        internal readonly bool ConvertSingleShot;
        internal readonly int OriginalBurstCount;
        internal readonly int OriginalBurstSpacing;
        internal readonly float OriginalWarmupSeconds;
        internal readonly int TimingBurstCount;
        internal readonly float TotalBurstSpacingTicks;
        internal readonly float ExperienceCycleCorrectionSeconds;

        internal RimKataPreparedWeaponValues(ThingDef originalDefinition, ThingDef definition,
            VerbProperties original, VerbProperties properties, int revision,
            RimKataStoredVerbTiming timing)
        {
            OriginalDefinition = originalDefinition;
            Definition = definition;
            OriginalProperties = original;
            Properties = properties;
            VerbIndex = timing.verbIndex;
            ConfigurationRevision = revision;
            ConvertSingleShot = timing.convertSingleShot;
            OriginalBurstCount = timing.originalBurstCount;
            OriginalBurstSpacing = timing.originalBurstSpacing;
            OriginalWarmupSeconds = timing.originalWarmupSeconds;
            TimingBurstCount = timing.timingBurstCount;
            TotalBurstSpacingTicks = timing.totalBurstSpacingTicks;
            ExperienceCycleCorrectionSeconds = timing.experienceCycleCorrectionSeconds;
        }
    }

    internal sealed class RimKataReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly RimKataReferenceComparer<T> Instance = new RimKataReferenceComparer<T>();
        public bool Equals(T left, T right) => ReferenceEquals(left, right);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }

    internal static class RimKataPreparedWeaponData
    {
        private static readonly AccessTools.FieldRef<Verb, int?> CachedBurstShotCount =
            AccessTools.FieldRefAccess<Verb, int?>("cachedBurstShotCount");
        private static readonly AccessTools.FieldRef<Verb, int?> CachedTicksBetweenBurstShots =
            AccessTools.FieldRefAccess<Verb, int?>("cachedTicksBetweenBurstShots");
        private static readonly AccessTools.FieldRef<ThingDef, List<VerbProperties>> DefinitionVerbs =
            AccessTools.FieldRefAccess<ThingDef, List<VerbProperties>>("verbs");
        private static readonly MethodInfo MemberwiseCloneMethod =
            AccessTools.Method(typeof(object), "MemberwiseClone");
        private static readonly Dictionary<VerbProperties, RimKataPreparedWeaponValues> PreparedDefinitions =
            new Dictionary<VerbProperties, RimKataPreparedWeaponValues>(RimKataReferenceComparer<VerbProperties>.Instance);
        private static readonly Dictionary<VerbProperties, List<RimKataPreparedWeaponValues>> PreparedVariants =
            new Dictionary<VerbProperties, List<RimKataPreparedWeaponValues>>(RimKataReferenceComparer<VerbProperties>.Instance);
        // Old bindings remain recognizable until restored. Weak keys let obsolete
        // revisions disappear without retaining every old definition copy.
        private static readonly ConditionalWeakTable<VerbProperties, RimKataPreparedWeaponValues> BoundProperties =
            new ConditionalWeakTable<VerbProperties, RimKataPreparedWeaponValues>();
        private static int preparedRevision = int.MinValue;

        // Only startup and settings changes perform definition preparation/disk
        // access. Bind never loads files or refreshes the definition collection.
        internal static void RefreshDefinitions()
        {
            PreparedDefinitions.Clear();
            PreparedVariants.Clear();
            preparedRevision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            List<string> selected = RimKataMod.Settings?.enabledWeaponDefNames;
            if (selected == null)
            {
                return;
            }

            RimKataAllowedWeaponStore.RemoveDisallowedWeapons(selected);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < selected.Count; i++)
            {
                string defName = selected[i];
                if (defName.NullOrEmpty() || !seen.Add(defName))
                {
                    continue;
                }
                ThingDef originalDefinition = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (originalDefinition == null)
                {
                    continue;
                }

                RimKataAllowedWeaponDef stored = RimKataAllowedWeaponStore.LoadOrCreate(originalDefinition);
                List<VerbProperties> originalVerbs = originalDefinition.Verbs;
                if (stored == null || originalVerbs == null)
                {
                    continue;
                }
                ThingDef preparedDefinition = null;
                for (int j = 0; j < originalVerbs.Count; j++)
                {
                    VerbProperties original = originalVerbs[j];
                    RimKataStoredVerbTiming timing = stored.convertedVerbs[j];
                    if (original == null || !timing.convertSingleShot)
                    {
                        continue;
                    }
                    if (preparedDefinition == null)
                    {
                        preparedDefinition = CloneDefinition(originalDefinition);
                    }
                    VerbProperties properties = CloneProperties(original, timing);
                    DefinitionVerbs(preparedDefinition)[j] = properties;
                    RimKataPreparedWeaponValues values = new RimKataPreparedWeaponValues(
                        originalDefinition, preparedDefinition, original, properties, preparedRevision, timing);
                    PreparedDefinitions[original] = values;
                    BoundProperties.Add(properties, values);
                }
            }
        }

        internal static RimKataPreparedWeaponValues Bind(Verb verb)
        {
            if (verb?.verbProps == null)
            {
                return null;
            }
            bool enabled = ConversionEnabled(verb);
            int revision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues current))
            {
                if (enabled && current.ConfigurationRevision == revision)
                {
                    return current;
                }
                Restore(verb);
            }

            // OFF and definitions which were already single-shot use exactly the
            // original object and native getter caches. Never prepare on demand.
            if (!enabled || preparedRevision != revision
                || !PreparedDefinitions.TryGetValue(verb.verbProps, out RimKataPreparedWeaponValues prepared))
            {
                return null;
            }
            int burstCount = Mathf.Max(1, verb.BurstShotCount);
            if (burstCount <= 1)
            {
                return null;
            }
            int burstSpacing = Mathf.Max(0, verb.TicksBetweenBurstShots);
            prepared = GetRuntimeVariant(prepared, burstCount, burstSpacing);
            verb.verbProps = prepared.Properties;
            // Retain native unique-weapon inputs in metadata, then prevent the
            // getters from applying the same traits to the prepared copy again.
            CachedBurstShotCount(verb) = 1;
            CachedTicksBetweenBurstShots(verb) = prepared.OriginalBurstSpacing;
            return prepared;
        }

        internal static bool TryGetPrepared(Verb verb, out RimKataPreparedWeaponValues prepared)
        {
            prepared = null;
            return verb?.verbProps != null && BoundProperties.TryGetValue(verb.verbProps, out prepared);
        }

        // Planning also queries timing before a verb is bound. Consult the same
        // prepared collection without performing conversion or definition I/O.
        internal static bool CanPrepareSingleShot(Verb verb)
        {
            if (verb?.verbProps == null || !ConversionEnabled(verb))
            {
                return false;
            }
            int revision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
            {
                return prepared.ConfigurationRevision == revision;
            }
            return preparedRevision == revision && PreparedDefinitions.ContainsKey(verb.verbProps)
                && verb.BurstShotCount > 1;
        }

        internal static bool IsCurrent(Verb verb)
        {
            if (verb?.verbProps == null)
            {
                return false;
            }
            bool enabled = ConversionEnabled(verb);
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
            {
                return enabled
                    && prepared.ConfigurationRevision == RimKataEquipmentUtility.WeaponConfigurationRevision;
            }
            // An original with no prepared conversion remains a valid native
            // attack, including while a new settings revision is not prepared.
            // Only an installed, obsolete conversion must be restored/rebound.
            return !enabled || preparedRevision != RimKataEquipmentUtility.WeaponConfigurationRevision
                || !PreparedDefinitions.ContainsKey(verb.verbProps) || verb.BurstShotCount <= 1;
        }

        internal static void Restore(Verb verb)
        {
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
            {
                verb.verbProps = prepared.OriginalProperties;
                CachedBurstShotCount(verb) = null;
                CachedTicksBetweenBurstShots(verb) = null;
            }
        }

        internal static int GetOriginalBurstCount(Verb verb)
        {
            return TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared)
                ? prepared.OriginalBurstCount : Mathf.Max(1, verb?.BurstShotCount ?? 1);
        }

        internal static int GetOriginalBurstSpacing(Verb verb)
        {
            return TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared)
                ? prepared.OriginalBurstSpacing : Mathf.Max(0, verb?.TicksBetweenBurstShots ?? 0);
        }

        internal static bool AdjustShootingExperienceCycleTime(Verb verb, ref float cycleSeconds)
        {
            if (!TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
            {
                return false;
            }
            cycleSeconds = (cycleSeconds + prepared.ExperienceCycleCorrectionSeconds)
                / prepared.OriginalBurstCount;
            return true;
        }

        private static bool ConversionEnabled(Verb verb)
        {
            return RimKataTargetAccess.SettingsFor(verb.CasterPawn)?.singleShotConversionEnabled != false;
        }

        private static ThingDef CloneDefinition(ThingDef original)
        {
            ThingDef copy = (ThingDef)MemberwiseCloneMethod.Invoke(original, null);
            DefinitionVerbs(copy) = new List<VerbProperties>(original.Verbs);
            return copy;
        }

        private static VerbProperties CloneProperties(VerbProperties original, RimKataStoredVerbTiming timing)
        {
            VerbProperties copy = (VerbProperties)MemberwiseCloneMethod.Invoke(original, null);
            copy.burstShotCount = timing.convertedBurstCount;
            copy.ticksBetweenBurstShots = timing.convertedBurstSpacing;
            copy.warmupTime = timing.convertedWarmupSeconds;
            return copy;
        }

        private static RimKataPreparedWeaponValues GetRuntimeVariant(
            RimKataPreparedWeaponValues prepared, int burstCount, int burstSpacing)
        {
            if (prepared.OriginalBurstCount == burstCount && prepared.OriginalBurstSpacing == burstSpacing)
            {
                return prepared;
            }
            if (!PreparedVariants.TryGetValue(prepared.OriginalProperties, out List<RimKataPreparedWeaponValues> variants))
            {
                variants = new List<RimKataPreparedWeaponValues>();
                PreparedVariants.Add(prepared.OriginalProperties, variants);
            }
            for (int i = 0; i < variants.Count; i++)
            {
                RimKataPreparedWeaponValues variant = variants[i];
                if (variant.OriginalBurstCount == burstCount && variant.OriginalBurstSpacing == burstSpacing)
                {
                    return variant;
                }
            }

            // Instance traits are unavailable during XML load. Compute each
            // actual count/spacing combination once per definition revision.
            RimKataStoredVerbTiming timing = RimKataStoredVerbTiming.Create(
                prepared.OriginalProperties, prepared.VerbIndex, burstCount, burstSpacing);
            VerbProperties properties = CloneProperties(prepared.OriginalProperties, timing);
            ThingDef definition = CloneDefinition(prepared.Definition);
            DefinitionVerbs(definition)[prepared.VerbIndex] = properties;
            RimKataPreparedWeaponValues values = new RimKataPreparedWeaponValues(
                prepared.OriginalDefinition, definition, prepared.OriginalProperties,
                properties, preparedRevision, timing);
            variants.Add(values);
            BoundProperties.Add(properties, values);
            return values;
        }
    }
}
