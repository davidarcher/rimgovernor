using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Realized consumption: the completed hourly entries after the hour
    // the brain already holds, from the saved ConsumptionState ring.
    internal static class NativeConsumptionTool
    {
        internal static Obs.ConsumptionSnapshot HandleReadConsumption(Obs.ConsumptionRequest request)
        {
            if (Current.Game == null || Find.TickManager == null)
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." });
            var state = ConsumptionState.For(Current.Game);
            var current = ConsumptionState.HourOf(Find.TickManager.TicksGame);
            var hours = state.After(request.SinceHour, current, out var first);
            return new Obs.ConsumptionSnapshot
            {
                CurrentHour = current,
                FirstHour = first,
                Hours = [.. hours.Select(hour => new Obs.ConsumptionHour
                {
                    Hour = hour.Index,
                    Rows = [.. hour.Rows.Select(pair => new Obs.ConsumptionRow { Definition = pair.Key.Def, Reason = ConsumptionState.Names[pair.Key.Reason], Count = pair.Value })],
                })],
            };
        }
    }
}
