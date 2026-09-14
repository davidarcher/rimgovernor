#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Typed paired read for SetProductionPolicy: the same map-scoped
    // ProductionPolicyGuard/DrillingGuard state NativeProductionPolicyOperations
    // writes, exposed as ProductionPolicySnapshot. Not paginated: floors,
    // commitments, stopped defs and drills are each already bounded (<=256/<=32
    // rows) by the write side, so a single reply always fits.
    public sealed class NativeProductionPolicyObservationTools
    {
        private const string ToolName = "rimgovernor/observations_read_production_policy";

        [Tool(ToolName, Title = "Read typed production policy", Description = "Map-scoped resource floors, transient construction commitments, stopped inputs and owned bounded drilling facilities, with a CAS snapshot token for SetProductionPolicy. No settings changes.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ProductionPolicyReply.", Always = true)]
        public async Task<object> ReadProductionPolicy(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ProductionPolicyRequest string.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ProductionPolicyRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ProductionPolicyReply { Failure = failure });
            if (parsed.Scope?.ExpectedIdentity == null)
                return ProtoBoundary.Encode(new Obs.ProductionPolicyReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity is required.") });
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Obs.ProductionPolicyReply { Failure = invalid });
                try
                {
                    return ProtoBoundary.Encode(new Obs.ProductionPolicyReply { Observed = NativeProductionPolicyOperations.Snapshot(map!, context) });
                }
                catch (System.Exception)
                {
                    return ProtoBoundary.Encode(new Obs.ProductionPolicyReply { Unavailable = new Common.Unavailable {
                        Reason = Common.UnavailableReason.ReadFailed, Detail = "Production policy state could not be read completely." } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
