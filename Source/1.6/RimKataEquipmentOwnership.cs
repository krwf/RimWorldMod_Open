using System;
using System.Collections.Generic;
using System.Xml;
using Verse;
using Verse.Steam;

namespace KRWF.RimKata
{
    // Persist only known equipment ownership; inactive installed mods still own their data.
    internal static class RimKataEquipmentOwnership
    {
        private const string Category = "equipment_owner";
        private static readonly string Fingerprint = RimKataEquipmentMemory.Fingerprint("1");
        private static readonly Dictionary<string, Owner> owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        private static readonly List<string> pendingRemoval = new List<string>();
        private static bool initialized;

        private sealed class Owner
        {
            internal string Def, Mod;
            internal bool Official;
            internal readonly HashSet<string> Executions = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<KeyValuePair<string, string>> Payloads = new List<KeyValuePair<string, string>>();
        }

        internal static bool Initialize()
        {
            if (initialized) return false;
            initialized = true;
            var memory = RimKataEquipmentMemory.Current;
            foreach (var record in memory.GetMetadata(Category))
            {
                XmlElement data = record.Value;
                string name = data.GetAttribute("def"), mod = Normalize(data.GetAttribute("mod"));
                if (data.GetAttribute("schema") != "1" || name != record.Key || name.Length == 0 || mod.Length == 0) continue;
                var owner = new Owner { Def = name, Mod = mod, Official = data.GetAttribute("official") == "1" };
                foreach (XmlNode node in data.ChildNodes)
                {
                    if (node is XmlElement item && item.Name == "execution" && item.GetAttribute("key").Length != 0)
                        owner.Executions.Add(item.GetAttribute("key"));
                    else if (node is XmlElement payload && payload.Name == "payload")
                        owner.Payloads.Add(new KeyValuePair<string, string>(payload.GetAttribute("hash"), payload.GetAttribute("file")));
                }
                owners[name] = owner;
            }
            foreach (var record in RimKataAllowedWeaponStore.GetKnownOwners())
                if (!owners.ContainsKey(record.Key)) Remember(record.Key, record.Value, false);
            RememberSettings();
            var knownKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Owner owner in owners.Values) knownKeys.UnionWith(owner.Executions);
            foreach (var record in memory.GetMetadata("weapon_effect_status"))
                RestoreExecutionLink(record.Key, record.Value.GetAttribute("weapon"), knownKeys);
            foreach (var record in memory.GetAnalysisWeaponLinks(knownKeys))
                RestoreExecutionLink(record.Key, record.Value, knownKeys);
            foreach (Owner owner in new List<Owner>(owners.Values))
            {
                ThingDef current = DefDatabase<ThingDef>.GetNamedSilentFail(owner.Def);
                if (current != null) RememberDefinition(current);
            }
            if (!TryInstalledPackages(out HashSet<string> installed)) return false;

            bool changed = false;
            var missing = new Dictionary<string, Owner>(StringComparer.Ordinal);
            foreach (Owner owner in owners.Values)
            {
                // A current definition wins even if another mod now supplies the same name.
                if (owner.Official || OfficialPackage(owner.Mod) || installed.Contains(owner.Mod)
                    || DefDatabase<ThingDef>.GetNamedSilentFail(owner.Def) != null) continue;
                missing.Add(owner.Def, owner);
            }
            foreach (Owner owner in missing.Values)
            {
                try
                {
                    bool removed = RimKataAllowedWeaponStore.RemoveDefinition(owner.Def);
                    removed = RimKataCloseFireMeleeStore.RemoveDefinition(owner.Def) && removed;
                    foreach (string key in owner.Executions)
                        foreach (string category in new[] { "weapon_execution", "weapon_effects" })
                            if (memory.TryGetAnalysisReference(category, key, out string payload, out string file))
                            {
                                var reference = new KeyValuePair<string, string>(payload, file);
                                if (!owner.Payloads.Contains(reference)) owner.Payloads.Add(reference);
                            }
                    // Keep exact pending files until both the index and physical deletion succeed.
                    Store(owner);
                    foreach (string key in owner.Executions)
                    {
                        memory.Remove("weapon_execution", key);
                        RimKataWeaponEffectCache.Invalidate(key);
                    }
                    RimKataSettings settings = RimKataMod.Settings;
                    if (settings != null)
                        foreach (List<string> list in Lists(settings))
                            if (list != null) changed |= list.RemoveAll(name => name == owner.Def) != 0;
                    if (removed) pendingRemoval.Add(owner.Def);
                }
                catch (Exception exception)
                {
                    Log.Warning("[RimKata] Could not finish removing equipment data for " + owner.Def
                        + ". Its ownership record is retained for the next startup. " + exception.Message);
                }
            }
            if (missing.Count != 0)
                foreach (var record in memory.GetMetadata("equipment-render-matches"))
                {
                    int separator = record.Key.IndexOf(':');
                    if (separator <= 0 || !missing.TryGetValue(record.Key.Substring(separator + 1), out Owner owner)) continue;
                    if (Normalize(record.Key.Substring(0, separator)) == owner.Mod)
                        memory.Remove("equipment-render-matches", record.Key);
                }
            return changed;
        }

