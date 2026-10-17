#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Storage occupancy: each requested storage group's hourly stack samples
    // after the hour the reader already holds, from the saved StorageOccupancyState ring.
    internal static class NativeStorageOccupancyTool
    {

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
