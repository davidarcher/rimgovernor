#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // New-colony start (#2019). The tools exist so the wire contract is served
    // end to end; generation is #2021 and answers Unavailable until it lands.
    public sealed class ProtoLifecycleNewColonyTools
    {
        private const string NewColonyToolName = "rimgovernor/lifecycle_new_colony";
        private const string ReadNewColonyToolName = "rimgovernor/lifecycle_read_new_colony";

        [Tool(NewColonyToolName, Title = "Start a new colony",
            Description = "Start generating a colony from a spec on the main menu. Returns NewColonyPending; poll rimgovernor/lifecycle_read_new_colony.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.NewColonyReply.", Always = true)]
        public async Task<object> NewColony(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle NewColonyRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NewColonyToolName, request, Lifecycle.NewColonyRequest.Parser, out _, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.NewColonyReply { Failure = failure });
            return await Task.FromResult(Unavailable()).ConfigureAwait(false);
        }

        [Tool(ReadNewColonyToolName, Title = "Read a new colony's progress",
            Description = "Poll a request_id from rimgovernor/lifecycle_new_colony for NewColonyCompleted/NewColonyPending/NewColonySuperseded/Failure.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.NewColonyReply.", Always = true)]
        public async Task<object> ReadNewColony(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle RequestStatus ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ReadNewColonyToolName, request, Lifecycle.RequestStatus.Parser, out _, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.NewColonyReply { Failure = failure });
            return await Task.FromResult(Unavailable()).ConfigureAwait(false);
        }

        private static object Unavailable() =>
            ProtoBoundary.Encode(new Lifecycle.NewColonyReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable,
                "New colony generation is not implemented yet.") });
    }
}
