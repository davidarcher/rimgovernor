#nullable enable

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
    // The one status shape produced by the driver. The typed
    // rimgovernor/presentation_render_state and .../presentation_render_demand
    // tools each project this into their own reply shape.
    internal readonly struct RenderDemandStatus
    {
        internal readonly bool Supported, Suspended, WindowVisible;
        internal readonly float RemainingSeconds;
        internal readonly string? UnavailableDetail;
        internal RenderDemandStatus(bool supported, bool suspended, bool windowVisible, float remainingSeconds, string? unavailableDetail)
        { Supported = supported; Suspended = suspended; WindowVisible = windowVisible; RemainingSeconds = remainingSeconds; UnavailableDetail = unavailableDetail; }
    }

    public sealed class RenderDemandDriver : MonoBehaviour
    {
        static RenderDemandDriver? instance;
        static float until;
        public static bool Suspended;
        readonly HashSet<Camera> disabled = new HashSet<Camera>();
        float nextCheck;
        bool windowVisible = true;
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

        internal static RenderDemandStatus Lease(int seconds)
        {
            if (Application.isBatchMode) return new RenderDemandStatus(false, true, false, 0, "Startup headless mode cannot render");
            if (instance == null)
            {
                instance = new GameObject("RimGovernorRenderDemand").AddComponent<RenderDemandDriver>();
                DontDestroyOnLoad(instance.gameObject);
                new Harmony("davidarcher.rimgovernor.render-demand").Patch(AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"),
                    prefix: new HarmonyMethod(typeof(RenderDemandDriver), nameof(DrawPrefix)));
            }
            until = Mathf.Max(until, Time.realtimeSinceStartup + seconds);
            instance.Apply();
            return new RenderDemandStatus(true, Suspended, instance.windowVisible, Mathf.Max(0, until - Time.realtimeSinceStartup), null);
        }

        // A read-only peek never installs the driver/Harmony patch and never
        // touches `until`: RenderState must be a zero-side-effect observation.
        // (Lease(0) is NOT side-effect free -- Mathf.Max(until, now) can still
        // extend a shorter existing lease up to "now", and it always installs
        // the driver on first use. RenderState needs a true no-op path.)
        internal static RenderDemandStatus Peek()
        {
            if (Application.isBatchMode) return new RenderDemandStatus(false, true, false, 0, "Startup headless mode cannot render");
            if (instance == null) return new RenderDemandStatus(true, false, true, 0, null);
            return new RenderDemandStatus(true, Suspended, instance.windowVisible, Mathf.Max(0, until - Time.realtimeSinceStartup), null);
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
