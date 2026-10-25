using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // Disposable test setup only. Ends the trigger shelter/manhunter_waitout or shelter/toxic_fallout staged: the toxic fallout condition ends
    // and every manhunter animal leaves the map (vanished, as an exit does), so the case can watch the served brain lift
    // the Safe-area restriction. The stage primitives fire a trigger but cannot end one.
    public sealed class ShelterFixture
    {
        [Tool("test/shelter_end", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: end the map's toxic fallout and take every manhunter animal off the map.")]
        public async Task<object> End(INativeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null) return new { success = false, reason = "A loaded colony map is required." };
                foreach (var condition in map.gameConditionManager.ActiveConditions.Where(c => c.def == GameConditionDefOf.ToxicFallout).ToList()) condition.End();
                foreach (var animal in map.mapPawns.AllPawnsSpawned.Where(p => p.MentalStateDef == MentalStateDefOf.ManhunterPermanent || p.MentalStateDef == MentalStateDefOf.Manhunter).ToList())
                    animal.ExitMap(false, Rot4.West);
                return new {
                    success = map.gameConditionManager.ActiveConditions.All(c => c.def != GameConditionDefOf.ToxicFallout)
                        && !map.mapPawns.AllPawnsSpawned.Any(p => p.InAggroMentalState && p.RaceProps.Animal),
                };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
