using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
        private readonly ConditionalWeakTable<Building_Door, PreparedDoor> doors =
            new ConditionalWeakTable<Building_Door, PreparedDoor>();
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private bool scheduled;
        private bool storeAttempted;
        private bool cleanupScheduled;

        private sealed class MaterialState
        {
            internal readonly Shader shader;
            internal readonly Texture texture, mask;
            internal readonly Color color, colorTwo;
            internal readonly Vector2 scale, offset;
            internal readonly int renderQueue;
            internal readonly string[] keywords;
            private readonly bool missing;
            private readonly bool hasColorTwo;

            internal MaterialState(Material material, Color secondaryColor)
            {
                if (material == null) { missing = true; return; }
                shader = material.shader;
                texture = material.mainTexture;
                mask = material.HasProperty(ShaderPropertyIDs.MaskTex) ? material.GetTexture(ShaderPropertyIDs.MaskTex) : null;
                color = material.color;
                hasColorTwo = material.HasProperty(ShaderPropertyIDs.ColorTwo);
                colorTwo = hasColorTwo ? material.GetColor(ShaderPropertyIDs.ColorTwo) : secondaryColor;
                scale = material.mainTextureScale;
                offset = material.mainTextureOffset;
                renderQueue = material.renderQueue;
                keywords = material.shaderKeywords;
                Array.Sort(keywords, StringComparer.Ordinal);
            }

            internal bool Matches(Material material)
            {
                if (missing) return material == null;
                if (material == null || material.shader != shader || material.mainTexture != texture
                    || !material.color.Equals(color) || !material.mainTextureScale.Equals(scale)
                    || !material.mainTextureOffset.Equals(offset) || material.renderQueue != renderQueue
                    || material.HasProperty(ShaderPropertyIDs.ColorTwo) != hasColorTwo
                    || hasColorTwo && !material.GetColor(ShaderPropertyIDs.ColorTwo).Equals(colorTwo)
                    || (material.HasProperty(ShaderPropertyIDs.MaskTex) ? material.GetTexture(ShaderPropertyIDs.MaskTex) : null) != mask)
                    return false;
                string[] current = material.shaderKeywords;
                if (current.Length != keywords.Length) return false;
                foreach (string keyword in current)
                    if (Array.IndexOf(keywords, keyword) < 0) return false;
                return true;
            }
        }

        private sealed class GraphicState
        {
            internal readonly Graphic graphic;
            private readonly Type type;
            private readonly string path, maskPath, shaderPath;
            private readonly Color color, colorTwo;
            private readonly Vector2 size;
            private readonly bool customParameters;
            internal MaterialState[] materials;

            internal GraphicState(Graphic graphic)
            {
                this.graphic = graphic;
                type = graphic?.GetType();
                path = graphic?.path;
                maskPath = graphic?.maskPath;
                shaderPath = graphic?.data?.shaderType?.shaderPath;
                color = graphic?.color ?? Color.white;
                colorTwo = graphic?.colorTwo ?? Color.white;
                size = graphic?.drawSize ?? Vector2.zero;
                customParameters = graphic?.data?.shaderParameters?.Count > 0;
            }

            internal bool Matches(Graphic current, Building_Door door, Rot4 rotation)
            {
                if (current?.GetType() != type || current?.path != path || current?.maskPath != maskPath
                    || current?.data?.shaderType?.shaderPath != shaderPath
                    || !(current?.color ?? Color.white).Equals(color)
                    || !(current?.colorTwo ?? Color.white).Equals(colorTwo)
                    || !(current?.drawSize ?? Vector2.zero).Equals(size)
                    || (current?.data?.shaderParameters?.Count > 0) != customParameters) return false;
                // An unsupported/unreadable source is a result too, retried on its next change event.
                return materials == null || materials[rotation.AsInt].Matches(current.MatAt(rotation, door));
            }
        }

        private sealed class PreparedDoor
        {
            private readonly ThingDef definition;
            private readonly IntVec2 size;
            private ThingDef stuff;
            private ThingStyleDef style;
            private Color color, colorTwo;
            private bool captured;
            internal GraphicState main, upper;
            internal RimKataBreachDoorSnapshot[] snapshots;

            internal PreparedDoor(Building_Door door) { definition = door.def; size = door.def.size; }

            internal void CaptureAppearance(Building_Door door)
            {
                stuff = door.Stuff;
                style = door.StyleDef;
                color = door.DrawColor;
                colorTwo = door.DrawColorTwo;
                main = new GraphicState(door.Graphic);
                Graphic upperGraphic = door is Building_MultiTileDoor ? door.def.building.upperMoverGraphic?.Graphic : null;
                upper = new GraphicState(upperGraphic);
                captured = true;
            }

            internal bool Matches(Building_Door door, Rot4 rotation)
            {
                if (door.def != definition || door.def.size != size) return false;
                if (!captured) return true;
                return door.Stuff == stuff && door.StyleDef == style
                    && door.DrawColor.Equals(color) && door.DrawColorTwo.Equals(colorTwo)
                    && main.Matches(door.Graphic, door, rotation)
                    && upper.Matches(door is Building_MultiTileDoor ? door.def.building.upperMoverGraphic?.Graphic : null,
                        door, rotation);
            }
        }

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
            catch (Exception exception)
            {
                store = new RimKataDoorCacheStore();
                Warn("directory", exception);
            }
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

        // Public appearance changes and unannounced source changes share this bounded door-only queue.
        internal void NotifyAppearanceChanged(Building_Door door)
        {
            if (ReferenceEquals(game, Verse.Current.Game) && door?.Spawned == true && !door.Destroyed)
                pending.Add(door);
        }

        public override void GameComponentUpdate()
        {
            if (scheduled || pending.Count == 0 || !UnityData.IsInMainThread
                || !ReferenceEquals(game, Verse.Current.Game)) return;
            Building_Door next = null;
            foreach (Building_Door door in pending) { next = door; break; }
            pending.Remove(next);
            if (next?.Spawned == true && !next.Destroyed) TryPrepare(next);
        }

        internal bool TryCapturePrepared(Building_Door door, Rot4 rotation, out RimKataBreachDoorSnapshot snapshot)
        {
            snapshot = null;
            if (!ReferenceEquals(game, Verse.Current.Game) || door?.Spawned != true || door.Destroyed
                || rotation.AsInt < 0 || rotation.AsInt >= 4) return false;
            try
            {
                if (doors.TryGetValue(door, out PreparedDoor ready) && ready.Matches(door, rotation))
                {
                    if (ready.snapshots?[rotation.AsInt] == null) return false;
                    snapshot = ready.snapshots[rotation.AsInt].Copy();
                    return true;
                }
            }
            catch (Exception exception) { Warn(door.def?.defName ?? "door", exception); }
            // This is a changed/unobserved appearance, not a retry of an unchanged failed recipe.
            // Do not execute XML, asset-path resolution or disk work on the admitting call stack.
            NotifyAppearanceChanged(door);
            return false;
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
                    TryPrepare(door);
        }

        private void TryPrepare(Building_Door door)
        {
            if (!ReferenceEquals(game, Verse.Current.Game) || door?.Spawned != true
                || door.Destroyed) return;
            // Repeated public notifications do not rebuild an unchanged usable appearance.
            if (doors.TryGetValue(door, out PreparedDoor previous) && previous.snapshots != null)
            {
                bool unchanged = true;
                try
                {
                    for (int i = 0; i < 4 && unchanged; i++)
                        unchanged = previous.snapshots[i] != null && previous.Matches(door, new Rot4(i));
                }
                catch (Exception) { unchanged = false; }
                if (unchanged) return;
            }
            var prepared = new PreparedDoor(door);
            try
            {
                prepared.CaptureAppearance(door);
                Graphic graphic = prepared.main.graphic;
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
                    AddLayer(render, door, prepared.main, split, 0f);
                    if (prepared.upper.graphic != null)
                        AddLayer(render, door, prepared.upper, true, 0.018292684f);
                }
                EnsureStore();
                RimKataDoorCacheRecord record = store.Prepare(metadata, render, out Exception persistenceFailure);
                if (persistenceFailure != null) Warn("save:" + record.Key, persistenceFailure);
                prepared.snapshots = RimKataBreachDoorSnapshot.Prepare(record,
                    exception => Warn(door.def?.defName ?? "door", exception));
            }
            catch (Exception exception)
            {
                Warn(door.def?.defName ?? "door", exception);
            }
            // Keep the observed failure as well; the same unsupported appearance is not rebuilt per attack.
            doors.Remove(door);
            doors.Add(door, prepared);
        }

        private static string UnsupportedReason(Graphic graphic)
        {
            if (graphic == null || string.IsNullOrEmpty(graphic.path))
                return "Door graphic has no persistent texture path.";
            if (graphic.data?.shaderParameters?.Count > 0)
                return "Custom shader parameters require a renderer adapter.";
            return null;
        }

        private static void AddLayer(XmlDocument render, Building_Door door, GraphicState source, bool split, float altitude)
        {
            Graphic graphic = source.graphic;
            string reason = UnsupportedReason(graphic);
            if (reason != null) throw new InvalidOperationException(reason);
            var materials = new MaterialState[4];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = new MaterialState(graphic.MatAt(new Rot4(i), door), graphic.colorTwo);
            source.materials = materials;
            XmlElement layer = render.CreateElement("layer");
            // Closed MultiTileDoor panels are half-width, separated by a quarter of the full width.
            layer.SetAttribute("scale", Number(door.def.size.x * (split ? 0.5f : 1f)) + ",1," + Number(door.def.size.z));
            layer.SetAttribute("offset", Number(split ? door.def.size.x * 0.25f : 0f));
            layer.SetAttribute("altitude", Number(altitude));
            var paths = new Dictionary<Texture, string>();
            for (int i = 0; i < 4; i++)
            {
                MaterialState material = materials[i];
                XmlElement element = render.CreateElement("material");
                element.SetAttribute("rotation", Number(i));
                element.SetAttribute("shaderPath", (graphic.data?.shaderType ?? ShaderTypeDefOf.Cutout).shaderPath);
                element.SetAttribute("shader", material.shader?.name ?? string.Empty);
                element.SetAttribute("texturePath", TexturePath(graphic, material.texture, false, paths));
                element.SetAttribute("maskPath", TexturePath(graphic, material.mask, true, paths));
                element.SetAttribute("color", ColorText(material.color));
                element.SetAttribute("colorTwo", ColorText(material.colorTwo));
                element.SetAttribute("textureScale", Number(material.scale.x) + "," + Number(material.scale.y));
                element.SetAttribute("textureOffset", Number(material.offset.x) + "," + Number(material.offset.y));
                element.SetAttribute("renderQueue", Number(material.renderQueue));
                element.SetAttribute("keywords", string.Join(",", material.keywords));
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
            // Folder-based graphics can keep the parent folder as their path.
            if (!string.IsNullOrEmpty(texture.name))
            {
                string childPath = root.TrimEnd('/') + "/" + texture.name;
                if (ContentFinder<Texture2D>.Get(childPath, false) == texture)
                {
                    paths[texture] = childPath;
                    return childPath;
                }
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

    [HarmonyPatch]
    internal static class Patch_BuildingDoor_RimKataAppearance
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ThingWithComps), nameof(ThingWithComps.Notify_ColorChanged));
            yield return AccessTools.Method(typeof(ThingWithComps), nameof(ThingWithComps.Notify_DefsHotReloaded));
            yield return AccessTools.PropertySetter(typeof(Thing), nameof(Thing.StyleDef));
        }

        private static void Postfix(Thing __instance)
        {
            if (__instance is Building_Door door) RimKataDoorCache.Current?.NotifyAppearanceChanged(door);
        }
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
