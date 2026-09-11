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
                return new Lifecycle.IdentityReply { Loaded = loaded };
            }, cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }
    }
}
