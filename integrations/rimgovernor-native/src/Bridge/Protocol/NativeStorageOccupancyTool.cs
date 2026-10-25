using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Storage occupancy: each requested storage group's hourly stack samples
    // after the hour the reader already holds, from the saved StorageOccupancyState ring.
    internal static class NativeStorageOccupancyTool
    {
        internal static Obs.StorageOccupancySnapshot HandleRead(Obs.StorageOccupancyRequest request)
        {
            if (Current.Game == null || Find.TickManager == null)
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." });
            var state = StorageOccupancyState.For(Current.Game);
            return new Obs.StorageOccupancySnapshot
            {
                CurrentHour = StorageOccupancyState.HourOf(Find.TickManager.TicksGame),
                Groups = [.. request.Groups
                    .Where(id => id.Length != 0)
                    .Select(id => (Id: id, Samples: state.After(id, request.SinceHour)))
                    .Where(g => g.Samples.Count != 0)
                    .Select(g => new Obs.StorageOccupancyGroup
                    {
                        Group = g.Id,
                        Samples = [.. g.Samples.Select(s => new Obs.StorageOccupancySample { Hour = s.Key, Stacks = s.Value })],
                    })],
            };
        }
    }
}
