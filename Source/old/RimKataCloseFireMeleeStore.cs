using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // This is RimKata selection metadata, not a replacement weapon or Verb definition.
    // Runtime damage, custom Verb classes and Tool subclasses remain on the original weapon.
    internal static class RimKataCloseFireMeleeStore
    {
        private const string SchemaVersion = "1";
        private const string RootName = "RimKataCloseFireMelee";
        private const string AbsentCategory = "close_fire_melee_absent";
        private static readonly HashSet<string> BasicToolLabels = new HashSet<string>(
            new[] { "handle", "handles", "grip", "grips", "stock", "stocks", "barrel", "barrels" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ThingDef, PreparedDefinition> Loaded =
            new Dictionary<ThingDef, PreparedDefinition>(RimKataReferenceComparer<ThingDef>.Instance);
        private static readonly Dictionary<ThingDef, PreparedDefinition> Prepared =
            new Dictionary<ThingDef, PreparedDefinition>(RimKataReferenceComparer<ThingDef>.Instance);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static string directory;
        private static string maneuverFingerprint;
        private static bool forceRefresh;

        private sealed class PreparedDefinition
        {
            internal string fingerprint;
            internal readonly List<int> toolIndices = new List<int>();
            internal readonly List<int> verbIndices = new List<int>();
            internal readonly HashSet<Tool> tools =
                new HashSet<Tool>(RimKataReferenceComparer<Tool>.Instance);
            internal readonly HashSet<VerbProperties> directVerbs =
                new HashSet<VerbProperties>(RimKataReferenceComparer<VerbProperties>.Instance);
            internal bool HasAdditionalMelee => toolIndices.Count != 0 || verbIndices.Count != 0;
        }

        internal static void ConfigureRoot(string modRoot)
        {
            directory = string.IsNullOrWhiteSpace(modRoot) ? null
                : Path.GetFullPath(Path.Combine(modRoot, "equipment-memory", "close-fire-melee"));
            ResetForXmlLoad();
        }

        internal static void ResetForXmlLoad()
        {
            Prepared.Clear();
            Loaded.Clear();
            maneuverFingerprint = null;
            forceRefresh = false;
        }

        // Call only after the refresh dialog is confirmed; closing it must not discard data.
        internal static void InvalidatePreparedCache()
        {
            Prepared.Clear();
            Loaded.Clear();
            maneuverFingerprint = null;
            forceRefresh = true;
        }

        internal static bool BeginPreparation()
        {
            Prepared.Clear();
            bool refresh = forceRefresh;
            forceRefresh = false;
            return refresh;
        }

        internal static void PrepareDefinition(ThingDef definition, bool refresh)
        {
            if (definition?.IsRangedWeapon != true) return;

            string fingerprint = Fingerprint(definition);
            if (!refresh && Loaded.TryGetValue(definition, out PreparedDefinition cached)
                && cached.fingerprint == fingerprint)
            {
                ResolveReferences(definition, cached);
                Prepared[definition] = cached;
                return;
            }

            PreparedDefinition result = null;
            // A checked ordinary gun needs only a compact entry in the shared cache.
            // Read that first so it does not require a per-weapon file lookup either.
            bool knownAbsent = !refresh && RimKataEquipmentMemory.Current.TryRead(
                AbsentCategory, definition.defName, fingerprint, out _);
            if (knownAbsent)
                result = new PreparedDefinition { fingerprint = fingerprint };
            string path = knownAbsent || directory == null ? null
                : Path.Combine(directory, RimKataAllowedWeaponStore.FileStem(definition.defName) + ".xml");
            if (result == null && !refresh && path != null && File.Exists(path))
            {
                try { result = Read(path, definition, fingerprint); }
                catch (Exception exception)
                {
                    WarnOnce("read:" + definition.defName,
                        "Could not read close-fire melee data for " + definition.defName
                        + "; it will be regenerated. " + exception.Message);
                }
            }
            if (result == null)
            {
                result = Extract(definition, fingerprint);
                if (result.HasAdditionalMelee && path != null)
                {
                    try { Write(path, definition, result); }
                    catch (Exception exception)
                    {
                        WarnOnce("write:" + definition.defName,
                            "Could not save close-fire melee data for " + definition.defName
                            + ". The in-memory data remains available. " + exception.Message);
                    }
                }
            }
            if (result.HasAdditionalMelee)
                RimKataEquipmentMemory.Current.Remove(AbsentCategory, definition.defName);
            else if (!knownAbsent)
                RimKataEquipmentMemory.Current.Store(AbsentCategory, definition.defName,
                    fingerprint, RimKataEquipmentMemory.NewData());
            ResolveReferences(definition, result);
            Loaded[definition] = result;
            Prepared[definition] = result;
        }

        // Called by a registered weapon cycle when its binding is dirty, never by GUI/Tick scans.
        internal static Verb[] Bind(ThingWithComps weapon)
        {
            if (weapon?.def == null || !Prepared.TryGetValue(weapon.def, out PreparedDefinition prepared)
                || !prepared.HasAdditionalMelee) return Array.Empty<Verb>();
            List<Verb> verbs = weapon.TryGetComp<CompEquippable>()?.AllVerbs;
            if (verbs == null) return Array.Empty<Verb>();
            List<Verb> result = null;
            for (int i = 0; i < verbs.Count; i++)
            {
                Verb verb = verbs[i];
                if (verb?.IsMeleeAttack != true) continue;
                bool selected = verb.tool != null ? prepared.tools.Contains(verb.tool)
                    : prepared.directVerbs.Contains(verb.verbProps);
                if (!selected) continue;
                (result ??= new List<Verb>()).Add(verb);
            }
            return result?.ToArray() ?? Array.Empty<Verb>();
        }

        private static PreparedDefinition Extract(ThingDef definition, string fingerprint)
        {
            var result = new PreparedDefinition { fingerprint = fingerprint };
            List<Tool> tools = definition.tools;
            if (tools != null)
            {
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool tool = tools[i];
                    if (tool == null || BasicToolLabels.Contains(OriginalLabel(tool).Trim())) continue;
                    foreach (ManeuverDef maneuver in tool.Maneuvers)
                    {
                        if (maneuver.verb?.IsMeleeAttack != true) continue;
                        result.toolIndices.Add(i);
                        break;
                    }
                }
            }
            List<VerbProperties> verbs = definition.Verbs;
            if (verbs != null)
            {
                for (int i = 0; i < verbs.Count; i++)
                    if (verbs[i]?.IsMeleeAttack == true) result.verbIndices.Add(i);
            }
            return result;
        }

        private static void ResolveReferences(ThingDef definition, PreparedDefinition prepared)
        {
            prepared.tools.Clear();
            prepared.directVerbs.Clear();
            for (int i = 0; i < prepared.toolIndices.Count; i++)
                prepared.tools.Add(definition.tools[prepared.toolIndices[i]]);
            List<VerbProperties> verbs = definition.Verbs;
            for (int i = 0; i < prepared.verbIndices.Count; i++)
                prepared.directVerbs.Add(verbs[prepared.verbIndices[i]]);
        }

        private static string Fingerprint(ThingDef definition)
        {
            if (maneuverFingerprint == null)
            {
                var maneuvers = new StringBuilder();
                List<ManeuverDef> all = DefDatabase<ManeuverDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                    maneuvers.Append(all[i].defName).Append('|').Append(all[i].requiredCapacity?.defName)
                        .Append('|').Append(all[i].verb?.verbClass?.AssemblyQualifiedName).Append('\n');
                maneuverFingerprint = RimKataEquipmentMemory.Fingerprint(maneuvers.ToString());
            }

            var text = new StringBuilder();
            text.Append(SchemaVersion).Append('\n').Append(definition.defName).Append('\n')
                .Append(definition.GetType().AssemblyQualifiedName).Append('\n')
                .Append(definition.modContentPack?.PackageIdPlayerFacing).Append('\n')
                .Append(maneuverFingerprint).Append('\n')
                .Append(RimKataAllowedWeaponStore.CapturedSourceXml(definition) ?? "<no-source-xml>").Append('\n');
            List<Tool> tools = definition.tools;
            if (tools != null)
            {
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool tool = tools[i];
                    text.Append(i).Append('|').Append(tool?.GetType().AssemblyQualifiedName)
                        .Append('|').Append(OriginalLabel(tool)).Append('|');
                    if (tool?.capacities != null)
                        for (int j = 0; j < tool.capacities.Count; j++)
                            text.Append(tool.capacities[j]?.defName).Append(';');
                    text.Append('\n');
                }
            }
            List<VerbProperties> verbs = definition.Verbs;
            if (verbs != null)
            {
                for (int i = 0; i < verbs.Count; i++)
                    text.Append(i).Append('|').Append(verbs[i]?.GetType().AssemblyQualifiedName)
                        .Append('|').Append(verbs[i]?.verbClass?.AssemblyQualifiedName).Append('\n');
            }
            return RimKataEquipmentMemory.Fingerprint(text.ToString());
        }

        private static string OriginalLabel(Tool tool)
        {
            return tool?.untranslatedLabel ?? tool?.label ?? string.Empty;
        }

        private static PreparedDefinition Read(string path, ThingDef definition, string fingerprint)
        {
            XmlElement root = RimKataAllowedWeaponStore.ReadDocument(path).DocumentElement;
            if (root?.Name != RootName || Text(root, "schemaVersion") != SchemaVersion
                || Text(root, "sourceDefName") != definition.defName
                || Text(root, "fingerprint") != fingerprint
                || Text(root, "closeFireOnly") != "true" || Text(root, "cooldownSource") != "rangedWeapon")
                return null;

            var result = new PreparedDefinition { fingerprint = fingerprint };
            ReadIndices(root, "tools", definition.tools?.Count ?? 0, result.toolIndices);
            ReadIndices(root, "directMeleeVerbs", definition.Verbs?.Count ?? 0, result.verbIndices);
            if (XmlConvert.ToBoolean(Text(root, "hasAdditionalMelee")) != result.HasAdditionalMelee)
                throw new InvalidDataException("The close-fire melee presence flag did not match its lists.");
            return result;
        }

        private static void ReadIndices(XmlNode root, string name, int count, List<int> indices)
        {
            XmlNode list = root.SelectSingleNode(name);
            if (list == null) throw new InvalidDataException("Missing close-fire melee list: " + name);
            foreach (XmlNode entry in list.ChildNodes)
            {
                if (!(entry is XmlElement) || entry.Name != "li") continue;
                int index = XmlConvert.ToInt32(Text(entry, "index"));
                if (index < 0 || index >= count || indices.Contains(index))
                    throw new InvalidDataException("Invalid close-fire melee index: " + index);
                indices.Add(index);
            }
        }

        private static void Write(string path, ThingDef definition, PreparedDefinition prepared)
        {
            var document = new XmlDocument { XmlResolver = null };
            XmlElement root = document.CreateElement(RootName);
            document.AppendChild(root);
            Append(document, root, "schemaVersion", SchemaVersion);
            Append(document, root, "sourceDefName", definition.defName);
            Append(document, root, "sourceModId", definition.modContentPack?.PackageIdPlayerFacing);
            Append(document, root, "fingerprint", prepared.fingerprint);
            Append(document, root, "closeFireOnly", "true");
            Append(document, root, "cooldownSource", "rangedWeapon");
            Append(document, root, "hasAdditionalMelee", XmlConvert.ToString(prepared.HasAdditionalMelee));
            root.AppendChild(document.CreateComment(
                "Tool exclusion is an exact original-label rule: handle(s), grip(s), stock(s), barrel(s). "
                + "Keep original melee Verb classes and damage; only RimKata's ranged slot selects these attacks."));
            XmlElement tools = document.CreateElement("tools");
            root.AppendChild(tools);
            for (int i = 0; i < prepared.toolIndices.Count; i++)
            {
                int index = prepared.toolIndices[i];
                XmlElement entry = document.CreateElement("li");
                tools.AppendChild(entry);
                Append(document, entry, "index", index.ToString(CultureInfo.InvariantCulture));
                Append(document, entry, "originalLabel", OriginalLabel(definition.tools[index]));
            }
            XmlElement verbs = document.CreateElement("directMeleeVerbs");
            root.AppendChild(verbs);
            for (int i = 0; i < prepared.verbIndices.Count; i++)
            {
                int index = prepared.verbIndices[i];
                XmlElement entry = document.CreateElement("li");
                verbs.AppendChild(entry);
                Append(document, entry, "index", index.ToString(CultureInfo.InvariantCulture));
                Append(document, entry, "verbClass", definition.Verbs[index].verbClass?.AssemblyQualifiedName);
            }
            RimKataAllowedWeaponStore.WriteDocument(path, document);
        }

        private static string Text(XmlNode root, string name) => root.SelectSingleNode(name)?.InnerText;

        private static void Append(XmlDocument document, XmlNode parent, string name, string value)
        {
            XmlElement child = document.CreateElement(name);
            child.InnerText = value ?? string.Empty;
            parent.AppendChild(child);
        }

        private static void WarnOnce(string key, string message)
        {
            if (Reported.Add(key)) Log.Warning("[RimKata] " + message);
        }
    }
}
