using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class AnimalContainmentFixture
    {
        [Tool("test/containment_setup", Description = "Prepare a disposable loose pen animal and pet. No pen or handling settings are created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var center = map.mapPawns.FreeColonistsSpawned.First().Position;
                var cell = GenRadial.RadialCellsAround(center, 8, true).First(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map));
                var animal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), Faction.OfPlayerSilentFail);
                var pet = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Husky"), Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(animal, cell, map);
                GenSpawn.Spawn(pet, cell, map);
                animal.needs.food.CurLevelPercentage = .95f;
                pet.needs.food.CurLevelPercentage = .95f;
                return new { success = true, animal = animal.GetUniqueLoadID(), pet = pet.GetUniqueLoadID() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
