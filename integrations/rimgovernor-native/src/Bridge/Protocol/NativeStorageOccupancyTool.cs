#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Storage occupancy: each requested storage group's hourly stack samples
    // after the hour the reader already holds, from the saved StorageOccupancyState ring.
    public sealed class NativeStorageOccupancyTool
    {
        private const string ToolName = "rimgovernor/observations_read_storage_occupancy";

        [Tool(ToolName, Title = "Read storage occupancy", Description = "Hourly (2500-tick) item stacks held by each requested storage group (stockpile zone or storage building, by load id) after since_hour, from the saved 15-day ring. A group with no samples is not listed.")]
        [ToolResponse("payload", "string", "Official ProtoJSON StorageOccupancyReply.", Always = true)]
        public async Task<object> ReadStorageOccupancy(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON StorageOccupancyRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, out Obs.StorageOccupancyRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.StorageOccupancyReply { Outcome = new Obs.StorageOccupancyReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleRead(parsed), cancellationToken).ConfigureAwait(false);
        }

        internal static Obs.StorageOccupancyReply HandleRead(Obs.StorageOccupancyRequest request)
        {
            var sinceHour = request.SinceHour ?? -1;
            if (Current.Game == null || Find.TickManager == null)
                return new Obs.StorageOccupancyReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };
            var state = StorageOccupancyState.For(Current.Game);
            var groups = new List<Obs.StorageOccupancyGroup>();
            foreach (var group in request.Groups)
            {
                var id = group?.Id ?? "";
                var samples = state.After(id, sinceHour);
                if (id.Length == 0 || samples.Count == 0) continue;
                groups.Add(new Obs.StorageOccupancyGroup
                {
                    Group = new Common.Ref { Id = id },
                    Samples = [.. samples.Select(s => new Obs.StorageOccupancySample { Hour = s.Key, Stacks = s.Value })],
                });
            }
            return new Obs.StorageOccupancyReply { Outcome = new Obs.StorageOccupancySnapshot { CurrentHour = StorageOccupancyState.HourOf(Find.TickManager.TicksGame), Groups = [.. groups] } };
        }
    }
}
