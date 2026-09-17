using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Disposable spike for issue #21 stage B: can a second orthographic camera
    // render a colonist neighbourhood or the whole map off the meshes RimWorld
    // submits each frame, and what does widening the culling rect cost? It
    // never touches the main camera's target or position; it may move the
    // main camera's zoom on request so LOD coupling can be observed.
    public sealed class VideoSourceFixture
    {
        [Tool("test/video_source_spike", Description = "Render a colonist feed or whole-map frame from a second camera, or measure per-frame draw cost across baseline, pawn feeds and full-map culling. Test builds only.")]
        public async Task<object> Probe(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "pawn, map or cost")] string mode = "pawn",
            [ToolParameter(Description = "Colonist load id for pawn mode; empty picks the first free colonist.")] string pawnId = "",
            [ToolParameter(Description = "near, far or empty: set the main camera zoom first.")] string mainZoom = "",
            [ToolParameter(Description = "Frames measured per cost configuration.")] int frames = 60,
            [ToolParameter(Description = "Pawn feeds rendered per frame in the cost run.")] int feeds = 4,
            [ToolParameter(Description = "Render width (pawn feed and cost feeds).")] int width = 320,
            [ToolParameter(Description = "Render height; map mode derives width from the map aspect.")] int height = 200)
        {
            var task = await ctx.MainThread.InvokeAsync(() => VideoSourceSpike.Begin(mode, pawnId, mainZoom, frames, feeds, width, height), cancellationToken);
            return await task.ConfigureAwait(false);
        }
    }

    public sealed class VideoSourceSpike : MonoBehaviour
    {
        static VideoSourceSpike instance;
        static Camera camera;
        static TaskCompletionSource<object> pending;
        static readonly Stopwatch updateClock = new Stopwatch();
        static List<CellRect> extraRects = new List<CellRect>();
        static bool drawing;
        static Action afterDraw;
        static float deadline;

        internal static Task<object> Begin(string mode, string pawnId, string mainZoom, int frames, int feeds, int width, int height)
        {
            if (Application.isBatchMode || Find.Camera == null) throw new InvalidOperationException("The spike requires a rendered game");
            if (pending != null) throw new InvalidOperationException("The spike is busy");
            var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map");
            Install();
            if (mainZoom == "far") Find.CameraDriver.SetRootPosAndSize(Find.CameraDriver.MapPosition.ToVector3(), 60f);
            else if (mainZoom == "near") Find.CameraDriver.SetRootPosAndSize(Find.CameraDriver.MapPosition.ToVector3(), 11f);
            pending = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            deadline = Time.realtimeSinceStartup + 120f;
            try
            {
                switch (mode)
                {
                    case "pawn": BeginPawn(map, pawnId, width, height); break;
                    case "map": BeginMap(map, height); break;
                    case "cost": BeginCost(map, frames, feeds, width, height); break;
                    default: throw new ArgumentException("mode must be pawn, map or cost");
                }
            }
            catch { pending = null; throw; }
            return pending.Task;
        }

        static Pawn FindPawn(Map map, string pawnId)
        {
            var colonists = map.mapPawns.FreeColonistsSpawned;
            var pawn = string.IsNullOrEmpty(pawnId) ? colonists.FirstOrDefault() : colonists.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
            return pawn ?? throw new InvalidOperationException("No such spawned free colonist");
        }

        static void BeginPawn(Map map, string pawnId, int width, int height)
        {
            var pawn = FindPawn(map, pawnId);
            extraRects = new List<CellRect> { Neighbourhood(pawn) };
            afterDraw = () =>
            {
                var clock = Stopwatch.StartNew();
                var target = RenderTexture.GetTemporary(width, height, 24);
                try
                {
                    Render(target, pawn.DrawPos, 5f);
                    var renderMs = clock.Elapsed.TotalMilliseconds;
                    var png = Encode(target);
                    Complete(new
                    {
                        success = true, mode = "pawn", pawnId = pawn.GetUniqueLoadID(), pawnLabel = pawn.LabelShort,
                        width, height, renderMs, totalMs = clock.Elapsed.TotalMilliseconds,
                        mainZoom = Find.CameraDriver.CurrentZoom.ToString(), mainRootSize = Find.CameraDriver.ZoomRootSize,
                        pngBase64 = Convert.ToBase64String(png),
                    });
                }
                finally { RenderTexture.ReleaseTemporary(target); }
            };
        }

        static void BeginMap(Map map, int height)
        {
            var width = Mathf.RoundToInt(height * (float)map.Size.x / map.Size.z);
            extraRects = new List<CellRect> { CellRect.WholeMap(map) };
            afterDraw = () =>
            {
                var clock = Stopwatch.StartNew();
                var target = RenderTexture.GetTemporary(width, height, 24);
                try
                {
                    Render(target, new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f), map.Size.z / 2f);
                    var renderMs = clock.Elapsed.TotalMilliseconds;
                    var png = Encode(target);
                    Complete(new
                    {
                        success = true, mode = "map", mapSizeX = map.Size.x, mapSizeZ = map.Size.z, width, height,
                        pixelsPerCell = (float)height / map.Size.z, renderMs, totalMs = clock.Elapsed.TotalMilliseconds,
                        mainZoom = Find.CameraDriver.CurrentZoom.ToString(), pngBase64 = Convert.ToBase64String(png),
                    });
                }
                finally { RenderTexture.ReleaseTemporary(target); }
            };
        }

        sealed class Phase
        {
            internal string Name;
            internal int Feeds;
            internal bool WholeMap;
            internal readonly List<double> UpdateMs = new List<double>(), ExtraMs = new List<double>(), FrameMs = new List<double>();
            internal int TicksAtStart = -1, TicksAtEnd;
        }

        static void BeginCost(Map map, int frames, int feeds, int width, int height)
        {
            const int warmup = 5;
            var phases = new List<Phase>
            {
                new Phase { Name = "baseline" },
                new Phase { Name = "pawn_feeds", Feeds = Math.Max(1, feeds) },
                new Phase { Name = "whole_map", WholeMap = true },
            };
            var pawns = map.mapPawns.FreeColonistsSpawned.Take(Math.Max(1, feeds)).ToList();
            if (pawns.Count == 0) throw new InvalidOperationException("No spawned free colonists");
            var mapWidth = Mathf.RoundToInt(height * (float)map.Size.x / map.Size.z);
            var feedTarget = new RenderTexture(width, height, 24);
            var mapTarget = new RenderTexture(mapWidth, height, 24);
            var feedPixels = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var mapPixels = new Texture2D(mapWidth, height, TextureFormat.RGBA32, false);
            int phaseIndex = 0, frame = 0;
            afterDraw = () =>
            {
                var phase = phases[phaseIndex];
                var updateMs = updateClock.Elapsed.TotalMilliseconds;
                var clock = Stopwatch.StartNew();
                if (phase.Feeds > 0)
                {
                    extraRects = pawns.Select(Neighbourhood).ToList();
                    foreach (var pawn in pawns)
                    {
                        Render(feedTarget, pawn.DrawPos, 5f);
                        Readback(feedTarget, feedPixels);
                    }
                }
                else if (phase.WholeMap)
                {
                    extraRects = new List<CellRect> { CellRect.WholeMap(map) };
                    Render(mapTarget, new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f), map.Size.z / 2f);
                    Readback(mapTarget, mapPixels);
                }
                else extraRects = new List<CellRect>();
                if (frame >= warmup)
                {
                    if (phase.TicksAtStart < 0) phase.TicksAtStart = Find.TickManager.TicksGame;
                    phase.TicksAtEnd = Find.TickManager.TicksGame;
                    phase.UpdateMs.Add(updateMs);
                    phase.ExtraMs.Add(clock.Elapsed.TotalMilliseconds);
                    phase.FrameMs.Add(Time.unscaledDeltaTime * 1000.0);
                }
                frame++;
                if (frame < warmup + frames) return;
                frame = 0;
                phaseIndex++;
                if (phaseIndex < phases.Count) return;
                feedTarget.Release(); mapTarget.Release();
                Destroy(feedTarget); Destroy(mapTarget); Destroy(feedPixels); Destroy(mapPixels);
                Complete(new
                {
                    success = true, mode = "cost", frames, feeds = pawns.Count, feedWidth = width, feedHeight = height,
                    mapWidth, mapHeight = height, mapSizeX = map.Size.x, mapSizeZ = map.Size.z,
                    mainZoom = Find.CameraDriver.CurrentZoom.ToString(), paused = Find.TickManager.Paused,
                    phases = phases.Select(p => new
                    {
                        name = p.Name, feeds = p.Feeds, wholeMap = p.WholeMap, samples = p.UpdateMs.Count,
                        ticksAdvanced = p.TicksAtEnd - p.TicksAtStart,
                        updatePlayMs = Summary(p.UpdateMs), extraRenderMs = Summary(p.ExtraMs), frameMs = Summary(p.FrameMs),
                    }).ToList(),
                });
            };
        }

        static object Summary(List<double> values)
        {
            if (values.Count == 0) return new { mean = 0.0, p50 = 0.0, p95 = 0.0, max = 0.0 };
            var sorted = values.OrderBy(v => v).ToList();
            double At(double fraction) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(fraction * (sorted.Count - 1)))];
            return new { mean = values.Average(), p50 = At(0.5), p95 = At(0.95), max = sorted[sorted.Count - 1] };
        }

        static CellRect Neighbourhood(Pawn pawn)
        {
            var rect = CellRect.CenteredOn(pawn.Position, 12);
            rect.ClipInsideMap(pawn.Map);
            return rect;
        }

        static void Render(RenderTexture target, Vector3 center, float orthographicSize)
        {
            var main = Find.Camera;
            camera.CopyFrom(main);
            camera.enabled = false;
            camera.targetTexture = target;
            camera.aspect = (float)target.width / target.height;
            camera.orthographicSize = orthographicSize;
            camera.transform.position = new Vector3(center.x, main.transform.position.y, center.z);
            camera.transform.rotation = main.transform.rotation;
            camera.farClipPlane = Mathf.Max(main.farClipPlane, 100f);
            camera.Render();
            camera.targetTexture = null;
        }

        static void Readback(RenderTexture source, Texture2D pixels)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            }
            finally { RenderTexture.active = previous; }
        }

        static byte[] Encode(RenderTexture source)
        {
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                Readback(source, pixels);
                pixels.Apply();
                return ImageConversion.EncodeToPNG(pixels);
            }
            finally { Destroy(pixels); }
        }

        static void Install()
        {
            if (instance != null) return;
            instance = new GameObject("RimGovernorVideoSourceSpike").AddComponent<VideoSourceSpike>();
            DontDestroyOnLoad(instance.gameObject);
            var cameraObject = new GameObject("RimGovernorVideoSourceSpikeCamera");
            DontDestroyOnLoad(cameraObject);
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            var harmony = new Harmony("davidarcher.rimgovernor.video-source-spike");
            harmony.Patch(AccessTools.Method(typeof(Game), "UpdatePlay"),
                prefix: new HarmonyMethod(typeof(VideoSourceSpike), nameof(BeforeDraw)),
                postfix: new HarmonyMethod(typeof(VideoSourceSpike), nameof(AfterDraw)),
                finalizer: new HarmonyMethod(typeof(VideoSourceSpike), nameof(DrawFinished)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentViewRect"),
                postfix: new HarmonyMethod(typeof(VideoSourceSpike), nameof(ViewRect)));
        }

        public static void BeforeDraw()
        {
            if (pending == null) return;
            drawing = true;
            updateClock.Restart();
        }

        public static void ViewRect(ref CellRect __result)
        {
            if (!drawing || extraRects.Count == 0) return;
            var result = __result;
            foreach (var extra in extraRects)
                result = CellRect.FromLimits(Math.Min(result.minX, extra.minX), Math.Min(result.minZ, extra.minZ),
                    Math.Max(result.maxX, extra.maxX), Math.Max(result.maxZ, extra.maxZ));
            __result = result;
        }

        public static void AfterDraw()
        {
            if (pending == null) return;
            updateClock.Stop();
            try { afterDraw?.Invoke(); }
            catch (Exception error) { Fail(error.ToString()); }
            finally { drawing = false; }
        }

        public static Exception DrawFinished(Exception __exception)
        {
            drawing = false;
            if (__exception != null && pending != null) Fail("Native map rendering failed: " + __exception.Message);
            return __exception;
        }

        void Update()
        {
            if (pending != null && Time.realtimeSinceStartup >= deadline) Fail("Timed out");
        }

        static void Complete(object result)
        {
            var request = pending;
            pending = null; afterDraw = null; extraRects = new List<CellRect>();
            request?.TrySetResult(result);
        }

        static void Fail(string message)
        {
            var request = pending;
            pending = null; afterDraw = null; extraRects = new List<CellRect>();
            request?.TrySetException(new InvalidOperationException(message));
        }
    }
}
