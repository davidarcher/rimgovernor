#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Realized consumption (#2441): the completed hourly entries after the hour
    // Go already holds, from the saved ConsumptionState ring.
    public sealed class NativeConsumptionTool
    {
        private const string ToolName = "rimgovernor/observations_read_consumption";

        [Tool(ToolName, Title = "Read realized consumption", Description = "Completed hourly (2500-tick) per-definition, per-reason stock consumption entries after since_hour, from the saved 60-day ring. Sparse; the hour still filling is not listed.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ConsumptionReply.", Always = true)]
        public async Task<object> ReadConsumption(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ConsumptionRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ConsumptionRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ConsumptionReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => Read(parsed.HasSinceHour ? parsed.SinceHour : -1), cancellationToken).ConfigureAwait(false);
        }

        internal static Obs.ConsumptionReply Read(int sinceHour)
        {
            if (Current.Game == null || Find.TickManager == null)
                return new Obs.ConsumptionReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };
            var state = ConsumptionState.For(Current.Game);
            var current = ConsumptionState.HourOf(Find.TickManager.TicksGame);
            var hours = state.After(sinceHour, current, out var first);
            var snapshot = new Obs.ConsumptionSnapshot { CurrentHour = current, FirstHour = first };
            foreach (var hour in hours)
            {
                var entry = new Obs.ConsumptionHour { Hour = hour.Index };
                foreach (var pair in hour.Rows)
                    entry.Rows.Add(new Obs.ConsumptionRow { Definition = pair.Key.Def, Reason = ConsumptionState.Names[pair.Key.Reason], Count = pair.Value });
                snapshot.Hours.Add(entry);
            }
            return new Obs.ConsumptionReply { Observed = snapshot };
        }
    }
}
