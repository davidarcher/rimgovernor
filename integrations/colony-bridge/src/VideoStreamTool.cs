using System;
using System.Collections;
using System.IO.MemoryMappedFiles;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using UnityEngine;
using UnityEngine.Rendering;

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

    public sealed class VideoStreamDriver : MonoBehaviour
    {
        // One latest RGBA32 frame, protected by a nonblocking cross-process mutex.
        const int Capacity = 32 + 3840 * 2160 * 4;
        static VideoStreamDriver instance;
        string bufferName;
        MemoryMappedFile mapping;
        MemoryMappedViewAccessor buffer;
        Mutex gate;
        FileStream file;
        [DllImport("libc", SetLastError = true)]
        static extern int flock(int fd, int operation);
        Texture2D texture;
        RenderTexture target;
        bool pending, asyncFailed;
        float until, next;
        long sequence;
        string error = "";
        bool UsePresented => Application.platform == RuntimePlatform.LinuxPlayer
            && Environment.GetEnvironmentVariable("RIMBOT_PRIVATE_DISPLAY") == "1"
            && Environment.GetEnvironmentVariable("RIMBOT_VIDEO_READBACK") != "sync"
            && Environment.GetEnvironmentVariable("RIMBOT_VIDEO_READBACK") != "async";
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
            (Environment.GetEnvironmentVariable("RIMBOT_VIDEO_READBACK") == "async" ||
             (Environment.GetEnvironmentVariable("RIMBOT_VIDEO_READBACK") != "sync" &&
              !SystemInfo.graphicsDeviceName.ToLowerInvariant().Contains("llvmpipe")));

        public static object Lease(int seconds)
        {
            if (Application.isBatchMode || (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.LinuxPlayer))
                return new { supported = false, reason = "Raw video requires a rendered Windows or Linux player" };
            if (instance == null && seconds == 0) return new { supported = true, active = false };
            if (instance == null)
            {
                instance = new GameObject("RimBotVideoStream").AddComponent<VideoStreamDriver>();
                DontDestroyOnLoad(instance.gameObject);
            }
            if (seconds > 0 && instance.mapping == null)
            {
                try { instance.Open(); }
                catch { instance.Release(); throw; }
            }
            instance.until = Time.realtimeSinceStartup + seconds;
            if (seconds > 0) RenderDemandDriver.Lease(seconds);
            else instance.Release();
            return new { supported = true, name = instance.bufferName, capacity = Capacity,
                fps = 30, format = instance.UsePresented ? "bgra32-top-down" : "rgba32-bottom-up", error = instance.error,
                capture = instance.UsePresented ? "private-presented-window" : instance.UseAsync ? "async-gpu" : "read-pixels",
                capturedFrames = instance.sequence,
                cpuSeconds = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds,
                workingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
                asyncReadbackSupported = SystemInfo.supportsAsyncGPUReadback, renderer = SystemInfo.graphicsDeviceName,
                focused = Application.isFocused, targetFrameRate = Application.targetFrameRate,
                frameSeconds = Time.unscaledDeltaTime, vSyncCount = QualitySettings.vSyncCount,
                refreshRate = Screen.currentResolution.refreshRateRatio.value };
        }

        void Open()
        {
            if (Application.platform == RuntimePlatform.LinuxPlayer)
            {
                bufferName = "/dev/shm/RimBotVideo-" + Guid.NewGuid().ToString("N");
                file = new FileStream(bufferName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                file.SetLength(Capacity);
                mapping = MemoryMappedFile.CreateFromFile(file, null, Capacity,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            }
            else
            {
                bufferName = "Local\\RimBotVideo-" + Guid.NewGuid().ToString("N");
                mapping = MemoryMappedFile.CreateNew(bufferName, Capacity);
                gate = new Mutex(false, bufferName + "-lock");
            }
            buffer = mapping.CreateViewAccessor();
            sequence = 0;
            error = "";
        }

        IEnumerator Start()
        {
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                if (Time.realtimeSinceStartup >= until) { Release(); continue; }
                if (mapping == null || Time.realtimeSinceStartup < next) continue;
                next = Time.realtimeSinceStartup + 1f / 30;
                if (UsePresented)
                {
                    // End-of-frame state belongs to the buffer about to be
                    // presented. On the next frame, read that front buffer before
                    // this frame's rendering, preserving its original view stamp.
                    var view = PlayerFrame.Capture((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                    yield return null;
                    if (mapping == null || Time.realtimeSinceStartup >= until) continue;
                    try { CapturePresented(view); }
                    catch (Exception e) { error = e.Message; until = 0; Release(); }
                    continue;
                }
                try { Capture(); }
                catch (Exception e) { error = e.Message; until = 0; Release(); }
            }
        }

        void Capture()
        {
            if (pending) return;
            double captured = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            var view = PlayerFrame.Capture(captured);
            var captureClock = System.Diagnostics.Stopwatch.StartNew();
            int width = Screen.width, height = Screen.height;
            if (width < 1 || height < 1 || width > 3840 || height > 2160)
                throw new InvalidOperationException("Video supports screen sizes up to 3840 x 2160");
            if (UseAsync)
            {
                if (target == null || target.width != width || target.height != height)
                {
                    if (target != null) { target.Release(); Destroy(target); }
                    target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                    target.Create();
                }
                ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
                pending = true;
                string generation = bufferName;
                AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
                {
                    pending = false;
                    if (mapping == null || Time.realtimeSinceStartup >= until) { Release(); return; }
                    if (generation != bufferName) return;
                    if (request.hasError) { asyncFailed = true; return; }
                    try
                    {
                        var data = request.GetData<byte>();
                        var pixels = data.ToArray();
                        Publish(width, height, captured, captureClock.Elapsed.TotalMilliseconds, pixels, view);
                    }
                    catch (Exception e) { error = e.Message; until = 0; }
                });
                return;
            }
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) Destroy(texture);
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            // Unity framebuffer access stays on its main thread, after UI rendering.
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            byte[] pixels = texture.GetRawTextureData();
            Publish(width, height, captured, captureClock.Elapsed.TotalMilliseconds, pixels, view);
        }

        void CapturePresented(PlayerFrame view)
        {
            if (view == null || view.Width != Screen.width || view.Height != Screen.height) return;
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
                Publish(view.Width, view.Height, view.Captured, clock.Elapsed.TotalMilliseconds, pixels, view);
            }
            finally { XDestroyImage(image); }
        }

        void Publish(int width, int height, double captured, double readbackMs, byte[] pixels, PlayerFrame view)
        {
            bool held;
            try { held = file != null ? flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) == 0 : gate.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            try
            {
                buffer.Write(8, width);
                buffer.Write(12, height);
                buffer.Write(16, captured);
                buffer.Write(24, readbackMs);
                // Mono's generic accessor array write visits each byte. Copy the
                // immutable payload in one operation while retaining the mapping.
                bool retained = false;
                var handle = buffer.SafeMemoryMappedViewHandle;
                try
                {
                    handle.DangerousAddRef(ref retained);
                    Marshal.Copy(pixels, 0, IntPtr.Add(handle.DangerousGetHandle(), 32), pixels.Length);
                }
                finally { if (retained) handle.DangerousRelease(); }
                PlayerFrame.Remember(bufferName, ++sequence, view);
                buffer.Write(0, sequence);
            }
            finally
            {
                if (file != null) flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 8);
                else gate.ReleaseMutex();
            }
        }

        void Release()
        {
            // The GPU owns an outstanding target until its callback. Do not reuse it.
            if (!pending && target != null) { target.Release(); Destroy(target); target = null; }
            if (texture != null) { Destroy(texture); texture = null; }
            buffer?.Dispose(); buffer = null;
            mapping?.Dispose(); mapping = null;
            gate?.Dispose(); gate = null;
            if (file != null) { file.Dispose(); file = null; File.Delete(bufferName); }
        }
        void OnDestroy() { Release(); instance = null; }
    }
}
