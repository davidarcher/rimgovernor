#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Realized consumption: the completed hourly entries after the hour
    // the brain already holds, from the saved ConsumptionState ring.
    internal static class NativeConsumptionTool
    {

        internal static Obs.ConsumptionReply HandleReadConsumption(Obs.ConsumptionRequest request)
        {
            var sinceHour = request.SinceHour ?? -1;
            if (Current.Game == null || Find.TickManager == null)
                return new Obs.ConsumptionReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };
            var state = ConsumptionState.For(Current.Game);
            var current = ConsumptionState.HourOf(Find.TickManager.TicksGame);
            var hours = state.After(sinceHour, current, out var first);
            var snapshot = new Obs.ConsumptionSnapshot { CurrentHour = current, FirstHour = first };
            foreach (var hour in hours)
            {
                var entry = new Obs.ConsumptionHour { Hour = hour.Index };
                foreach (var pair in hour.Rows)
                    entry = entry with { Rows = [.. entry.Rows, new Obs.ConsumptionRow { Definition = pair.Key.Def, Reason = ConsumptionState.Names[pair.Key.Reason], Count = pair.Value }] };
                snapshot = snapshot with { Hours = [.. snapshot.Hours, entry] };
            }
            return new Obs.ConsumptionReply { Outcome = snapshot };
        }
    }
}
