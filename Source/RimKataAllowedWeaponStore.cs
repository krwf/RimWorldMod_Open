using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // This is a storage Def, never a ThingDef. The nested source XML is deliberately
    // opaque to the game loader, including when its originating mod was removed.
    public sealed class RimKataAllowedWeaponDef : Def
    {
        public int schemaVersion;
        public string sourceDefName;
        public string sourceModId;
        public string sourceDefType;
        public string fingerprint;
        public bool sourceXmlAvailable;
        public string sourceXmlStatus;
        public RimKataWeaponXmlPayload sourceWeapon = new RimKataWeaponXmlPayload();
        public List<RimKataStoredVerbTiming> convertedVerbs = new List<RimKataStoredVerbTiming>();
    }

    public sealed class RimKataWeaponXmlPayload
    {
        internal string Xml;

        public void LoadDataFromXmlCustom(XmlNode node)
        {
            Xml = null;
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child is XmlElement)
                {
                    // A string retains no reference to the unified mod document.
                    Xml = child.OuterXml;
                    break;
                }
            }
        }
    }

    public sealed class RimKataStoredVerbTiming
    {
        public int verbIndex;
        public string verbPropertiesType;
        public bool convertSingleShot;
        public int originalBurstCount = 1;
        public int originalBurstSpacing;
        public float originalWarmupSeconds;
        public int convertedBurstCount = 1;
        public int convertedBurstSpacing;
        public float convertedWarmupSeconds;
        public int timingBurstCount = 1;
        public float totalBurstSpacingTicks;
        public float experienceCycleCorrectionSeconds;

        internal static RimKataStoredVerbTiming Create(
            VerbProperties original, int index, int burstCount, int burstSpacing)
        {
            int count = Mathf.Max(1, burstCount);
            int spacing = Mathf.Max(0, burstSpacing);
            float warmup = NonNegativeFinite(original?.warmupTime ?? 0f);
            bool convert = original != null && !original.IsMeleeAttack && count > 1;
            int timingCount = convert ? count : 1;
            float convertedWarmup = warmup / timingCount;
            return new RimKataStoredVerbTiming
            {
                verbIndex = index,
                verbPropertiesType = original?.GetType().AssemblyQualifiedName ?? string.Empty,
                convertSingleShot = convert,
                originalBurstCount = count,
                originalBurstSpacing = spacing,
                originalWarmupSeconds = warmup,
                convertedBurstCount = convert ? 1 : count,
                convertedBurstSpacing = spacing,
                convertedWarmupSeconds = convertedWarmup,
                timingBurstCount = timingCount,
                totalBurstSpacingTicks = (timingCount - 1f) * spacing,
                experienceCycleCorrectionSeconds = convert
                    ? warmup - convertedWarmup + (count - 1f) * spacing / 60f : 0f
            };
        }

        internal bool Matches(VerbProperties original, int index)
        {
            RimKataStoredVerbTiming expected = Create(original, index,
                original?.burstShotCount ?? 1, original?.ticksBetweenBurstShots ?? 0);
            return verbIndex == expected.verbIndex
                && verbPropertiesType == expected.verbPropertiesType
                && convertSingleShot == expected.convertSingleShot
                && originalBurstCount == expected.originalBurstCount
                && originalBurstSpacing == expected.originalBurstSpacing
                && originalWarmupSeconds == expected.originalWarmupSeconds
                && convertedBurstCount == expected.convertedBurstCount
                && convertedBurstSpacing == expected.convertedBurstSpacing
                && convertedWarmupSeconds == expected.convertedWarmupSeconds
                && timingBurstCount == expected.timingBurstCount
                && totalBurstSpacingTicks == expected.totalBurstSpacingTicks
                && experienceCycleCorrectionSeconds == expected.experienceCycleCorrectionSeconds;
        }

        private static float NonNegativeFinite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Max(0f, value);
        }
    }

    internal static class RimKataAllowedWeaponStore
    {
        private const int SchemaVersion = 1;
        private const string StorageTypeName = "KRWF.RimKata.RimKataAllowedWeaponDef";
        private static readonly Dictionary<string, string> CapturedXml =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<ThingDef, RimKataAllowedWeaponDef> Loaded =
            new Dictionary<ThingDef, RimKataAllowedWeaponDef>(RimKataReferenceComparer<ThingDef>.Instance);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static string directory;
        private static bool captureHookInstalled;

        internal static void ConfigureRoot(string modRoot)
        {
            directory = string.IsNullOrWhiteSpace(modRoot)
                ? null : Path.GetFullPath(Path.Combine(modRoot, "Defs", "AllowedWeapons"));
        }

        // Mod classes are constructed before XML patches/inheritance are applied.
        // StaticConstructorOnStartup runs too late to install these capture hooks.
        internal static void InstallCaptureHook()
        {
            if (captureHookInstalled)
            {
                return;
            }
            try
            {
                Harmony harmony = new Harmony("krwf.rimkata.allowed-weapon-xml");
                HarmonyMethod capture = new HarmonyMethod(
                    AccessTools.Method(typeof(RimKataAllowedWeaponStore), nameof(CaptureDefinition)));
                MethodInfo modern = AccessTools.Method(
                    AccessTools.TypeByName("Verse.DirectXmlToObjectNew"), "DefFromNodeNew",
                    new[] { typeof(XmlNode), typeof(LoadableXmlAsset) });
                MethodInfo legacy = AccessTools.Method(typeof(DirectXmlLoader), "DefFromNode",
                    new[] { typeof(XmlNode), typeof(LoadableXmlAsset) });
                if (modern == null && legacy == null)
                {
                    throw new MissingMethodException("No supported RimWorld XML Def parser was found.");
                }
                if (modern != null)
                {
                    harmony.Patch(modern, postfix: capture);
                }
                if (legacy != null)
                {
                    harmony.Patch(legacy, postfix: capture);
                }
                harmony.Patch(AccessTools.Method(typeof(LoadedModManager), "ParseAndProcessXML"),
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(RimKataAllowedWeaponStore), nameof(BeginXmlLoad))));
                captureHookInstalled = true;
            }
            catch (Exception exception)
            {
                WarnOnce("capture-hook", "Could not capture resolved weapon XML. "
                    + "Weapon preparation will still use the loaded definitions. " + exception.Message);
            }
        }

        private static void BeginXmlLoad()
        {
            CapturedXml.Clear();
            Loaded.Clear();
        }

        private static void CaptureDefinition(XmlNode node, Def __result)
        {
            if (!(__result is ThingDef definition) || definition.defName.NullOrEmpty()
                || (definition.equipmentType != EquipmentType.Primary && !definition.IsWeapon))
            {
                return;
            }
            try
            {
                XmlNode resolved = XmlInheritance.GetResolvedNodeFor(node);
                if (resolved != null)
                {
                    // Serialize the resolved node immediately; no parent document,
                    // inheritance cache, or original XML asset remains referenced.
                    CapturedXml[definition.defName] = resolved.OuterXml;
                }
            }
            catch (Exception exception)
            {
                WarnOnce("capture:" + definition.defName,
                    "Could not capture XML for " + definition.defName + ". " + exception.Message);
            }
        }

        internal static void RemoveDisallowedWeapons(ICollection<string> selected)
        {
            if (selected == null || directory == null)
            {
                return;
            }

            HashSet<string> allowed = new HashSet<string>(selected, StringComparer.Ordinal);
            List<RimKataAllowedWeaponDef> records = new List<RimKataAllowedWeaponDef>(
                DefDatabase<RimKataAllowedWeaponDef>.AllDefsListForReading);
            records.AddRange(Loaded.Values);
            HashSet<string> checkedNames = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> removedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < records.Count; i++)
            {
                RimKataAllowedWeaponDef record = records[i];
                string name = record?.sourceDefName;
                if (name.NullOrEmpty() || allowed.Contains(name)
                    || record.defName != "RimKata_AllowedWeapon_" + FileStem(name)
                    || !checkedNames.Add(name))
                {
                    continue;
                }

                try
                {
                    // FileStem is also used by Write: only this weapon's flat
                    // storage file under Defs/AllowedWeapons is removed.
                    File.Delete(Path.Combine(directory, FileStem(name) + ".xml"));
                    removedNames.Add(name);
                }
                catch (Exception exception)
                {
                    // Keep its record so a later settings refresh can retry.
                    WarnOnce("delete:" + name,
                        "Could not remove the prepared weapon file for " + name + ". " + exception.Message);
                }
            }
            if (removedNames.Count == 0)
            {
                return;
            }

            foreach (ThingDef original in new List<ThingDef>(Loaded.Keys))
            {
                if (removedNames.Contains(Loaded[original].sourceDefName))
                {
                    Loaded.Remove(original);
                }
            }
        }

        internal static bool RequiresSingleShotConversion(ThingDef definition)
        {
            List<VerbProperties> verbs = definition?.Verbs;
            if (verbs == null)
            {
                return false;
            }
            for (int i = 0; i < verbs.Count; i++)
            {
                VerbProperties properties = verbs[i];
                if (properties != null && !properties.IsMeleeAttack && properties.burstShotCount > 1)
                {
                    return true;
                }
            }
            return false;
        }

        internal static RimKataAllowedWeaponDef LoadOrCreate(ThingDef definition)
        {
            // Filter before consulting either storage cache or disk. Weapons
            // without a ranged burst need neither a copy nor a timing file.
            if (!RequiresSingleShotConversion(definition))
            {
                return null;
            }
            CapturedXml.TryGetValue(definition.defName, out string sourceXml);
            string fingerprint = ComputeFingerprint(definition, sourceXml);
            if (Loaded.TryGetValue(definition, out RimKataAllowedWeaponDef cached)
                && cached.fingerprint == fingerprint)
            {
                return cached;
            }

            string path = directory == null ? null : Path.Combine(directory, FileStem(definition.defName) + ".xml");
            // The game's Def loader has already read existing storage wrappers.
            // Reuse that object first; only newly created or stale records need
            // direct file loading during a later settings refresh.
            RimKataAllowedWeaponDef result = DefDatabase<RimKataAllowedWeaponDef>.GetNamedSilentFail(
                "RimKata_AllowedWeapon_" + FileStem(definition.defName));
            if (result != null && !IsCurrent(result, definition, sourceXml, fingerprint))
            {
                result = null;
            }
            if (result == null && path != null && File.Exists(path))
            {
                try
                {
                    RimKataAllowedWeaponDef stored = Read(path);
                    if (IsCurrent(stored, definition, sourceXml, fingerprint))
                    {
                        result = stored;
                    }
                }
                catch (Exception exception)
                {
                    WarnOnce("read:" + definition.defName,
                        "Could not read the prepared weapon file for " + definition.defName
                        + "; it will be regenerated. " + exception.Message);
                }
            }
            bool createFile = result == null;
            if (createFile)
            {
                result = Create(definition, sourceXml, fingerprint);
            }
            // A wrapper loaded at startup can outlive its deleted file. Reusing
            // it after the weapon is allowed again must recreate that file.
            if (path != null && (createFile || !File.Exists(path)))
            {
                try
                {
                    Write(path, result);
                }
                catch (Exception exception)
                {
                    WarnOnce("write:" + definition.defName,
                        "Could not save the prepared weapon file for " + definition.defName
                        + ". The in-memory prepared weapon remains available. " + exception.Message);
                }
            }
            Loaded[definition] = result;
            return result;
        }

        private static bool IsCurrent(RimKataAllowedWeaponDef stored, ThingDef definition,
            string sourceXml, string fingerprint)
        {
            List<VerbProperties> verbs = definition.Verbs;
            if (stored.schemaVersion != SchemaVersion || stored.sourceDefName != definition.defName
                || stored.sourceDefType != definition.GetType().AssemblyQualifiedName
                || stored.fingerprint != fingerprint || stored.sourceXmlAvailable != (sourceXml != null)
                || CanonicalXml(stored.sourceWeapon?.Xml) != CanonicalXml(sourceXml)
                || stored.convertedVerbs == null
                || stored.convertedVerbs.Count != verbs.Count)
            {
                return false;
            }
            for (int i = 0; i < verbs.Count; i++)
            {
                if (stored.convertedVerbs[i] == null || !stored.convertedVerbs[i].Matches(verbs[i], i))
                {
                    return false;
                }
            }
            return true;
        }

        private static RimKataAllowedWeaponDef Create(
            ThingDef definition, string sourceXml, string fingerprint)
        {
            RimKataAllowedWeaponDef result = new RimKataAllowedWeaponDef
            {
                defName = "RimKata_AllowedWeapon_" + FileStem(definition.defName),
                label = definition.label,
                schemaVersion = SchemaVersion,
                sourceDefName = definition.defName,
                sourceModId = definition.modContentPack?.PackageIdPlayerFacing ?? string.Empty,
                sourceDefType = definition.GetType().AssemblyQualifiedName,
                fingerprint = fingerprint,
                sourceXmlAvailable = sourceXml != null,
                sourceXmlStatus = sourceXml != null
                    ? "Captured after XML patches and inheritance resolution."
                    : "No source XML was captured. Prepared from the loaded runtime definition.",
                sourceWeapon = new RimKataWeaponXmlPayload { Xml = sourceXml }
            };
            List<VerbProperties> verbs = definition.Verbs;
            for (int i = 0; i < verbs.Count; i++)
            {
                VerbProperties properties = verbs[i];
                result.convertedVerbs.Add(RimKataStoredVerbTiming.Create(properties, i,
                    properties?.burstShotCount ?? 1, properties?.ticksBetweenBurstShots ?? 0));
            }
            return result;
        }

        private static string ComputeFingerprint(ThingDef definition, string sourceXml)
        {
            StringBuilder input = new StringBuilder();
            input.Append(SchemaVersion).Append('\n').Append(definition.defName).Append('\n')
                .Append(definition.GetType().AssemblyQualifiedName).Append('\n')
                .Append(definition.modContentPack?.PackageIdPlayerFacing).Append('\n')
                .Append(CanonicalXml(sourceXml) ?? "<no-source-xml>").Append('\n');
            List<VerbProperties> verbs = definition.Verbs;
            for (int i = 0; i < verbs.Count; i++)
            {
                VerbProperties properties = verbs[i];
                input.Append(i).Append('|').Append(properties?.GetType().AssemblyQualifiedName).Append('|')
                    .Append(properties?.IsMeleeAttack ?? false).Append('|')
                    .Append(properties?.burstShotCount ?? 1).Append('|')
                    .Append(properties?.ticksBetweenBurstShots ?? 0).Append('|')
                    .Append((properties?.warmupTime ?? 0f).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            return Hash(input.ToString());
        }

        private static string Hash(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder output = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    output.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return output.ToString();
            }
        }

        private static string CanonicalXml(string xml)
        {
            if (xml == null)
            {
                return null;
            }
            XmlDocument document = new XmlDocument { XmlResolver = null };
            document.LoadXml(xml);
            return document.DocumentElement.OuterXml;
        }

        private static string FileStem(string defName)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder result = new StringBuilder(defName.Length);
            for (int i = 0; i < defName.Length; i++)
            {
                char character = defName[i];
                result.Append(Array.IndexOf(invalid, character) >= 0 || character == '.' ? '_' : character);
            }
            string stem = result.ToString();
            return stem == defName ? stem : stem + "_" + Hash(defName).Substring(0, 8);
        }

        private static XmlDocument ReadDocument(string path)
        {
            XmlDocument document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (XmlReader reader = XmlReader.Create(path,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                document.Load(reader);
            }
            return document;
        }

        private static RimKataAllowedWeaponDef Read(string path)
        {
            XmlDocument document = ReadDocument(path);
            XmlElement root = document.DocumentElement;
            XmlElement element = root?.SelectSingleNode(StorageTypeName) as XmlElement;
            if (root?.Name != "Defs" || element == null)
            {
                throw new InvalidDataException("The stored weapon Def wrapper is missing.");
            }
            RimKataAllowedWeaponDef result = new RimKataAllowedWeaponDef
            {
                defName = Text(element, "defName"),
                label = Text(element, "label"),
                schemaVersion = Integer(element, "schemaVersion"),
                sourceDefName = Text(element, "sourceDefName"),
                sourceModId = Text(element, "sourceModId"),
                sourceDefType = Text(element, "sourceDefType"),
                fingerprint = Text(element, "fingerprint"),
                sourceXmlAvailable = Boolean(element, "sourceXmlAvailable"),
                sourceXmlStatus = Text(element, "sourceXmlStatus")
            };
            XmlNode payload = element.SelectSingleNode("sourceWeapon");
            if (payload != null)
            {
                result.sourceWeapon.LoadDataFromXmlCustom(payload);
            }
            XmlNode entries = element.SelectSingleNode("convertedVerbs");
            if (entries == null)
            {
                throw new InvalidDataException("The stored conversion list is missing.");
            }
            foreach (XmlNode entry in entries.ChildNodes)
            {
                if (!(entry is XmlElement) || entry.Name != "li")
                {
                    continue;
                }
                result.convertedVerbs.Add(new RimKataStoredVerbTiming
                {
                    verbIndex = Integer(entry, "verbIndex"),
                    verbPropertiesType = Text(entry, "verbPropertiesType"),
                    convertSingleShot = Boolean(entry, "convertSingleShot"),
                    originalBurstCount = Integer(entry, "originalBurstCount"),
                    originalBurstSpacing = Integer(entry, "originalBurstSpacing"),
                    originalWarmupSeconds = Number(entry, "originalWarmupSeconds"),
                    convertedBurstCount = Integer(entry, "convertedBurstCount"),
                    convertedBurstSpacing = Integer(entry, "convertedBurstSpacing"),
                    convertedWarmupSeconds = Number(entry, "convertedWarmupSeconds"),
                    timingBurstCount = Integer(entry, "timingBurstCount"),
                    totalBurstSpacingTicks = Number(entry, "totalBurstSpacingTicks"),
                    experienceCycleCorrectionSeconds = Number(entry, "experienceCycleCorrectionSeconds")
                });
            }
            return result;
        }

        private static void Write(string path, RimKataAllowedWeaponDef value)
        {
            XmlDocument document = new XmlDocument { XmlResolver = null };
            XmlElement root = document.CreateElement("Defs");
            document.AppendChild(root);
            XmlElement element = document.CreateElement(StorageTypeName);
            root.AppendChild(element);
            Append(document, element, "defName", value.defName);
            Append(document, element, "label", value.label ?? value.sourceDefName);
            Append(document, element, "schemaVersion", value.schemaVersion);
            Append(document, element, "sourceDefName", value.sourceDefName);
            Append(document, element, "sourceModId", value.sourceModId);
            Append(document, element, "sourceDefType", value.sourceDefType);
            Append(document, element, "fingerprint", value.fingerprint);
            Append(document, element, "sourceXmlAvailable", value.sourceXmlAvailable);
            Append(document, element, "sourceXmlStatus", value.sourceXmlStatus);
            XmlElement payload = document.CreateElement("sourceWeapon");
            element.AppendChild(payload);
            if (value.sourceWeapon?.Xml != null)
            {
                XmlDocument source = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
                source.LoadXml(value.sourceWeapon.Xml);
                payload.AppendChild(document.ImportNode(source.DocumentElement, true));
            }
            XmlElement entries = document.CreateElement("convertedVerbs");
            element.AppendChild(entries);
            for (int i = 0; i < value.convertedVerbs.Count; i++)
            {
                RimKataStoredVerbTiming timing = value.convertedVerbs[i];
                XmlElement entry = document.CreateElement("li");
                entries.AppendChild(entry);
                Append(document, entry, "verbIndex", timing.verbIndex);
                Append(document, entry, "verbPropertiesType", timing.verbPropertiesType);
                Append(document, entry, "convertSingleShot", timing.convertSingleShot);
                Append(document, entry, "originalBurstCount", timing.originalBurstCount);
                Append(document, entry, "originalBurstSpacing", timing.originalBurstSpacing);
                Append(document, entry, "originalWarmupSeconds", timing.originalWarmupSeconds);
                Append(document, entry, "convertedBurstCount", timing.convertedBurstCount);
                Append(document, entry, "convertedBurstSpacing", timing.convertedBurstSpacing);
                Append(document, entry, "convertedWarmupSeconds", timing.convertedWarmupSeconds);
                Append(document, entry, "timingBurstCount", timing.timingBurstCount);
                Append(document, entry, "totalBurstSpacingTicks", timing.totalBurstSpacingTicks);
                Append(document, entry, "experienceCycleCorrectionSeconds", timing.experienceCycleCorrectionSeconds);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n"
                }))
                {
                    document.Save(writer);
                }
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static string Text(XmlNode parent, string name)
        {
            return parent.SelectSingleNode(name)?.InnerText ?? string.Empty;
        }

        private static int Integer(XmlNode parent, string name) => XmlConvert.ToInt32(Text(parent, name));
        private static float Number(XmlNode parent, string name) => XmlConvert.ToSingle(Text(parent, name));
        private static bool Boolean(XmlNode parent, string name) => XmlConvert.ToBoolean(Text(parent, name));

        private static void Append(XmlDocument document, XmlNode parent, string name, object value)
        {
            XmlElement element = document.CreateElement(name);
            element.InnerText = value is float number ? XmlConvert.ToString(number)
                : value is bool flag ? XmlConvert.ToString(flag)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            parent.AppendChild(element);
        }

        private static void WarnOnce(string key, string message)
        {
            if (Reported.Add(key))
            {
                Log.Warning("[RimKata] " + message);
            }
        }
    }
}
