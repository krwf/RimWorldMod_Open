using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public class RimKataDebugHUD : GameComponent
    {
        public static bool Enabled = false;
        public static bool SearchRangeEnabled = false;

        private const int EntryPopupTicks = 45;
        private sealed class GuiRegistration
        {
            public RimKataDebugHUD component;
            public int index = -1;
        }

        private static readonly ConditionalWeakTable<Game, GuiRegistration> GuiRegistrations =
            new ConditionalWeakTable<Game, GuiRegistration>();

        private sealed class SearchGraphics
        {
            internal readonly Material CellMaterial;
            internal readonly Mesh Mesh;

            // Created by the enabled map-update renderer, never by the loading thread.
            internal SearchGraphics()
            {
                CellMaterial = SolidColorMaterials.SimpleSolidColorMaterial(
                    new Color(0f, 0f, 0f, 0.3f));
                Mesh = CreateSearchMesh();
            }
        }

        private static SearchGraphics searchGraphics;
        private static readonly List<IntVec3> SearchCells = new List<IntVec3>();
        private static readonly HashSet<IntVec3> UniqueSearchCells =
            new HashSet<IntVec3>();
        private static readonly List<Vector3> SearchMeshVertices =
            new List<Vector3>();
        private static readonly List<int> SearchMeshTriangles =
            new List<int>();
        private static Map searchMeshMap;
        private static int searchMeshTick = -1;

        private struct ActualSearchCell
        {
            public Map map;
            public IntVec3 cell;
        }

        private static readonly List<ActualSearchCell> ActualSearchCells =
            new List<ActualSearchCell>();
        private static int actualSearchCellTick = -1;

        private struct RPopup
        {
            public int startTick;
            public bool state;
        }
        private static readonly Dictionary<Pawn, bool> previousCombatState = new Dictionary<Pawn, bool>();

        private static readonly Dictionary<Pawn, List<RPopup>> combatPopups = new Dictionary<Pawn, List<RPopup>>();

        private struct UPopup
        {
            public int startTick;
            public bool state;
        }

        private static readonly Dictionary<Pawn, bool> previousUsingState = new Dictionary<Pawn, bool>();

        private static readonly Dictionary<Pawn, UPopup> usingPopups = new Dictionary<Pawn, UPopup>();

        private enum LowerPopupType : byte
        {
            PrimaryResponse,
            Search,
            SecondaryResponse
        }

        private struct LowerPopup
        {
            public int startTick;
            public LowerPopupType type;
        }

        private static readonly Dictionary<Pawn, List<LowerPopup>> lowerPopups =
            new Dictionary<Pawn, List<LowerPopup>>();

        private static void UpdateCombatPopup(
            Pawn pawn,
            bool combatActive)
        {
            if (!previousCombatState.TryGetValue(pawn, out bool previous))
            {
                previousCombatState[pawn] = combatActive;
                return;
            }

            if (previous == combatActive)
            {
                return;
            }

            if (!combatPopups.TryGetValue(pawn, out List<RPopup> popups))
            {
                popups = new List<RPopup>();
                combatPopups[pawn] = popups;
            }

            popups.Add(new RPopup
            {
                startTick = Find.TickManager.TicksGame,
                state = combatActive
            });

            previousCombatState[pawn] = combatActive;
        }

        private static void UpdateUsingPopup(
            Pawn pawn,
            bool usingRimKata)
        {
            if (!previousUsingState.TryGetValue(pawn, out bool previous))
            {
                previousUsingState[pawn] = usingRimKata;
                return;
            }

            if (previous == usingRimKata)
            {
                return;
            }

            usingPopups[pawn] = new UPopup
            {
                startTick = Find.TickManager.TicksGame,
                state = usingRimKata
            };

            previousUsingState[pawn] = usingRimKata;
        }

        private static void AddLowerPopup(Pawn pawn, LowerPopupType type)
        {
            if (!Prefs.DevMode
                || !Enabled
                || pawn == null
                || RimKataEligibility.IsHostileToPlayerFaction(pawn))
            {
                return;
            }

            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (currentTick < 0)
            {
                return;
            }

            if (!lowerPopups.TryGetValue(pawn, out List<LowerPopup> popups))
            {
                popups = new List<LowerPopup>();
                lowerPopups[pawn] = popups;
            }

            for (int i = popups.Count - 1; i >= 0; i--)
            {
                if (popups[i].startTick == currentTick
                    && popups[i].type == type)
                {
                    return;
                }
            }

            popups.Add(new LowerPopup
            {
                startTick = currentTick,
                type = type
            });
        }

        internal static void RecordResponseIndicator(
            Pawn pawn,
            ThingWithComps weapon)
        {
            if (!Prefs.DevMode
                || !Enabled
                || pawn == null
                || weapon == null
                || RimKataEligibility.IsHostileToPlayerFaction(pawn))
            {
                return;
            }

            if (weapon == RimKataWeaponSlotUtility.PrimaryWeapon(pawn))
            {
                AddLowerPopup(pawn, LowerPopupType.PrimaryResponse);
                return;
            }

            if (weapon == RimKataWeaponSlotUtility.SecondaryWeapon(pawn))
            {
                AddLowerPopup(pawn, LowerPopupType.SecondaryResponse);
            }
        }

        internal static void RecordSearchIndicator(Pawn pawn)
        {
            AddLowerPopup(pawn, LowerPopupType.Search);
        }

        public RimKataDebugHUD(Game game)
        {
        }

        internal static void RefreshGuiRegistration(Game game)
        {
            if (game == null)
            {
                return;
            }

            GuiRegistration registration = GuiRegistrations.GetValue(
                game, _ => new GuiRegistration());
            int index = game.components.FindIndex(component => component is RimKataDebugHUD);
            if (index >= 0)
            {
                // Loading replaces the list, so prefer its newly deserialized instance.
                registration.component = (RimKataDebugHUD)game.components[index];
                registration.index = index;
                if (!Prefs.DevMode || !Enabled)
                {
                    game.components.RemoveAt(index);
                }
            }
            else if (Prefs.DevMode && Enabled)
            {
                if (registration.component == null)
                {
                    registration.component = new RimKataDebugHUD(game);
                }

                int insertIndex = registration.index < 0
                    ? game.components.Count
                    : System.Math.Min(registration.index, game.components.Count);
                game.components.Insert(insertIndex, registration.component);
            }
        }

        public static void SetHudEnabled(bool enabled)
        {
            Enabled = enabled && Prefs.DevMode;
            if (!Enabled)
            {
                ClearHudData();
            }

            RefreshGuiRegistration(Current.Game);
        }

        private static void ClearHudData()
        {
            previousCombatState.Clear();
            combatPopups.Clear();
            previousUsingState.Clear();
            usingPopups.Clear();
            lowerPopups.Clear();
        }

        public static void SetSearchRangeEnabled(bool enabled)
        {
            SearchRangeEnabled = enabled && Prefs.DevMode;
            if (!SearchRangeEnabled)
            {
                ClearSearchRangeData();
            }
        }

        private static void ClearSearchRangeData()
        {
            ActualSearchCells.Clear();
            actualSearchCellTick = -1;
            SearchCells.Clear();
            UniqueSearchCells.Clear();
            SearchMeshVertices.Clear();
            SearchMeshTriangles.Clear();
            // The next enabled draw rebuilds the mesh. Game changes can run off the main thread.
            searchMeshMap = null;
            searchMeshTick = -1;
        }

        internal static void NotifyGameChanged()
        {
            ClearHudData();
            ClearSearchRangeData();
            RefreshGuiRegistration(Current.Game);
        }

        internal static bool TryBeginActualSearchCellRecording(
            Pawn owner,
            Map map)
        {
            if (owner == null
                || RimKataEligibility.IsHostileToPlayerFaction(owner)
                || map == null
                || map.Disposed
                || owner.Map != map)
            {
                return false;
            }

            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (currentTick < 0)
            {
                return false;
            }

            if (actualSearchCellTick != currentTick)
            {
                ActualSearchCells.Clear();
                actualSearchCellTick = currentTick;
            }

            searchMeshTick = -1;
            return true;
        }

        internal static void RecordActualSearchCell(Map map, IntVec3 cell)
        {
            ActualSearchCells.Add(new ActualSearchCell
            {
                map = map,
                cell = cell
            });
        }

        internal static void DisableForDeveloperMode()
        {
            SetHudEnabled(false);
            SetSearchRangeEnabled(false);
        }

        public override void GameComponentOnGUI()
        {
            // Registration changes occur at events, not while this list is being dispatched.
            if (!Prefs.DevMode || !Enabled)
            {
                return;
            }

            Map map = Find.CurrentMap;
            if (map == null || map.Disposed)
            {
                return;
            }

            IReadOnlyList<Pawn> pawns = RimKataEligibilityCache.GetQualifiedPawns(map);
            for (int i = 0; i < pawns.Count; i++)
            {
                DrawPawnDebug(pawns[i]);
            }
        }

        private static void DrawPawnDebug(Pawn pawn)
        {
            if (RimKataEligibility.IsHostileToPlayerFaction(pawn))
            {
                RimKataDualWeaponController.DebugTryGetExistingUsingState(
                    pawn,
                    out bool hostileUsingRimKata);
                DrawMinimalUsingState(pawn, hostileUsingRimKata);
                return;
            }

            bool cacheKnown = RimKataEligibilityCache.TryGetCachedAccess(pawn, out bool cachedAccess);

            bool sharedSearch = RimKataDualWeaponController.DebugSharedSearchActive(pawn);

            RimKataDualWeaponController.TryGetDebugState(pawn, out char primaryState, out char secondaryState, out bool usingRimKata, out bool combatActive);

            string combatReasons = RimKataDraftedFireController.DebugCombatDemandReasons(pawn);

            UpdateUsingPopup(pawn, usingRimKata);
            UpdateCombatPopup(pawn, combatActive);

            Vector2 screenPos = pawn.DrawPos.MapToUIPosition();

            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;

            string cacheState;

            if (!cacheKnown)
            {
                cacheState = "F";
            }
            else if (cachedAccess)
            {
                cacheState = "T";
            }
            else
            {
                cacheState = "X";
            }

            ThingWithComps primaryWeapon = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);

            ThingWithComps secondaryWeapon =
                RimKataWeaponSlotUtility.SecondaryWeapon(pawn);

            Verb primaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, primaryWeapon);

            Verb secondaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, secondaryWeapon);

            char primaryType =
                primaryVerb == null
                    ? '-'
                    : primaryVerb.IsMeleeAttack ? 'S' : 'L';

            char secondaryType =
                secondaryVerb == null
                    ? '-'
                    : secondaryVerb.IsMeleeAttack ? 'S' : 'L';

            RimKataDualWeaponController.GetDebugWeaponState(
                pawn,
                primaryWeapon,
                out primaryState,
                out bool primaryVanillaState);
            RimKataDualWeaponController.GetDebugWeaponState(
                pawn,
                secondaryWeapon,
                out secondaryState,
                out bool secondaryVanillaState);

            string primaryStateText = primaryVanillaState
                ? "B" + primaryState
                : primaryState.ToString();
            string secondaryStateText = secondaryVanillaState
                ? "B" + secondaryState
                : secondaryState.ToString();

            string status = $"RK-{cacheState}"
                + $"_A{primaryType}-{primaryStateText}"
                + $"_B{secondaryType}-{secondaryStateText}";

            Rect statusRect = new Rect(screenPos.x - 90f, screenPos.y + 34f, 180f, 24f);
            Widgets.Label(statusRect, status);

            RimKataVisualSnapshot visualSnapshot =
                pawn.Map?.GetComponent<RimKataMapComponent>()
                    ?.GetVisualSnapshot(pawn)
                ?? default(RimKataVisualSnapshot);
            bool primaryResponse = visualSnapshot.responsePoseActive
                && visualSnapshot.responsePoseWeapon == primaryWeapon;
            bool secondaryResponse = visualSnapshot.responsePoseActive
                && visualSnapshot.responsePoseWeapon == secondaryWeapon;

            Rect primaryIndicatorRect = new Rect(
                screenPos.x - 48f,
                screenPos.y + 52f,
                32f,
                24f);
            Rect searchIndicatorRect = new Rect(
                screenPos.x - 16f,
                screenPos.y + 52f,
                32f,
                24f);
            Rect secondaryIndicatorRect = new Rect(
                screenPos.x + 16f,
                screenPos.y + 52f,
                32f,
                24f);
            Widgets.Label(primaryIndicatorRect, primaryResponse ? "AP" : "A-");
            Widgets.Label(searchIndicatorRect, sharedSearch ? "S" : "-");
            Widgets.Label(secondaryIndicatorRect, secondaryResponse ? "BP" : "B-");

            Rect usingRect = new Rect(screenPos.x - 50f, screenPos.y - 57f, 40f, 24f);

            Widgets.Label(usingRect, usingRimKata ? "UT" : "UF");

            string combatText =
                combatActive
                    ? $"RT-{combatReasons}"
                    : $"RF-{combatReasons}";

            float combatWidth = Mathf.Max(40f, Text.CalcSize(combatText).x + 8f);

            Rect combatRect = new Rect(screenPos.x + 5f, screenPos.y - 57f, combatWidth, 24f);

            Widgets.Label(combatRect, combatText);

            DrawUsingPopup(pawn, screenPos);
            DrawCombatPopups(pawn, screenPos);
            DrawLowerPopups(pawn, screenPos);

            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
        }

        private static void DrawMinimalUsingState(
            Pawn pawn,
            bool usingRimKata)
        {
            Vector2 screenPos = pawn.DrawPos.MapToUIPosition();
            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Rect usingRect = new Rect(
                screenPos.x - 20f,
                screenPos.y - 57f,
                40f,
                24f);
            Widgets.Label(usingRect, usingRimKata ? "UT" : "UF");
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
        }

        private static Mesh CreateSearchMesh()
        {
            Mesh mesh = new Mesh
            {
                name = "RimKata progressive search cells",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };
            mesh.MarkDynamic();
            return mesh;
        }

        internal static void DrawSearchPulses(Map map)
        {
            if (!Prefs.DevMode
                || !SearchRangeEnabled
                || map == null
                || map.Disposed)
            {
                return;
            }

            SearchGraphics graphics = searchGraphics ?? (searchGraphics = new SearchGraphics());
            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (searchMeshMap != map || searchMeshTick != currentTick)
            {
                searchMeshMap = map;
                searchMeshTick = currentTick;
                SearchCells.Clear();
                UniqueSearchCells.Clear();
                if (actualSearchCellTick == currentTick)
                {
                    for (int i = 0; i < ActualSearchCells.Count; i++)
                    {
                        ActualSearchCell searchCell = ActualSearchCells[i];
                        if (searchCell.map != map
                            || !UniqueSearchCells.Add(searchCell.cell))
                        {
                            continue;
                        }

                        SearchCells.Add(searchCell.cell);
                    }
                }

                BuildSearchMesh(graphics.Mesh, SearchCells);
            }

            if (SearchCells.Count == 0)
            {
                return;
            }

            Graphics.DrawMesh(
                graphics.Mesh,
                Matrix4x4.identity,
                graphics.CellMaterial,
                0);
        }

        private static void BuildSearchMesh(
            Mesh mesh,
            List<IntVec3> cells)
        {
            SearchMeshVertices.Clear();
            SearchMeshTriangles.Clear();
            const float halfSize = 0.48f;
            for (int i = 0; i < cells.Count; i++)
            {
                Vector3 center = cells[i].ToVector3ShiftedWithAltitude(
                    AltitudeLayer.MetaOverlays);
                int vertex = SearchMeshVertices.Count;
                SearchMeshVertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
                SearchMeshVertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
                SearchMeshVertices.Add(center + new Vector3(halfSize, 0f, halfSize));
                SearchMeshVertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
                SearchMeshTriangles.Add(vertex);
                SearchMeshTriangles.Add(vertex + 1);
                SearchMeshTriangles.Add(vertex + 2);
                SearchMeshTriangles.Add(vertex);
                SearchMeshTriangles.Add(vertex + 2);
                SearchMeshTriangles.Add(vertex + 3);
            }

            mesh.Clear();
            mesh.SetVertices(SearchMeshVertices);
            mesh.SetTriangles(SearchMeshTriangles, 0);
            mesh.RecalculateBounds();
        }

        private static void DrawUsingPopup(
            Pawn pawn,
            Vector2 basePosition)
        {
            if (!usingPopups.TryGetValue(pawn, out UPopup popup))
            {
                return;
            }

            int elapsed = Find.TickManager.TicksGame - popup.startTick;

            if (elapsed < 0 || elapsed >= EntryPopupTicks)
            {
                usingPopups.Remove(pawn);
                return;
            }

            float progress = elapsed / (float)EntryPopupTicks;

            float offsetX = -55f - 30f * progress;
            float offsetY = -57f - 32f * progress;

            Rect popupRect = new Rect(basePosition.x + offsetX, basePosition.y + offsetY, 40f, 24f);

            Widgets.Label(popupRect, popup.state ? "UT" : "UF");
        }

        private static void DrawCombatPopups(
            Pawn pawn,
            Vector2 basePosition)
        {
            if (!combatPopups.TryGetValue(pawn, out List<RPopup> popups))
            {
                return;
            }

            int currentTick = Find.TickManager.TicksGame;

            for (int i = popups.Count - 1; i >= 0; i--)
            {
                RPopup popup = popups[i];
                int elapsed = currentTick - popup.startTick;

                if (elapsed < 0 || elapsed >= EntryPopupTicks)
                {
                    popups.RemoveAt(i);
                    continue;
                }

                float progress = elapsed / (float)EntryPopupTicks;

                float offsetX = 35f + 35f * progress;
                float offsetY = -57f - 32f * progress;

                Rect popupRect = new Rect(basePosition.x + offsetX, basePosition.y + offsetY, 40f, 24f);

                Widgets.Label(popupRect, popup.state ? "RT" : "RF");
            }

            if (popups.Count == 0)
            {
                combatPopups.Remove(pawn);
            }
        }

        private static void DrawLowerPopups(
            Pawn pawn,
            Vector2 basePosition)
        {
            if (!lowerPopups.TryGetValue(pawn, out List<LowerPopup> popups))
            {
                return;
            }

            int currentTick = Find.TickManager.TicksGame;
            for (int i = popups.Count - 1; i >= 0; i--)
            {
                LowerPopup popup = popups[i];
                int elapsed = currentTick - popup.startTick;
                if (elapsed < 0 || elapsed >= EntryPopupTicks)
                {
                    popups.RemoveAt(i);
                    continue;
                }

                float progress = elapsed / (float)EntryPopupTicks;
                float offsetX;
                string label;
                switch (popup.type)
                {
                    case LowerPopupType.PrimaryResponse:
                        offsetX = -48f - 30f * progress;
                        label = "AP";
                        break;
                    case LowerPopupType.SecondaryResponse:
                        offsetX = 16f + 30f * progress;
                        label = "BP";
                        break;
                    default:
                        offsetX = -16f;
                        label = "S";
                        break;
                }

                float offsetY = 52f + 32f * progress;
                Rect popupRect = new Rect(
                    basePosition.x + offsetX,
                    basePosition.y + offsetY,
                    32f,
                    24f);
                Widgets.Label(popupRect, label);
            }

            if (popups.Count == 0)
            {
                lowerPopups.Remove(pawn);
            }
        }
    }

    // Keep the GameComponent type for old saves, but register it only while the text HUD is on.
    [HarmonyPatch(typeof(Game), "FillComponents")]
    public static class Patch_Game_RimKataDebugHUDRegistration
    {
        public static void Postfix(Game __instance)
        {
            RimKataDebugHUD.RefreshGuiRegistration(__instance);
        }
    }

    [HarmonyPatch(typeof(Current), nameof(Current.Game), MethodType.Setter)]
    public static class Patch_Current_RimKataDebugHUDGameChanged
    {
        public static void Prefix(Game value, out bool __state)
        {
            __state = Current.Game != value;
        }

        public static void Postfix(bool __state)
        {
            if (__state)
            {
                RimKataDebugHUD.NotifyGameChanged();
            }
        }
    }

    [HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Setter)]
    public static class Patch_Prefs_RimKataDebugHUDDisabled
    {
        public static void Postfix(bool value)
        {
            // The options GUI also assigns the unchanged value on every GUI event.
            if (!value && (RimKataDebugHUD.Enabled || RimKataDebugHUD.SearchRangeEnabled))
            {
                RimKataDebugHUD.DisableForDeveloperMode();
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_RimKataDebugHUDGizmo
    {
        public static IEnumerable<Gizmo> Postfix(
            IEnumerable<Gizmo> __result,
            Pawn __instance)
        {
            return Prefs.DevMode
                ? AddDebugGizmo(__result, __instance)
                : __result;
        }

        private static IEnumerable<Gizmo> AddDebugGizmo(
            IEnumerable<Gizmo> gizmos,
            Pawn pawn)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
            }

            if (!Prefs.DevMode
                || pawn == null
                || !pawn.Spawned
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                yield break;
            }

            Pawn firstRimKataPawn =
                Find.Selector.SelectedObjects
                    .OfType<Pawn>()
                    .FirstOrDefault(
                        p => p != null
                            && p.Spawned
                            && RimKataEligibilityCache.IsCachedQualifiedPawn(p));

            if (firstRimKataPawn != pawn)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "KRWF_RimKata_DebugMenuLabel".Translate(),
                action = OpenDebugMenu
            };
        }

        private static void OpenDebugMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption(
                    ToggleOptionLabel(
                        "KRWF_RimKata_DebugHUDOption",
                        RimKataDebugHUD.Enabled),
                    delegate
                    {
                        RimKataDebugHUD.SetHudEnabled(
                            !RimKataDebugHUD.Enabled);
                    }),
                new FloatMenuOption(
                    ToggleOptionLabel(
                        "KRWF_RimKata_DebugSearchRangeOption",
                        RimKataDebugHUD.SearchRangeEnabled),
                    delegate
                    {
                        RimKataDebugHUD.SetSearchRangeEnabled(
                            !RimKataDebugHUD.SearchRangeEnabled);
                    })
            };

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static string ToggleOptionLabel(
            string translationKey,
            bool enabled)
        {
            return (enabled ? "[x] " : "[ ] ")
                + translationKey.Translate();
        }
    }
}
