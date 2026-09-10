using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class RenderDemandTools
    {
        [Tool("home/render_demand", Description = "Controller rendering lease. No simulation or game-speed changes. Visible game window always renders.")]
        public async Task<object> Demand(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Keep rendering for this many real seconds; zero only reads status.", DefaultValue = 0)] int seconds = 0)
        {
            if (seconds < 0 || seconds > 30) throw new ArgumentOutOfRangeException(nameof(seconds));
            return await ctx.MainThread.InvokeAsync(() => RenderDemandDriver.Lease(seconds), cancellationToken);
        }
    }

    public sealed class RenderDemandDriver : MonoBehaviour
    {
        static RenderDemandDriver instance;
        static float until;
        public static bool Suspended;
#if THROUGHPUT_FIXTURE
        public static float TestSuspendUntil;
#endif
        readonly HashSet<Camera> disabled = new HashSet<Camera>();
        float nextCheck;
        bool windowVisible = true;
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

        public static object Lease(int seconds)
        {
            if (Application.isBatchMode) return new { supported = false, suspended = true, reason = "Startup headless mode cannot render" };
            if (instance == null)
            {
                instance = new GameObject("RimGovernorRenderDemand").AddComponent<RenderDemandDriver>();
                DontDestroyOnLoad(instance.gameObject);
                new Harmony("davidarcher.rimgovernor.render-demand").Patch(AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"),
                    prefix: new HarmonyMethod(typeof(RenderDemandDriver), nameof(DrawPrefix)));
            }
            until = Mathf.Max(until, Time.realtimeSinceStartup + seconds);
            instance.Apply();
            return new { supported = true, suspended = Suspended, windowVisible = instance.windowVisible,
                leaseSeconds = Mathf.Max(0, until-Time.realtimeSinceStartup) };
        }
        public static bool DrawPrefix() => !Suspended;
        void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + .25f;
            Apply();
        }
        void Apply()
        {
            // Unknown platform/window state fails open. A covered but non-minimized window still renders.
            windowVisible = true;
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero) windowVisible = IsWindowVisible(window) && !IsIconic(window);
                }
            }
            catch { windowVisible = true; }
            Suspended = !windowVisible && Time.realtimeSinceStartup >= until;
#if THROUGHPUT_FIXTURE
            // Private Linux acceptance exercises the same camera suspension path
            // without depending on Windows window-visibility APIs.
            Suspended |= Time.realtimeSinceStartup < TestSuspendUntil;
#endif
            if (Suspended)
            {
                foreach (var camera in Camera.allCameras)
                    if (camera != null && camera.enabled) { disabled.Add(camera); camera.enabled = false; }
            }
            else Restore();
        }
        void Restore()
        {
            foreach (var camera in disabled) if (camera != null) camera.enabled = true;
            disabled.Clear();
        }
        void OnDestroy() { Suspended = false; Restore(); }
    }
}
