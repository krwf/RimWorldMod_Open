using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataDoorCache : GameComponent
    {
        private static string modRoot;
        private readonly Game game;
        private string gameId = Guid.NewGuid().ToString("N");
        private string environmentKey;
        private XmlDocument runtimeEnvironment;
        private string runtimeEnvironmentKey;
        private RimKataDoorCacheStore store;
        private readonly HashSet<Building_Door> pending = new HashSet<Building_Door>();
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private bool scheduled;
        private bool storeAttempted;
        private bool cleanupScheduled;

        public RimKataDoorCache(Game game) { this.game = game; }

        internal static RimKataDoorCache Current => Verse.Current.Game?.GetComponent<RimKataDoorCache>();

        internal static void ConfigureRoot(string root)
        {
            modRoot = root;
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                try
                {
                    EnsureEnvironment();
                    environmentKey = runtimeEnvironmentKey;
                }
                catch (Exception exception)
                {
                    environmentKey = null;
                    Warn("environment", exception);
                }
            }
            Scribe_Values.Look(ref gameId, "gameId");
            Scribe_Values.Look(ref environmentKey, "environmentKey");
            // Older saves acquire this persistent ID on their first load/save.
            if (!Guid.TryParseExact(gameId, "N", out _)) gameId = Guid.NewGuid().ToString("N");
        }

        public override void LoadedGame() => ScheduleCleanup();

        private void EnsureEnvironment()
        {
            if (runtimeEnvironment != null) return;
            XmlDocument captured = RimKataDoorCacheEnvironment.Capture();
            runtimeEnvironmentKey = RimKataDoorCacheStore.EnvironmentKey(captured);
            runtimeEnvironment = captured;
        }

        internal void ScheduleCleanup()
        {
            if (cleanupScheduled || !ReferenceEquals(game, Verse.Current.Game)) return;
            cleanupScheduled = true;
            LongEventHandler.ExecuteWhenFinished(Cleanup);
        }

        private void Cleanup()
        {
            cleanupScheduled = false;
            if (!ReferenceEquals(game, Verse.Current.Game) || string.IsNullOrEmpty(modRoot)) return;
            try
            {
                EnsureEnvironment();
                RimKataDoorCacheCleanup.Prune(Path.Combine(modRoot, "door"),
                    GenFilePaths.SavedGamesFolderPath, gameId, runtimeEnvironmentKey);
            }
            catch (Exception exception) { Warn("cleanup", exception); }
        }

        private bool EnsureStore()
        {
            if (storeAttempted) return store != null;
            storeAttempted = true;
            try
            {
                EnsureEnvironment();
                store = new RimKataDoorCacheStore(modRoot, gameId, runtimeEnvironment);
            }
            catch (Exception exception) { Warn("directory", exception); }
            return store != null;
        }

        internal void NotifySpawned(Building_Door door)
        {
            if (!ReferenceEquals(game, Verse.Current.Game) || door?.Spawned != true) return;
            pending.Add(door);
            if (scheduled) return;
            scheduled = true;
            // Map generation/loading can run off the Unity thread; Graphic/Material access waits for the long event.
            LongEventHandler.ExecuteWhenFinished(Flush);
        }

        private void Flush()
        {
            // Spawned/Map use the current game's map indices, even in a stale callback.
            if (!ReferenceEquals(game, Verse.Current.Game))
            {
                pending.Clear();
                scheduled = false;
                return;
            }
            Building_Door[] doors = new Building_Door[pending.Count];
            pending.CopyTo(doors);
            pending.Clear();
            scheduled = false;
            foreach (Building_Door door in doors)
                if (door?.Spawned == true && !door.Destroyed)
                    TryPrepare(door, out _);
        }

        internal bool TryPrepare(Building_Door door, out RimKataDoorCacheRecord record)
        {
            record = null;
            if (!ReferenceEquals(game, Verse.Current.Game) || door?.Spawned != true
                || door.Destroyed || !EnsureStore()) return false;
            try
            {
                Graphic graphic = door.Graphic;
                XmlDocument metadata = RimKataDoorCacheStore.Document("RimKataDoorMetadata");
                Add(metadata, "defName", door.def.defName);
                Add(metadata, "sourceModId", door.def.modContentPack?.PackageIdPlayerFacing);
                Add(metadata, "thingClass", door.GetType().FullName);
                Add(metadata, "assemblyId", door.GetType().Module.ModuleVersionId.ToString("D"));
                Add(metadata, "stuff", door.Stuff?.defName);
                Add(metadata, "style", door.StyleDef?.defName);
                Add(metadata, "sizeX", door.def.size.x);
                Add(metadata, "sizeZ", door.def.size.z);
                Add(metadata, "color", ColorText(door.DrawColor));
                Add(metadata, "colorTwo", ColorText(door.DrawColorTwo));
                Add(metadata, "graphicClass", graphic?.GetType().FullName);
                Add(metadata, "graphicPath", graphic?.path);
                Add(metadata, "maskPath", graphic?.maskPath);
                Add(metadata, "shaderPath", graphic?.data?.shaderType?.shaderPath);
                Add(metadata, "graphicColor", ColorText(graphic?.color ?? Color.white));
                Add(metadata, "graphicColorTwo", ColorText(graphic?.colorTwo ?? Color.white));
                Add(metadata, "graphicSizeX", graphic?.drawSize.x ?? 0f);
                Add(metadata, "graphicSizeY", graphic?.drawSize.y ?? 0f);

                XmlDocument render = RimKataDoorCacheStore.Document("RimKataDoorRender");
                string reason = UnsupportedReason(graphic);
                bool supported = reason == null;
                Add(render, "supported", supported);
                Add(render, "scope", "closed-main-panels-only");
                Add(render, "reason", reason);
                Add(render, "renderer", supported ? "door-panels-v2" : "unsupported");
                Add(render, "rotationSource", "selected-door-at-use");
                Add(render, "includesCompOverlays", false);
                Add(render, "includesShadows", false);
                if (supported)
                {
                    bool split = door is Building_MultiTileDoor;
                    AddLayer(render, door, graphic, split, 0f);
                    if (split && door.def.building.upperMoverGraphic != null)
                        AddLayer(render, door, door.def.building.upperMoverGraphic.Graphic, true, 0.018292684f);
                }
                record = store.Prepare(metadata, render);
                return true;
            }
            catch (Exception exception)
            {
                Warn(door.def?.defName ?? "door", exception);
                return false;
            }
        }

        private static string UnsupportedReason(Graphic graphic)
        {
            if (graphic == null || string.IsNullOrEmpty(graphic.path))
                return "Door graphic has no persistent texture path.";
            if (graphic.data?.shaderParameters?.Count > 0)
                return "Custom shader parameters require a renderer adapter.";
            return null;
        }

        private static void AddLayer(XmlDocument render, Building_Door door, Graphic graphic, bool split, float altitude)
        {
            string reason = UnsupportedReason(graphic);
            if (reason != null) throw new InvalidOperationException(reason);
            XmlElement layer = render.CreateElement("layer");
            // Closed MultiTileDoor panels are half-width, separated by a quarter of the full width.
            layer.SetAttribute("scale", Number(door.def.size.x * (split ? 0.5f : 1f)) + ",1," + Number(door.def.size.z));
            layer.SetAttribute("offset", Number(split ? door.def.size.x * 0.25f : 0f));
            layer.SetAttribute("altitude", Number(altitude));
            var paths = new Dictionary<Texture, string>();
            for (int i = 0; i < 4; i++)
            {
                Material material = graphic.MatAt(new Rot4(i), door);
                if (material == null) throw new InvalidOperationException("Door material is missing.");
                XmlElement element = render.CreateElement("material");
                element.SetAttribute("rotation", Number(i));
                element.SetAttribute("shaderPath", (graphic.data?.shaderType ?? ShaderTypeDefOf.Cutout).shaderPath);
                element.SetAttribute("shader", material.shader?.name ?? string.Empty);
                element.SetAttribute("texturePath", TexturePath(graphic, material.mainTexture, false, paths));
                Texture mask = material.HasProperty(ShaderPropertyIDs.MaskTex) ? material.GetTexture(ShaderPropertyIDs.MaskTex) : null;
                element.SetAttribute("maskPath", TexturePath(graphic, mask, true, paths));
                element.SetAttribute("color", ColorText(material.color));
                element.SetAttribute("colorTwo", ColorText(graphic.colorTwo));
                element.SetAttribute("textureScale", Number(material.mainTextureScale.x) + "," + Number(material.mainTextureScale.y));
                element.SetAttribute("textureOffset", Number(material.mainTextureOffset.x) + "," + Number(material.mainTextureOffset.y));
                element.SetAttribute("renderQueue", Number(material.renderQueue));
                string[] keywords = material.shaderKeywords;
                Array.Sort(keywords, StringComparer.Ordinal);
                element.SetAttribute("keywords", string.Join(",", keywords));
                layer.AppendChild(element);
            }
            render.DocumentElement.AppendChild(layer);
        }

        private static string TexturePath(Graphic graphic, Texture texture, bool mask, Dictionary<Texture, string> paths)
        {
            if (texture == null)
            {
                if (mask) return string.Empty;
                throw new InvalidOperationException("Door panel texture is missing.");
            }
            if (paths.TryGetValue(texture, out string known)) return known;
            if (texture == Texture2D.whiteTexture) return "@white";
            if (texture == Texture2D.blackTexture) return "@black";
            string root = mask && !string.IsNullOrEmpty(graphic.maskPath) ? graphic.maskPath : graphic.path;
            string suffix = mask && string.IsNullOrEmpty(graphic.maskPath) ? "m" : string.Empty;
            string[] candidates = { mask && suffix.Length != 0 ? root + "_m" : root,
                root + "_north" + suffix, root + "_east" + suffix,
                root + "_south" + suffix, root + "_west" + suffix };
            foreach (string path in candidates)
            {
                if (ContentFinder<Texture2D>.Get(path, false) != texture) continue;
                paths[texture] = path;
                return path;
            }
            throw new InvalidOperationException("Door panel texture has no reusable asset path: " + texture.name);
        }

        private static void Add(XmlDocument document, string name, object value)
        {
            XmlElement element = document.CreateElement(name);
            element.InnerText = value is bool flag ? (flag ? "true" : "false")
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            document.DocumentElement.AppendChild(element);
        }

        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string ColorText(Color color) => Number(color.r) + "," + Number(color.g)
            + "," + Number(color.b) + "," + Number(color.a);

        private void Warn(string key, Exception exception)
        {
            if (reported.Add(key))
                Log.Warning("[RimKata] Door cache operation failed (" + key + "). " + exception.Message);
        }
    }

    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.SpawnSetup))]
    internal static class Patch_BuildingDoor_RimKataCache
    {
        private static void Postfix(Building_Door __instance) => RimKataDoorCache.Current?.NotifySpawned(__instance);
    }

    // SafeSaver rethrows failures; SaveGame catches them, so only SafeSaver's normal return confirms success.
    [HarmonyPatch(typeof(SafeSaver), nameof(SafeSaver.Save),
        new[] { typeof(string), typeof(string), typeof(Action), typeof(bool) })]
    internal static class Patch_SaveGame_RimKataDoorCacheCleanup
    {
        private static void Postfix(string __1, bool __runOriginal)
        {
            if (__runOriginal && __1 == "savegame") RimKataDoorCache.Current?.ScheduleCleanup();
        }
    }
}
