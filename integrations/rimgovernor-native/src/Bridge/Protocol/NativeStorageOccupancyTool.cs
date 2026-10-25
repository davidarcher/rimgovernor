using System.Linq;
using RimGovernor.Brain.GameAccess;
using Verse;

namespace RimGovernor.Native
{
    // Storage occupancy: each requested storage group's hourly stack samples
    // after the hour the reader already holds, from the saved StorageOccupancyState ring.
    internal static class NativeStorageOccupancyTool
    {
        internal static RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot HandleRead(RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest request)
        {
            if (Current.Game == null || Find.TickManager == null)
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." });
            var state = StorageOccupancyState.For(Current.Game);
            return new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot
            {
                CurrentHour = StorageOccupancyState.HourOf(Find.TickManager.TicksGame),
                Groups = [.. request.Groups
                    .Where(id => id.Length != 0)
                    .Select(id => (Id: id, Samples: state.After(id, request.SinceHour)))
                    .Where(g => g.Samples.Count != 0)
                    .Select(g => new RimGovernor.Brain.GameFacts.Observations.StorageOccupancyGroup
                    {
                        Group = g.Id,
                        Samples = [.. g.Samples.Select(s => new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySample { Hour = s.Key, Stacks = s.Value })],
                    })],
            };
        }
    }
}
