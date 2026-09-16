using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public enum RimKataCandidateRangeMode
    {
        Short,
        Medium,
        Long,
        Unlimited,
        Custom
    }

    public enum RimKataCreepJoinerGeneChoice
    {
        MindNumbSerumDependency,
        RimKata
    }

    public sealed class RimKataSettingsProfile : IExposable
    {
        private static readonly FieldInfo[] ProfileFields = typeof(RimKataSettings)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.DeclaringType == typeof(RimKataSettings)
                && IsSupportedType(field.FieldType)
                && !IsSharedSetting(field.Name))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
        private static readonly Dictionary<string, FieldInfo> ProfileFieldsByName =
            ProfileFields.ToDictionary(field => field.Name, StringComparer.Ordinal);

        private List<string> entries = new List<string>();

        internal List<string> CopyEntries()
        {
            return entries == null ? new List<string>() : new List<string>(entries);
        }

        internal static RimKataSettingsProfile FromEntries(IEnumerable<string> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            RimKataSettingsProfile profile = new RimKataSettingsProfile();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in source)
            {
                int separator = entry?.IndexOf('=') ?? -1;
                if (separator <= 0)
                {
                    throw new FormatException("Invalid profile setting: " + entry);
                }

                string name = entry.Substring(0, separator).Trim();
                if (!names.Add(name))
                {
                    throw new FormatException("Duplicate profile setting: " + name);
                }

                if (!ProfileFieldsByName.TryGetValue(name, out FieldInfo field))
                {
                    continue;
                }

                if (!TryDeserialize(entry.Substring(separator + 1).Trim(), field.FieldType, out object value)
                    || (value is float number && (float.IsNaN(number) || float.IsInfinity(number))))
                {
                    throw new FormatException("Invalid value for profile setting: " + name);
                }

                profile.entries.Add(name + "=" + Serialize(value, field.FieldType));
            }

            profile.entries.Sort(StringComparer.Ordinal);
            return profile;
        }

        public static RimKataSettingsProfile Capture(RimKataSettings settings)
        {
            RimKataSettingsProfile profile = new RimKataSettingsProfile();
            if (settings == null)
            {
                return profile;
            }

            for (int i = 0; i < ProfileFields.Length; i++)
            {
                FieldInfo field = ProfileFields[i];
                profile.entries.Add(field.Name + "=" + Serialize(field.GetValue(settings), field.FieldType));
            }

            return profile;
        }

        internal bool Matches(RimKataSettings settings)
        {
            if (settings == null
                || entries == null
                || entries.Count != ProfileFields.Length)
            {
                return false;
            }

            for (int i = 0; i < ProfileFields.Length; i++)
            {
                FieldInfo field = ProfileFields[i];
                string currentEntry = field.Name + "="
                    + Serialize(field.GetValue(settings), field.FieldType);
                if (!string.Equals(entries[i], currentEntry, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public void ApplyTo(RimKataSettings settings)
        {
            if (settings == null || entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                string entry = entries[i];
                int separator = entry?.IndexOf('=') ?? -1;
                if (separator <= 0
                    || !ProfileFieldsByName.TryGetValue(entry.Substring(0, separator), out FieldInfo field)
                    || !TryDeserialize(entry.Substring(separator + 1), field.FieldType, out object value))
                {
                    continue;
                }

                field.SetValue(settings, value);
            }

            settings.SanitizeModFeatures();
        }

        public void FillMissingFrom(RimKataSettingsProfile defaults)
        {
            entries ??= new List<string>();
            if (defaults?.entries == null)
            {
                return;
            }

            HashSet<string> existingNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                int separator = entries[i]?.IndexOf('=') ?? -1;
                if (separator > 0)
                {
                    existingNames.Add(entries[i].Substring(0, separator));
                }
            }

            for (int i = 0; i < defaults.entries.Count; i++)
            {
                string entry = defaults.entries[i];
                int separator = entry?.IndexOf('=') ?? -1;
                if (separator > 0 && existingNames.Add(entry.Substring(0, separator)))
                {
                    entries.Add(entry);
                }
            }

            entries.Sort(StringComparer.Ordinal);
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref entries, "entries", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                entries ??= new List<string>();
                entries.RemoveAll(entry =>
                {
                    int separator = entry?.IndexOf('=') ?? -1;
                    return separator <= 0 || !ProfileFieldsByName.ContainsKey(entry.Substring(0, separator));
                });
            }
        }

        private static bool IsSupportedType(Type type)
        {
            return type == typeof(float)
                || type == typeof(int)
                || type == typeof(bool)
                || type.IsEnum;
        }

        private static bool IsSharedSetting(string name)
        {
            return name == nameof(RimKataSettings.enableFriendlyPawnEffects)
                || name == nameof(RimKataSettings.enableHostilePawnEffects)
                || name == nameof(RimKataSettings.creepJoinerDependencyGeneChancePercent)
                || name == nameof(RimKataSettings.creepJoinerRimKataGeneChancePercent)
                || name == nameof(RimKataSettings.enableRimKataA)
                || name == nameof(RimKataSettings.enableRimKataP)
                || name == nameof(RimKataSettings.enableRimKataI)
                || name == nameof(RimKataSettings.enableRimKataG)
                || name == nameof(RimKataSettings.enableSerumDependency)
                || name == nameof(RimKataSettings.aiSecondaryWeaponChancePercent);
        }

        private static string Serialize(object value, Type type)
        {
            if (type == typeof(float))
            {
                return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            }

            if (type == typeof(bool))
            {
                return (bool)value ? "1" : "0";
            }

            if (type.IsEnum)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool TryDeserialize(string text, Type type, out object value)
        {
            if (type == typeof(float)
                && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
            {
                value = floatValue;
                return true;
            }

            if (type == typeof(int)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
            {
                value = intValue;
                return true;
            }

            if (type == typeof(bool) && (text == "0" || text == "1"))
            {
                value = text == "1";
                return true;
            }

            if (type.IsEnum
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int enumValue)
                && Enum.IsDefined(type, enumValue))
            {
                value = Enum.ToObject(type, enumValue);
                return true;
            }

            value = null;
            return false;
        }
    }

    public sealed class RimKataSettings : ModSettings
    {
        private const int CurrentSettingsModelVersion = 1;
        private int settingsModelVersion = CurrentSettingsModelVersion;
        private const int CurrentCombatDefaultsVersion = 1;
        private int combatDefaultsVersion = CurrentCombatDefaultsVersion;
        private bool opProfileActive;
        private RimKataSettingsProfile opProfile;
        private RimKataSettingsProfile normalProfileBackup;
        private string activeProfileId;
        private bool profileSavePending;
        private const float LegacyRangedDodgeChancePercent = 25f;
        private const float LegacyMeleeResponseChancePercent = 25f;
        private const float LegacyMeleeDodgeChancePercent = 25f;
        private const float LegacyInterceptionChancePercent = 30f;
        private const float LegacyInterceptionCriticalChancePercent = 10f;
        private const float LegacyMovingAccuracyMultiplierPercent = 100f;
        private const float LegacyArmorCooldownReductionPercent = 50f;
        private const int LegacyRangedDodgeDurationTicks = 30;
        private const float LegacyResponseCooldownReductionPercent = 70f;
        private const float LegacySerumMultiplierPercent = 100f;

        public const float DefaultRangedDodgeChancePercent = 85f;
        public const float DefaultRangedDodgeChanceGrowthPerLevelPercent = 2f;
        public const float DefaultRangedDodgeChanceMinimumPercent = 20f;
        public const bool DefaultRangedDodgeChanceFixed = false;

        public const float DefaultMeleeResponseChancePercent = 85f;
        public const float DefaultMeleeResponseChanceGrowthPerLevelPercent = 5f;
        public const float DefaultMeleeResponseChanceMinimumPercent = 0f;
        public const bool DefaultMeleeResponseChanceFixed = false;

        public const float DefaultMeleeDodgeChancePercent = 40f;
        public const float DefaultMeleeDodgeChanceGrowthPerLevelPercent = 3f;
        public const float DefaultMeleeDodgeChanceMinimumPercent = 0f;
        public const bool DefaultMeleeDodgeChanceFixed = false;

        public const float DefaultInterceptionChancePercent = 50f;
        public const float DefaultInterceptionChanceGrowthPerLevelPercent = 3f;
        public const float DefaultInterceptionChanceMinimumPercent = 10f;
        public const bool DefaultInterceptionChanceFixed = false;

        public const float DefaultInterceptionCriticalChancePercent = 70f;
        public const float DefaultInterceptionCriticalChanceGrowthPerLevelPercent = 3f;
        public const float DefaultInterceptionCriticalChanceMinimumPercent = 30f;
        public const bool DefaultInterceptionCriticalChanceFixed = false;

        public const float DefaultMovingAccuracyMultiplierPercent = 90f;
        public const float DefaultMovingAccuracyMultiplierGrowthPerLevelPercent = 3f;
        public const float DefaultMovingAccuracyMultiplierMinimumPercent = 40f;
        public const bool DefaultMovingAccuracyMultiplierFixed = false;

        public const float DefaultArmorCooldownReductionPercent = 30f;
        public const float DefaultArmorCooldownReductionGrowthPerLevelPercent = 2f;
        public const float DefaultArmorCooldownReductionMinimumPercent = 10f;
        public const bool DefaultArmorCooldownReductionFixed = false;

        public const int DefaultRangedDodgeDurationTicks = 30;
        public const float DefaultRangedDodgeDurationGrowthPerLevelTicks = 1f;
        public const int DefaultRangedDodgeDurationBaseTicks = 40;
        public const bool DefaultRangedDodgeDurationFixed = true;
        public const int MinimumRangedDodgeDurationTicks = 1;
        public const int MaximumRangedDodgeDurationTicks = 600;

        public const RimKataCandidateRangeMode DefaultCandidateRangeMode = RimKataCandidateRangeMode.Unlimited;
        public const float DefaultCustomCandidateRange = 0f;
        public const float MinimumCustomCandidateRange = 1f;
        public const float MaximumCustomCandidateRange = 999f;
        public const int MinimumCandidateLimit = 1;
        public const int MaximumCandidateLimit = 999;
        public const int DefaultTouchCandidateLimit = 8;
        public const int DefaultShortCandidateLimit = 16;
        public const int DefaultMediumCandidateLimit = 12;
        public const int DefaultLongCandidateLimit = 8;
        public const int DefaultBeyondCandidateLimit = 4;
        public const float DefaultImmediateTumbleChancePercent = 3f;
        public const float DefaultResponseAttackerSpinChancePercent = 20f;
        public const float DefaultUnarmedWeaponStealChancePercent = 3f;
        public const float DefaultResponseWeaponDurabilityLossChancePercent = 0f;
        public const int DefaultResponseWeaponDurabilityLossAmount = 1;
        public const int MinimumResponseWeaponDurabilityLossAmount = 1;
        public const int MaximumResponseWeaponDurabilityLossAmount = 999;
        public const float DefaultCreepJoinerDependencyGeneChancePercent = 0f;
        public const RimKataCreepJoinerGeneChoice DefaultCreepJoinerGeneChoice = RimKataCreepJoinerGeneChoice.RimKata;
        public const float DefaultAiSecondaryWeaponChancePercent = 0f;

        public const float DefaultResponseDisarmChancePercent = 3f;
        public const float DefaultResponseDisarmChanceGrowthPerLevelPercent = 0.1f;
        public const float DefaultResponseDisarmChanceMinimumPercent = 2f;
        public const bool DefaultResponseDisarmChanceFixed = false;

        public const float DefaultResponseAccidentalFireChancePercent = 20f;

        public const float DefaultResponseCooldownReductionPercent = 70f;
        public const float DefaultResponseCooldownReductionGrowthPerLevelPercent = 3f;
        public const float DefaultResponseCooldownReductionMinimumPercent = 20f;
        public const bool DefaultResponseCooldownReductionFixed = false;

        public const float DefaultSerumDodgeMultiplierPercent = 120f;
        public const float DefaultSerumResponseMultiplierPercent = 120f;
        public const float DefaultSerumInterceptionMultiplierPercent = 120f;
        public const float DefaultSerumMultiplierPercent = DefaultSerumDodgeMultiplierPercent;
        public const float DefaultSerumDodgeMultiplierGrowthPerLevelPercent = 3f;
        public const float DefaultSerumDodgeMultiplierMinimumPercent = 100f;
        public const bool DefaultSerumDodgeMultiplierFixed = true;
        public const float DefaultSerumResponseMultiplierGrowthPerLevelPercent = 3f;
        public const float DefaultSerumResponseMultiplierMinimumPercent = 100f;
        public const bool DefaultSerumResponseMultiplierFixed = true;
        public const float DefaultSerumInterceptionMultiplierGrowthPerLevelPercent = 3f;
        public const float DefaultSerumInterceptionMultiplierMinimumPercent = 100f;
        public const bool DefaultSerumInterceptionMultiplierFixed = true;

        public const bool DefaultSecondaryWeaponEnabled = true;
        public const bool DefaultSingleShotConversionEnabled = true;
        public const bool DefaultRandomAttackEnabled = true;
        public const bool DefaultExplosiveInterceptionEnabled = true;
        public const bool DefaultMovingFireEnabled = true;
        public const bool DefaultCloseFireEnabled = true;
        public const bool DefaultTargetRushEnabled = true;
        public const bool DefaultResponseEnabled = true;
        public const bool DefaultRangedDodgeEnabled = true;
        public const bool DefaultTumbleEnabled = true;
        public const bool DefaultAccessRestrictionsDisabled = false;

        public static readonly string[] DefaultEnabledWeaponDefNames =
        {
            "Gun_Revolver",
            "Gun_Autopistol",
            "Gun_MachinePistol",
            "Gun_Revolver_Unique"
        };

        public static readonly string[] DefaultEnabledArmorDefNames =
        {
            "Apparel_CollarShirt",
            "Apparel_ShirtRuffle",
            "Apparel_PsyfocusShirt"
        };

        public float rangedDodgeChancePercent = DefaultRangedDodgeChancePercent;
        public float rangedDodgeChanceGrowthPerLevelPercent = DefaultRangedDodgeChanceGrowthPerLevelPercent;
        public float rangedDodgeChanceMinimumPercent = DefaultRangedDodgeChanceMinimumPercent;
        public bool rangedDodgeChanceFixed = DefaultRangedDodgeChanceFixed;

        public float meleeResponseChancePercent = DefaultMeleeResponseChancePercent;
        public float meleeResponseChanceGrowthPerLevelPercent = DefaultMeleeResponseChanceGrowthPerLevelPercent;
        public float meleeResponseChanceMinimumPercent = DefaultMeleeResponseChanceMinimumPercent;
        public bool meleeResponseChanceFixed = DefaultMeleeResponseChanceFixed;

        public float meleeDodgeChancePercent = DefaultMeleeDodgeChancePercent;
        public float meleeDodgeChanceGrowthPerLevelPercent = DefaultMeleeDodgeChanceGrowthPerLevelPercent;
        public float meleeDodgeChanceMinimumPercent = DefaultMeleeDodgeChanceMinimumPercent;
        public bool meleeDodgeChanceFixed = DefaultMeleeDodgeChanceFixed;

        public float interceptionChancePercent = DefaultInterceptionChancePercent;
        public float interceptionChanceGrowthPerLevelPercent = DefaultInterceptionChanceGrowthPerLevelPercent;
        public float interceptionChanceMinimumPercent = DefaultInterceptionChanceMinimumPercent;
        public bool interceptionChanceFixed = DefaultInterceptionChanceFixed;

        public float interceptionCriticalChancePercent = DefaultInterceptionCriticalChancePercent;
        public float interceptionCriticalChanceGrowthPerLevelPercent = DefaultInterceptionCriticalChanceGrowthPerLevelPercent;
        public float interceptionCriticalChanceMinimumPercent = DefaultInterceptionCriticalChanceMinimumPercent;
        public bool interceptionCriticalChanceFixed = DefaultInterceptionCriticalChanceFixed;

        public float movingAccuracyMultiplierPercent = DefaultMovingAccuracyMultiplierPercent;
        public float movingAccuracyMultiplierGrowthPerLevelPercent = DefaultMovingAccuracyMultiplierGrowthPerLevelPercent;
        public float movingAccuracyMultiplierMinimumPercent = DefaultMovingAccuracyMultiplierMinimumPercent;
        public bool movingAccuracyMultiplierFixed = DefaultMovingAccuracyMultiplierFixed;

        public float armorCooldownReductionPercent = DefaultArmorCooldownReductionPercent;
        public float armorCooldownReductionGrowthPerLevelPercent = DefaultArmorCooldownReductionGrowthPerLevelPercent;
        public float armorCooldownReductionMinimumPercent = DefaultArmorCooldownReductionMinimumPercent;
        public bool armorCooldownReductionFixed = DefaultArmorCooldownReductionFixed;

        public int rangedDodgeDurationTicks = DefaultRangedDodgeDurationTicks;
        public float rangedDodgeDurationGrowthPerLevelTicks = DefaultRangedDodgeDurationGrowthPerLevelTicks;
        public int rangedDodgeDurationBaseTicks = DefaultRangedDodgeDurationBaseTicks;
        public bool rangedDodgeDurationFixed = DefaultRangedDodgeDurationFixed;

        public RimKataCandidateRangeMode candidateRangeMode = DefaultCandidateRangeMode;
        public float customCandidateRange = DefaultCustomCandidateRange;
        public int touchCandidateLimit = DefaultTouchCandidateLimit;
        public int shortCandidateLimit = DefaultShortCandidateLimit;
        public int mediumCandidateLimit = DefaultMediumCandidateLimit;
        public int longCandidateLimit = DefaultLongCandidateLimit;
        public int beyondCandidateLimit = DefaultBeyondCandidateLimit;
        public bool showRangedWeaponCooldown = true;
        public bool showMeleeWeaponAimTime = true;
        public bool showFocusedAttackLine = true;
        public float immediateTumbleChancePercent = DefaultImmediateTumbleChancePercent;
        public float responseAttackerSpinChancePercent = DefaultResponseAttackerSpinChancePercent;
        public float unarmedWeaponStealChancePercent = DefaultUnarmedWeaponStealChancePercent;
        public float responseWeaponDurabilityLossChancePercent = DefaultResponseWeaponDurabilityLossChancePercent;
        public int responseWeaponDurabilityLossAmount = DefaultResponseWeaponDurabilityLossAmount;
        public float creepJoinerDependencyGeneChancePercent = DefaultCreepJoinerDependencyGeneChancePercent;
        public float creepJoinerRimKataGeneChancePercent = 0f;
        public bool enableRimKataA = true;
        public bool enableRimKataP = true;
        public bool enableRimKataI = true;
        public bool enableRimKataG = true;
        public bool enableSerumDependency = true;
        private int creepJoinerGeneProbabilityVersion = 1;
        public float aiSecondaryWeaponChancePercent = DefaultAiSecondaryWeaponChancePercent;

        public float responseDisarmChancePercent = DefaultResponseDisarmChancePercent;
        public float responseDisarmChanceGrowthPerLevelPercent = DefaultResponseDisarmChanceGrowthPerLevelPercent;
        public float responseDisarmChanceMinimumPercent = DefaultResponseDisarmChanceMinimumPercent;
        public bool responseDisarmChanceFixed = DefaultResponseDisarmChanceFixed;

        public float responseAccidentalFireChancePercent = DefaultResponseAccidentalFireChancePercent;

        public float responseCooldownReductionPercent = DefaultResponseCooldownReductionPercent;
        public float responseCooldownReductionGrowthPerLevelPercent = DefaultResponseCooldownReductionGrowthPerLevelPercent;
        public float responseCooldownReductionMinimumPercent = DefaultResponseCooldownReductionMinimumPercent;
        public bool responseCooldownReductionFixed = DefaultResponseCooldownReductionFixed;

        public float serumDodgeMultiplierPercent = DefaultSerumDodgeMultiplierPercent;
        public float serumDodgeMultiplierGrowthPerLevelPercent = DefaultSerumDodgeMultiplierGrowthPerLevelPercent;
        public float serumDodgeMultiplierMinimumPercent = DefaultSerumDodgeMultiplierMinimumPercent;
        public bool serumDodgeMultiplierFixed = DefaultSerumDodgeMultiplierFixed;

        public float serumResponseMultiplierPercent = DefaultSerumResponseMultiplierPercent;
        public float serumResponseMultiplierGrowthPerLevelPercent = DefaultSerumResponseMultiplierGrowthPerLevelPercent;
        public float serumResponseMultiplierMinimumPercent = DefaultSerumResponseMultiplierMinimumPercent;
        public bool serumResponseMultiplierFixed = DefaultSerumResponseMultiplierFixed;

        public float serumInterceptionMultiplierPercent = DefaultSerumInterceptionMultiplierPercent;
        public float serumInterceptionMultiplierGrowthPerLevelPercent = DefaultSerumInterceptionMultiplierGrowthPerLevelPercent;
        public float serumInterceptionMultiplierMinimumPercent = DefaultSerumInterceptionMultiplierMinimumPercent;
        public bool serumInterceptionMultiplierFixed = DefaultSerumInterceptionMultiplierFixed;

        public bool secondaryWeaponEnabled = DefaultSecondaryWeaponEnabled;
        public bool singleShotConversionEnabled = DefaultSingleShotConversionEnabled;
        public bool randomAttackEnabled = DefaultRandomAttackEnabled;
        public bool explosiveInterceptionEnabled = DefaultExplosiveInterceptionEnabled;
        public bool movingFireEnabled = DefaultMovingFireEnabled;
        public bool closeFireEnabled = DefaultCloseFireEnabled;
        public bool targetRushEnabled = DefaultTargetRushEnabled;
        public bool responseEnabled = DefaultResponseEnabled;
        public bool rangedDodgeEnabled = DefaultRangedDodgeEnabled;
        public bool tumbleEnabled = DefaultTumbleEnabled;
        // Legacy blanket override is only read to migrate into the shared target list.
        internal bool accessRestrictionsDisabled = DefaultAccessRestrictionsDisabled;
        internal bool targetAccessInitialized;
        public List<RimKataTargetRule> targetAccessRules = new List<RimKataTargetRule>();
        public bool enableFriendlyPawnEffects = true;
        public bool enableHostilePawnEffects = true;

        public List<string> enabledWeaponDefNames = DefaultEnabledWeaponDefNames.ToList();
        public List<string> enabledArmorDefNames = DefaultEnabledArmorDefNames.ToList();
        public List<string> twoHandWeaponDefNames = new List<string>();
        public List<string> oneHandWeaponOverrideDefNames = new List<string>();

        public float RangedDodgeChance => ChanceFromPercent(rangedDodgeChancePercent);
        public float MeleeResponseChance => ChanceFromPercent(meleeResponseChancePercent);
        public float MeleeDodgeChance => ChanceFromPercent(meleeDodgeChancePercent);
        public float InterceptionAccuracyBonusMultiplier => BonusMultiplierFromPercent(interceptionChancePercent);
        public float InterceptionCriticalChance => ChanceFromPercent(interceptionCriticalChancePercent);
        public float MovingAccuracyMultiplier => MultiplierFromPercent(movingAccuracyMultiplierPercent);
        public float ArmorCooldownFactor => 1f - ChanceFromPercent(armorCooldownReductionPercent);
        public float ResponseAccidentalFireChance => ChanceFromPercent(responseAccidentalFireChancePercent);
        public float ResponseCooldownFactor => 1f - ChanceFromPercent(responseCooldownReductionPercent);
        public float SerumDodgeMultiplier => MultiplierFromPercent(serumDodgeMultiplierPercent);
        public float SerumResponseMultiplier => MultiplierFromPercent(serumResponseMultiplierPercent);
        public float SerumInterceptionMultiplier => MultiplierFromPercent(serumInterceptionMultiplierPercent);

        public int GetRangedDodgeDurationTicks(Pawn pawn)
        {
            if (rangedDodgeDurationFixed)
            {
                return Mathf.Clamp(rangedDodgeDurationTicks, MinimumRangedDodgeDurationTicks, MaximumRangedDodgeDurationTicks);
            }

            return Mathf.Clamp(
                Mathf.RoundToInt(
                    rangedDodgeDurationBaseTicks
                    - rangedDodgeDurationGrowthPerLevelTicks
                    * SkillLevel(pawn, SkillDefOf.Melee)),
                MinimumRangedDodgeDurationTicks,
                MaximumRangedDodgeDurationTicks);
        }

        public float GetRangedDodgeChance(Pawn pawn) => ChanceFromPercent(ResolvePercent(
            pawn, rangedDodgeChanceFixed, rangedDodgeChancePercent,
            rangedDodgeChanceMinimumPercent, rangedDodgeChanceGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetMeleeResponseChance(Pawn pawn) => ChanceFromPercent(ResolvePercent(
            pawn, meleeResponseChanceFixed, meleeResponseChancePercent,
            meleeResponseChanceMinimumPercent, meleeResponseChanceGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetMeleeResponseBonusMultiplier(Pawn pawn) => BonusMultiplierFromPercent(ResolvePercent(
            pawn, meleeResponseChanceFixed, meleeResponseChancePercent,
            meleeResponseChanceMinimumPercent, meleeResponseChanceGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetResponseDisarmChance(Pawn pawn) => ChanceFromPercent(ResolvePercent(
            pawn, responseDisarmChanceFixed, responseDisarmChancePercent,
            responseDisarmChanceMinimumPercent, responseDisarmChanceGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetMeleeDodgeChance(Pawn pawn) => ChanceFromPercent(ResolvePercent(
            pawn, meleeDodgeChanceFixed, meleeDodgeChancePercent,
            meleeDodgeChanceMinimumPercent, meleeDodgeChanceGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetMeleeDodgeBonusMultiplier(Pawn pawn) => BonusMultiplierFromPercent(ResolvePercent(
            pawn, meleeDodgeChanceFixed, meleeDodgeChancePercent,
            meleeDodgeChanceMinimumPercent, meleeDodgeChanceGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetInterceptionAccuracyBonusMultiplier(Pawn pawn) => BonusMultiplierFromPercent(ResolvePercent(
            pawn, interceptionChanceFixed, interceptionChancePercent,
            interceptionChanceMinimumPercent, interceptionChanceGrowthPerLevelPercent, SkillDefOf.Shooting));
        public float GetInterceptionCriticalChance(Pawn pawn) => ChanceFromPercent(ResolvePercent(
            pawn, interceptionCriticalChanceFixed, interceptionCriticalChancePercent,
            interceptionCriticalChanceMinimumPercent, interceptionCriticalChanceGrowthPerLevelPercent, SkillDefOf.Shooting));
        public float GetMovingAccuracyMultiplier(Pawn pawn) => MultiplierFromPercent(ResolvePercent(
            pawn, movingAccuracyMultiplierFixed, movingAccuracyMultiplierPercent,
            movingAccuracyMultiplierMinimumPercent, movingAccuracyMultiplierGrowthPerLevelPercent, SkillDefOf.Shooting));
        public float GetArmorCooldownFactor(Pawn pawn) => 1f - ChanceFromPercent(ResolvePercent(
            pawn, armorCooldownReductionFixed, armorCooldownReductionPercent,
            armorCooldownReductionMinimumPercent, armorCooldownReductionGrowthPerLevelPercent, SkillDefOf.Shooting));
        public float GetResponseCooldownFactor(Pawn pawn) => 1f - ChanceFromPercent(ResolvePercent(
            pawn, responseCooldownReductionFixed, responseCooldownReductionPercent,
            responseCooldownReductionMinimumPercent, responseCooldownReductionGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetSerumDodgeMultiplier(Pawn pawn) => MultiplierFromPercent(ResolvePercent(
            pawn, serumDodgeMultiplierFixed, serumDodgeMultiplierPercent,
            serumDodgeMultiplierMinimumPercent, serumDodgeMultiplierGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetSerumResponseMultiplier(Pawn pawn) => MultiplierFromPercent(ResolvePercent(
            pawn, serumResponseMultiplierFixed, serumResponseMultiplierPercent,
            serumResponseMultiplierMinimumPercent, serumResponseMultiplierGrowthPerLevelPercent, SkillDefOf.Melee));
        public float GetSerumInterceptionMultiplier(Pawn pawn) => MultiplierFromPercent(ResolvePercent(
            pawn, serumInterceptionMultiplierFixed, serumInterceptionMultiplierPercent,
            serumInterceptionMultiplierMinimumPercent, serumInterceptionMultiplierGrowthPerLevelPercent, SkillDefOf.Shooting));
        public float ResponseWeaponDurabilityLossChance => ChanceFromPercent(responseWeaponDurabilityLossChancePercent);
        public float ImmediateTumbleChance => ChanceFromPercent(immediateTumbleChancePercent);
        public float ResponseAttackerSpinChance => ChanceFromPercent(responseAttackerSpinChancePercent);
        public float UnarmedWeaponStealChance => ChanceFromPercent(unarmedWeaponStealChancePercent);
        public float AiSecondaryWeaponChance => ChanceFromPercent(aiSecondaryWeaponChancePercent);
        internal string ActiveProfileId
        {
            get => activeProfileId;
            set => activeProfileId = value;
        }

        internal bool LegacyOpProfileActive => opProfileActive;

        internal bool ProfileSavePending
        {
            get => profileSavePending;
            set => profileSavePending = value;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref settingsModelVersion, "settingsModelVersion", 0);
            Scribe_Values.Look(ref combatDefaultsVersion, "combatDefaultsVersion", 0);
            Scribe_Values.Look(ref activeProfileId, "activeProfileId");
            Scribe_Values.Look(ref profileSavePending, "profileSavePending", false);
            if (string.IsNullOrEmpty(activeProfileId))
            {
                Scribe_Values.Look(ref opProfileActive, "opProfileActive", false);
                Scribe_Deep.Look(ref opProfile, "opProfile");
                Scribe_Deep.Look(ref normalProfileBackup, "normalProfileBackup");
            }
            bool migrateLegacyScalarModel = Scribe.mode == LoadSaveMode.LoadingVars
                && settingsModelVersion < CurrentSettingsModelVersion;
            // Previous default values may have been omitted from the settings file.
            bool preservePreviousCombatDefaults = Scribe.mode == LoadSaveMode.LoadingVars
                && combatDefaultsVersion < CurrentCombatDefaultsVersion;

            Scribe_Values.Look(ref rangedDodgeChancePercent, "rangedDodgeChancePercent", migrateLegacyScalarModel ? LegacyRangedDodgeChancePercent : DefaultRangedDodgeChancePercent);
            Scribe_Values.Look(ref rangedDodgeChanceGrowthPerLevelPercent, "rangedDodgeChanceGrowthPerLevelPercent", preservePreviousCombatDefaults ? 3f : DefaultRangedDodgeChanceGrowthPerLevelPercent);
            Scribe_Values.Look(ref rangedDodgeChanceMinimumPercent, "rangedDodgeChanceMinimumPercent", preservePreviousCombatDefaults ? 30f : DefaultRangedDodgeChanceMinimumPercent);
            LookFixedMode(ref rangedDodgeChanceFixed, "rangedDodgeChanceFixed", DefaultRangedDodgeChanceFixed, "rangedDodgeChancePercent");
            LookRenamedFloat(ref meleeResponseChancePercent, "meleeResponseChancePercent", "meleeCounterChancePercent", migrateLegacyScalarModel ? LegacyMeleeResponseChancePercent : DefaultMeleeResponseChancePercent);
            Scribe_Values.Look(ref meleeResponseChanceGrowthPerLevelPercent, "meleeResponseChanceGrowthPerLevelPercent", preservePreviousCombatDefaults ? 3f : DefaultMeleeResponseChanceGrowthPerLevelPercent);
            Scribe_Values.Look(ref meleeResponseChanceMinimumPercent, "meleeResponseChanceMinimumPercent", preservePreviousCombatDefaults ? 30f : DefaultMeleeResponseChanceMinimumPercent);
            LookFixedMode(ref meleeResponseChanceFixed, "meleeResponseChanceFixed", DefaultMeleeResponseChanceFixed, "meleeResponseChancePercent", "meleeCounterChancePercent");
            Scribe_Values.Look(ref meleeDodgeChancePercent, "meleeDodgeChancePercent", migrateLegacyScalarModel ? LegacyMeleeDodgeChancePercent : DefaultMeleeDodgeChancePercent);
            Scribe_Values.Look(ref meleeDodgeChanceGrowthPerLevelPercent, "meleeDodgeChanceGrowthPerLevelPercent", DefaultMeleeDodgeChanceGrowthPerLevelPercent);
            Scribe_Values.Look(ref meleeDodgeChanceMinimumPercent, "meleeDodgeChanceMinimumPercent", DefaultMeleeDodgeChanceMinimumPercent);
            LookFixedMode(ref meleeDodgeChanceFixed, "meleeDodgeChanceFixed", DefaultMeleeDodgeChanceFixed, "meleeDodgeChancePercent");
            Scribe_Values.Look(ref interceptionChancePercent, "interceptionChancePercent", migrateLegacyScalarModel ? LegacyInterceptionChancePercent : DefaultInterceptionChancePercent);
            Scribe_Values.Look(ref interceptionChanceGrowthPerLevelPercent, "interceptionChanceGrowthPerLevelPercent", DefaultInterceptionChanceGrowthPerLevelPercent);
            Scribe_Values.Look(ref interceptionChanceMinimumPercent, "interceptionChanceMinimumPercent", DefaultInterceptionChanceMinimumPercent);
            LookFixedMode(ref interceptionChanceFixed, "interceptionChanceFixed", DefaultInterceptionChanceFixed, "interceptionChancePercent");
            Scribe_Values.Look(ref interceptionCriticalChancePercent, "interceptionCriticalChancePercent", migrateLegacyScalarModel ? LegacyInterceptionCriticalChancePercent : DefaultInterceptionCriticalChancePercent);
            Scribe_Values.Look(ref interceptionCriticalChanceGrowthPerLevelPercent, "interceptionCriticalChanceGrowthPerLevelPercent", DefaultInterceptionCriticalChanceGrowthPerLevelPercent);
            Scribe_Values.Look(ref interceptionCriticalChanceMinimumPercent, "interceptionCriticalChanceMinimumPercent", DefaultInterceptionCriticalChanceMinimumPercent);
            LookFixedMode(ref interceptionCriticalChanceFixed, "interceptionCriticalChanceFixed", DefaultInterceptionCriticalChanceFixed, "interceptionCriticalChancePercent");
            Scribe_Values.Look(ref movingAccuracyMultiplierPercent, "movingAccuracyMultiplierPercent", migrateLegacyScalarModel ? LegacyMovingAccuracyMultiplierPercent : DefaultMovingAccuracyMultiplierPercent);
            Scribe_Values.Look(ref movingAccuracyMultiplierGrowthPerLevelPercent, "movingAccuracyMultiplierGrowthPerLevelPercent", DefaultMovingAccuracyMultiplierGrowthPerLevelPercent);
            Scribe_Values.Look(ref movingAccuracyMultiplierMinimumPercent, "movingAccuracyMultiplierMinimumPercent", DefaultMovingAccuracyMultiplierMinimumPercent);
            LookFixedMode(ref movingAccuracyMultiplierFixed, "movingAccuracyMultiplierFixed", DefaultMovingAccuracyMultiplierFixed, "movingAccuracyMultiplierPercent");
            Scribe_Values.Look(ref armorCooldownReductionPercent, "armorCooldownReductionPercent", migrateLegacyScalarModel ? LegacyArmorCooldownReductionPercent : DefaultArmorCooldownReductionPercent);
            Scribe_Values.Look(ref armorCooldownReductionGrowthPerLevelPercent, "armorCooldownReductionGrowthPerLevelPercent", DefaultArmorCooldownReductionGrowthPerLevelPercent);
            Scribe_Values.Look(ref armorCooldownReductionMinimumPercent, "armorCooldownReductionMinimumPercent", DefaultArmorCooldownReductionMinimumPercent);
            LookFixedMode(ref armorCooldownReductionFixed, "armorCooldownReductionFixed", DefaultArmorCooldownReductionFixed, "armorCooldownReductionPercent");
            Scribe_Values.Look(ref rangedDodgeDurationTicks, "rangedDodgeDurationTicks", migrateLegacyScalarModel ? LegacyRangedDodgeDurationTicks : DefaultRangedDodgeDurationTicks);
            LookRenamedFloat(ref rangedDodgeDurationGrowthPerLevelTicks, "rangedDodgeDurationGrowthPerLevelTicks", "rangedDodgeDurationGrowthPerLevelPercent", DefaultRangedDodgeDurationGrowthPerLevelTicks);
            Scribe_Values.Look(ref rangedDodgeDurationBaseTicks, "rangedDodgeDurationBaseTicks", DefaultRangedDodgeDurationBaseTicks);
            LookFixedMode(ref rangedDodgeDurationFixed, "rangedDodgeDurationFixed", DefaultRangedDodgeDurationFixed, "rangedDodgeDurationTicks");
            Scribe_Values.Look(ref candidateRangeMode, "candidateRangeMode", DefaultCandidateRangeMode);
            Scribe_Values.Look(ref customCandidateRange, "customCandidateRange", DefaultCustomCandidateRange);
            Scribe_Values.Look(ref touchCandidateLimit, "touchCandidateLimit", DefaultTouchCandidateLimit);
            Scribe_Values.Look(ref shortCandidateLimit, "shortCandidateLimit", DefaultShortCandidateLimit);
            Scribe_Values.Look(ref mediumCandidateLimit, "mediumCandidateLimit", DefaultMediumCandidateLimit);
            Scribe_Values.Look(ref longCandidateLimit, "longCandidateLimit", DefaultLongCandidateLimit);
            Scribe_Values.Look(ref beyondCandidateLimit, "beyondCandidateLimit", DefaultBeyondCandidateLimit);
            Scribe_Values.Look(ref showRangedWeaponCooldown, "showRangedWeaponCooldown", true);
            Scribe_Values.Look(ref showMeleeWeaponAimTime, "showMeleeWeaponAimTime", true);
            Scribe_Values.Look(ref showFocusedAttackLine, "showFocusedAttackLine", true);
            Scribe_Values.Look(ref immediateTumbleChancePercent, "immediateTumbleChancePercent", preservePreviousCombatDefaults ? 0f : DefaultImmediateTumbleChancePercent);
            Scribe_Values.Look(ref responseAttackerSpinChancePercent, "responseAttackerSpinChancePercent", preservePreviousCombatDefaults ? 0f : DefaultResponseAttackerSpinChancePercent);
            Scribe_Values.Look(ref unarmedWeaponStealChancePercent, "unarmedWeaponStealChancePercent", DefaultUnarmedWeaponStealChancePercent);
            if (preservePreviousCombatDefaults)
                combatDefaultsVersion = CurrentCombatDefaultsVersion;
            Scribe_Values.Look(ref responseWeaponDurabilityLossChancePercent, "responseWeaponDurabilityLossChancePercent", DefaultResponseWeaponDurabilityLossChancePercent);
            Scribe_Values.Look(ref responseWeaponDurabilityLossAmount, "responseWeaponDurabilityLossAmount", DefaultResponseWeaponDurabilityLossAmount);
            Scribe_Values.Look(ref creepJoinerDependencyGeneChancePercent, "creepJoinerDependencyGeneChancePercent", DefaultCreepJoinerDependencyGeneChancePercent);
            Scribe_Values.Look(ref creepJoinerRimKataGeneChancePercent, "creepJoinerRimKataGeneChancePercent", 0f);
            Scribe_Values.Look(ref creepJoinerGeneProbabilityVersion, "creepJoinerGeneProbabilityVersion", 0);
            Scribe_Values.Look(ref enableRimKataA, "enableRimKataA", true);
            Scribe_Values.Look(ref enableRimKataP, "enableRimKataP", true);
            Scribe_Values.Look(ref enableRimKataI, "enableRimKataI", true);
            Scribe_Values.Look(ref enableRimKataG, "enableRimKataG", true);
            Scribe_Values.Look(ref enableSerumDependency, "enableSerumDependency", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars && creepJoinerGeneProbabilityVersion < 1)
            {
                RimKataCreepJoinerGeneChoice legacyChoice = DefaultCreepJoinerGeneChoice;
                Scribe_Values.Look(ref legacyChoice, "creepJoinerGeneChoice", DefaultCreepJoinerGeneChoice);
                if (legacyChoice != RimKataCreepJoinerGeneChoice.MindNumbSerumDependency)
                {
                    creepJoinerRimKataGeneChancePercent = creepJoinerDependencyGeneChancePercent;
                    creepJoinerDependencyGeneChancePercent = 0f;
                }
                creepJoinerGeneProbabilityVersion = 1;
            }
            Scribe_Values.Look(ref aiSecondaryWeaponChancePercent, "aiSecondaryWeaponChancePercent", DefaultAiSecondaryWeaponChancePercent);
            Scribe_Values.Look(ref responseDisarmChancePercent, "responseDisarmChancePercent", DefaultResponseDisarmChancePercent);
            Scribe_Values.Look(ref responseDisarmChanceGrowthPerLevelPercent, "responseDisarmChanceGrowthPerLevelPercent", DefaultResponseDisarmChanceGrowthPerLevelPercent);
            Scribe_Values.Look(ref responseDisarmChanceMinimumPercent, "responseDisarmChanceMinimumPercent", DefaultResponseDisarmChanceMinimumPercent);
            LookFixedMode(ref responseDisarmChanceFixed, "responseDisarmChanceFixed", DefaultResponseDisarmChanceFixed, "responseDisarmChancePercent");
            Scribe_Values.Look(ref responseAccidentalFireChancePercent, "responseAccidentalFireChancePercent", DefaultResponseAccidentalFireChancePercent);
            LookRenamedFloat(ref responseCooldownReductionPercent, "responseCooldownReductionPercent", "counterCooldownReductionPercent", migrateLegacyScalarModel ? LegacyResponseCooldownReductionPercent : DefaultResponseCooldownReductionPercent);
            Scribe_Values.Look(ref responseCooldownReductionGrowthPerLevelPercent, "responseCooldownReductionGrowthPerLevelPercent", DefaultResponseCooldownReductionGrowthPerLevelPercent);
            Scribe_Values.Look(ref responseCooldownReductionMinimumPercent, "responseCooldownReductionMinimumPercent", DefaultResponseCooldownReductionMinimumPercent);
            LookFixedMode(ref responseCooldownReductionFixed, "responseCooldownReductionFixed", DefaultResponseCooldownReductionFixed, "responseCooldownReductionPercent", "counterCooldownReductionPercent");
            Scribe_Values.Look(ref serumDodgeMultiplierPercent, "serumDodgeMultiplierPercent", migrateLegacyScalarModel ? LegacySerumMultiplierPercent : DefaultSerumDodgeMultiplierPercent);
            Scribe_Values.Look(ref serumDodgeMultiplierGrowthPerLevelPercent, "serumDodgeMultiplierGrowthPerLevelPercent", DefaultSerumDodgeMultiplierGrowthPerLevelPercent);
            Scribe_Values.Look(ref serumDodgeMultiplierMinimumPercent, "serumDodgeMultiplierMinimumPercent", DefaultSerumDodgeMultiplierMinimumPercent);
            LookFixedMode(ref serumDodgeMultiplierFixed, "serumDodgeMultiplierFixed", DefaultSerumDodgeMultiplierFixed, "serumDodgeMultiplierPercent");
            LookRenamedFloat(ref serumResponseMultiplierPercent, "serumResponseMultiplierPercent", "serumCounterMultiplierPercent", migrateLegacyScalarModel ? LegacySerumMultiplierPercent : DefaultSerumResponseMultiplierPercent);
            Scribe_Values.Look(ref serumResponseMultiplierGrowthPerLevelPercent, "serumResponseMultiplierGrowthPerLevelPercent", DefaultSerumResponseMultiplierGrowthPerLevelPercent);
            Scribe_Values.Look(ref serumResponseMultiplierMinimumPercent, "serumResponseMultiplierMinimumPercent", DefaultSerumResponseMultiplierMinimumPercent);
            LookFixedMode(ref serumResponseMultiplierFixed, "serumResponseMultiplierFixed", DefaultSerumResponseMultiplierFixed, "serumResponseMultiplierPercent", "serumCounterMultiplierPercent");
            Scribe_Values.Look(ref serumInterceptionMultiplierPercent, "serumInterceptionMultiplierPercent", migrateLegacyScalarModel ? LegacySerumMultiplierPercent : DefaultSerumInterceptionMultiplierPercent);
            Scribe_Values.Look(ref serumInterceptionMultiplierGrowthPerLevelPercent, "serumInterceptionMultiplierGrowthPerLevelPercent", DefaultSerumInterceptionMultiplierGrowthPerLevelPercent);
            Scribe_Values.Look(ref serumInterceptionMultiplierMinimumPercent, "serumInterceptionMultiplierMinimumPercent", DefaultSerumInterceptionMultiplierMinimumPercent);
            LookFixedMode(ref serumInterceptionMultiplierFixed, "serumInterceptionMultiplierFixed", DefaultSerumInterceptionMultiplierFixed, "serumInterceptionMultiplierPercent");
            Scribe_Values.Look(ref secondaryWeaponEnabled, "secondaryWeaponEnabled", DefaultSecondaryWeaponEnabled);
            Scribe_Values.Look(ref singleShotConversionEnabled, "singleShotConversionEnabled", DefaultSingleShotConversionEnabled);
            Scribe_Values.Look(ref randomAttackEnabled, "randomAttackEnabled", DefaultRandomAttackEnabled);
            Scribe_Values.Look(ref movingFireEnabled, "movingFireEnabled", DefaultMovingFireEnabled);
            Scribe_Values.Look(ref explosiveInterceptionEnabled, "explosiveInterceptionEnabled", DefaultExplosiveInterceptionEnabled);
            Scribe_Values.Look(ref closeFireEnabled, "closeFireEnabled", DefaultCloseFireEnabled);
            Scribe_Values.Look(ref targetRushEnabled, "targetRushEnabled", DefaultTargetRushEnabled);
            Scribe_Values.Look(ref accessRestrictionsDisabled, "accessRestrictionsDisabled", DefaultAccessRestrictionsDisabled);
            Scribe_Values.Look(ref targetAccessInitialized, "targetAccessInitialized", false);
            Scribe_Collections.Look(ref targetAccessRules, "targetAccessRules", LookMode.Deep);
            Scribe_Values.Look(ref responseEnabled, "responseEnabled", DefaultResponseEnabled);
            Scribe_Values.Look(ref rangedDodgeEnabled, "rangedDodgeEnabled", DefaultRangedDodgeEnabled);
            Scribe_Values.Look(ref tumbleEnabled, "tumbleEnabled", DefaultTumbleEnabled);
            Scribe_Values.Look(ref enableFriendlyPawnEffects, "enableFriendlyPawnEffects", true);
            Scribe_Values.Look(ref enableHostilePawnEffects, "enableHostilePawnEffects", true);
            Scribe_Collections.Look(ref enabledWeaponDefNames, "enabledWeaponDefNames", LookMode.Value);
            Scribe_Collections.Look(ref enabledArmorDefNames, "enabledArmorDefNames", LookMode.Value);
            Scribe_Collections.Look(ref twoHandWeaponDefNames, "twoHandWeaponDefNames", LookMode.Value);
            Scribe_Collections.Look(ref oneHandWeaponOverrideDefNames, "oneHandWeaponOverrideDefNames", LookMode.Value);

            if (migrateLegacyScalarModel)
            {
                rangedDodgeDurationFixed = true;
                rangedDodgeChanceFixed = true;
                meleeResponseChanceFixed = true;
                meleeDodgeChanceFixed = true;
                interceptionChanceFixed = true;
                interceptionCriticalChanceFixed = true;
                movingAccuracyMultiplierFixed = true;
                armorCooldownReductionFixed = true;
                responseCooldownReductionFixed = true;
                serumDodgeMultiplierFixed = true;
                serumResponseMultiplierFixed = true;
                serumInterceptionMultiplierFixed = true;
                settingsModelVersion = CurrentSettingsModelVersion;
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                rangedDodgeDurationTicks = Mathf.Clamp(rangedDodgeDurationTicks, MinimumRangedDodgeDurationTicks, MaximumRangedDodgeDurationTicks);
                SanitizeModFeatures();
                rangedDodgeDurationBaseTicks = Mathf.Clamp(rangedDodgeDurationBaseTicks, MinimumRangedDodgeDurationTicks, MaximumRangedDodgeDurationTicks);
                if (!Enum.IsDefined(typeof(RimKataCandidateRangeMode), candidateRangeMode))
                {
                    candidateRangeMode = DefaultCandidateRangeMode;
                }

                if (float.IsNaN(customCandidateRange)
                    || float.IsInfinity(customCandidateRange)
                    || customCandidateRange <= 0f)
                {
                    customCandidateRange = DefaultCustomCandidateRange;
                }
                else
                {
                    customCandidateRange = Mathf.Clamp(customCandidateRange, MinimumCustomCandidateRange, MaximumCustomCandidateRange);
                }

                if (float.IsNaN(responseWeaponDurabilityLossChancePercent)
                    || float.IsInfinity(responseWeaponDurabilityLossChancePercent))
                {
                    responseWeaponDurabilityLossChancePercent = DefaultResponseWeaponDurabilityLossChancePercent;
                }
                else
                {
                    responseWeaponDurabilityLossChancePercent = Mathf.Clamp(responseWeaponDurabilityLossChancePercent, 0f, 100f);
                }
                responseWeaponDurabilityLossAmount = Mathf.Clamp(
                    responseWeaponDurabilityLossAmount,
                    MinimumResponseWeaponDurabilityLossAmount,
                    MaximumResponseWeaponDurabilityLossAmount);
                SanitizeCreepJoinerGeneChances();
                aiSecondaryWeaponChancePercent = SanitizePercent(
                    aiSecondaryWeaponChancePercent,
                    DefaultAiSecondaryWeaponChancePercent);
                responseDisarmChancePercent = SanitizePercent(
                    responseDisarmChancePercent,
                    DefaultResponseDisarmChancePercent);
                responseDisarmChanceGrowthPerLevelPercent = SanitizeNonNegative(
                    responseDisarmChanceGrowthPerLevelPercent,
                    DefaultResponseDisarmChanceGrowthPerLevelPercent);
                responseDisarmChanceMinimumPercent = SanitizePercent(
                    responseDisarmChanceMinimumPercent,
                    DefaultResponseDisarmChanceMinimumPercent);
                enabledWeaponDefNames = SanitizeDefNames(enabledWeaponDefNames);
                targetAccessRules = (targetAccessRules ?? new List<RimKataTargetRule>())
                    .Where(rule => rule != null && !string.IsNullOrEmpty(rule.key))
                    .GroupBy(rule => rule.key, StringComparer.Ordinal)
                    .Select(group => group.Last()).ToList();
                enabledArmorDefNames = SanitizeDefNames(enabledArmorDefNames);
                twoHandWeaponDefNames = SanitizeDefNames(twoHandWeaponDefNames);
                oneHandWeaponOverrideDefNames = SanitizeDefNames(oneHandWeaponOverrideDefNames);

                if (string.IsNullOrEmpty(activeProfileId))
                {
                    if (opProfile == null)
                    {
                        opProfile = CreateOpProfileDefaults();
                    }
                    else
                    {
                        opProfile.FillMissingFrom(CreateOpProfileDefaults());
                    }

                    if (!opProfileActive)
                    {
                        normalProfileBackup = null;
                    }
                    else if (normalProfileBackup == null)
                    {
                        normalProfileBackup = CreateNormalProfileDefaults();
                    }
                    else
                    {
                        normalProfileBackup.FillMissingFrom(CreateNormalProfileDefaults());
                    }
                }
            }
        }

        internal void SanitizeModFeatures()
        {
            touchCandidateLimit = Mathf.Clamp(touchCandidateLimit, MinimumCandidateLimit, MaximumCandidateLimit);
            shortCandidateLimit = Mathf.Clamp(shortCandidateLimit, MinimumCandidateLimit, MaximumCandidateLimit);
            mediumCandidateLimit = Mathf.Clamp(mediumCandidateLimit, MinimumCandidateLimit, MaximumCandidateLimit);
            longCandidateLimit = Mathf.Clamp(longCandidateLimit, MinimumCandidateLimit, MaximumCandidateLimit);
            beyondCandidateLimit = Mathf.Clamp(beyondCandidateLimit, MinimumCandidateLimit, MaximumCandidateLimit);
            immediateTumbleChancePercent = SanitizePercent(immediateTumbleChancePercent, DefaultImmediateTumbleChancePercent);
            responseAttackerSpinChancePercent = SanitizePercent(responseAttackerSpinChancePercent, DefaultResponseAttackerSpinChancePercent);
            unarmedWeaponStealChancePercent = SanitizePercent(unarmedWeaponStealChancePercent, DefaultUnarmedWeaponStealChancePercent);
            responseAccidentalFireChancePercent = SanitizePercent(
                responseAccidentalFireChancePercent, DefaultResponseAccidentalFireChancePercent);
        }

        internal void SanitizeCreepJoinerGeneChances()
        {
            creepJoinerRimKataGeneChancePercent = enableRimKataG
                ? SanitizePercent(creepJoinerRimKataGeneChancePercent, 0f)
                : 0f;
            creepJoinerDependencyGeneChancePercent = enableSerumDependency
                ? Mathf.Min(SanitizePercent(creepJoinerDependencyGeneChancePercent, 0f),
                    100f - creepJoinerRimKataGeneChancePercent)
                : 0f;
        }

        internal RimKataCreepJoinerGeneChoice? CreepJoinerGeneChoiceForRoll(float rollPercent)
        {
            if (enableRimKataG && creepJoinerRimKataGeneChancePercent > 0f
                && (creepJoinerRimKataGeneChancePercent >= 100f
                    || rollPercent < creepJoinerRimKataGeneChancePercent))
                return RimKataCreepJoinerGeneChoice.RimKata;
            float combinedChance = creepJoinerRimKataGeneChancePercent + creepJoinerDependencyGeneChancePercent;
            if (enableSerumDependency && creepJoinerDependencyGeneChancePercent > 0f
                && (combinedChance >= 100f || rollPercent < combinedChance))
                return RimKataCreepJoinerGeneChoice.MindNumbSerumDependency;
            return null;
        }

        public void ResetNumericDefaults()
        {
            rangedDodgeChancePercent = DefaultRangedDodgeChancePercent;
            rangedDodgeChanceGrowthPerLevelPercent = DefaultRangedDodgeChanceGrowthPerLevelPercent;
            rangedDodgeChanceMinimumPercent = DefaultRangedDodgeChanceMinimumPercent;
            rangedDodgeChanceFixed = DefaultRangedDodgeChanceFixed;
            meleeResponseChancePercent = DefaultMeleeResponseChancePercent;
            meleeResponseChanceGrowthPerLevelPercent = DefaultMeleeResponseChanceGrowthPerLevelPercent;
            meleeResponseChanceMinimumPercent = DefaultMeleeResponseChanceMinimumPercent;
            meleeResponseChanceFixed = DefaultMeleeResponseChanceFixed;
            meleeDodgeChancePercent = DefaultMeleeDodgeChancePercent;
            meleeDodgeChanceGrowthPerLevelPercent = DefaultMeleeDodgeChanceGrowthPerLevelPercent;
            meleeDodgeChanceMinimumPercent = DefaultMeleeDodgeChanceMinimumPercent;
            meleeDodgeChanceFixed = DefaultMeleeDodgeChanceFixed;
            interceptionChancePercent = DefaultInterceptionChancePercent;
            interceptionChanceGrowthPerLevelPercent = DefaultInterceptionChanceGrowthPerLevelPercent;
            interceptionChanceMinimumPercent = DefaultInterceptionChanceMinimumPercent;
            interceptionChanceFixed = DefaultInterceptionChanceFixed;
            interceptionCriticalChancePercent = DefaultInterceptionCriticalChancePercent;
            interceptionCriticalChanceGrowthPerLevelPercent = DefaultInterceptionCriticalChanceGrowthPerLevelPercent;
            interceptionCriticalChanceMinimumPercent = DefaultInterceptionCriticalChanceMinimumPercent;
            interceptionCriticalChanceFixed = DefaultInterceptionCriticalChanceFixed;
            movingAccuracyMultiplierPercent = DefaultMovingAccuracyMultiplierPercent;
            movingAccuracyMultiplierGrowthPerLevelPercent = DefaultMovingAccuracyMultiplierGrowthPerLevelPercent;
            movingAccuracyMultiplierMinimumPercent = DefaultMovingAccuracyMultiplierMinimumPercent;
            movingAccuracyMultiplierFixed = DefaultMovingAccuracyMultiplierFixed;
            armorCooldownReductionPercent = DefaultArmorCooldownReductionPercent;
            armorCooldownReductionGrowthPerLevelPercent = DefaultArmorCooldownReductionGrowthPerLevelPercent;
            armorCooldownReductionMinimumPercent = DefaultArmorCooldownReductionMinimumPercent;
            armorCooldownReductionFixed = DefaultArmorCooldownReductionFixed;
            rangedDodgeDurationTicks = DefaultRangedDodgeDurationTicks;
            rangedDodgeDurationGrowthPerLevelTicks = DefaultRangedDodgeDurationGrowthPerLevelTicks;
            rangedDodgeDurationBaseTicks = DefaultRangedDodgeDurationBaseTicks;
            rangedDodgeDurationFixed = DefaultRangedDodgeDurationFixed;
            candidateRangeMode = DefaultCandidateRangeMode;
            customCandidateRange = DefaultCustomCandidateRange;
            touchCandidateLimit = DefaultTouchCandidateLimit;
            shortCandidateLimit = DefaultShortCandidateLimit;
            mediumCandidateLimit = DefaultMediumCandidateLimit;
            longCandidateLimit = DefaultLongCandidateLimit;
            beyondCandidateLimit = DefaultBeyondCandidateLimit;
            showRangedWeaponCooldown = true;
            showMeleeWeaponAimTime = true;
            showFocusedAttackLine = true;
            immediateTumbleChancePercent = DefaultImmediateTumbleChancePercent;
            responseAttackerSpinChancePercent = DefaultResponseAttackerSpinChancePercent;
            unarmedWeaponStealChancePercent = DefaultUnarmedWeaponStealChancePercent;
            responseWeaponDurabilityLossChancePercent = DefaultResponseWeaponDurabilityLossChancePercent;
            responseWeaponDurabilityLossAmount = DefaultResponseWeaponDurabilityLossAmount;
            responseDisarmChancePercent = DefaultResponseDisarmChancePercent;
            responseDisarmChanceGrowthPerLevelPercent = DefaultResponseDisarmChanceGrowthPerLevelPercent;
            responseDisarmChanceMinimumPercent = DefaultResponseDisarmChanceMinimumPercent;
            responseDisarmChanceFixed = DefaultResponseDisarmChanceFixed;
            responseAccidentalFireChancePercent = DefaultResponseAccidentalFireChancePercent;
            responseCooldownReductionPercent = DefaultResponseCooldownReductionPercent;
            responseCooldownReductionGrowthPerLevelPercent = DefaultResponseCooldownReductionGrowthPerLevelPercent;
            responseCooldownReductionMinimumPercent = DefaultResponseCooldownReductionMinimumPercent;
            responseCooldownReductionFixed = DefaultResponseCooldownReductionFixed;
            serumDodgeMultiplierPercent = DefaultSerumDodgeMultiplierPercent;
            serumDodgeMultiplierGrowthPerLevelPercent = DefaultSerumDodgeMultiplierGrowthPerLevelPercent;
            serumDodgeMultiplierMinimumPercent = DefaultSerumDodgeMultiplierMinimumPercent;
            serumDodgeMultiplierFixed = DefaultSerumDodgeMultiplierFixed;
            serumResponseMultiplierPercent = DefaultSerumResponseMultiplierPercent;
            serumResponseMultiplierGrowthPerLevelPercent = DefaultSerumResponseMultiplierGrowthPerLevelPercent;
            serumResponseMultiplierMinimumPercent = DefaultSerumResponseMultiplierMinimumPercent;
            serumResponseMultiplierFixed = DefaultSerumResponseMultiplierFixed;
            serumInterceptionMultiplierPercent = DefaultSerumInterceptionMultiplierPercent;
            serumInterceptionMultiplierGrowthPerLevelPercent = DefaultSerumInterceptionMultiplierGrowthPerLevelPercent;
            serumInterceptionMultiplierMinimumPercent = DefaultSerumInterceptionMultiplierMinimumPercent;
            serumInterceptionMultiplierFixed = DefaultSerumInterceptionMultiplierFixed;
            secondaryWeaponEnabled = DefaultSecondaryWeaponEnabled;
            singleShotConversionEnabled = DefaultSingleShotConversionEnabled;
            randomAttackEnabled = DefaultRandomAttackEnabled;
            movingFireEnabled = DefaultMovingFireEnabled;
            closeFireEnabled = DefaultCloseFireEnabled;
            targetRushEnabled = DefaultTargetRushEnabled;
            accessRestrictionsDisabled = DefaultAccessRestrictionsDisabled;
        }

        internal RimKataSettingsProfile GetLegacyNormalProfile()
        {
            return opProfileActive
                ? normalProfileBackup ?? CreateNormalProfileDefaults()
                : RimKataSettingsProfile.Capture(this);
        }

        internal RimKataSettingsProfile GetLegacyOpProfile()
        {
            return opProfileActive
                ? RimKataSettingsProfile.Capture(this)
                : opProfile ?? CreateOpProfileDefaults();
        }

        internal void CompleteProfileMigration(string id)
        {
            activeProfileId = id;
            normalProfileBackup = null;
            opProfile = null;
            opProfileActive = false;
        }

        private void ApplyOpProfileDefaults()
        {
            rangedDodgeChanceFixed = true;
            rangedDodgeChancePercent = 99f;
            rangedDodgeChanceGrowthPerLevelPercent = 4f;
            rangedDodgeChanceMinimumPercent = 60f;
            meleeResponseChanceFixed = true;
            meleeResponseChancePercent = 99f;
            meleeResponseChanceGrowthPerLevelPercent = 4f;
            meleeResponseChanceMinimumPercent = 60f;
            responseDisarmChanceFixed = true;
            responseDisarmChancePercent = 20f;
            responseDisarmChanceGrowthPerLevelPercent = 2f;
            responseDisarmChanceMinimumPercent = 10f;
            meleeDodgeChanceFixed = true;
            meleeDodgeChancePercent = 99f;
            meleeDodgeChanceGrowthPerLevelPercent = 4f;
            meleeDodgeChanceMinimumPercent = 60f;
            interceptionChanceFixed = true;
            interceptionChancePercent = 100f;
            interceptionChanceGrowthPerLevelPercent = 4f;
            interceptionChanceMinimumPercent = 60f;
            interceptionCriticalChanceFixed = true;
            interceptionCriticalChancePercent = 80f;
            interceptionCriticalChanceGrowthPerLevelPercent = 4f;
            interceptionCriticalChanceMinimumPercent = 60f;
            movingAccuracyMultiplierFixed = true;
            movingAccuracyMultiplierPercent = 300f;
            movingAccuracyMultiplierGrowthPerLevelPercent = 20f;
            movingAccuracyMultiplierMinimumPercent = 100f;
            armorCooldownReductionFixed = true;
            armorCooldownReductionPercent = 99f;
            armorCooldownReductionGrowthPerLevelPercent = 4f;
            armorCooldownReductionMinimumPercent = 60f;
            rangedDodgeDurationFixed = true;
            rangedDodgeDurationTicks = 20;
            rangedDodgeDurationGrowthPerLevelTicks = 1;
            rangedDodgeDurationBaseTicks = 30;
            candidateRangeMode = (RimKataCandidateRangeMode)0;
            customCandidateRange = 0f;
            responseWeaponDurabilityLossChancePercent = 0f;
            responseWeaponDurabilityLossAmount = 0;
            creepJoinerDependencyGeneChancePercent = 0f;
            creepJoinerRimKataGeneChancePercent = 0f;
            aiSecondaryWeaponChancePercent = 0f;
            responseAccidentalFireChancePercent = 80f;
            responseCooldownReductionFixed = true;
            responseCooldownReductionPercent = 80f;
            responseCooldownReductionGrowthPerLevelPercent = 4f;
            responseCooldownReductionMinimumPercent = 60f;
            serumDodgeMultiplierFixed = true;
            serumDodgeMultiplierPercent = 500f;
            serumDodgeMultiplierGrowthPerLevelPercent = 20f;
            serumDodgeMultiplierMinimumPercent = 300f;
            serumResponseMultiplierFixed = true;
            serumResponseMultiplierPercent = 500f;
            serumResponseMultiplierGrowthPerLevelPercent = 20f;
            serumResponseMultiplierMinimumPercent = 300f;
            serumInterceptionMultiplierFixed = true;
            serumInterceptionMultiplierPercent = 500f;
            serumInterceptionMultiplierGrowthPerLevelPercent = 20f;
            serumInterceptionMultiplierMinimumPercent = 300f;
            secondaryWeaponEnabled = DefaultSecondaryWeaponEnabled;
            singleShotConversionEnabled = DefaultSingleShotConversionEnabled;
            randomAttackEnabled = DefaultRandomAttackEnabled;
            movingFireEnabled = DefaultMovingFireEnabled;
            closeFireEnabled = DefaultCloseFireEnabled;
            targetRushEnabled = DefaultTargetRushEnabled;
            accessRestrictionsDisabled = DefaultAccessRestrictionsDisabled;
        }

        private static RimKataSettingsProfile CreateNormalProfileDefaults()
        {
            return RimKataSettingsProfile.Capture(new RimKataSettings());
        }

        private static RimKataSettingsProfile CreateOpProfileDefaults()
        {
            RimKataSettings defaults = new RimKataSettings();
            defaults.ApplyOpProfileDefaults();
            return RimKataSettingsProfile.Capture(defaults);
        }

        private static float ResolvePercent(Pawn pawn, bool fixedValue, float fixedPercent, float minimumPercent, float growthPerLevelPercent, SkillDef skill)
        {
            return fixedValue
                ? fixedPercent
                : minimumPercent + growthPerLevelPercent * SkillLevel(pawn, skill);
        }

        private static int SkillLevel(Pawn pawn, SkillDef skill)
        {
            return pawn?.skills?.GetSkill(skill)?.Level ?? 0;
        }

        private static float ChanceFromPercent(float percent) => Mathf.Clamp01(percent / 100f);
        private static float MultiplierFromPercent(float percent) => Mathf.Max(0f, percent / 100f);
        private static float BonusMultiplierFromPercent(float percent) => 1f + Mathf.Max(0f, percent) / 100f;

        private static float SanitizePercent(float value, float defaultValue)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? defaultValue
                : Mathf.Clamp(value, 0f, 100f);
        }

        private static float SanitizeNonNegative(float value, float defaultValue)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? defaultValue
                : Mathf.Max(0f, value);
        }

        private static void LookRenamedFloat(ref float value, string key, string legacyKey, float defaultValue)
        {
            bool loadLegacyKey = Scribe.mode == LoadSaveMode.LoadingVars && Scribe.loader.curXmlParent?[key] == null;
            Scribe_Values.Look(ref value, loadLegacyKey ? legacyKey : key, defaultValue);
        }

        private static void LookFixedMode(
            ref bool value,
            string key,
            bool defaultValue,
            string scalarKey,
            string legacyScalarKey = null)
        {
            bool migrateOldScalarAsFixed = Scribe.mode == LoadSaveMode.LoadingVars && Scribe.loader.curXmlParent?[key] == null && (Scribe.loader.curXmlParent?[scalarKey] != null || (!legacyScalarKey.NullOrEmpty() && Scribe.loader.curXmlParent?[legacyScalarKey] != null));
            Scribe_Values.Look(ref value, key, defaultValue);
            if (migrateOldScalarAsFixed)
            {
                value = true;
            }
        }

        private static List<string> SanitizeDefNames(List<string> source)
        {
            if (source == null)
            {
                return new List<string>();
            }

            return source.Where(name => !name.NullOrEmpty())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
