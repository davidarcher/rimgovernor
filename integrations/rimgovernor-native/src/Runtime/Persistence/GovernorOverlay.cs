#nullable enable
using RimGovernor.Host.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// One colored region of an overlay layer: filled quads, or the outline
    /// of the cell set.
    /// </summary>
    public sealed class OverlayGroup
    {
        public Color Color;
        public bool Outline;
        public readonly List<CellRect> Fills = new List<CellRect>();
        public readonly HashSet<IntVec3> Cells = new HashSet<IntVec3>();
    }

    /// <summary>
    /// The controller's map overlay: named layers drawn as flat
    /// transparent meshes plus text labels. A request replaces one layer by
    /// id; nothing reads it back and nothing is saved, so a reload starts
    /// blank until the controller redraws.
    /// </summary>
    public sealed class GovernorOverlay : MapComponent
    {
        private const float EdgeWidth = 0.1f;

        private sealed class Layer
        {
            public List<OverlayGroup> Groups = new List<OverlayGroup>();
            public List<(string Text, IntVec3 Cell)> Labels = new List<(string, IntVec3)>();
            public List<(Mesh Mesh, Material Material)>? Meshes;
        }

        private readonly Dictionary<string, Layer> layers = new Dictionary<string, Layer>(StringComparer.Ordinal);

        public GovernorOverlay(Map map) : base(map) { }

        public int Count => layers.Count;

        // Code-drawn groups (activity): drawn every frame on the
        // current map and toggled like request-fed layers.
        private static readonly List<(string Group, Action<Map> Draw)> drawers = new List<(string, Action<Map>)>();

        public static void RegisterDrawer(string group, Action<Map> draw)
        {
            drawers.Add((group, draw));
            OverlayVisibility.Known(group);
        }

        public void Replace(string id, List<OverlayGroup> groups, List<(string, IntVec3)> labels)
        {
            Remove(id);
            layers[id] = new Layer { Groups = groups, Labels = labels };
            OverlayVisibility.Known(id);
        }

        public void Remove(string id)
        {
            if (!layers.TryGetValue(id, out var layer)) return;
            Release(layer);
            layers.Remove(id);
        }

        public override void MapComponentUpdate()
        {
            if (!Application.isBatchMode && Find.CurrentMap == map) WidenZoomOut();
            if (!OverlayVisibility.Master || Application.isBatchMode
                || Find.CurrentMap != map || WorldRendererUtility.WorldSelected) return;
            foreach (var (group, draw) in drawers)
            {
                if (!OverlayVisibility.Shown(group)) continue;
                try { draw(map); }
                catch (Exception ex) { ModLog.Error("overlay", "overlay drawer " + group + " failed: " + ex, null, "overlay.drawer:" + group); }
            }
            foreach (var entry in layers)
            {
                if (!OverlayVisibility.Shown(entry.Key)) continue;
                var layer = entry.Value;
                layer.Meshes ??= Build(layer.Groups);
                foreach (var (mesh, material) in layer.Meshes)
                    Graphics.DrawMesh(mesh, Vector3.zero, Quaternion.identity, material, 0);
            }
        }

        // Vanilla caps scroll-out at root size 60, well short of a whole map on a
        // large monitor. Raise the cap so the entire map (plus a margin) fits in
        // either axis at any aspect ratio; root size is half the visible height.
        void WidenZoomOut()
        {
            var config = Find.CameraDriver?.config;
            if (config == null) return;
            var aspect = Math.Max(0.1f, (float)UI.screenWidth / Math.Max(1, UI.screenHeight));
            var fit = Math.Max(map.Size.z / 2f, map.Size.x / (2f * aspect)) + 8f;
            if (config.sizeRange.max < fit) config.sizeRange.max = fit;
        }

        public override void MapComponentOnGUI()
        {
            if (layers.Count == 0 || !OverlayVisibility.Master || Find.CurrentMap != map || WorldRendererUtility.WorldSelected) return;
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            foreach (var entry in layers)
            {
                if (!OverlayVisibility.Shown(entry.Key)) continue;
                foreach (var (text, cell) in entry.Value.Labels)
                    if (view.Contains(cell))
                        GenMapUI.DrawThingLabel(GenMapUI.LabelDrawPosFor(cell), text, Color.white);
            }
        }

        public override void MapRemoved()
        {
            foreach (var layer in layers.Values) Release(layer);
            layers.Clear();
        }

        private static void Release(Layer layer)
        {
            if (layer.Meshes == null) return;
            foreach (var (mesh, _) in layer.Meshes) UnityEngine.Object.Destroy(mesh);
            layer.Meshes = null;
        }

        // One mesh per (color, style); game thread with a GPU only.
        private static List<(Mesh, Material)> Build(List<OverlayGroup> groups)
        {
            var y = AltitudeLayer.MetaOverlays.AltitudeFor();
            var result = new List<(Mesh, Material)>();
            foreach (var bucket in groups.GroupBy(g => (g.Color, g.Outline)))
            {
                var quads = new List<Rect>();
                if (bucket.Key.Outline) Edges(bucket.SelectMany(g => g.Cells).ToHashSet(), quads);
                else foreach (var g in bucket) foreach (var r in g.Fills) quads.Add(new Rect(r.minX, r.minZ, r.Width, r.Height));
                if (quads.Count == 0) continue;
                var verts = new Vector3[quads.Count * 4];
                var tris = new int[quads.Count * 6];
                for (var i = 0; i < quads.Count; i++)
                {
                    var q = quads[i];
                    verts[i * 4] = new Vector3(q.xMin, y, q.yMin);
                    verts[i * 4 + 1] = new Vector3(q.xMin, y, q.yMax);
                    verts[i * 4 + 2] = new Vector3(q.xMax, y, q.yMax);
                    verts[i * 4 + 3] = new Vector3(q.xMax, y, q.yMin);
                    tris[i * 6] = i * 4; tris[i * 6 + 1] = i * 4 + 1; tris[i * 6 + 2] = i * 4 + 2;
                    tris[i * 6 + 3] = i * 4; tris[i * 6 + 4] = i * 4 + 2; tris[i * 6 + 5] = i * 4 + 3;
                }
                var mesh = new Mesh { name = "RimGovernorOverlay", indexFormat = IndexFormat.UInt32, vertices = verts, triangles = tris };
                mesh.RecalculateBounds();
                result.Add((mesh, MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.MetaOverlay, bucket.Key.Color)));
            }
            return result;
        }

        // Boundary edges of the cell set as thin quads inside the cells,
        // collinear neighbours merged into one quad.
        private static void Edges(HashSet<IntVec3> cells, List<Rect> quads)
        {
            var sorted = cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            // South (dz=-1) and north (dz=+1) edges run along x.
            foreach (var dz in new[] { -1, 1 })
            {
                IntVec3? start = null, last = null;
                void Flush()
                {
                    if (start is IntVec3 s && last is IntVec3 l)
                        quads.Add(new Rect(s.x, dz < 0 ? s.z : s.z + 1 - EdgeWidth, l.x + 1 - s.x, EdgeWidth));
                    start = last = null;
                }
                foreach (var c in sorted)
                {
                    if (cells.Contains(new IntVec3(c.x, 0, c.z + dz))) { Flush(); continue; }
                    if (last is IntVec3 l && (l.z != c.z || l.x + 1 != c.x)) Flush();
                    start ??= c;
                    last = c;
                }
                Flush();
            }
            sorted = cells.OrderBy(c => c.x).ThenBy(c => c.z).ToList();
            foreach (var dx in new[] { -1, 1 })
            {
                IntVec3? start = null, last = null;
                void Flush()
                {
                    if (start is IntVec3 s && last is IntVec3 l)
                        quads.Add(new Rect(dx < 0 ? s.x : s.x + 1 - EdgeWidth, s.z, EdgeWidth, l.z + 1 - s.z));
                    start = last = null;
                }
                foreach (var c in sorted)
                {
                    if (cells.Contains(new IntVec3(c.x + dx, 0, c.z))) { Flush(); continue; }
                    if (last is IntVec3 l && (l.x != c.x || l.z + 1 != c.z)) Flush();
                    start ??= c;
                    last = c;
                }
                Flush();
            }
        }
    }

    /// <summary>
    /// Player-side overlay visibility: a master switch, a pawn traffic heat
    /// switch (the "heat" group, off by default: it is busy) and one switch
    /// per other layer group (the layer id up to its first '.', ':' or '/'),
    /// all on the play-settings bar. Session only.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OverlayVisibility
    {
        public static bool Master = true;
        public static bool Traffic;
        private const string TrafficGroup = "heat";
        private static readonly SortedSet<string> groups = new SortedSet<string>(StringComparer.Ordinal);
        // The plan's field-block lattice ("fields") is busy, so it starts hidden.
        private static readonly HashSet<string> hidden = new HashSet<string>(StringComparer.Ordinal) { "fields" };
        private static readonly Texture2D MasterIcon = ContentFinder<Texture2D>.Get("UI/Buttons/ShowZones", false) ?? BaseContent.BadTex;
        private static readonly Texture2D GroupsIcon = ContentFinder<Texture2D>.Get("UI/Buttons/ShowRoomStats", false) ?? BaseContent.BadTex;
        private static readonly Texture2D TrafficIcon = ContentFinder<Texture2D>.Get("UI/Buttons/ShowBeauty", false) ?? BaseContent.BadTex;

        static OverlayVisibility()
        {
            try
            {
                new Harmony("rimgovernor.overlay.toggle").Patch(
                    AccessTools.Method(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))
                        ?? throw new MissingMethodException("PlaySettings.DoPlaySettingsGlobalControls"),
                    postfix: new HarmonyMethod(typeof(OverlayVisibility), nameof(Controls)));
            }
            catch (Exception ex)
            {
                ModLog.Error("overlay", "Overlay toggle installation failed: " + ex);
            }
        }

        public static string Group(string id)
        {
            var cut = id.IndexOfAny(new[] { '.', ':', '/' });
            return cut < 0 ? id : id.Substring(0, cut);
        }

        public static void Known(string id) => groups.Add(Group(id));
        public static bool Shown(string id)
        {
            var group = Group(id);
            return group == TrafficGroup ? Traffic : !hidden.Contains(group);
        }

        private static void Controls(WidgetRow row, bool worldView)
        {
            if (worldView || row == null) return;
            row.ToggleableIcon(ref Master, MasterIcon, "Show the RimGovernor overlay.", SoundDefOf.Mouseover_ButtonToggle);
            if (!Master) return;
            row.ToggleableIcon(ref Traffic, TrafficIcon, "Show the RimGovernor pawn traffic heat map.", SoundDefOf.Mouseover_ButtonToggle);
            var others = groups.Where(g => g != TrafficGroup).ToList();
            if (others.Count == 0) return;
            if (row.ButtonIcon(GroupsIcon, "Choose which RimGovernor overlay layers are shown."))
                Find.WindowStack.Add(new FloatMenu(others.Select(g => new FloatMenuOption(
                    (hidden.Contains(g) ? "Show " : "Hide ") + g,
                    () => { if (!hidden.Remove(g)) hidden.Add(g); })).ToList()));
        }
    }
}
