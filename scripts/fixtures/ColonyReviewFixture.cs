#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Colony review recorder (review/colony-week): once started, every
    // IntervalTicks of game time it renders the colony from above into
    // colony-<tick>.jpg in Dir, with a whole-map map-<tick>.jpg once a game
    // day. Colony facts are not its business: the run's timeline and
    // telemetry already hold them. It runs from the frame loop, so the
    // controller drives the game undisturbed while it records. Needs a
    // graphics device: a -batchmode game launched without -nographics
    // (RIMGOVERNOR_ACCEPT_GRAPHICS=1) renders on demand, it only never
    // presents a window.
    public static class ColonyReview
    {
        const int DayTicks = 60000;
        static bool patched;
        static string? dir;
        static int interval = 2500, size = 1280, nextTick = -1, lastTick = -1, frames;
        static CellRect? viewOverride;
        static string? lastError;

        public static object Start(string directory, int intervalTicks, int imageSize)
        {
            Directory.CreateDirectory(directory);
            dir = directory; interval = Math.Max(250, intervalTicks); size = Mathf.Clamp(imageSize, 256, 4096);
            nextTick = Find.TickManager.TicksGame; lastTick = -1; lastError = null;
            EnsurePatched();
            return Status();
        }

        public static object Stop() { dir = null; return Status(); }

        public static object Status() => new { success = true, recording = dir != null, dir, interval, size, nextTick, frames,
            batchMode = Application.isBatchMode, graphics = SystemInfo.graphicsDeviceType.ToString(), lastError };

        static void EnsurePatched()
        {
            if (patched) return;
            var harmony = new Harmony("rimgovernor.test.colony-review");
            harmony.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)),
                prefix: new HarmonyMethod(typeof(ColonyReview), nameof(BeforeMapUpdate)),
                postfix: new HarmonyMethod(typeof(ColonyReview), nameof(AfterMapUpdate)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect)),
                postfix: new HarmonyMethod(typeof(ColonyReview), nameof(ViewRect)));
            patched = true;
        }

        // Map sections and things are drawn only inside the camera driver's
        // view rect, so the capture frame widens it to the shot's rect.
        static void ViewRect(ref CellRect __result)
        {
            if (viewOverride is CellRect rect) __result = rect;
        }

        static void BeforeMapUpdate(Map __instance)
        {
            if (dir == null || __instance != Find.CurrentMap || Find.TickManager.TicksGame < nextTick) return;
            viewOverride = ColonyRect(__instance);
        }

        static void AfterMapUpdate(Map __instance)
        {
            if (viewOverride is not CellRect rect || __instance != Find.CurrentMap) return;
            var tick = Find.TickManager.TicksGame;
            try
            {
                if (frames == 0) LogSections(__instance);
                Render(rect, $"colony-{tick:D8}.jpg");
                if (lastTick < 0 || tick / DayTicks != lastTick / DayTicks)
                {
                    viewOverride = CellRect.WholeMap(__instance);
                    Render(viewOverride.Value, $"map-{tick:D8}.jpg");
                }
                frames++;
            }
            catch (Exception e)
            {
                lastError = e.ToString();
                Log.Warning("[RimGovernor] colony review capture failed: " + e.Message);
            }
            finally
            {
                viewOverride = null;
                lastTick = tick;
                nextTick = (tick / interval + 1) * interval;
            }
        }

        // Why a frame might lack terrain: what the map drawer holds for the
        // section under the map centre (layers, submeshes, shaders).
        static void LogSections(Map map)
        {
            try
            {
                var sections = Traverse.Create(map.mapDrawer).Field("sections").GetValue<Array>();
                var section = (Section)sections.GetValue(sections.GetLength(0) / 2, sections.GetLength(1) / 2);
                var lines = new List<string>();
                foreach (var layer in Traverse.Create(section).Field("layers").GetValue<List<SectionLayer>>())
                {
                    var subMeshes = Traverse.Create(layer).Field("subMeshes").GetValue<List<LayerSubMesh>>();
                    lines.Add($"{layer.GetType().Name} visible={layer.Visible} " + string.Join(",", subMeshes.Select(m =>
                        $"[{m.material?.shader?.name} supported={m.material?.shader?.isSupported} verts={m.mesh?.vertexCount} finalized={m.finalized} disabled={m.disabled}]")));
                }
                Log.Message($"[RimGovernor] colony review sections: camera mask={Find.Camera.cullingMask} clear={Find.Camera.clearFlags} far={Find.Camera.farClipPlane} y={Find.Camera.transform.position.y}\n" + string.Join("\n", lines));
            }
            catch (Exception e) { Log.Warning("[RimGovernor] colony review section probe failed: " + e.Message); }
        }

        // The home area plus every colonist and colony building, with a
        // margin, squared and kept on the map; at least 40 cells across.
        static CellRect ColonyRect(Map map)
        {
            var cells = map.areaManager.Home.ActiveCells
                .Concat(map.mapPawns.FreeColonistsSpawned.Select(p => p.Position))
                .Concat(map.listerBuildings.allBuildingsColonist.Select(b => b.Position)).ToList();
            if (cells.Count == 0) return CellRect.CenteredOn(map.Center, 30).ClipInsideMap(map);
            int minX = cells.Min(c => c.x), maxX = cells.Max(c => c.x), minZ = cells.Min(c => c.z), maxZ = cells.Max(c => c.z);
            var half = Math.Max(20, Math.Max(maxX - minX, maxZ - minZ) / 2 + 8);
            var center = new IntVec3((minX + maxX) / 2, 0, (minZ + maxZ) / 2);
            return CellRect.CenteredOn(center, half).ClipInsideMap(map);
        }

        static void Render(CellRect rect, string name)
        {
            var camera = Find.Camera;
            var position = camera.transform.position;
            var ortho = camera.orthographicSize;
            var aspect = camera.aspect;
            var target = camera.targetTexture;
            var texture = RenderTexture.GetTemporary(size, size, 24);
            var previous = RenderTexture.active;
            try
            {
                var half = Math.Max(rect.Width, rect.Height) / 2f;
                camera.transform.position = new Vector3(rect.minX + rect.Width / 2f, position.y, rect.minZ + rect.Height / 2f);
                camera.orthographicSize = half;
                camera.aspect = 1f;
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                var image = new Texture2D(size, size, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(dir!, name), image.EncodeToJPG(80));
                UnityEngine.Object.Destroy(image);
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = target;
                camera.transform.position = position;
                camera.orthographicSize = ortho;
                camera.aspect = aspect;
                RenderTexture.ReleaseTemporary(texture);
            }
        }
    }

    public sealed class ColonyReviewFixture
    {
        [Tool("test/colony_review", Description = "UNSAFE FOR MODEL EXECUTION. Test recorder: action=start renders the colony to a JPEG in dir every intervalTicks of game time (a whole-map shot once a day); stop ends it; status reads it. Needs a graphics device (a game launched without -nographics).")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "start, stop or status (default).")] string action = "status",
            [ToolParameter(Description = "Output directory for start.")] string dir = "",
            [ToolParameter(Description = "Game ticks between captures (2500 is one in-game hour).")] int intervalTicks = 2500,
            [ToolParameter(Description = "Square image size in pixels.")] int size = 1280)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                switch (action)
                {
                    case "start":
                        if (string.IsNullOrEmpty(dir)) throw new ArgumentException("start needs dir.");
                        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                            throw new InvalidOperationException("No graphics device: launch the game without -nographics.");
                        return ColonyReview.Start(dir, intervalTicks, size);
                    case "stop": return ColonyReview.Stop();
                    case "status": return ColonyReview.Status();
                    default: throw new ArgumentException("Unknown action.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
