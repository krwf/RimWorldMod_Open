using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private readonly List<RimKataStoredProfile> profiles = new List<RimKataStoredProfile>();
        public IReadOnlyList<RimKataStoredProfile> Profiles { get; }
        public RimKataStoredProfile Current { get; private set; }
        public bool IsInitialized { get; private set; }

        public RimKataProfileStore(string modRoot)
        {
            if (string.IsNullOrWhiteSpace(modRoot))
                throw new ArgumentException("The mod root is missing.", nameof(modRoot));
            directory = Path.GetFullPath(Path.Combine(modRoot, "Defs", "Profiles"));
            Profiles = profiles.AsReadOnly();
        }

        public void Initialize(RimKataSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (IsInitialized)
                return;

            // Validate every existing file before creating or changing any file.
            List<RimKataStoredProfile> loaded = new List<RimKataStoredProfile>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RimKataSettingsProfile defaults = RimKataSettingsProfile.Capture(new RimKataSettings());
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    RimKataStoredProfile profile = ReadProfile(path, defaults);
                    if (!ids.Add(profile.Id) || !names.Add(profile.Name))
                        throw new InvalidDataException("Duplicate profile ID or name: " + path);
                    loaded.Add(profile);
                }
            }

            foreach (RimKataStoredProfile profile in loaded)
                MoveProfileFile(profile, ProfileFilePath(profile.Name, profile.FilePath));

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
                // Preserve live settings when migrating beside existing files or recovering a missing selection.
                selected = CreateProfile(NewId(), UniqueName("Recovered profile", loaded),
                    RimKataSettingsProfile.Capture(settings));
                loaded.Add(selected);
                pending.Add(selected);
            }
            foreach (RimKataStoredProfile profile in pending)
                WriteProfile(profile, profile.Name, profile.Values, false);
            if (settings.ProfileSavePending && !pending.Contains(selected))
            {
                RimKataSettingsProfile values = RimKataSettingsProfile.Capture(settings);
                WriteProfile(selected, selected.Name, values, true);
                selected.AcceptSaved(selected.Name, values);
            }

            selected.Values.ApplyTo(settings);
            settings.CompleteProfileMigration(selected.Id);
            settings.ProfileSavePending = false;
            profiles.AddRange(loaded);
            Current = selected;
            IsInitialized = true;
        }

        public void SaveCurrent(RimKataSettings settings)
        {
            RequireInitialized(settings);
            if (Current.Values.Matches(settings))
            {
                settings.ProfileSavePending = false;
                return;
            }
            RimKataSettingsProfile values = RimKataSettingsProfile.Capture(settings);
            settings.ProfileSavePending = true;
            WriteProfile(Current, Current.Name, values, true);
            Current.AcceptSaved(Current.Name, values);
            settings.ProfileSavePending = false;
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
            File.Delete(Current.FilePath);
            foreach (RimKataTargetRule rule in settings.targetAccessRules)
            {
                if (rule.profileId == Current.Id) rule.profileId = replacement.Id;
            }
            profiles.RemoveAt(index);
            replacement.Values.ApplyTo(settings);
            Current = replacement;
            settings.ActiveProfileId = replacement.Id;
            settings.ProfileSavePending = false;
        }

        public void Add(RimKataSettings settings, string name)
        {
            RequireInitialized(settings);
            name = ValidateName(name, null);
            SaveCurrent(settings);
            RimKataStoredProfile profile = CreateProfile(NewId(), name, RimKataSettingsProfile.Capture(settings));
            WriteProfile(profile, profile.Name, profile.Values, false);
            profiles.Add(profile);
            Current = profile;
            settings.ActiveProfileId = profile.Id;
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
            WriteProfile(Current, name, values, true);
            Current.AcceptSaved(name, values);
            settings.ProfileSavePending = false;
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
            return new RimKataStoredProfile(id, name, values, ProfileFilePath(name, null));
        }

        private string ProfileFilePath(string name, string currentPath)
        {
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            string fileName = new string(name.Select(character =>
                Array.IndexOf(invalidCharacters, character) >= 0 ? '_' : character).ToArray())
                .TrimEnd(' ', '.');
            if (fileName.Length == 0)
                fileName = "_";

            string deviceName = fileName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (deviceName == "CON" || deviceName == "PRN" || deviceName == "AUX"
                || deviceName == "NUL" || deviceName == "CONIN$" || deviceName == "CONOUT$"
                || (deviceName.Length == 4
                    && (deviceName.StartsWith("COM", StringComparison.Ordinal)
                        || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".IndexOf(deviceName[3]) >= 0))
                fileName = "_" + fileName;

            string path = Path.Combine(directory, fileName + ".xml");
            for (int suffix = 2; !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)
                && (File.Exists(path) || Directory.Exists(path)); suffix++)
                path = Path.Combine(directory, fileName + " (" + suffix + ").xml");
            return path;
        }

        private static void MoveProfileFile(RimKataStoredProfile profile, string path)
        {
            if (string.Equals(profile.FilePath, path, StringComparison.Ordinal))
                return;
            File.Move(profile.FilePath, path);
            profile.FilePath = path;
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

        private void WriteProfile(RimKataStoredProfile profile, string name, RimKataSettingsProfile values,
            bool replaceExisting)
        {
            XmlDocument document = new XmlDocument { XmlResolver = null };
            XmlElement root = document.CreateElement("Defs");
            document.AppendChild(root);
            XmlElement definition = document.CreateElement(ProfileTypeName);
            root.AppendChild(definition);
            AppendText(document, definition, "defName", profile.Id);
            AppendText(document, definition, "label", name);
            XmlElement entries = document.CreateElement("entries");
            definition.AppendChild(entries);
            foreach (string entry in values.CopyEntries())
                AppendText(document, entries, "li", entry);

            Directory.CreateDirectory(directory);
            string previousPath = profile.FilePath;
            string destinationPath = ProfileFilePath(name, replaceExisting ? previousPath : null);
            string temporaryPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
                    {
                        Encoding = new UTF8Encoding(false),
                        Indent = true,
                        CloseOutput = false
                    }))
                        document.Save(writer);
                    stream.Flush(true);
                }
                if (replaceExisting && File.Exists(previousPath))
                {
                    // Move the existing XML before replacing its contents so there
                    // is never a second XML with the same defName during a rename.
                    MoveProfileFile(profile, destinationPath);
                    File.Replace(temporaryPath, profile.FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                    profile.FilePath = destinationPath;
                }
            }
            catch
            {
                // A failed replacement still contains the original XML. Restore
                // its filename if possible, without hiding the save exception.
                if (!string.Equals(profile.FilePath, previousPath, StringComparison.Ordinal))
                {
                    try { MoveProfileFile(profile, previousPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
            finally
            {
                // Cleanup must not replace the original save error or change an existing profile.
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void AppendText(XmlDocument document, XmlElement parent, string name, string value)
        {
            XmlElement element = document.CreateElement(name);
            element.InnerText = value;
            parent.AppendChild(element);
        }
    }
}
