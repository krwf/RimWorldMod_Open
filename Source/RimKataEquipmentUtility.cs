using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public enum RimKataGripType
    {
        OneHand,
        TwoHand
    }

    public static class RimKataEquipmentUtility
    {
        private static HashSet<string> enabledWeaponDefNames;
        private static HashSet<string> enabledArmorDefNames;
        private static List<ThingDef> enabledOneHandGeneratableWeapons;

        internal static int WeaponConfigurationRevision { get; private set; }

        public static List<ThingDef> EnabledOneHandGeneratableWeapons
        {
            get
            {
                EnsureCaches();
                if (enabledOneHandGeneratableWeapons == null)
                {
                    enabledOneHandGeneratableWeapons = new List<ThingDef>();
                    List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
                    for (int i = 0; i < defs.Count; i++)
                    {
                        ThingDef def = defs[i];
                        if (def?.equipmentType == EquipmentType.Primary
                            && def.PlayerAcquirable
                            && def.generateAllowChance > 0f
                            && IsWeaponEnabled(def)
                            && RimKataGripUtility.GripTypeFor(def) == RimKataGripType.OneHand)
                        {
                            enabledOneHandGeneratableWeapons.Add(def);
                        }
                    }
                }

                return enabledOneHandGeneratableWeapons;
            }
        }

        public static bool IsPrimaryWeaponEnabled(Pawn pawn)
        {
            return IsWeaponEnabled(pawn?.equipment?.Primary?.def);
        }

        public static bool IsWeaponEnabled(ThingDef def)
        {
            EnsureCaches();
            return def != null && enabledWeaponDefNames.Contains(def.defName);
        }

        public static bool IsArmorEnabled(ThingDef def)
        {
            EnsureCaches();
            return def != null && enabledArmorDefNames.Contains(def.defName);
        }

        public static bool HasEnabledArmor(Pawn pawn)
        {
            if (pawn?.apparel?.WornApparel == null)
            {
                return false;
            }

            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (IsArmorEnabled(worn[i].def))
                {
                    return true;
                }
            }

            return false;
        }

        public static int LoadedSelectionCount(IEnumerable<string> defNames, RimKataDefSelectionKind kind)
        {
            if (defNames == null)
            {
                return 0;
            }

            int count = 0;
            HashSet<string> seenDefNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string defName in defNames)
            {
                if (defName.NullOrEmpty() || !seenDefNames.Add(defName))
                {
                    continue;
                }

                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (Dialog_RimKataDefSelector.IsCandidate(def, kind))
                {
                    count++;
                }
            }

            return count;
        }

        public static void InvalidateCaches()
        {
            unchecked
            {
                WeaponConfigurationRevision++;
            }
            enabledWeaponDefNames = null;
            enabledArmorDefNames = null;
            enabledOneHandGeneratableWeapons = null;
            RimKataGripUtility.InvalidateCache();
            RimKataPreparedWeaponData.RefreshDefinitions();
            RimKataWeaponRenderProbe.RefreshDefinitions();
        }

        private static void EnsureCaches()
        {
            if (enabledWeaponDefNames != null && enabledArmorDefNames != null)
            {
                return;
            }

            RimKataSettings settings = RimKataMod.Settings;
            enabledWeaponDefNames = new HashSet<string>(settings?.enabledWeaponDefNames ?? new List<string>(), StringComparer.Ordinal);
            enabledArmorDefNames = new HashSet<string>(settings?.enabledArmorDefNames ?? new List<string>(), StringComparer.Ordinal);
        }
    }

    public static class RimKataGripUtility
    {
        private static readonly HashSet<string> officialOneHandedDefNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Beer",
            "MeleeWeapon_Axe",
            "MeleeWeapon_Gladius",
            "MeleeWeapon_MonoSwordBladelink",
            "MeleeWeapon_PlasmaSwordBladelink",
            "MeleeWeapon_Club",
            "Gun_MachinePistol",
            "Gun_HeavySMG",
            "MeleeWeapon_Knife",
            "MeleeWeapon_MonoSword",
            "MeleeWeapon_Ikwa",
            "Gun_Revolver",
            "AlphaThrumboHorn",
            "MeleeWeapon_PsyfocusStaff",
            "Gun_Autopistol",
            "MeleeWeapon_LongSword",
            "MeleeWeapon_Mace",
            "ThrumboHorn",
            "Gun_HeavySMG_Unique",
            "Gun_Revolver_Unique",
            "MeleeWeapon_PlasmaSword"
        };
        private static readonly Dictionary<ThingDef, RimKataGripType> gripCache = new Dictionary<ThingDef, RimKataGripType>();
        private static HashSet<string> forcedTwoHandedDefNames;
        private static HashSet<string> forcedOneHandedDefNames;

        public static RimKataGripType GripTypeFor(ThingDef weaponDef)
        {
            if (weaponDef == null)
            {
                return RimKataGripType.OneHand;
            }

            if (gripCache.TryGetValue(weaponDef, out RimKataGripType cached))
            {
                return cached;
            }

            EnsureOverrides();
            RimKataGripType result;
            if (forcedOneHandedDefNames.Contains(weaponDef.defName))
            {
                result = RimKataGripType.OneHand;
            }
            else if (forcedTwoHandedDefNames.Contains(weaponDef.defName))
            {
                result = RimKataGripType.TwoHand;
            }
            else
            {
                result = AutoGripTypeFor(weaponDef);
            }

            gripCache[weaponDef] = result;
            return result;
        }

        public static RimKataGripType AutoGripTypeFor(ThingDef weaponDef)
        {
            if (weaponDef == null)
            {
                return RimKataGripType.OneHand;
            }

            // Non-human equipment starts at two hands, including modded weapons.
            // Explicit one-hand selections still take priority in GripTypeFor.
            if (RimKataNonHumanEquipmentCatalog.IsCandidate(weaponDef, RimKataDefSelectionKind.Weapon))
            {
                return RimKataGripType.TwoHand;
            }

            ModContentPack sourceMod = weaponDef.modContentPack;
            if (sourceMod != null && (sourceMod.IsCoreMod || sourceMod.IsOfficialMod))
            {
                return officialOneHandedDefNames.Contains(weaponDef.defName)
                    ? RimKataGripType.OneHand
                    : RimKataGripType.TwoHand;
            }

            if (ContainsTwoHandToken(weaponDef.defName)
                || ContainsTwoHandToken(weaponDef.label)
                || ContainsTwoHandToken(weaponDef.description)
                || AnyContainsTwoHandToken(weaponDef.weaponTags)
                || ContainsTwoHandToken(weaponDef.thingClass?.FullName))
            {
                return RimKataGripType.TwoHand;
            }

            if (weaponDef.comps != null)
            {
                for (int i = 0; i < weaponDef.comps.Count; i++)
                {
                    if (ContainsTwoHandToken(weaponDef.comps[i]?.GetType().FullName))
                    {
                        return RimKataGripType.TwoHand;
                    }
                }
            }

            if (weaponDef.modExtensions != null)
            {
                for (int i = 0; i < weaponDef.modExtensions.Count; i++)
                {
                    if (ContainsTwoHandToken(weaponDef.modExtensions[i]?.GetType().FullName))
                    {
                        return RimKataGripType.TwoHand;
                    }
                }
            }

            if (weaponDef.weaponClasses != null)
            {
                for (int i = 0; i < weaponDef.weaponClasses.Count; i++)
                {
                    WeaponClassDef weaponClass = weaponDef.weaponClasses[i];
                    if (ContainsTwoHandToken(weaponClass?.defName) || ContainsTwoHandToken(weaponClass?.label))
                    {
                        return RimKataGripType.TwoHand;
                    }
                }
            }

            return RimKataGripType.OneHand;
        }

        public static void InvalidateCache()
        {
            gripCache.Clear();
            forcedTwoHandedDefNames = null;
            forcedOneHandedDefNames = null;
        }

        private static void EnsureOverrides()
        {
            if (forcedTwoHandedDefNames != null && forcedOneHandedDefNames != null)
            {
                return;
            }

            RimKataSettings settings = RimKataMod.Settings;
            forcedTwoHandedDefNames = new HashSet<string>(settings?.twoHandWeaponDefNames ?? new List<string>(), StringComparer.Ordinal);
            forcedOneHandedDefNames = new HashSet<string>(settings?.oneHandWeaponOverrideDefNames ?? new List<string>(), StringComparer.Ordinal);
        }

        private static bool AnyContainsTwoHandToken(IEnumerable<string> values)
        {
            if (values == null)
            {
                return false;
            }

            foreach (string value in values)
            {
                if (ContainsTwoHandToken(value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsTwoHandToken(string value)
        {
            return !value.NullOrEmpty()
                && (value.IndexOf("twohand", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("two hand", StringComparison.OrdinalIgnoreCase) >= 0);
        }

    }
}
