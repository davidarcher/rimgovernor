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
                        Support = Lifecycle.CapabilitySupport.Supported, Detail = "PlaceBuilding is implemented; other commands return unsupported."
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
                foreach (var method in new[] { "Camera", "Selection", "Colonists" })
                    loaded.Capabilities.Add(new Lifecycle.Capability
                    {
                        FullMethodName = "rimgovernor.presentation.v1.PresentationReads/" + method,
                        Support = Lifecycle.CapabilitySupport.Supported,
                        Detail = "Read-only native presentation facts. Camera/selection require graphics; roster covers spawned colonists on loaded maps."
                    });
                loaded.Capabilities.Add(new Lifecycle.Capability
                {
                    FullMethodName = "rimgovernor.observations.v1.Observations/ListPawns",
                    Support = Lifecycle.CapabilitySupport.Supported,
                    Detail = "Complete bounded map pawn census with exact intersecting filters and useful detail facts. Social, CAS and additional gear/animal details carry explicit issues."
                });
                return new Lifecycle.IdentityReply { Loaded = loaded };
            }, cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }
    }
}
