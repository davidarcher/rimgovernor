using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class ComfortFixture
    {
        [Tool("test/comfort_inputs", Description = "Supply disposable construction wood, or prepare hunger/recreation needs after furniture construction. Does not place furniture or order its use.")]
        public async Task<object> Inputs(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Prepare needs only after ordinary furniture construction.", DefaultValue = false)] bool needs = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                var anchor = map.listerBuildings.allBuildingsColonist.First(b => b.def == ThingDefOf.Wall).Position;
                var resource = needs ? ThingDefOf.MealSimple : ThingDefOf.WoodLog;
                for (int i = 0; i < 4; i++) {
                    var item = ThingMaker.MakeThing(resource);
                    item.stackCount = resource.stackLimit;
                    GenPlace.TryPlaceThing(item, anchor, map, ThingPlaceMode.Near);
                    item.SetForbidden(false, false);
                }
                foreach (var p in people) {
                    p.needs.joy.CurLevelPercentage = needs ? .05f : .95f;
                    if (needs) {
                        p.needs.food.CurLevelPercentage = .1f;
                        p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
                return new { success = true, needs, people = people.Select(p => p.GetUniqueLoadID()).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
