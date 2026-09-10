using System;
using System.Collections;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using UnityEngine;

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
        // One latest RGB24 frame, protected by a nonblocking cross-process mutex.
        const int Capacity = 32 + 3840 * 2160 * 3;
        static VideoStreamDriver instance;
        string bufferName;
        MemoryMappedFile mapping;
        MemoryMappedViewAccessor buffer;
        Mutex gate;
        Texture2D texture;
        float until, next;
        long sequence;
        string error = "";

        public static object Lease(int seconds)
        {
            if (Application.isBatchMode || Application.platform != RuntimePlatform.WindowsPlayer)
                return new { supported = false, reason = "Raw video requires a rendered Windows player" };
            if (instance == null && seconds == 0) return new { supported = true, active = false };
            if (instance == null)
            {
                instance = new GameObject("RimBotVideoStream").AddComponent<VideoStreamDriver>();
                DontDestroyOnLoad(instance.gameObject);
            }
            if (seconds > 0 && instance.mapping == null) instance.Open();
            instance.until = Time.realtimeSinceStartup + seconds;
            if (seconds > 0) RenderDemandDriver.Lease(seconds);
            else instance.Release();
            return new { supported = true, name = instance.bufferName, capacity = Capacity,
                fps = 30, format = "rgb24-bottom-up", error = instance.error };
        }

        void Open()
        {
            bufferName = "Local\\RimBotVideo-" + Guid.NewGuid().ToString("N");
            mapping = MemoryMappedFile.CreateNew(bufferName, Capacity);
            buffer = mapping.CreateViewAccessor();
            gate = new Mutex(false, bufferName + "-lock");
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
                try { Capture(); }
                catch (Exception e) { error = e.Message; until = 0; Release(); }
            }
        }

        void Capture()
        {
            int width = Screen.width, height = Screen.height;
            if (width < 1 || height < 1 || width > 3840 || height > 2160)
                throw new InvalidOperationException("Video supports screen sizes up to 3840 x 2160");
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) Destroy(texture);
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            }
            // Unity framebuffer access stays on its main thread, after UI rendering.
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            byte[] pixels = texture.GetRawTextureData();
            bool held;
            try { held = gate.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            try
            {
                buffer.Write(8, width);
                buffer.Write(12, height);
                buffer.Write(16, (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                buffer.WriteArray(32, pixels, 0, pixels.Length);
                buffer.Write(0, ++sequence);
            }
            finally { gate.ReleaseMutex(); }
        }

        void Release()
        {
            if (texture != null) { Destroy(texture); texture = null; }
            buffer?.Dispose(); buffer = null;
            mapping?.Dispose(); mapping = null;
            gate?.Dispose(); gate = null;
        }
        void OnDestroy() { Release(); instance = null; }
    }
}
