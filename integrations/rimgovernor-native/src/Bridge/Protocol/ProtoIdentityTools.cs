using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    public sealed class ProtoIdentityTools
    {
        [Tool("rimgovernor/lifecycle_read_identity", Title = "Read native identity",
            Description = "Read the current colony, load, map and native contract capabilities.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.IdentityReply.", Always = true)]
        public async Task<object> ReadIdentity(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle IdentityRequest ProtoJSON string.")] object request = null)
        {
            Lifecycle.IdentityRequest parsed;
            Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/lifecycle_read_identity", request,
                Lifecycle.IdentityRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Lifecycle.IdentityReply { Failure = failure });

            var reply = await ctx.MainThread.InvokeAsync(() =>
            {
                Common.ObservationContext context;
                Common.Unavailable unavailable;
                if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out context, out unavailable))
                    return new Lifecycle.IdentityReply { Unavailable = unavailable };
                var loaded = new Lifecycle.LoadedIdentity { Context = context, Paused = Find.TickManager.Paused };
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.lifecycle.v1.Lifecycle/ReadIdentity",
                    Support = Lifecycle.CapabilitySupport.Supported
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.placement.v1.Placement/Preview",
                    Support = Lifecycle.CapabilitySupport.Supported
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.authority.v1.Authority/ReadStatus",
                    Support = Lifecycle.CapabilitySupport.Supported
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.lifecycle.v1.Lifecycle/Save",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Trusted pause-gated single checkpoint save with pre/post identity, tick and pause re-verification. No load, reconnect or media/control ownership yet."
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.lifecycle.v1.Lifecycle/Load",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Async native load of a named save into the running process; returns LoadPending, poll ReadLoad for MAP readiness. VISUAL readiness is accepted but not distinguished from MAP. No reconnect-after-disconnect or competing-viewer arbitration yet."
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.lifecycle.v1.Lifecycle/ReadLoad",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Polls a rimgovernor/lifecycle_load request_id for LoadCompleted/LoadPending/LoadSuperseded/Failure."
                });
                foreach (var method in new[] {
                    "rimgovernor.authority.v1.Authority/Control",
                    "rimgovernor.observations.v1.Observations/ReadStatus",
                    "rimgovernor.observations.v1.Observations/GetCells",
                    "rimgovernor.receipts.v1.Attempts/Lookup",
                    "rimgovernor.receipts.v1.Attempts/ObserveProgress" })
                    loaded.Capabilities.Add(new Lifecycle.Capability { FullMethodName = method, Support = Lifecycle.CapabilitySupport.Supported });
                foreach (var method in new[] { "Preview", "Execute" })
                    loaded.Capabilities.Add(new Lifecycle.Capability
                    {
                        FullMethodName = "rimgovernor.operations.v1.Operations/" + method,
                        Support = Lifecycle.CapabilitySupport.Supported, Detail = "PlaceBuilding, temporary SetDrafted and exact MovePawn under an existing owned draft are implemented; persistent drafting and other commands return unsupported."
                    });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.observations.v1.Observations/ListBuildings",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Complete bounded core building/blueprint/frame facts and construction resources. Exact filters; no frozen paging, CAS snapshots, settings, services, bills, inspect text or power networks."
                });
                foreach (var method in new[] { "Start", "Renew", "ChangeSpeed", "Pause", "ReadStatus", "ReadEvents", "ReadAttempt" })
                    loaded.Capabilities.Add(new Lifecycle.Capability
                    {
                        FullMethodName = "rimgovernor.clock.v1.Clock/" + method,
                        Support = Lifecycle.CapabilitySupport.Supported,
                        Detail = "Canonical owned epochs, monotonic leases and immutable observed events. Missing historical evidence is explicit."
                    });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.observations.v1.Observations/ListSupplies",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Complete bounded stock/ownership quantities, spawned-root inventories and containers. Exact filters; no frozen paging or CAS snapshots. Worn gear, orbital stock and delivered construction resources are excluded."
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.observations.v1.Observations/ReadResearch",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Complete bounded project and prerequisite facts with optional unlocks and map-local benches/researchers. Existing saved progress and slots are read without initialization. No frozen paging or CAS snapshot."
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.observations.v1.Observations/ListRooms",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Complete bounded native room geometry, statistics and memberships with exact filters and optional cells/boundary contents. Optional unreadable facts carry issues; no frozen paging or CAS snapshots."
                });
                foreach (var method in new[] { "Camera", "Selection", "Colonists", "RenderState" })
                    loaded.Capabilities.Add(new Lifecycle.Capability
                    {
                        FullMethodName = "rimgovernor.presentation.v1.PresentationReads/" + method,
                        Support = Lifecycle.CapabilitySupport.Supported,
                        Detail = "Read-only native presentation facts. Camera/selection require graphics; roster covers spawned colonists on loaded maps; RenderState is a zero-side-effect read of the controller rendering lease."
                    });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.presentation.v1.PresentationMedia/DemandRendering",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Controller rendering lease of up to 30 real seconds; zero only reads status. No simulation or game-speed changes; PlayerIdentity's direction/viewer fields authenticate nothing."
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.presentation.v1.PresentationMedia/CapturePawn",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Single-frame colonist portrait or nearby-map follow PNG capture, async across a draw cycle. Batch-mode or no-camera games return a typed failure. CaptureScreenshot is not implemented."
                });
                foreach (var method in new[] { "LeaseVideo", "ReadFrame", "AcknowledgeFrame" })
                    loaded.Capabilities.Add(new Lifecycle.Capability
                    {
                        FullMethodName = "rimgovernor.presentation.v1.PresentationMedia/" + method,
                        Support = Lifecycle.CapabilitySupport.Supported,
                        Detail = "Raw (uncompressed RGBA32/BGRA32) in-process video capture shared with the legacy home/video_stream mmap tool. LeaseVideo starts/stops the shared capture (0-15 real seconds); ReadFrame reads the latest captured bytes without extending the lease; AcknowledgeFrame is accept-and-record telemetry only, with no viewer-driven capture throttling yet."
                    });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.observations.v1.Observations/ListPawns",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Complete bounded map pawn census with exact filters. Available draft controllers expose draft-control CAS and causal claims; other entities, social and additional gear/animal details carry explicit issues."
                });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.operations.v1.Operations/ReleaseOwnedDraft",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Exact claim, original owner and unchanged pawn cleanup after revocation, independently of ordinary attempt capacity."
                });
                return new Lifecycle.IdentityReply { Loaded = loaded };
            }, cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }
    }
}
