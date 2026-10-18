using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Separate metadata preserves custom VerbProperties subclasses and private fields.
    internal sealed class RimKataPreparedWeaponValues
    {
        internal readonly VerbProperties OriginalProperties;
        internal readonly VerbProperties Properties;
        internal readonly ThingDef OriginalDefinition;
        internal readonly ThingDef Definition;
        internal readonly int VerbIndex;
        internal readonly int ConfigurationRevision;
        internal readonly bool ConvertSingleShot;
        internal int OriginalBurstCount;
        internal int OriginalBurstSpacing;
        internal float OriginalWarmupSeconds;
        internal int TimingBurstCount;
        internal float TotalBurstSpacingTicks;
        internal float ExperienceCycleCorrectionSeconds;
        internal readonly bool DynamicTiming;

        internal RimKataPreparedWeaponValues(ThingDef originalDefinition, ThingDef definition,
            VerbProperties original, VerbProperties properties, int revision,
            RimKataStoredVerbTiming timing, bool dynamicTiming = false)
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
            DynamicTiming = dynamicTiming;
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
        private static readonly ConditionalWeakTable<VerbProperties, RimKataPreparedWeaponValues> BoundProperties =
            new ConditionalWeakTable<VerbProperties, RimKataPreparedWeaponValues>();
        private static readonly ConditionalWeakTable<Verb, RimKataPreparedWeaponValues> DynamicValues =
            new ConditionalWeakTable<Verb, RimKataPreparedWeaponValues>();
        private static int preparedRevision = int.MinValue;

        internal static void RefreshDefinitions()
        {
            PreparedDefinitions.Clear();
            PreparedVariants.Clear();
            bool refreshCloseFireMelee = RimKataCloseFireMeleeStore.BeginPreparation();
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

                RimKataCloseFireMeleeStore.PrepareDefinition(originalDefinition, refreshCloseFireMelee);
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
                    if (original == null || original.IsMeleeAttack)
                    {
                        continue;
                    }
                    if (!timing.convertSingleShot)
                    {
                        // A custom mode can supply a burst even when the XML default is one shot.
                        // Keep an original template; do not clone data for an unused conversion.
                        PreparedDefinitions[original] = new RimKataPreparedWeaponValues(originalDefinition,
                            originalDefinition, original, original, preparedRevision, timing);
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
            RimKataWeaponExecutionPlan plan = RimKataWeaponExecutionRuntime.Bind(verb);
            bool enabled = ConversionEnabled(verb, plan);
            bool dynamic = UsesDynamicTiming(plan);
            int revision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues current))
            {
                // Keep the same properties and timing throughout a native request. A mode change
                // is sampled when the existing cycle starts its next warmup, never mid-burst.
                if (RimKataNativeAttack.HasPendingRequest(verb)) return current;
                if (enabled && current.ConfigurationRevision == revision && !dynamic
                    && RimKataCombatExtendedFire.PreparedModeIsCurrent(verb, current))
                {
                    return current;
                }
                Restore(verb);
            }

            if (!enabled || preparedRevision != revision
                || !PreparedDefinitions.TryGetValue(verb.verbProps, out RimKataPreparedWeaponValues prepared))
            {
                return null;
            }
            int burstCount;
            int burstSpacing;
            float warmup = 0f;
            if (dynamic)
                ReadOriginalTiming(plan, verb, out burstCount, out burstSpacing, out warmup);
            else
            {
                burstCount = RimKataCombatExtendedFire.OriginalBurstCount(verb);
                burstSpacing = Mathf.Max(0, verb.TicksBetweenBurstShots);
            }
            if (burstCount <= 1)
            {
                return null;
            }
            prepared = dynamic ? GetDynamicValues(verb, prepared, burstCount, burstSpacing, warmup)
                : GetRuntimeVariant(prepared, burstCount, burstSpacing);
            verb.verbProps = prepared.Properties;
            // Native getters would otherwise apply unique-weapon traits to the converted copy again.
            CachedBurstShotCount(verb) = 1;
            CachedTicksBetweenBurstShots(verb) = prepared.OriginalBurstSpacing;
            return prepared;
        }

        internal static bool TryGetPrepared(Verb verb, out RimKataPreparedWeaponValues prepared)
        {
            prepared = null;
            return verb?.verbProps != null && BoundProperties.TryGetValue(verb.verbProps, out prepared);
        }

        internal static bool CanPrepareSingleShot(Verb verb)
        {
            RimKataWeaponExecutionPlan plan = RimKataWeaponExecutionRuntime.GetPlan(verb);
            if (verb?.verbProps == null || !ConversionEnabled(verb, plan))
            {
                return false;
            }
            int revision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
            {
                return prepared.ConfigurationRevision == revision;
            }
            return preparedRevision == revision && PreparedDefinitions.ContainsKey(verb.verbProps)
                && ReadOriginalBurstCount(plan, verb) > 1;
        }

        internal static bool IsCurrent(Verb verb)
        {
            if (verb?.verbProps == null)
            {
                return false;
            }
            RimKataWeaponExecutionPlan plan = RimKataWeaponExecutionRuntime.GetPlan(verb);
            bool enabled = ConversionEnabled(verb, plan);
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
            {
                return enabled
                    && prepared.ConfigurationRevision == RimKataEquipmentUtility.WeaponConfigurationRevision
                    && (RimKataNativeAttack.HasPendingRequest(verb)
                        || RimKataCombatExtendedFire.PreparedModeIsCurrent(verb, prepared));
            }
            return !enabled || preparedRevision != RimKataEquipmentUtility.WeaponConfigurationRevision
                || !PreparedDefinitions.ContainsKey(verb.verbProps)
                || ReadOriginalBurstCount(plan, verb) <= 1;
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
                ? prepared.OriginalBurstCount
                : ReadOriginalBurstCount(RimKataWeaponExecutionRuntime.GetPlan(verb), verb);
        }

        internal static int GetOriginalBurstSpacing(Verb verb)
        {
            return TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared)
                ? prepared.OriginalBurstSpacing
                : verb == null ? 0 : RimKataWeaponExecutionRuntime.ReadBurstSpacing(
                    RimKataWeaponExecutionRuntime.GetPlan(verb), verb);
        }

        internal static float GetOriginalWarmup(Verb verb)
        {
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues prepared))
                return prepared.DynamicTiming ? prepared.OriginalWarmupSeconds
                    : RimKataCombatExtendedFire.OriginalWarmup(verb, prepared);
            return verb == null ? 0f : RimKataWeaponExecutionRuntime.ReadWarmup(
                RimKataWeaponExecutionRuntime.GetPlan(verb), verb);
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

        private static bool ConversionEnabled(Verb verb, RimKataWeaponExecutionPlan plan)
        {
            return (plan == null || plan.AllowConversion)
                && RimKataTargetAccess.SettingsFor(verb.CasterPawn)?.singleShotConversionEnabled != false;
        }

        private static bool UsesDynamicTiming(RimKataWeaponExecutionPlan plan)
            => plan != null && !plan.HandledByCompatibility && plan.Mode != RimKataWeaponExecutionMode.Vanilla;

        private static int ReadOriginalBurstCount(RimKataWeaponExecutionPlan plan, Verb verb)
            => verb == null ? 1 : plan == null || plan.HandledByCompatibility
                ? RimKataCombatExtendedFire.OriginalBurstCount(verb)
                : RimKataWeaponExecutionRuntime.ReadBurstCount(plan, verb);

        private static void ReadOriginalTiming(RimKataWeaponExecutionPlan plan, Verb verb,
            out int count, out int spacing, out float warmup)
        {
            VerbProperties properties = verb.verbProps;
            int? cachedCount = CachedBurstShotCount(verb);
            int? cachedSpacing = CachedTicksBetweenBurstShots(verb);
            if (TryGetPrepared(verb, out RimKataPreparedWeaponValues previous))
                verb.verbProps = previous.OriginalProperties;
            CachedBurstShotCount(verb) = null;
            CachedTicksBetweenBurstShots(verb) = null;
            try
            {
                count = Mathf.Max(1, RimKataWeaponExecutionRuntime.ReadBurstCount(plan, verb));
                spacing = Mathf.Max(0, RimKataWeaponExecutionRuntime.ReadBurstSpacing(plan, verb));
                warmup = RimKataWeaponExecutionRuntime.ReadWarmup(plan, verb);
                warmup = float.IsNaN(warmup) || float.IsInfinity(warmup) ? 0f : Mathf.Max(0f, warmup);
            }
            finally
            {
                verb.verbProps = properties;
                CachedBurstShotCount(verb) = cachedCount;
                CachedTicksBetweenBurstShots(verb) = cachedSpacing;
            }
        }

        private static RimKataPreparedWeaponValues GetDynamicValues(Verb verb,
            RimKataPreparedWeaponValues template, int count, int spacing, float warmup)
        {
            if (!DynamicValues.TryGetValue(verb, out RimKataPreparedWeaponValues values)
                || values.ConfigurationRevision != preparedRevision
                || !ReferenceEquals(values.OriginalProperties, template.OriginalProperties))
            {
                RimKataStoredVerbTiming timing = RimKataStoredVerbTiming.Create(
                    template.OriginalProperties, template.VerbIndex, count, spacing);
                VerbProperties properties = CloneProperties(template.OriginalProperties, timing);
                ThingDef definition = CloneDefinition(template.OriginalDefinition);
                DefinitionVerbs(definition)[template.VerbIndex] = properties;
                values = new RimKataPreparedWeaponValues(template.OriginalDefinition, definition,
                    template.OriginalProperties, properties, preparedRevision, timing, dynamicTiming: true);
                DynamicValues.Remove(verb);
                DynamicValues.Add(verb, values);
                BoundProperties.Add(properties, values);
            }
            // One mutable snapshot belongs to this Verb only. Continuously changing Hediff
            // warmup values must not append a fresh shared variant on every attack.
            values.OriginalBurstCount = values.TimingBurstCount = count;
            values.OriginalBurstSpacing = spacing;
            values.OriginalWarmupSeconds = warmup;
            values.TotalBurstSpacingTicks = (count - 1f) * spacing;
            values.ExperienceCycleCorrectionSeconds = warmup - warmup / count
                + values.TotalBurstSpacingTicks / 60f;
            values.Properties.ticksBetweenBurstShots = spacing;
            // This is still raw XML warmup: custom getters may apply their own factors.
            values.Properties.warmupTime = template.OriginalProperties.warmupTime / count;
            return values;
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

            // Weapon-instance traits are unavailable during XML load.
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