        // The caller saves changed settings first. A failed save must keep ownership for retry.
        internal static void CommitCleanup()
        {
            if (pendingRemoval.Count == 0 || !RimKataEquipmentMemory.Flush()) return;
            foreach (string name in pendingRemoval)
            {
                bool deleted = true;
                foreach (var payload in owners[name].Payloads)
                    deleted = RimKataEquipmentMemory.Current.TryDeleteUnreferencedAnalysis(payload.Key, payload.Value) && deleted;
                if (!deleted) continue;
                owners.Remove(name);
                RimKataEquipmentMemory.Current.Remove(Category, name);
            }
            pendingRemoval.Clear();
        }

        internal static void RememberSettings()
        {
            RimKataSettings settings = RimKataMod.Settings;
            if (!initialized || settings == null) return;
            foreach (List<string> list in Lists(settings))
                if (list != null)
                    foreach (string name in list)
                        RememberDefinition(DefDatabase<ThingDef>.GetNamedSilentFail(name));
        }

        private static IEnumerable<List<string>> Lists(RimKataSettings settings)
        {
            yield return settings.enabledWeaponDefNames;
            yield return settings.enabledRangedArmorDefNames;
            yield return settings.enabledMeleeArmorDefNames;
            yield return settings.twoHandWeaponDefNames;
            yield return settings.oneHandWeaponOverrideDefNames;
        }

        internal static void RememberDefinition(ThingDef def)
        {
            if (!initialized || def?.modContentPack == null || (!def.IsWeapon && !def.IsApparel)) return;
            ModContentPack mod = def.modContentPack;
            Remember(def.defName, mod.ModMetaData?.PackageIdNonUnique ?? mod.PackageIdPlayerFacing, mod.IsOfficialMod);
        }

        internal static void RememberExecution(ThingDef def, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            RememberDefinition(def);
            if (def != null && owners.TryGetValue(def.defName, out Owner owner) && owner.Executions.Add(key)) Store(owner);
        }

        private static void RestoreExecutionLink(string key, string name, ISet<string> knownKeys)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name)) return;
            RememberDefinition(DefDatabase<ThingDef>.GetNamedSilentFail(name));
            if (!owners.TryGetValue(name, out Owner owner)) return;
            if (owner.Executions.Add(key)) Store(owner);
            knownKeys.Add(key);
        }

        // Call after all current verb types were prepared, never between two verbs of one weapon.
        internal static void ForgetObsoleteExecutions(ThingDef def, ISet<string> currentKeys)
        {
            if (def == null || !owners.TryGetValue(def.defName, out Owner owner)) return;
            bool changed = false;
            foreach (string key in new List<string>(owner.Executions))
            {
                if (currentKeys.Contains(key)) continue;
                RimKataEquipmentMemory.Current.Remove("weapon_execution", key);
                RimKataWeaponEffectCache.Invalidate(key);
                owner.Executions.Remove(key);
                changed = true;
            }
            if (changed) Store(owner);
        }

        private static void Remember(string name, string package, bool official)
        {
            string mod = Normalize(package);
            if (string.IsNullOrEmpty(name) || mod.Length == 0) return;
            if (!owners.TryGetValue(name, out Owner owner))
                owners.Add(name, owner = new Owner { Def = name });
            else if (owner.Mod == mod && owner.Official == official) return;
            owner.Mod = mod;
            owner.Official = official;
            Store(owner);
        }

        private static void Store(Owner owner)
        {
            XmlElement data = RimKataEquipmentMemory.NewData();
            data.SetAttribute("schema", "1");
            data.SetAttribute("def", owner.Def);
            data.SetAttribute("mod", owner.Mod);
            if (owner.Official) data.SetAttribute("official", "1");
            var keys = new List<string>(owner.Executions);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                XmlElement item = data.OwnerDocument.CreateElement("execution");
                item.SetAttribute("key", key);
                data.AppendChild(item);
            }
            foreach (var payload in owner.Payloads)
            {
                XmlElement item = data.OwnerDocument.CreateElement("payload");
                item.SetAttribute("hash", payload.Key);
                item.SetAttribute("file", payload.Value);
                data.AppendChild(item);
            }
            RimKataEquipmentMemory.Current.Store(Category, owner.Def, Fingerprint, data);
        }

        private static string Normalize(string package)
        {
            string value = package?.Trim().ToLowerInvariant() ?? string.Empty;
            string postfix = ModMetaData.SteamModPostfix;
            return value.EndsWith(postfix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - postfix.Length) : value;
        }

        private static bool OfficialPackage(string package)
        {
            foreach (string official in ModContentPack.ProductPackageIDs)
                if (Normalize(official) == package) return true;
            return false;
        }

        private static bool TryInstalledPackages(out HashSet<string> installed)
        {
            installed = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                // Do not interpret an unfinished Steam inventory as uninstalling every mod.
                if (!SteamManager.Initialized) return false;
                if (SteamManager.Active)
                {
                    if (WorkshopItems.DownloadingItemsCount != 0) return false;
                    foreach (WorkshopItem item in WorkshopItems.AllSubscribedItems)
                        if (item?.Directory == null || !item.Directory.Exists) return false;
                }
                foreach (ModMetaData mod in ModLister.AllInstalledMods)
                {
                    string package = Normalize(mod.PackageIdNonUnique);
                    if (package.Length != 0) installed.Add(package);
                }
                // An empty inventory is not evidence that all remembered mods were removed.
                return installed.Count != 0;
            }
            catch (Exception exception)
            {
                Log.Warning("[RimKata] Equipment ownership cleanup was skipped because the installed mod list"
                    + " is unavailable. " + exception.Message);
                return false;
            }
        }
    }
}

