using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Compiled only with -p:InstallFixture=true for disposable tests.
    public sealed class InstallFixture
    {
        [Tool("test/packed_furniture", Description = "Disposable test setup; not part of the installed gameplay surface.")]
        public async Task<object> Create(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var cell = GenRadial.RadialCellsAround(pawn.Position, 8, true).First(c => c.InBounds(map) && !c.Fogged(map)
                    && c.Standable(map) && c.GetEdifice(map) == null);
                var bed = ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFaction(Faction.OfPlayer);
                var mini = bed.MakeMinified();
                GenSpawn.Spawn(mini, cell, map);
                mini.SetForbidden(false, false);
                foreach (var worker in map.mapPawns.FreeColonistsSpawned)
                    if (!worker.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                        worker.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                return (object)new { success = true, thingId = mini.GetUniqueLoadID(), innerId = bed.GetUniqueLoadID(), x = cell.x, z = cell.z };
            }, cancellationToken);
        }
    }
}
