using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class UpkeepBedTools
    {
        [Tool("home/upkeep_bed", Description = "Assign an empty ordinary bed to an exact colonist only while the observed previous bed assignment still matches. Preserves occupied assignments, medical/prisoner beds, restrictions and native assignment eligibility. Assignment does not prove sleeping use.")]
        public async Task<object> Assign(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact pawn Thing ID.")] string pawn,
            [ToolParameter(Description = "Exact empty target bed Thing ID.")] string bed,
            [ToolParameter(Description = "Exact observed previous owned bed Thing ID, or none.")] string previousBed,
            [ToolParameter(Description = "Validate without assigning.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                    return new { success = false, error = "Paused current map required." };
                var p = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(x => x.GetUniqueLoadID() == pawn);
                var target = map.listerThings.AllThings.OfType<Building_Bed>().SingleOrDefault(b => b.GetUniqueLoadID() == bed);
                if (p == null || p.Dead || p.Downed || p.Drafted || p.InMentalState || p.ownership == null
                    || p.CurJob?.playerForced == true || p.health.HasHediffsNeedingTend())
                    return new { success = false, error = "Pawn unavailable or player work protected." };
                if ((p.ownership.OwnedBed?.GetUniqueLoadID() ?? "none") != previousBed)
                    return new { success = false, error = "Previous bed assignment changed; observe before recovery." };
                if (target == null || !target.Spawned || target.Faction != Faction.OfPlayerSilentFail
                    || !target.def.building.bed_humanlike || target.Medical || target.ForPrisoners
                    || target.OwnersForReading.Any() || target.IsForbidden(p) || target.IsBurning()
                    || !target.OccupiedRect().All(c => c.Roofed(map)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[c]))
                    || !p.CanReach(target, PathEndMode.OnCell, Danger.None)
                    || target.AmbientTemperature < p.GetStatValue(StatDefOf.ComfyTemperatureMin)
                    || target.AmbientTemperature > p.GetStatValue(StatDefOf.ComfyTemperatureMax))
                    return new { success = false, error = "Bed unavailable, assigned, restricted or thermally unsafe." };
                var assignable = target.GetComp<CompAssignableToPawn>();
                if (assignable == null || !assignable.AssigningCandidates.Contains(p)
                    || !assignable.CanAssignTo(p).Accepted || assignable.IdeoligionForbids(p))
                    return new { success = false, error = "Native bed assignment eligibility refused." };
                if (!dryRun) assignable.TryAssignPawn(p);
                return new { success = dryRun || p.ownership.OwnedBed == target, dryRun,
                    pawn, bed, previousBed, sleeping = p.CurrentBed() == target };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
