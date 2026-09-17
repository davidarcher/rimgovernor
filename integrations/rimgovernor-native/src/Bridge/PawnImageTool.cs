using System;
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
    public sealed class PawnImageTool
    {
        [Tool("home/pawn_image", Description = "Read-only native colonist portrait or independent nearby map image. No selection, orders, clock or player camera navigation.")]
        public async Task<object> Image(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Current-map colonist unique load ID.")] string pawnId,
            [ToolParameter(Description = "Exact colonyId:mapId:loadToken from colony_identity.")] string sessionId,
            [ToolParameter(Description = "portrait or follow.", DefaultValue = "portrait")] string view = "portrait")
        {
            if (view != "portrait" && view != "follow") throw new ArgumentException("Unknown pawn view");
            try
            {
                var pending = await ctx.MainThread.InvokeAsync(() => PawnImageCapture.Begin(pawnId, sessionId, view), cancellationToken);
                var result = await pending.ConfigureAwait(false);
                return new { success = true, pawnId = result.PawnId, sessionId = result.SessionId,
                    tick = result.Tick, pngBase64 = Convert.ToBase64String(result.Png) };
            }
            catch (InvalidOperationException error)
            {
                // A stale viewer is an ordinary refusal, not a game attention event.
                return new { success = false, error = error.Message };
            }
        }
    }

    // The one result shape produced by a completed capture. The legacy JSON tool
    // and the typed rimgovernor/presentation_capture_pawn tool each project this
    // into their own reply shape; only one Harmony patch registration exists.
    internal sealed class PawnCaptureResult
    {
        internal string PawnId, SessionId, View;
        internal int Tick;
        internal byte[] Png;
        internal int Width, Height;
        internal double ReadbackMs;
    }

    // A single bounded request is serviced after map draw submission. The camera's
    // temporary offscreen target is restored synchronously before Unity presents it.
    public sealed class PawnImageCapture : MonoBehaviour
    {
        static PawnImageCapture instance;
        static TaskCompletionSource<PawnCaptureResult> pending;
        static string pawnId, sessionId, view;
        static Pawn pawn;
        static float deadline;
        static CellRect? extraView;
        static Stopwatch stopwatch;

        static string Session()
        {
            var identity = Current.Game?.GetComponent<ColonyIdentity>();
            return identity == null || Find.CurrentMap == null ? null :
                identity.ColonyId + ":" + Find.CurrentMap.uniqueID + ":" + identity.LoadToken;
        }

        internal static Task<PawnCaptureResult> Begin(string id, string session, string kind)
        {
            if (Application.isBatchMode || Find.Camera == null)
                throw new InvalidOperationException("Pawn images require a rendered game");
            if (Session() != session) throw new InvalidOperationException("Loaded colony changed");
            if (pending != null) throw new InvalidOperationException("Pawn image capture is busy");
            PresentationLifecycle.EnsurePatched();
            if (instance == null)
            {
                instance = new GameObject("RimGovernorPawnImages").AddComponent<PawnImageCapture>();
                DontDestroyOnLoad(instance.gameObject);
                var harmony = new Harmony("davidarcher.rimgovernor.pawn-images");
                harmony.Patch(AccessTools.Method(typeof(Game), "UpdatePlay"),
                    prefix: new HarmonyMethod(typeof(PawnImageCapture), nameof(BeforeDraw)),
                    postfix: new HarmonyMethod(typeof(PawnImageCapture), nameof(AfterDraw)),
                    finalizer: new HarmonyMethod(typeof(PawnImageCapture), nameof(DrawFinished)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentViewRect"),
                    postfix: new HarmonyMethod(typeof(PawnImageCapture), nameof(ViewRect)));
                harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)),
                    prefix: new HarmonyMethod(typeof(PawnImageCapture), nameof(Destroying)));
            }
            pawnId = id; sessionId = session; view = kind;
            deadline = Time.realtimeSinceStartup + 4;
            stopwatch = Stopwatch.StartNew();
            RenderDemandDriver.Lease(5);
            pending = new TaskCompletionSource<PawnCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return pending.Task;
        }

        void Update()
        {
            if (pending != null && Time.realtimeSinceStartup >= deadline)
                Fail("Timed out waiting for native pawn rendering");
        }

        static void Fail(string message)
        {
            var request = pending;
            pending = null; extraView = null; pawn = null;
            request?.TrySetException(new InvalidOperationException(message));
        }

        /// <summary>Any thread. The game or map the request was made against is
        /// gone; fail it now rather than at the deadline.</summary>
        internal static void Abandon(string reason)
        {
            if (pending != null) Fail(reason);
        }

        /// <summary>The requested colonist is being destroyed before its frame
        /// was drawn; fail now rather than waiting for the next draw.</summary>
        public static void Destroying(Thing __instance)
        {
            if (pending == null || pawnId == null || !(__instance is Pawn)) return;
            if (__instance.GetUniqueLoadID() == pawnId) Fail("Colonist was destroyed before it could be captured");
        }

        public static void BeforeDraw()
        {
            if (pending == null) return;
            if (Session() != sessionId) { Fail("Loaded colony changed"); return; }
            pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
            if (pawn == null || pawn.Dead || pawn.Position.Fogged(pawn.Map))
            { Fail("Colonist is no longer visible on the current map"); return; }
            if (view == "follow")
            {
                var rect = CellRect.CenteredOn(pawn.Position, 12);
                rect.ClipInsideMap(pawn.Map);
                extraView = rect;
            }
        }

        public static void ViewRect(ref CellRect __result)
        {
            if (!extraView.HasValue) return;
            var extra = extraView.Value;
            __result = CellRect.FromLimits(Math.Min(__result.minX, extra.minX),
                Math.Min(__result.minZ, extra.minZ), Math.Max(__result.maxX, extra.maxX),
                Math.Max(__result.maxZ, extra.maxZ));
        }

        public static Exception DrawFinished(Exception __exception)
        {
            extraView = null;
            if (__exception != null && pending != null) Fail("Native map rendering failed");
            return __exception;
        }

        public static void AfterDraw()
        {
            if (pending == null || pawn == null) return;
            try
            {
                if (Session() != sessionId || !pawn.Spawned || pawn.Map != Find.CurrentMap)
                    throw new InvalidOperationException("Loaded colony changed");
                var bytes = view == "portrait" ? Portrait(pawn) : Follow(pawn);
                var elapsed = stopwatch?.Elapsed.TotalMilliseconds ?? 0;
                pending.TrySetResult(new PawnCaptureResult
                {
                    PawnId = pawnId, SessionId = sessionId, View = view,
                    Tick = Find.TickManager.TicksGame, Png = bytes,
                    Width = view == "portrait" ? 192 : 640, Height = view == "portrait" ? 192 : 400,
                    ReadbackMs = elapsed,
                });
                pending = null;
            }
            catch (Exception error) { Fail(error.Message); }
            finally { extraView = null; pawn = null; }
        }

        static byte[] Portrait(Pawn target)
        {
            var texture = PortraitsCache.Get(target, new Vector2(192, 192), Rot4.South,
                Vector3.zero, 1f, false, false, true, true);
            if (texture == null) throw new InvalidOperationException("Native portrait unavailable");
            var weapon = target.equipment?.Primary;
            if (weapon?.def?.uiIcon == null) return Encode(texture);
            var output = RenderTexture.GetTemporary(192, 192, 0);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, output);
                RenderTexture.active = output;
                GL.PushMatrix();
                try
                {
                    GL.LoadPixelMatrix(0, 192, 192, 0);
                    Graphics.DrawTexture(new Rect(132, 132, 56, 56), weapon.def.uiIcon,
                        new Rect(0, 0, 1, 1), 0, 0, 0, 0, weapon.DrawColor);
                }
                finally { GL.PopMatrix(); }
                return Encode(output);
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(output); }
        }

        static byte[] Follow(Pawn target)
        {
            var camera = Find.Camera;
            var position = camera.transform.position;
            var size = camera.orthographicSize;
            var aspect = camera.aspect;
            var naturalAspect = camera.pixelHeight > 0 &&
                Mathf.Approximately(aspect, (float)camera.pixelWidth / camera.pixelHeight);
            var far = camera.farClipPlane;
            var previous = camera.targetTexture;
            var output = RenderTexture.GetTemporary(640, 400, 24);
            try
            {
                camera.targetTexture = output;
                camera.aspect = 1.6f;
                camera.orthographicSize = 5f;
                var center = target.DrawPos;
                camera.transform.position = new Vector3(center.x, position.y, center.z);
                camera.farClipPlane = Mathf.Max(far, 100f);
                camera.Render();
                return Encode(output);
            }
            finally
            {
                camera.targetTexture = previous;
                camera.transform.position = position;
                camera.orthographicSize = size;
                if (naturalAspect) camera.ResetAspect();
                else camera.aspect = aspect;
                camera.farClipPlane = far;
                RenderTexture.ReleaseTemporary(output);
            }
        }

        static byte[] Encode(RenderTexture source)
        {
            var previous = RenderTexture.active;
            Texture2D pixels = null;
            try
            {
                RenderTexture.active = source;
                pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                pixels.Apply();
                return ImageConversion.EncodeToPNG(pixels);
            }
            finally
            {
                RenderTexture.active = previous;
                if (pixels != null) Destroy(pixels);
            }
        }
    }
}
