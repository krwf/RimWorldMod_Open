using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Xml;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataSettingsProfileDef : Def
    {
        public List<string> entries = new List<string>();
    }

    public sealed class RimKataStoredProfile
    {
        public string Id { get; }
        public string Name { get; private set; }
        public RimKataSettingsProfile Values { get; private set; }
        internal RimKataSettings RuntimeSettings { get; private set; }
        internal string FilePath { get; set; }

        internal RimKataStoredProfile(string id, string name, RimKataSettingsProfile values, string filePath)
        {
            Id = id;
            Name = name;
            Values = values;
            FilePath = filePath;
            RuntimeSettings = new RimKataSettings();
            values.ApplyTo(RuntimeSettings);
        }

        internal void AcceptSaved(string name, RimKataSettingsProfile values)
        {
            Name = name;
            Values = values;
            RuntimeSettings = new RimKataSettings();
            values.ApplyTo(RuntimeSettings);
        }
    }

    public sealed class RimKataProfileStore
    {
        private const string ProfileTypeName = "KRWF.RimKata.RimKataSettingsProfileDef";
        private const string NormalId = "RimKata_Profile_Normal";
        private const string OpId = "RimKata_Profile_Op";
        private readonly string directory;
        private readonly string legacyDirectory;
        private readonly List<RimKataStoredProfile> profiles = new List<RimKataStoredProfile>();
        private List<RimKataStoredProfile> preparedProfiles;
        private Exception preparationError;
        public IReadOnlyList<RimKataStoredProfile> Profiles { get; }
        public RimKataStoredProfile Current { get; private set; }
        public bool IsInitialized { get; private set; }
        internal bool NeedsPersistence { get; private set; }

        public RimKataProfileStore(string modRoot)
        {
            if (string.IsNullOrWhiteSpace(modRoot))
                throw new ArgumentException("The mod root is missing.", nameof(modRoot));
            directory = Path.GetFullPath(Path.Combine(modRoot, "Profiles"));
            legacyDirectory = Path.GetFullPath(Path.Combine(modRoot, "Defs", "Profiles"));
            Profiles = profiles.AsReadOnly();
        }

        // Profiles are personal settings, not game Defs. Import legacy XML before LoadModXML;
        // subsequent edits travel with the normal ModSettings payload, including in multiplayer.
        internal void Prepare(RimKataSettings settings)
        {
            if (IsInitialized || preparedProfiles != null || preparationError != null) return;
            try
            {
                MigrateLegacyFiles();
                NeedsPersistence = settings.storedProfiles == null || settings.storedProfiles.Count == 0;
                var loaded = new List<RimKataStoredProfile>();
                RimKataSettingsProfile defaults = RimKataSettingsProfile.Capture(new RimKataSettings());
                if (settings.storedProfiles != null && settings.storedProfiles.Count != 0)
                {
                    foreach (RimKataProfileRecord record in settings.storedProfiles)
                    {
                        if (record == null || string.IsNullOrEmpty(record.id) || record.values == null)
                            throw new InvalidDataException("Invalid saved RimKata profile.");
                        RimKataSettingsProfile values = RimKataSettingsProfile.FromEntries(record.values.CopyEntries());
                        values.FillMissingFrom(defaults);
                        loaded.Add(CreateProfile(record.id, CheckName(record.name), values));
                    }
                }
                else if (Directory.Exists(directory))
                    foreach (string path in Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        loaded.Add(ReadProfile(path, defaults));

                var owners = new Dictionary<string, RimKataStoredProfile>(StringComparer.OrdinalIgnoreCase);
                var reservedIds = new HashSet<string>(loaded.Select(profile => profile.Id), StringComparer.OrdinalIgnoreCase);
                var reservedNames = new HashSet<string>(loaded.Select(profile => profile.Name), StringComparer.OrdinalIgnoreCase);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RimKataStoredProfile profile in loaded)
                {
                    bool exactActive = string.Equals(settings.ActiveProfileId, profile.Id, StringComparison.Ordinal);
                    if (!owners.TryGetValue(profile.Id, out RimKataStoredProfile owner)
                        || (exactActive && !string.Equals(settings.ActiveProfileId, owner.Id, StringComparison.Ordinal))
                        || (exactActive && settings.ActiveProfileId == owner.Id
                            && profile.Values.Matches(settings) && !owner.Values.Matches(settings)))
                        owners[profile.Id] = profile;
                }

                // An update can leave the same seed in both the old Def directory
                // and the new import directory. Preserve one identical identity.
                if (loaded.RemoveAll(profile => !ReferenceEquals(owners[profile.Id], profile)
                    && profile.Values.CopyEntries().SequenceEqual(owners[profile.Id].Values.CopyEntries())) > 0)
                    NeedsPersistence = true;

                for (int i = 0; i < loaded.Count; i++)
                {
                    RimKataStoredProfile profile = loaded[i];
                    string id = profile.Id;
                    string name = profile.Name;
                    if (!ReferenceEquals(owners[id], profile))
                        do { id = NewId(); } while (!reservedIds.Add(id));
                    if (!usedNames.Add(name))
                    {
                        for (int suffix = 2; ; suffix++)
                        {
                            string ending = " (" + suffix + ")";
                            name = profile.Name.Substring(0, Math.Min(profile.Name.Length, 60 - ending.Length)) + ending;
                            if (reservedNames.Add(name)) break;
                        }
                        usedNames.Add(name);
                    }
                    if (id == profile.Id && name == profile.Name) continue;
                    NeedsPersistence = true;
                    loaded[i] = new RimKataStoredProfile(id, name, profile.Values, profile.FilePath);
                }
                preparedProfiles = loaded;
            }
            catch (Exception exception)
            {
                // Report once through the normal initialization boundary, when Mod setup is complete.
                preparationError = exception;
            }
        }

        private void MigrateLegacyFiles()
        {
            if (!Directory.Exists(legacyDirectory)) return;
            string[] files = Directory.GetFiles(legacyDirectory, "*.xml", SearchOption.TopDirectoryOnly);
            if (files.Length == 0) return;
            Directory.CreateDirectory(directory);
            foreach (string source in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string fullSource = Path.GetFullPath(source);
                if (!string.Equals(Path.GetDirectoryName(fullSource), legacyDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A legacy profile is outside its directory.");
                string stem = Path.GetFileNameWithoutExtension(fullSource);
                string destination = Path.Combine(directory, Path.GetFileName(fullSource));
                for (int suffix = 2; File.Exists(destination) || Directory.Exists(destination); suffix++)
                    destination = Path.Combine(directory, stem + " (import " + suffix + ").xml");
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(destination)), directory,
                    StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A profile import destination is outside its directory.");
                // Preserve the old XML as an import source outside the game's Def input.
                File.Move(fullSource, destination);
            }
        }

        private void CaptureProfiles(RimKataSettings settings)
        {
            settings.storedProfiles = profiles.Select(profile => new RimKataProfileRecord
            {
                id = profile.Id, name = profile.Name,
                values = RimKataSettingsProfile.FromEntries(profile.Values.CopyEntries())
            }).ToList();
        }

        public void Initialize(RimKataSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (IsInitialized)
                return;

            Prepare(settings);
            if (preparationError != null)
            {
                Exception error = preparationError;
                preparationError = null;
                ExceptionDispatchInfo.Capture(error).Throw();
            }
            List<RimKataStoredProfile> loaded = new List<RimKataStoredProfile>(preparedProfiles);
            HashSet<string> ids = new HashSet<string>(loaded.Select(profile => profile.Id), StringComparer.OrdinalIgnoreCase);
            RimKataSettingsProfile defaults = RimKataSettingsProfile.Capture(new RimKataSettings());

            List<RimKataStoredProfile> pending = new List<RimKataStoredProfile>();
            RimKataStoredProfile selected;
            if (string.IsNullOrEmpty(settings.ActiveProfileId))
            {
                RimKataSettingsProfile normal = settings.LegacyOpProfileActive
                    ? settings.GetLegacyNormalProfile() : RimKataSettingsProfile.Capture(settings);
                RimKataSettingsProfile op = settings.LegacyOpProfileActive
                    ? RimKataSettingsProfile.Capture(settings) : settings.GetLegacyOpProfile();
                normal.FillMissingFrom(defaults);
                op.FillMissingFrom(defaults);
                string[] legacyIds = { NormalId, OpId };
                string[] legacyNames = { "OP OFF", "OP ON" };
                RimKataSettingsProfile[] legacyValues = { normal, op };
                for (int i = 0; i < legacyIds.Length; i++)
                {
                    if (ids.Contains(legacyIds[i]))
                        continue;
                    RimKataStoredProfile profile = CreateProfile(legacyIds[i],
                        UniqueName(legacyNames[i], loaded), legacyValues[i]);
                    loaded.Add(profile);
                    pending.Add(profile);
                }
                string legacyActiveId = settings.LegacyOpProfileActive ? OpId : NormalId;
                selected = loaded.Find(profile => string.Equals(profile.Id, legacyActiveId,
                    StringComparison.OrdinalIgnoreCase));
                if (!selected.Values.Matches(settings))
                    selected = null;
            }
            else
            {
                selected = loaded.Find(profile => string.Equals(profile.Id, settings.ActiveProfileId,
                    StringComparison.Ordinal));
            }
            if (selected == null)
            {
                selected = loaded.Find(profile => profile.Values.Matches(settings));
                if (selected == null)
                {
                    selected = CreateProfile(NewId(), UniqueName("Recovered profile", loaded),
                        RimKataSettingsProfile.Capture(settings));
                    loaded.Add(selected);
                    pending.Add(selected);
                }
            }
            if (settings.ProfileSavePending && !pending.Contains(selected))
            {
                RimKataSettingsProfile values = RimKataSettingsProfile.Capture(settings);
                selected.AcceptSaved(selected.Name, values);
            }

            NeedsPersistence |= settings.ActiveProfileId != selected.Id || settings.ProfileSavePending;
            selected.Values.ApplyTo(settings);
            settings.CompleteProfileMigration(selected.Id);
            settings.ProfileSavePending = false;
            profiles.AddRange(loaded);
            Current = selected;
            IsInitialized = true;
            CaptureProfiles(settings);
        }

        public void SaveCurrent(RimKataSettings settings)
        {
            RequireInitialized(settings);
            if (Current.Values.Matches(settings))
            {
                settings.ProfileSavePending = false;
                CaptureProfiles(settings);
                return;
            }
            RimKataSettingsProfile values = RimKataSettingsProfile.Capture(settings);
            settings.ProfileSavePending = true;
            Current.AcceptSaved(Current.Name, values);
            settings.ProfileSavePending = false;
            CaptureProfiles(settings);
        }

        public void Select(RimKataSettings settings, RimKataStoredProfile profile)
        {
            RequireInitialized(settings);
            if (profile == null || !profiles.Contains(profile))
                throw new ArgumentException("The selected profile is not in this store.", nameof(profile));
            SaveCurrent(settings);
            if (ReferenceEquals(profile, Current))
                return;
            profile.Values.ApplyTo(settings);
            Current = profile;
            settings.ActiveProfileId = profile.Id;
        }

        public void DeleteCurrent(RimKataSettings settings)
        {
            RequireInitialized(settings);
            if (profiles.Count <= 1)
                throw new InvalidOperationException("At least one profile must remain.");

            int index = profiles.IndexOf(Current);
            RimKataStoredProfile replacement = profiles[index > 0 ? index - 1 : 1];
            foreach (RimKataTargetRule rule in settings.targetAccessRules)
            {
                if (rule.profileIdFriendly == Current.Id) rule.profileIdFriendly = replacement.Id;
                if (rule.profileIdHostile == Current.Id) rule.profileIdHostile = replacement.Id;
            }
            profiles.RemoveAt(index);
            replacement.Values.ApplyTo(settings);
            Current = replacement;
            settings.ActiveProfileId = replacement.Id;
            settings.ProfileSavePending = false;
            CaptureProfiles(settings);
        }

        public void Add(RimKataSettings settings, string name)
        {
            RequireInitialized(settings);
            name = ValidateName(name, null);
            SaveCurrent(settings);
            RimKataStoredProfile profile = CreateProfile(NewId(), name, RimKataSettingsProfile.Capture(settings));
            profiles.Add(profile);
            Current = profile;
            settings.ActiveProfileId = profile.Id;
            CaptureProfiles(settings);
        }

        public void RenameCurrent(RimKataSettings settings, string name)
        {
            RequireInitialized(settings);
            name = ValidateName(name, Current);
            if (string.Equals(name, Current.Name, StringComparison.Ordinal))
            {
                SaveCurrent(settings);
                return;
            }
            RimKataSettingsProfile values = RimKataSettingsProfile.Capture(settings);
            settings.ProfileSavePending = true;
            Current.AcceptSaved(name, values);
            settings.ProfileSavePending = false;
            CaptureProfiles(settings);
        }

        private void RequireInitialized(RimKataSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (!IsInitialized)
                throw new InvalidOperationException("The profile store has not been initialized.");
        }

        private string ValidateName(string name, RimKataStoredProfile excluding)
        {
            name = CheckName(name);
            if (profiles.Exists(profile => !ReferenceEquals(profile, excluding)
                && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A profile with this name already exists.", nameof(name));
            return name;
        }

        private static string CheckName(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 60)
                throw new ArgumentException("Profile names must contain between 1 and 60 characters.", nameof(name));
            XmlConvert.VerifyXmlChars(name);
            return name;
        }

        private RimKataStoredProfile CreateProfile(string id, string name, RimKataSettingsProfile values)
        {
            return new RimKataStoredProfile(id, name, values, null);
        }

        private static string NewId()
        {
            return "RimKata_Profile_" + Guid.NewGuid().ToString("N");
        }

        private static string UniqueName(string baseName, List<RimKataStoredProfile> loaded)
        {
            string name = baseName;
            for (int suffix = 2; loaded.Exists(profile => string.Equals(profile.Name, name,
                StringComparison.OrdinalIgnoreCase)); suffix++)
                name = baseName + " " + suffix;
            return name;
        }

        private RimKataStoredProfile ReadProfile(string path, RimKataSettingsProfile defaults)
        {
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The profile file is outside the profile directory.");
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            }))
                document.Load(reader);
            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "Defs" || root.ChildNodes.Count != 1
                || !(root.FirstChild is XmlElement definition) || definition.Name != ProfileTypeName
                || definition.ChildNodes.Count != 3)
                throw new InvalidDataException("Invalid profile XML structure: " + path);

            string id = ReadText(definition, "defName", path);
            if (id.Length == 0 || id.Length > 100 || !IsAsciiLetter(id[0])
                || id.Any(character => !IsAsciiLetter(character) && !char.IsDigit(character) && character != '_'))
                throw new InvalidDataException("Invalid profile ID: " + path);
            string name = CheckName(ReadText(definition, "label", path));
            XmlNode entriesNode = definition["entries"];
            if (entriesNode == null)
                throw new InvalidDataException("Missing profile entries: " + path);
            List<string> entries = new List<string>();
            foreach (XmlNode entry in entriesNode.ChildNodes)
            {
                if (!(entry is XmlElement element) || element.Name != "li"
                    || element.ChildNodes.Cast<XmlNode>().Any(child => child.NodeType != XmlNodeType.Text))
                    throw new InvalidDataException("Invalid profile entry: " + path);
                entries.Add(element.InnerText);
            }
            RimKataSettingsProfile values = RimKataSettingsProfile.FromEntries(entries);
            values.FillMissingFrom(defaults);
            return new RimKataStoredProfile(id, name, values, path);
        }

        private static bool IsAsciiLetter(char character)
        {
            return (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z');
        }

        private static string ReadText(XmlElement parent, string name, string path)
        {
            XmlNode node = parent[name];
            if (node == null || parent.ChildNodes.Cast<XmlNode>().Count(child => child.Name == name) != 1
                || node.ChildNodes.Cast<XmlNode>().Any(child => child.NodeType != XmlNodeType.Text))
                throw new InvalidDataException("Missing or invalid profile " + name + ": " + path);
            return node.InnerText;
        }

    }
}
