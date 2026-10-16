using System;
using System.Globalization;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataBreachDoorSnapshot : IExposable
    {
        private string metadataXml;
        private string renderXml;
        private Rot4 rotation;
        private Material material;
        private Vector3 scale;
        private PanelLayer[] layers;
        private bool resolved;

        private sealed class PanelLayer
        {
            internal Material material;
            internal Vector3 scale;
            internal float offset, altitude;
        }

        public RimKataBreachDoorSnapshot() { }

        internal static bool TryCapture(Building_Door door, out RimKataBreachDoorSnapshot snapshot)
        {
            snapshot = null;
            if (door?.Spawned != true || door.Open
                || RimKataDoorCache.Current == null
                || !RimKataDoorCache.Current.TryPrepare(door, out RimKataDoorCacheRecord recipe)) return false;
            var value = new RimKataBreachDoorSnapshot
            {
                metadataXml = recipe.MetadataXml,
                renderXml = recipe.RenderXml,
                // DoorPreDraw normally refreshes this value from adjoining walls.
                rotation = door.def.size == IntVec2.One
                    ? DoorUtility.DoorRotationAt(door.Position, door.Map, door.def.building.preferConnectingToFences)
                    : door.Rotation
            };
            if (!value.Resolve()) return false;
            snapshot = value;
            return true;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref metadataXml, "metadataXml");
            Scribe_Values.Look(ref renderXml, "renderXml");
            Scribe_Values.Look(ref rotation, "doorRotation");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                LongEventHandler.ExecuteWhenFinished(() => Resolve());
        }

        private bool Resolve()
        {
            if (resolved) return material != null || layers != null;
            if (!UnityData.IsInMainThread) return false;
            resolved = true;
            try
            {
                XmlDocument metadata = Read(metadataXml, "RimKataDoorMetadata");
                XmlDocument render = Read(renderXml, "RimKataDoorRender");
                if (render.DocumentElement["supported"]?.InnerText != "true") return false;
                string renderer = render.DocumentElement["renderer"]?.InnerText;
                if (renderer == "door-panels-v2") return ResolveLayers(render);
                // Older saved debris contains only a single-layer recipe.
                if (renderer != "vanilla-door-movers-v1") return false;
                string path = render.DocumentElement["texturePath"]?.InnerText;
                string shaderPath = metadata.DocumentElement["shaderPath"]?.InnerText;
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(shaderPath)) return false;
                XmlElement sample = render.SelectSingleNode("/RimKataDoorRender/material[@rotation='"
                    + rotation.AsInt.ToString(CultureInfo.InvariantCulture) + "']") as XmlElement;
                if (sample == null) return false;
                Shader shader = ShaderDatabase.LoadShader(shaderPath);
                Texture2D texture = ContentFinder<Texture2D>.Get(path, false);
                if (shader == null || texture == null || shader.name != sample.GetAttribute("shader")) return false;
                string maskPath = render.DocumentElement["maskPath"]?.InnerText;
                Texture2D mask = shader.SupportsMaskTex()
                    ? ContentFinder<Texture2D>.Get(string.IsNullOrEmpty(maskPath) ? path + "_m" : maskPath, false)
                    : null;
                var request = new MaterialRequest(texture, shader, ColorValue(sample.GetAttribute("color")))
                {
                    colorTwo = ColorValue(sample.GetAttribute("colorTwo")),
                    maskTex = mask,
                    renderQueue = int.Parse(sample.GetAttribute("renderQueue"), CultureInfo.InvariantCulture)
                };
                Material candidate = MaterialPool.MatFrom(request);
                if (!SamePair(sample.GetAttribute("textureScale"), candidate.mainTextureScale)
                    || !SamePair(sample.GetAttribute("textureOffset"), candidate.mainTextureOffset)) return false;
                string[] keywords = candidate.shaderKeywords;
                Array.Sort(keywords, StringComparer.Ordinal);
                if (string.Join(",", keywords) != sample.GetAttribute("keywords")) return false;
                float[] dimensions = Numbers(render.DocumentElement["scale"]?.InnerText, 3);
                if (dimensions[0] <= 0f || dimensions[2] <= 0f) return false;
                scale = new Vector3(dimensions[0], 1f, dimensions[2]);
                material = candidate;
                return true;
            }
            catch (Exception exception) when (exception is XmlException || exception is FormatException
                || exception is ArgumentException || exception is OverflowException)
            {
                return false;
            }
        }

        private bool ResolveLayers(XmlDocument render)
        {
            XmlNodeList nodes = render.SelectNodes("/RimKataDoorRender/layer");
            if (nodes == null || nodes.Count == 0 || nodes.Count > 16) return false;
            var result = new PanelLayer[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var layer = (XmlElement)nodes[i];
                XmlElement sample = layer.SelectSingleNode("material[@rotation='"
                    + rotation.AsInt.ToString(CultureInfo.InvariantCulture) + "']") as XmlElement;
                if (sample == null) return false;
                Material selected = ResolveMaterial(sample);
                if (selected == null) return false;
                float[] size = Numbers(layer.GetAttribute("scale"), 3);
                float offset = Numbers(layer.GetAttribute("offset"), 1)[0];
                float altitude = Numbers(layer.GetAttribute("altitude"), 1)[0];
                if (size[0] <= 0f || size[2] <= 0f || offset < 0f) return false;
                result[i] = new PanelLayer { material = selected,
                    scale = new Vector3(size[0], size[1], size[2]), offset = offset, altitude = altitude };
            }
            layers = result;
            return true;
        }

        private static Material ResolveMaterial(XmlElement sample)
        {
            string shaderPath = sample.GetAttribute("shaderPath");
            string path = sample.GetAttribute("texturePath");
            if (string.IsNullOrEmpty(shaderPath) || string.IsNullOrEmpty(path)) return null;
            Shader shader = ShaderDatabase.LoadShader(shaderPath);
            Texture2D texture = LoadTexture(path);
            if (shader == null || texture == null || shader.name != sample.GetAttribute("shader")) return null;
            string maskPath = sample.GetAttribute("maskPath");
            Texture2D mask = string.IsNullOrEmpty(maskPath) ? null : LoadTexture(maskPath);
            if (!string.IsNullOrEmpty(maskPath) && mask == null) return null;
            var request = new MaterialRequest(texture, shader, ColorValue(sample.GetAttribute("color")))
            {
                colorTwo = ColorValue(sample.GetAttribute("colorTwo")), maskTex = mask,
                renderQueue = int.Parse(sample.GetAttribute("renderQueue"), CultureInfo.InvariantCulture)
            };
            Material candidate = MaterialPool.MatFrom(request);
            string[] keywords = candidate.shaderKeywords;
            Array.Sort(keywords, StringComparer.Ordinal);
            string savedKeywords = sample.GetAttribute("keywords");
            if (SamePair(sample.GetAttribute("textureScale"), candidate.mainTextureScale)
                && SamePair(sample.GetAttribute("textureOffset"), candidate.mainTextureOffset)
                && string.Join(",", keywords) == savedKeywords) return candidate;

            // The source material may be pooled and shared.
            var copy = new Material(candidate);
            float[] uvScale = Numbers(sample.GetAttribute("textureScale"), 2);
            float[] uvOffset = Numbers(sample.GetAttribute("textureOffset"), 2);
            copy.mainTextureScale = new Vector2(uvScale[0], uvScale[1]);
            copy.mainTextureOffset = new Vector2(uvOffset[0], uvOffset[1]);
            copy.shaderKeywords = string.IsNullOrEmpty(savedKeywords) ? Array.Empty<string>() : savedKeywords.Split(',');
            return copy;
        }

        private static Texture2D LoadTexture(string path)
            => path == "@white" ? Texture2D.whiteTexture
                : path == "@black" ? Texture2D.blackTexture : ContentFinder<Texture2D>.Get(path, false);

        private static XmlDocument Read(string text, string root)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 1024 * 1024) throw new FormatException();
            var document = new XmlDocument { XmlResolver = null };
            using (var source = new System.IO.StringReader(text))
            using (XmlReader reader = XmlReader.Create(source, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            if (document.DocumentElement?.Name != root
                || document.DocumentElement.GetAttribute("schemaVersion") != "1") throw new FormatException();
            return document;
        }

        private static float[] Numbers(string text, int count)
        {
            string[] parts = text?.Split(',');
            if (parts?.Length != count) throw new FormatException();
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) throw new FormatException();
            }
            return values;
        }

        private static Color ColorValue(string text)
        {
            float[] color = Numbers(text, 4);
            return new Color(color[0], color[1], color[2], color[3]);
        }

        private static bool SamePair(string text, Vector2 actual)
        {
            float[] values = Numbers(text, 2);
            return Mathf.Approximately(values[0], actual.x) && Mathf.Approximately(values[1], actual.y);
        }

        internal void Draw(Vector3 location)
        {
            location.y = AltitudeLayer.Filth.AltitudeFor() + 0.001f;
            if (layers != null)
            {
                foreach (PanelLayer layer in layers)
                {
                    Vector3 center = location + new Vector3(0f, layer.altitude, 0f);
                    // DrawMovers rotates its local Z offsets clockwise first.
                    Vector3 displacement = rotation.AsQuat * new Vector3(layer.offset, 0f, 0f);
                    Graphics.DrawMesh(MeshPool.plane10,
                        Matrix4x4.TRS(center - displacement, rotation.AsQuat, layer.scale), layer.material, 0);
                    Graphics.DrawMesh(MeshPool.plane10Flip,
                        Matrix4x4.TRS(center + displacement, rotation.AsQuat, layer.scale), layer.material, 0);
                }
                return;
            }
            if (material == null) return;
            Matrix4x4 matrix = Matrix4x4.TRS(location, rotation.AsQuat, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, material, 0);
            Graphics.DrawMesh(MeshPool.plane10Flip, matrix, material, 0);
        }

        internal void Drop(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map)) return;
            ThingDef definition = DefDatabase<ThingDef>.GetNamedSilentFail("KRWF_RimKataBreachDoorDebris");
            if (definition == null) return;
            var debris = (Filth_RimKataBreachDoor)ThingMaker.MakeThing(definition);
            debris.SetSnapshot(this);
            // FilthMaker merges equal defs and would discard individual door recipes.
            GenSpawn.Spawn(debris, cell, map);
        }
    }

    public sealed class Filth_RimKataBreachDoor : Filth
    {
        private RimKataBreachDoorSnapshot snapshot;

        internal void SetSnapshot(RimKataBreachDoorSnapshot value) => snapshot = value;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref snapshot, "doorSnapshot");
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false) => snapshot?.Draw(drawLoc);
    }
}
