using System.Linq;
using RimGovernor.Brain.GameAccess;
using Verse;

namespace RimGovernor.Native
{
    // Realized consumption: the completed hourly entries after the hour
    // the brain already holds, from the saved ConsumptionState ring.
    internal static class NativeConsumptionTool
    {
        internal static RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot HandleReadConsumption(RimGovernor.Brain.GameOrders.Observations.ConsumptionRequest request)
        {
            if (Current.Game == null || Find.TickManager == null)
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." });
            var state = ConsumptionState.For(Current.Game);
            var current = ConsumptionState.HourOf(Find.TickManager.TicksGame);
            var hours = state.After(request.SinceHour, current, out var first);
            return new RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot
            {
                CurrentHour = current,
                FirstHour = first,
                Hours = [.. hours.Select(hour => new RimGovernor.Brain.GameFacts.Observations.ConsumptionHour
                {
                    Hour = hour.Index,
                    Rows = [.. hour.Rows.Select(pair => new RimGovernor.Brain.GameFacts.Observations.ConsumptionRow { Definition = pair.Key.Def, Reason = ConsumptionState.Names[pair.Key.Reason], Count = pair.Value })],
                })],
            };
        }
    }
}
