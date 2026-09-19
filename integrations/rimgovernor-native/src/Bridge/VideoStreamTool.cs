#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class VideoStreamTool
    {
        [Tool("home/video_stream", Description = "Lease raw current-screen video for the local dashboard. Presentation only; no input or simulation changes.")]
        public async Task<object> Stream(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Capture lease in real seconds, 0 stops capture.", DefaultValue = 8)] int seconds = 8)
        {
            if (seconds < 0 || seconds > 15) throw new ArgumentOutOfRangeException(nameof(seconds));
            return await ctx.MainThread.InvokeAsync(() => VideoStreamDriver.Lease(seconds), cancellationToken);
        }
    }

    internal enum VideoSourceKind { Screen, Pawn, Map }

    // What one lease captures. Screen is the presented framebuffer; Pawn and
    // Map are rendered by a second camera into a feed of the given size at
    // the given cadence. Equal specs share one source (and one buffer).
    internal readonly struct VideoSourceSpec : IEquatable<VideoSourceSpec>
    {
        internal const int MinSide = 16, MaxWidth = 3840, MaxHeight = 2160;
        internal VideoSourceSpec(VideoSourceKind kind, string? pawnId, int width, int height, double framesPerSecond)
        {
            Kind = kind; PawnId = kind == VideoSourceKind.Pawn ? pawnId : null;
            Width = width; Height = height; FramesPerSecond = framesPerSecond;
        }
        internal static readonly VideoSourceSpec Screen = new VideoSourceSpec(VideoSourceKind.Screen, null, 0, 0, 60);
        internal VideoSourceKind Kind { get; }
        internal string? PawnId { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal double FramesPerSecond { get; }
        internal bool Rendered => Kind != VideoSourceKind.Screen;

        internal static double DefaultFps(VideoSourceKind kind) => kind == VideoSourceKind.Map ? 4 : kind == VideoSourceKind.Pawn ? 15 : 60;
        internal static double MaxFps(VideoSourceKind kind) => kind == VideoSourceKind.Map ? 10 : kind == VideoSourceKind.Pawn ? 30 : 60;

        // Normalizes a request: absent sizes and cadences take the kind's
        // defaults; anything out of range is a refusal with a reason.
        internal static bool TryCreate(VideoSourceKind kind, string? pawnId, int width, int height, double fps,
            out VideoSourceSpec spec, out string? reason)
        {
            spec = Screen; reason = null;
            if (kind == VideoSourceKind.Screen) return true;
            if (kind == VideoSourceKind.Pawn && string.IsNullOrEmpty(pawnId)) { reason = "A pawn source requires pawn_id."; return false; }
            if (width == 0) width = kind == VideoSourceKind.Map ? 0 : 320;
            if (height == 0) height = kind == VideoSourceKind.Map ? 400 : 200;
            if ((width != 0 && (width < MinSide || width > MaxWidth)) || height < MinSide || height > MaxHeight)
            { reason = "Feed size must be within 16 x 16 and 3840 x 2160."; return false; }
            if (fps == 0) fps = DefaultFps(kind);
            if (fps < 0.5 || fps > MaxFps(kind)) { reason = "frames_per_second must be within 0.5 and " + MaxFps(kind) + " for this source."; return false; }
            spec = new VideoSourceSpec(kind, pawnId, width, height, fps);
            return true;
        }

        public bool Equals(VideoSourceSpec other) => Kind == other.Kind && PawnId == other.PawnId
            && Width == other.Width && Height == other.Height && FramesPerSecond.Equals(other.FramesPerSecond);
        public override bool Equals(object obj) => obj is VideoSourceSpec other && Equals(other);
        public override int GetHashCode() => unchecked(((int)Kind * 397 ^ (PawnId?.GetHashCode() ?? 0)) * 397 ^ Width * 31 ^ Height);
    }

    // Shared with the typed rimgovernor/presentation_lease_video and
    // rimgovernor/presentation_read_frame RPCs: a plain in-process snapshot of
    // the same capture VideoStreamDriver already performs for the shared
    // buffer, read directly off this process's memory rather than by reopening
    // the buffer (which the Go relay maps for itself).
    internal readonly struct VideoFrameSnapshot
    {
        internal VideoFrameSnapshot(string sourceId, long sequence, int width, int height,
            double capturedUnixSeconds, double readbackMs, byte[] data, bool topDown, bool bgra, string captureMethod)
        {
            SourceId = sourceId; Sequence = sequence; Width = width; Height = height;
            CapturedUnixSeconds = capturedUnixSeconds; ReadbackMs = readbackMs; Data = data;
            TopDown = topDown; Bgra = bgra; CaptureMethod = captureMethod;
        }
        internal string SourceId { get; }
        internal long Sequence { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal double CapturedUnixSeconds { get; }
        internal double ReadbackMs { get; }
        internal byte[] Data { get; }
        internal bool TopDown { get; }
        internal bool Bgra { get; }
        internal string CaptureMethod { get; }
    }

    // A rendered source that cannot be opened right now: no current map, or a
    // pawn that is not spawned on it. Capture itself is supported.
    internal sealed class VideoSourceUnavailableException : InvalidOperationException
    {
        internal VideoSourceUnavailableException(string message) : base(message) { }
    }

    internal readonly struct VideoLeaseStatus
    {
        internal VideoLeaseStatus(bool supported, string? unavailableDetail, bool active, string? sourceId,
            float remainingSeconds, long capturedFrames, bool topDown, bool bgra, string? captureMethod,
            bool asyncReadbackSupported, string renderer, bool focused, int targetFrameRate, float frameSeconds,
            int vsyncCount, double refreshRate, ulong workingSetBytes, double processCpuSeconds, VideoSourceSpec source)
        {
            Supported = supported; UnavailableDetail = unavailableDetail; Active = active; SourceId = sourceId;
            RemainingSeconds = remainingSeconds; CapturedFrames = capturedFrames; TopDown = topDown; Bgra = bgra;
            CaptureMethod = captureMethod; AsyncReadbackSupported = asyncReadbackSupported; Renderer = renderer;
            Focused = focused; TargetFrameRate = targetFrameRate; FrameSeconds = frameSeconds;
            VsyncCount = vsyncCount; RefreshRate = refreshRate; WorkingSetBytes = workingSetBytes;
            ProcessCpuSeconds = processCpuSeconds; Source = source;
        }
        internal bool Supported { get; }
        internal string? UnavailableDetail { get; }
        internal bool Active { get; }
        internal string? SourceId { get; }
        internal float RemainingSeconds { get; }
        internal long CapturedFrames { get; }
        internal bool TopDown { get; }
        internal bool Bgra { get; }
        internal string? CaptureMethod { get; }
        internal bool AsyncReadbackSupported { get; }
        internal string Renderer { get; }
        internal bool Focused { get; }
        internal int TargetFrameRate { get; }
        internal float FrameSeconds { get; }
        internal int VsyncCount { get; }
        internal double RefreshRate { get; }
        internal ulong WorkingSetBytes { get; }
        internal double ProcessCpuSeconds { get; }
        internal VideoSourceSpec Source { get; }
    }

    // One leased source: its shared-memory buffer (latest RGBA32 frame behind
    // a nonblocking cross-process lock), its lease clock and, for rendered
    // sources, the render target the second camera draws into.
    internal sealed class VideoSource
    {
        internal const int Capacity = 40 + 3840 * 2160 * 4;
        [DllImport("libc", SetLastError = true)]
        static extern int flock(int fd, int operation);

        internal readonly VideoSourceSpec Spec;
        internal readonly string Name;
        internal readonly Map Map;
        MemoryMappedFile? mapping;
        MemoryMappedViewAccessor? buffer;
        Mutex? gate;
        FileStream? file;
        internal RenderTexture? Target;
        internal Texture2D? Texture;
        internal bool Pending;
        internal Pawn? FramePawn;
        // Each viewer's lease expiry; the source lives while any viewer holds
        // it, so one tab stopping or timing out does not end another's feed.
        internal readonly Dictionary<string, float> Holds = new Dictionary<string, float>();
        internal float Until, Next;
        internal long Sequence;
        internal byte[]? LatestFrame;
        internal int LatestWidth, LatestHeight;
        internal double LatestCapturedUnixSeconds, LatestReadbackMs;

        internal VideoSource(VideoSourceSpec spec, Map map)
        {
            Spec = spec; Map = map;
            if (Application.platform == RuntimePlatform.LinuxPlayer)
            {
                Name = "/dev/shm/RimGovernorVideo-" + Guid.NewGuid().ToString("N");
                file = new FileStream(Name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                file.SetLength(Capacity);
                mapping = MemoryMappedFile.CreateFromFile(file, null, Capacity,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            }
            else
            {
                Name = "Local\\RimGovernorVideo-" + Guid.NewGuid().ToString("N");
                mapping = MemoryMappedFile.CreateNew(Name, Capacity);
                gate = new Mutex(false, Name + "-lock");
            }
            buffer = mapping.CreateViewAccessor();
        }

        internal bool Open => mapping != null;
        internal double Interval => 1.0 / Spec.FramesPerSecond;

        internal void Publish(int width, int height, double captured, double readbackMs, byte[] pixels, PlayerFrame? view)
        {
            var buffer = this.buffer;
            if (mapping == null || buffer == null) return;
            bool held;
            try { held = file != null ? flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) == 0 : gate != null && gate.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            try
            {
                buffer.Write(8, width);
                buffer.Write(12, height);
                buffer.Write(16, captured);
                buffer.Write(24, readbackMs);
                buffer.Write(32, view?.Selection ?? 0);
                // Mono's generic accessor array write visits each byte. Copy the
                // immutable payload in one operation while retaining the mapping.
                bool retained = false;
                var handle = buffer.SafeMemoryMappedViewHandle;
                try
                {
                    handle.DangerousAddRef(ref retained);
                    Marshal.Copy(pixels, 0, IntPtr.Add(handle.DangerousGetHandle(), 40), pixels.Length);
                }
                finally { if (retained) handle.DangerousRelease(); }
                ++Sequence;
                // Only the screen's frames map back to player input.
                if (view != null) PlayerFrame.Remember(Name, Sequence, view);
                buffer.Write(0, Sequence);
                LatestFrame = pixels; LatestWidth = width; LatestHeight = height;
                LatestCapturedUnixSeconds = captured; LatestReadbackMs = readbackMs;
            }
            finally
            {
                if (file != null) flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 8);
                else gate?.ReleaseMutex();
            }
        }

        internal void Release()
        {
            // The GPU owns an outstanding target until its callback. Do not reuse it.
            if (!Pending && Target != null) { Target.Release(); UnityEngine.Object.Destroy(Target); Target = null; }
            if (Texture != null) { UnityEngine.Object.Destroy(Texture); Texture = null; }
            buffer?.Dispose(); buffer = null;
            mapping?.Dispose(); mapping = null;
            LatestFrame = null;
            gate?.Dispose(); gate = null;
            if (file != null) { file.Dispose(); file = null; File.Delete(Name); }
        }
    }

    public sealed class VideoStreamDriver : MonoBehaviour
    {
        const int Capacity = VideoSource.Capacity;
        static VideoStreamDriver? instance;
        static bool patched;
        static Camera? feedCamera;
        // Sources by buffer name; the screen source is the one whose spec is Screen.
        readonly Dictionary<string, VideoSource> sources = new Dictionary<string, VideoSource>();
        // Rendered sources due this frame, decided before the map draw so their
        // cells are culled in and their render follows the same draw pass.
        readonly List<VideoSource> due = new List<VideoSource>();
        readonly List<CellRect> dueRects = new List<CellRect>();
        bool drawing, asyncFailed;
        string error = "";
        int? savedVsync, savedFrameRate;
        bool UsePresented => Application.platform == RuntimePlatform.LinuxPlayer
            && Environment.GetEnvironmentVariable("RIMGOVERNOR_PRIVATE_DISPLAY") == "1"
            && Environment.GetEnvironmentVariable("RIMGOVERNOR_VIDEO_READBACK") != "sync"
            && Environment.GetEnvironmentVariable("RIMGOVERNOR_VIDEO_READBACK") != "async";
        [StructLayout(LayoutKind.Sequential)]
        struct XImage
        {
            public int width, height, xoffset, format;
            public IntPtr data;
            public int byteOrder, bitmapUnit, bitmapBitOrder, bitmapPad, depth, bytesPerLine, bitsPerPixel;
            public UIntPtr redMask, greenMask, blueMask;
        }
        [DllImport("libX11.so.6")] static extern IntPtr XGetImage(IntPtr display, UIntPtr drawable,
            int x, int y, uint width, uint height, UIntPtr planes, int format);
        [DllImport("libX11.so.6")] static extern int XDestroyImage(IntPtr image);
        bool UseAsync => SystemInfo.supportsAsyncGPUReadback && !asyncFailed &&
            (Environment.GetEnvironmentVariable("RIMGOVERNOR_VIDEO_READBACK") == "async" ||
             (Environment.GetEnvironmentVariable("RIMGOVERNOR_VIDEO_READBACK") != "sync" &&
              !SystemInfo.graphicsDeviceName.ToLowerInvariant().Contains("llvmpipe")));

        static bool Supported => !Application.isBatchMode && (Application.platform == RuntimePlatform.WindowsPlayer ||
            Application.platform == RuntimePlatform.LinuxPlayer);
        VideoSource? Screen => sources.Values.FirstOrDefault(s => !s.Spec.Rendered);
        string CaptureMethod(VideoSource source) => source.Spec.Rendered ? (UseAsync ? "async-gpu" : "read-pixels")
            : UsePresented ? "private-presented-window" : UseAsync ? "async-gpu" : "read-pixels";
        bool Bgra(VideoSource source) => !source.Spec.Rendered && UsePresented;

        public static object Lease(int seconds)
        {
            if (!Supported) return new { supported = false, reason = "Raw video requires a rendered Windows or Linux player" };
            if (instance == null && seconds == 0) return new { supported = true, active = false };
            var driver = Ensure();
            var source = seconds > 0 ? driver.Begin(VideoSourceSpec.Screen, seconds, "") : null;
            var screen = driver.Screen;
            if (seconds == 0 && screen != null) driver.Stop(screen.Name, "");
            return new { supported = true, name = source?.Name, capacity = Capacity,
                fps = 60, format = driver.UsePresented ? "bgra32-top-down" : "rgba32-bottom-up", error = driver.error,
                capture = driver.UsePresented ? "private-presented-window" : driver.UseAsync ? "async-gpu" : "read-pixels",
                capturedFrames = source?.Sequence ?? 0,
                cpuSeconds = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds,
                workingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
                asyncReadbackSupported = SystemInfo.supportsAsyncGPUReadback, renderer = SystemInfo.graphicsDeviceName,
                focused = Application.isFocused, targetFrameRate = Application.targetFrameRate,
                frameSeconds = Time.unscaledDeltaTime, vSyncCount = QualitySettings.vSyncCount,
                refreshRate = UnityEngine.Screen.currentResolution.refreshRateRatio.value };
        }

        // Typed counterpart of Lease(): starts or extends one viewer's hold on
        // a source and reports that source. seconds == 0 drops the viewer's
        // hold on sourceId, or on every source when sourceId is null; a source
        // ends when its last hold is gone. viewer is the caller's viewer id
        // (empty when it sent none), so viewers with the same spec share a
        // source without ending each other's feed.
        internal static VideoLeaseStatus LeaseTyped(int seconds, VideoSourceSpec spec, string? sourceId, string viewer)
        {
            if (!Supported)
                return Status(null, "Raw video requires a rendered Windows or Linux player", spec, viewer);
            VideoSource? source = null;
            if (seconds > 0)
            {
                try { source = Ensure().Begin(spec, seconds, viewer); }
                catch (VideoSourceUnavailableException e) { return Status(null, e.Message, spec, viewer, true); }
                catch (Exception e) { return Status(null, e.Message, spec, viewer); }
            }
            else if (instance != null) instance.Stop(sourceId, viewer);
            return Status(source, null, spec, viewer);
        }

        static VideoLeaseStatus Status(VideoSource? source, string? unavailable, VideoSourceSpec spec, string viewer, bool supported = false)
        {
            supported = supported || unavailable == null;
            var driver = instance;
            var live = source != null && source.Open && driver != null ? source : null;
            bool active = live != null;
            float until = live != null && live.Holds.TryGetValue(viewer, out var held) ? held : source?.Until ?? 0;
            return new VideoLeaseStatus(supported, unavailable, active, live?.Name,
                active ? Mathf.Max(0, until - Time.realtimeSinceStartup) : 0,
                source?.Sequence ?? 0, live != null && driver != null && !driver.Bgra(live), live != null && driver != null && driver.Bgra(live),
                live != null && driver != null ? driver.CaptureMethod(live) : null,
                SystemInfo.supportsAsyncGPUReadback, SystemInfo.graphicsDeviceName, Application.isFocused,
                Application.targetFrameRate, Time.unscaledDeltaTime, QualitySettings.vSyncCount,
                UnityEngine.Screen.currentResolution.refreshRateRatio.value,
                (ulong)System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
                System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds, spec);
        }

        // Reads the same in-process bytes Publish() just captured; never touches
        // the shared buffer. A null sourceId means the screen source, or the only
        // source when no screen is leased.
        internal static bool TryReadLatestFrame(string? sourceId, out VideoFrameSnapshot frame)
        {
            frame = default;
            if (instance == null) return false;
            VideoSource? source;
            if (sourceId != null) instance.sources.TryGetValue(sourceId, out source);
            else source = instance.Screen ?? (instance.sources.Count == 1 ? instance.sources.Values.First() : null);
            if (source == null || !source.Open || source.LatestFrame == null) return false;
            frame = new VideoFrameSnapshot(source.Name, source.Sequence, source.LatestWidth, source.LatestHeight,
                source.LatestCapturedUnixSeconds, source.LatestReadbackMs, source.LatestFrame,
                !instance.Bgra(source), instance.Bgra(source), instance.CaptureMethod(source));
            return true;
        }

        static VideoStreamDriver Ensure()
        {
            if (instance == null)
            {
                instance = new GameObject("RimGovernorVideoStream").AddComponent<VideoStreamDriver>();
                DontDestroyOnLoad(instance.gameObject);
            }
            return instance;
        }

        VideoSource Begin(VideoSourceSpec spec, int seconds, string viewer)
        {
            var map = Find.CurrentMap;
            if (spec.Rendered && (map == null || Find.Camera == null))
                throw new VideoSourceUnavailableException("A rendered feed requires a current map.");
            if (spec.Kind == VideoSourceKind.Pawn && FeedPawn(spec.PawnId, map) == null)
                throw new VideoSourceUnavailableException("The pawn is not spawned on the current map.");
            var source = sources.Values.FirstOrDefault(s => s.Spec.Equals(spec) && ReferenceEquals(s.Map, map));
            if (source == null)
            {
                if (sources.Count == 0 && savedVsync == null)
                {
                    savedVsync = QualitySettings.vSyncCount; savedFrameRate = Application.targetFrameRate;
                }
                if (spec.Rendered) InstallFeedPatches();
                source = new VideoSource(spec, map);
                sources[source.Name] = source;
                error = "";
            }
            source.Holds[viewer] = Time.realtimeSinceStartup + seconds;
            source.Until = source.Holds.Values.Max();
            ApplyDisplayPacing();
            RenderDemandDriver.Lease(seconds);
            return source;
        }

        // Drops viewer's hold on sourceId (every source when null) and ends
        // the sources nobody holds any more; a null viewer ends them outright.
        void Stop(string? sourceId, string? viewer)
        {
            foreach (var name in sources.Keys.Where(k => sourceId == null || k == sourceId).ToArray())
            {
                var source = sources[name];
                if (viewer != null)
                {
                    source.Holds.Remove(viewer);
                    if (source.Holds.Count > 0) { source.Until = source.Holds.Values.Max(); continue; }
                }
                source.Release();
                sources.Remove(name);
            }
            if (sources.Count == 0) RestoreDisplay();
        }

        // Feed intervals select the requested cadence on a 60 Hz render clock.
        // Display refresh (including a 1 Hz virtual display) must not throttle
        // capture. Reapply after game preference/focus updates while leased.
        void LateUpdate()
        {
            Expire();
            ApplyDisplayPacing();
        }

        void ApplyDisplayPacing()
        {
            if (sources.Count == 0) return;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }

        void RestoreDisplay()
        {
            if (!(savedVsync is int vsync) || !(savedFrameRate is int frameRate)) return;
            QualitySettings.vSyncCount = vsync;
            Application.targetFrameRate = frameRate;
            savedVsync = savedFrameRate = null;
        }

        void Fail(VideoSource source, Exception e)
        {
            error = e.Message;
            source.Until = 0;
        }

        // Ends sources past their lease, and rendered sources whose map is no
        // longer current (a load replaced it): a viewer re-leases for the new map.
        void Expire()
        {
            var now = Time.realtimeSinceStartup;
            var map = Find.CurrentMap;
            foreach (var source in sources.Values.Where(s => now >= s.Until || (s.Spec.Rendered && !ReferenceEquals(s.Map, map))).ToArray())
                Stop(source.Name, null);
        }

        IEnumerator Start()
        {
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                Expire();
                var screen = Screen;
                if (screen == null || Time.realtimeSinceStartup < screen.Next) continue;
                screen.Next = Time.realtimeSinceStartup + 1f / 60;
                if (UsePresented)
                {
                    // End-of-frame state belongs to the buffer about to be
                    // presented. On the next frame, read that front buffer before
                    // this frame's rendering, preserving its original view stamp.
                    var view = PlayerFrame.Capture((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                    yield return null;
                    if (!screen.Open || Time.realtimeSinceStartup >= screen.Until) continue;
                    try { CapturePresented(screen, view); }
                    catch (Exception e) { Fail(screen, e); }
                    continue;
                }
                try { CaptureScreen(screen); }
                catch (Exception e) { Fail(screen, e); }
            }
        }

        void CaptureScreen(VideoSource source)
        {
            if (source.Pending) return;
            double captured = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            var view = PlayerFrame.Capture(captured);
            var captureClock = System.Diagnostics.Stopwatch.StartNew();
            int width = UnityEngine.Screen.width, height = UnityEngine.Screen.height;
            if (width < 1 || height < 1 || width > 3840 || height > 2160)
                throw new InvalidOperationException("Video supports screen sizes up to 3840 x 2160");
            if (UseAsync)
            {
                EnsureTarget(source, width, height);
                ScreenCapture.CaptureScreenshotIntoRenderTexture(source.Target);
                ReadbackAsync(source, width, height, captured, captureClock, view);
                return;
            }
            if (source.Texture == null || source.Texture.width != width || source.Texture.height != height)
            {
                if (source.Texture != null) Destroy(source.Texture);
                source.Texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            // Unity framebuffer access stays on its main thread, after UI rendering.
            source.Texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            byte[] pixels = source.Texture.GetRawTextureData();
            source.Publish(width, height, captured, captureClock.Elapsed.TotalMilliseconds, pixels, view);
        }

        static void EnsureTarget(VideoSource source, int width, int height)
        {
            if (source.Target != null && source.Target.width == width && source.Target.height == height) return;
            if (source.Target != null) { source.Target.Release(); Destroy(source.Target); }
            source.Target = new RenderTexture(width, height, source.Spec.Rendered ? 24 : 0, RenderTextureFormat.ARGB32);
            source.Target.Create();
        }

        // The published frame is bottom-up (what ReadPixels yields). A
        // RenderTexture read back on a top-left-origin graphics API (D3D,
        // Metal) holds its rows top-first, for a camera render and for
        // ScreenCapture alike, so those rows are flipped before publishing.
        void ReadbackAsync(VideoSource source, int width, int height, double captured, System.Diagnostics.Stopwatch clock, PlayerFrame? view)
        {
            bool topDown = SystemInfo.graphicsUVStartsAtTop;
            source.Pending = true;
            AsyncGPUReadback.Request(source.Target, 0, TextureFormat.RGBA32, request =>
            {
                source.Pending = false;
                if (!source.Open || Time.realtimeSinceStartup >= source.Until) { source.Release(); return; }
                if (request.hasError) { asyncFailed = true; return; }
                try
                {
                    var pixels = request.GetData<byte>().ToArray();
                    if (topDown) FlipRows(pixels, width, height);
                    source.Publish(width, height, captured, clock.Elapsed.TotalMilliseconds, pixels, view);
                }
                catch (Exception e) { Fail(source, e); }
            });
        }

        static void FlipRows(byte[] pixels, int width, int height)
        {
            int stride = width * 4;
            var row = new byte[stride];
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
            }
        }

        void CapturePresented(VideoSource source, PlayerFrame? view)
        {
            if (view == null || view.Width != UnityEngine.Screen.width || view.Height != UnityEngine.Screen.height) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            UIntPtr window = PrivatePlayerInput.VideoWindow(out IntPtr display);
            IntPtr image = XGetImage(display, window, 0, 0, (uint)view.Width, (uint)view.Height, new UIntPtr(ulong.MaxValue), 2);
            if (image == IntPtr.Zero) throw new InvalidOperationException("Private presented framebuffer unavailable");
            try
            {
                var native = (XImage)Marshal.PtrToStructure(image, typeof(XImage));
                if (native.width != view.Width || native.height != view.Height || native.bitsPerPixel != 32
                    || native.byteOrder != 0 || native.bytesPerLine != view.Width * 4
                    || native.redMask.ToUInt64() != 0xff0000 || native.greenMask.ToUInt64() != 0xff00
                    || native.blueMask.ToUInt64() != 0xff)
                    throw new InvalidOperationException("Unsupported private framebuffer layout");
                var pixels = new byte[native.bytesPerLine * native.height];
                Marshal.Copy(native.data, pixels, 0, pixels.Length);
                source.Publish(view.Width, view.Height, view.Captured, clock.Elapsed.TotalMilliseconds, pixels, view);
            }
            finally { XDestroyImage(image); }
        }

        // Rendered feeds. The game's draw pass (Game.UpdatePlay) submits meshes
        // for the cells inside CameraDriver.CurrentViewRect only, so while a
        // feed is due that rect is widened to cover it; the feed camera then
        // renders the same submissions right after the pass, before Unity
        // discards them at the end of the frame.
        static void InstallFeedPatches()
        {
            if (patched) return;
            patched = true;
            var cameraObject = new GameObject("RimGovernorVideoFeedCamera");
            DontDestroyOnLoad(cameraObject);
            feedCamera = cameraObject.AddComponent<Camera>();
            feedCamera.enabled = false;
            var harmony = new Harmony("davidarcher.rimgovernor.video-feeds");
            harmony.Patch(AccessTools.Method(typeof(Game), "UpdatePlay"),
                prefix: new HarmonyMethod(typeof(VideoStreamDriver), nameof(BeforeDraw)),
                postfix: new HarmonyMethod(typeof(VideoStreamDriver), nameof(AfterDraw)),
                finalizer: new HarmonyMethod(typeof(VideoStreamDriver), nameof(DrawFinished)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentViewRect"),
                postfix: new HarmonyMethod(typeof(VideoStreamDriver), nameof(ViewRect)));
        }

        // The cells a feed shows: the pawn's neighbourhood at 10 cells of
        // height, or the whole map.
        static CellRect? FeedRect(VideoSource source, Map map, Pawn? pawn)
        {
            if (source.Spec.Kind == VideoSourceKind.Map) return CellRect.WholeMap(map);
            if (pawn == null) return null;
            var cells = Mathf.CeilToInt(FeedHalfHeight * 2 * Mathf.Max(1f, (float)source.Spec.Width / source.Spec.Height)) + 4;
            var rect = CellRect.CenteredOn(pawn.Position, cells / 2);
            rect.ClipInsideMap(map);
            return rect;
        }
        const float FeedHalfHeight = 5f;
        // CameraDriver's minimum camera height; feeds always look down from there.
        const float FeedCameraAltitude = 15f;

        static Pawn? FeedPawn(VideoSource source, Map map) => FeedPawn(source.Spec.PawnId, map);

        static Pawn? FeedPawn(string? pawnId, Map map)
        {
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn.GetUniqueLoadID() == pawnId) return pawn.Dead ? null : pawn;
            return null;
        }

        public static void BeforeDraw()
        {
            if (instance == null) return;
            instance.due.Clear();
            instance.dueRects.Clear();
            var map = Find.CurrentMap;
            if (map == null || Find.Camera == null) return;
            var now = Time.realtimeSinceStartup;
            List<VideoSource>? gone = null;
            foreach (var source in instance.sources.Values)
            {
                if (!source.Spec.Rendered || !ReferenceEquals(source.Map, map) || source.Pending || now < source.Next) continue;
                source.Next = now + (float)source.Interval;
                source.FramePawn = source.Spec.Kind == VideoSourceKind.Pawn ? FeedPawn(source, map) : null;
                if (source.Spec.Kind == VideoSourceKind.Pawn && source.FramePawn == null)
                {
                    // The pawn left this map (dead, removed, in a caravan): the
                    // feed ends rather than freezing on its last frame; a viewer
                    // that re-leases learns it is unavailable until it returns.
                    (gone ??= new List<VideoSource>()).Add(source);
                    continue;
                }
                var rect = FeedRect(source, map, source.FramePawn);
                if (!rect.HasValue) continue;
                instance.due.Add(source);
                instance.dueRects.Add(rect.Value);
            }
            if (gone != null) foreach (var source in gone) instance.Stop(source.Name, null);
            instance.drawing = instance.due.Count > 0;
        }

        public static void ViewRect(ref CellRect __result)
        {
            if (instance == null || !instance.drawing) return;
            var result = __result;
            foreach (var extra in instance.dueRects)
                result = CellRect.FromLimits(Math.Min(result.minX, extra.minX), Math.Min(result.minZ, extra.minZ),
                    Math.Max(result.maxX, extra.maxX), Math.Max(result.maxZ, extra.maxZ));
            __result = result;
        }

        public static void AfterDraw()
        {
            if (instance == null || !instance.drawing) return;
            instance.drawing = false;
            var map = Find.CurrentMap;
            foreach (var source in instance.due)
            {
                try { instance.RenderFeed(source, map); }
                catch (Exception e) { instance.Fail(source, e); }
            }
            instance.due.Clear();
            instance.dueRects.Clear();
        }

        public static Exception? DrawFinished(Exception __exception)
        {
            if (instance != null) { instance.drawing = false; if (__exception != null) { instance.due.Clear(); instance.dueRects.Clear(); } }
            return __exception;
        }

        void RenderFeed(VideoSource source, Map map)
        {
            if (!source.Open) return;
            Vector3 center; float halfHeight;
            if (source.Spec.Kind == VideoSourceKind.Map)
            {
                center = new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f);
                halfHeight = map.Size.z / 2f;
            }
            else
            {
                var pawn = source.FramePawn;
                if (pawn == null || !pawn.Spawned) return;
                center = pawn.DrawPos;
                halfHeight = FeedHalfHeight;
            }
            int height = source.Spec.Height;
            int width = source.Spec.Width > 0 ? source.Spec.Width : Mathf.Clamp(Mathf.RoundToInt(height * (float)map.Size.x / map.Size.z), VideoSourceSpec.MinSide, VideoSourceSpec.MaxWidth);
            double captured = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            EnsureTarget(source, width, height);
            var main = Find.Camera;
            var camera = feedCamera;
            if (camera == null) return;
            camera.CopyFrom(main);
            camera.enabled = false;
            camera.targetTexture = source.Target;
            camera.aspect = (float)width / height;
            camera.orthographicSize = halfHeight;
            camera.transform.position = new Vector3(center.x, FeedCameraAltitude, center.z);
            camera.transform.rotation = main.transform.rotation;
            // Silhouettes and the overlays above them are drawn at the player's
            // zoom for the player's camera; clipping their altitudes keeps the
            // feed showing the pawns themselves.
            camera.nearClipPlane = FeedCameraAltitude - AltitudeLayer.Silhouettes.AltitudeFor() + 0.005f;
            camera.farClipPlane = FeedCameraAltitude + 5f;
            camera.Render();
            camera.targetTexture = null;
            if (UseAsync)
            {
                ReadbackAsync(source, width, height, captured, clock, null);
                return;
            }
            if (source.Texture == null || source.Texture.width != width || source.Texture.height != height)
            {
                if (source.Texture != null) Destroy(source.Texture);
                source.Texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source.Target;
                source.Texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            }
            finally { RenderTexture.active = previous; }
            source.Publish(width, height, captured, clock.Elapsed.TotalMilliseconds, source.Texture.GetRawTextureData(), null);
        }

        void OnDestroy()
        {
            Stop(null, null);
            instance = null;
        }
    }
}
