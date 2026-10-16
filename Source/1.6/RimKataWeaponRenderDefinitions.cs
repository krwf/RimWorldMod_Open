using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using Verse;

namespace KRWF.RimKata
{
    internal static partial class RimKataWeaponRenderProbe
    {
        internal sealed class DefinitionPlan
        {
            private const string Category = "equipment-render-matches";
            private readonly IReadOnlyList<RimKataWeaponRenderDiscovery.Renderer> renderers;
            private readonly RimKataEquipmentMemory.View memory;
            private readonly Dictionary<ThingDef, RimKataWeaponRenderDiscovery.Renderer[]> prepared =
                new Dictionary<ThingDef, RimKataWeaponRenderDiscovery.Renderer[]>();
            private readonly Dictionary<string, RimKataWeaponRenderDiscovery.Renderer> byKey =
                new Dictionary<string, RimKataWeaponRenderDiscovery.Renderer>(StringComparer.Ordinal);
            private readonly string rendererFingerprint;

            internal DefinitionPlan(IReadOnlyList<RimKataWeaponRenderDiscovery.Renderer> renderers,
                RimKataEquipmentMemory.View memory)
            {
                this.renderers = renderers;
                this.memory = memory;
                var text = new StringBuilder("definition-matches-1\n");
                foreach (var renderer in renderers)
                {
                    byKey[renderer.Key] = renderer;
                    text.Append(renderer.Key).Append('\n');
                }
                rendererFingerprint = RimKataEquipmentMemory.Fingerprint(text.ToString());
            }

            internal IEnumerable<object> Scan()
            {
                if (renderers.Count == 0) yield break;
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.IsWeapon || def.IsApparel) ReadDefinition(def);
                    yield return null;
                }
            }

            private void ReadDefinition(ThingDef def)
            {
                string key = (def.modContentPack?.PackageId ?? string.Empty) + ":" + def.defName;
                var text = new StringBuilder(rendererFingerprint);
                if (def.comps != null)
                    foreach (CompProperties comp in def.comps)
                    {
                        Type type = comp?.compClass;
                        if (type != null)
                            text.Append('\n').Append(type.AssemblyQualifiedName).Append('|').Append(type.Module.ModuleVersionId);
                    }
                string fingerprint = RimKataEquipmentMemory.Fingerprint(text.ToString());
                if (memory.TryRead(Category, key, fingerprint, out XmlElement data)
                    && TryReadMatches(data, out var restored))
                {
                    prepared[def] = restored;
                    RimKataStartupDiagnostics.Count("definition_cache_hit");
                    return;
                }
                RimKataStartupDiagnostics.Count("definition_cache_miss");
                var matches = new List<RimKataWeaponRenderDiscovery.Renderer>();
                data = RimKataEquipmentMemory.NewData();
                data.SetAttribute("schema", "1");
                foreach (var renderer in renderers)
                {
                    if (!renderer.Supports(def)) continue;
                    matches.Add(renderer);
                    XmlElement element = data.OwnerDocument.CreateElement("renderer");
                    element.SetAttribute("key", renderer.Key);
                    data.AppendChild(element);
                }
                prepared[def] = matches.Count == 0 ? noRenderers : matches.ToArray();
                memory.Store(Category, key, fingerprint, data);
            }

            private bool TryReadMatches(XmlElement data, out RimKataWeaponRenderDiscovery.Renderer[] matches)
            {
                matches = null;
                if (data.GetAttribute("schema") != "1") return false;
                var result = new List<RimKataWeaponRenderDiscovery.Renderer>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (XmlNode node in data.ChildNodes)
                {
                    if (!(node is XmlElement element) || element.Name != "renderer") return false;
                    string key = element.GetAttribute("key");
                    if (!seen.Add(key) || !byKey.TryGetValue(key, out var renderer)) return false;
                    result.Add(renderer);
                }
                matches = result.Count == 0 ? noRenderers : result.ToArray();
                return true;
            }

            internal void Apply(IReadOnlyList<RimKataWeaponRenderDiscovery.Renderer> installed)
            {
                var available = new Dictionary<string, RimKataWeaponRenderDiscovery.Renderer>(StringComparer.Ordinal);
                foreach (var renderer in installed) available[renderer.Key] = renderer;
                var replacement = new Dictionary<ThingDef, RimKataWeaponRenderDiscovery.Renderer[]>();
                foreach (var pair in prepared)
                {
                    var matches = new List<RimKataWeaponRenderDiscovery.Renderer>();
                    foreach (var renderer in pair.Value)
                        if (available.TryGetValue(renderer.Key, out var live)) matches.Add(live);
                    replacement[pair.Key] = matches.Count == 0 ? noRenderers : matches.ToArray();
                }
                renderersByDef = replacement;
                failedProbes.Clear();
                failedFallbacks.Clear();
            }
        }
    }
}
