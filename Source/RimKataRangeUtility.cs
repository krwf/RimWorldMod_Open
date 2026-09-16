using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public readonly struct RimKataRangeBands
    {
        public readonly float Touch;
        public readonly float Short;
        public readonly float Medium;
        public readonly float Long;

        public RimKataRangeBands(
            float touchRange,
            float shortRange,
            float mediumRange,
            float longRange)
        {
            Touch = touchRange;
            Short = shortRange;
            Medium = mediumRange;
            Long = longRange;
        }
    }

    public static class RimKataRangeUtility
    {
        // Settings resolve to a logical limit capped by effective weapon range.
        // Radial cell queries use this extra envelope around that limit.
        internal const float CandidateCellRadiusPadding = 0.7f;

        private sealed class CachedWeaponRange
        {
            public ThingWithComps weapon;
            public Verb verb;
            public Map map;
            public RimKataMapComponent mapComponent;
            public int weatherRevision;
            public float effectiveRange;
            public bool valid;

            public void AssignWeapon(ThingWithComps assignedWeapon)
            {
                weapon = assignedWeapon;
                verb = null;
                map = null;
                mapComponent = null;
                weatherRevision = 0;
                effectiveRange = 0f;
                valid = false;
            }
        }

        private sealed class PawnWeaponRangeCache
        {
            private readonly CachedWeaponRange first = new CachedWeaponRange();
            private readonly CachedWeaponRange second = new CachedWeaponRange();
            private bool replaceFirst;

            public CachedWeaponRange EntryFor(ThingWithComps weapon)
            {
                if (ReferenceEquals(first.weapon, weapon))
                {
                    return first;
                }

                if (ReferenceEquals(second.weapon, weapon))
                {
                    return second;
                }

                if (first.weapon == null)
                {
                    first.AssignWeapon(weapon);
                    return first;
                }

                if (second.weapon == null)
                {
                    second.AssignWeapon(weapon);
                    return second;
                }

                CachedWeaponRange replacement = replaceFirst ? first : second;
                replaceFirst = !replaceFirst;
                replacement.AssignWeapon(weapon);
                return replacement;
            }
        }

        private const float ProbeLow = 0.2f;
        private const float ProbeHigh = 0.8f;
        private const float ProbeTolerance = 0.000001f;
        private const float ProbeSearchLimit = 4096f;
        private static readonly ConditionalWeakTable<Pawn, PawnWeaponRangeCache>
            WeaponRangeCaches =
                new ConditionalWeakTable<Pawn, PawnWeaponRangeCache>();
        private static readonly ConditionalWeakTable<Pawn, PawnWeaponRangeCache>
            .CreateValueCallback CreateWeaponRangeCache =
                delegate { return new PawnWeaponRangeCache(); };
        private static bool runtimeBandsAvailable;
        private static readonly RimKataRangeBands CachedBands = DetectRuntimeBands();

        public static RimKataRangeBands CurrentBands
        {
            get => CachedBands;
        }

        public static float ResolveCandidateCellRadius(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb)
        {
            float effectiveWeaponRange = ResolveEffectiveRange(
                pawn,
                weapon,
                verb);
            float logicalCandidateRange = ResolveLogicalCandidateRange(
                effectiveWeaponRange, pawn);
            return verb?.IsMeleeAttack == false
                ? ApplyCandidateCellRadiusPadding(
                    effectiveWeaponRange,
                    logicalCandidateRange)
                : logicalCandidateRange;
        }

        internal static float ApplyCandidateCellRadiusPadding(
            float effectiveWeaponRange,
            float logicalCandidateRange)
        {
            if (effectiveWeaponRange <= 0f || logicalCandidateRange <= 0f)
            {
                return 0f;
            }

            return Mathf.Min(
                effectiveWeaponRange,
                logicalCandidateRange + CandidateCellRadiusPadding);
        }

        public static float ResolveEffectiveRange(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb)
        {
            if (verb == null)
            {
                return 0f;
            }

            if (ShouldAlwaysResample(verb)
                || pawn?.Map == null
                || weapon == null
                || !ReferenceEquals(verb.Caster, pawn)
                || !ReferenceEquals(verb.EquipmentSource, weapon))
            {
                return SampleEffectiveRange(verb);
            }

            Map map = pawn.Map;
            PawnWeaponRangeCache pawnCache = WeaponRangeCaches.GetValue(
                pawn,
                CreateWeaponRangeCache);
            CachedWeaponRange cached = pawnCache.EntryFor(weapon);
            RimKataMapComponent mapComponent =
                ReferenceEquals(cached.map, map)
                    ? cached.mapComponent
                    : map.GetComponent<RimKataMapComponent>();
            if (mapComponent == null)
            {
                return SampleEffectiveRange(verb);
            }

            int weatherRevision = mapComponent.WeatherRangeRevision;
            if (cached.valid
                && ReferenceEquals(cached.verb, verb)
                && ReferenceEquals(cached.map, map)
                && ReferenceEquals(cached.mapComponent, mapComponent)
                && cached.weatherRevision == weatherRevision)
            {
                return cached.effectiveRange;
            }

            float effectiveRange = SampleEffectiveRange(verb);
            cached.verb = verb;
            cached.map = map;
            cached.mapComponent = mapComponent;
            cached.weatherRevision = weatherRevision;
            cached.effectiveRange = effectiveRange;
            cached.valid = true;
            return effectiveRange;
        }

        public static void InvalidateWeaponRanges(Pawn pawn)
        {
            if (pawn != null)
            {
                WeaponRangeCaches.Remove(pawn);
            }
        }

        private static bool ShouldAlwaysResample(Verb verb)
        {
            return verb.verbProps?.rangeStat != null
                || verb.GetType().Assembly != typeof(Verb).Assembly;
        }

        private static float SampleEffectiveRange(Verb verb)
        {
            return Mathf.Max(0f, verb?.EffectiveRange ?? 0f);
        }

        internal static float ResolveLogicalCandidateRange(
            float effectiveWeaponRange,
            Pawn pawn = null)
        {
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(pawn);
            if (settings == null)
            {
                return RuntimeBandsAvailable
                    ? Mathf.Min(effectiveWeaponRange, CurrentBands.Short)
                    : effectiveWeaponRange;
            }

            if (!RuntimeBandsAvailable
                && (settings.candidateRangeMode != RimKataCandidateRangeMode.Custom
                    || settings.customCandidateRange <= 0f))
            {
                return effectiveWeaponRange;
            }

            float configuredRange;
            RimKataRangeBands bands = CurrentBands;
            switch (settings.candidateRangeMode)
            {
                case RimKataCandidateRangeMode.Medium:
                    configuredRange = bands.Medium;
                    break;
                case RimKataCandidateRangeMode.Long:
                    configuredRange = bands.Long;
                    break;
                case RimKataCandidateRangeMode.Unlimited:
                    configuredRange = effectiveWeaponRange;
                    break;
                case RimKataCandidateRangeMode.Custom:
                    configuredRange = settings.customCandidateRange > 0f
                        ? settings.customCandidateRange
                        : bands.Short;
                    break;
                default:
                    configuredRange = bands.Short;
                    break;
            }

            return Mathf.Min(
                effectiveWeaponRange,
                Mathf.Max(0f, configuredRange));
        }

        public static float PresetRange(RimKataCandidateRangeMode mode)
        {
            RimKataRangeBands bands = CurrentBands;
            switch (mode)
            {
                case RimKataCandidateRangeMode.Medium:
                    return bands.Medium;
                case RimKataCandidateRangeMode.Long:
                    return bands.Long;
                default:
                    return bands.Short;
            }
        }

        public static bool RuntimeBandsAvailable
        {
            get
            {
                _ = CurrentBands;
                return runtimeBandsAvailable;
            }
        }

        private static RimKataRangeBands DetectRuntimeBands()
        {
            try
            {
                VerbProperties touchShortProbe = CreateProbe(
                    ProbeLow,
                    ProbeHigh,
                    ProbeHigh,
                    ProbeHigh);
                VerbProperties shortMediumProbe = CreateProbe(ProbeLow, ProbeLow, ProbeHigh, ProbeHigh);
                VerbProperties mediumLongProbe = CreateProbe(ProbeLow, ProbeLow, ProbeLow, ProbeHigh);
                float touchRange = FindLowPlateauEnd(touchShortProbe);
                float verifiedShortRange = FindHighPlateauStart(touchShortProbe);
                float shortRange = FindLowPlateauEnd(shortMediumProbe);
                float mediumRange = FindHighPlateauStart(shortMediumProbe);
                float verifiedMediumRange = FindLowPlateauEnd(mediumLongProbe);
                float longRange = FindHighPlateauStart(mediumLongProbe);
                touchRange = NormalizeBoundary(touchRange);
                verifiedShortRange = NormalizeBoundary(verifiedShortRange);
                shortRange = NormalizeBoundary(shortRange);
                mediumRange = NormalizeBoundary(mediumRange);
                verifiedMediumRange = NormalizeBoundary(verifiedMediumRange);
                longRange = NormalizeBoundary(longRange);
                if (IsValidBands(
                    touchRange,
                    verifiedShortRange,
                    shortRange,
                    mediumRange,
                    verifiedMediumRange,
                    longRange))
                {
                    runtimeBandsAvailable = true;
                    return new RimKataRangeBands(
                        touchRange,
                        shortRange,
                        mediumRange,
                        longRange);
                }

                Log.Warning("[RimKata] Active shooting range bands failed validation; presets will use each weapon's effective range.");
            }
            catch (Exception exception)
            {
                Log.Warning("[RimKata] Could not read the active shooting range bands; presets will use each weapon's effective range. " + exception.Message);
            }

            runtimeBandsAvailable = false;
            return new RimKataRangeBands(
                ProbeSearchLimit,
                ProbeSearchLimit,
                ProbeSearchLimit,
                ProbeSearchLimit);
        }

        private static VerbProperties CreateProbe(float touch, float shortAccuracy, float mediumAccuracy, float longAccuracy)
        {
            return new VerbProperties
            {
                accuracyTouch = touch,
                accuracyShort = shortAccuracy,
                accuracyMedium = mediumAccuracy,
                accuracyLong = longAccuracy
            };
        }

        private static float FindLowPlateauEnd(VerbProperties probe)
        {
            return FindBoundary(probe, value => value <= ProbeLow + ProbeTolerance);
        }

        private static float FindHighPlateauStart(VerbProperties probe)
        {
            return FindBoundary(probe, value => value < ProbeHigh - ProbeTolerance);
        }

        private static float FindBoundary(VerbProperties probe, Func<float, bool> remainsBeforeBoundary)
        {
            float low = 0f;
            float high = 1f;
            while (high < ProbeSearchLimit)
            {
                float value = probe.GetHitChanceFactor(null, high);
                if (!IsFinite(value))
                {
                    throw new InvalidOperationException("The shooting range curve returned a non-finite value.");
                }

                if (!remainsBeforeBoundary(value))
                {
                    break;
                }

                low = high;
                high *= 2f;
            }

            if (high >= ProbeSearchLimit && remainsBeforeBoundary(probe.GetHitChanceFactor(null, high)))
            {
                throw new InvalidOperationException("The shooting range curve has no detectable boundary.");
            }

            for (int i = 0; i < 24; i++)
            {
                float middle = (low + high) * 0.5f;
                float value = probe.GetHitChanceFactor(null, middle);
                if (!IsFinite(value))
                {
                    throw new InvalidOperationException("The shooting range curve returned a non-finite value.");
                }

                if (remainsBeforeBoundary(value))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return high;
        }

        private static bool IsValidBands(
            float touchRange,
            float verifiedShortRange,
            float shortRange,
            float mediumRange,
            float verifiedMediumRange,
            float longRange)
        {
            return IsFinite(touchRange)
                && IsFinite(verifiedShortRange)
                && IsFinite(shortRange)
                && IsFinite(mediumRange)
                && IsFinite(verifiedMediumRange)
                && IsFinite(longRange)
                && touchRange > 0f
                && touchRange < shortRange
                && Mathf.Abs(shortRange - verifiedShortRange) <= 0.01f
                && shortRange < mediumRange
                && Mathf.Abs(mediumRange - verifiedMediumRange) <= 0.01f
                && mediumRange < longRange;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }


        private static float NormalizeBoundary(float value)
        {
            return Mathf.Round(value * 1000f) / 1000f;
        }
    }

    [HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.TransitionTo), new[] { typeof(WeatherDef) })]
    public static class Patch_WeatherManager_RimKataRangeRevision
    {
        public static void Postfix(WeatherManager __instance)
        {
            __instance?.map?.GetComponent<RimKataMapComponent>()?
                .RefreshWeatherRangeRevision(true);
        }
    }
}
