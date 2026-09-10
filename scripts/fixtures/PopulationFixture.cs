using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Prepared starting conditions only. Capture, care and recruitment run normally afterward.
    public sealed class PopulationFixture
    {
        private static Pawn candidate;

        [Tool("test/population_setup", Description = "Disposable population fixture, excluded from production and model access. Prepares a downed hostile candidate, prison, spare housing and supplies. Does not capture or recruit.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (candidate != null || Find.CurrentMap == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("One setup on a paused disposable map is required");
                var map = Find.CurrentMap;
                var workers = map.mapPawns.FreeColonistsSpawned.ToList();
                var anchor = workers.First().Position;
                var origins = map.AllCells.OrderBy(c => c.DistanceToSquared(anchor)).Where(c =>
                    CellRect.FromLimits(c, c + new IntVec3(15, 0, 9)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))).Take(1).ToList();
                if (origins.Count == 0) throw new InvalidOperationException("Fixture requires an unfogged 16x10 heavy-terrain area without edifices");
                var origin = origins[0];
                foreach (var cell in CellRect.FromLimits(origin, origin + new IntVec3(15, 0, 9)).Cells)
                    cell.GetPlant(map)?.Destroy();
                Func<string, int, int, Thing> spawn = (name, x, z) => {
                    var def = ThingDef.Named(name);
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    if (def.CanHaveFaction) thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, origin + new IntVec3(x, 0, z), map);
                    thing.SetForbidden(false, false);
                    return thing;
                };
                for (int x = 0; x <= 6; x++) for (int z = 0; z <= 6; z++)
                {
                    var cell = origin + new IntVec3(x, 0, z);
                    if (x == 0 || x == 6 || z == 0 || z == 6)
                        spawn(x == 3 && z == 0 ? "Door" : "Wall", x, z);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[cell] = true;
                }
                var prison = (Building_Bed)spawn("Bed", 2, 3);
                prison.ForPrisoners = true;
                for (int i = 0; i <= workers.Count + 1; i++) spawn("SleepingSpot", 8 + i % 6, 1 + i / 6 * 2);
                for (int i = 0; i < 10; i++) spawn("MealSurvivalPack", 8 + i % 6, 6 + i / 6).stackCount = 10;
                spawn("MedicineIndustrial", 10, 4).stackCount = 20;
                spawn("Gun_Revolver", 11, 4);
                foreach (var p in workers)
                {
                    if (p.drafter != null) p.drafter.Drafted = false;
                    foreach (var def in new[] { WorkTypeDefOf.Doctor, WorkTypeDefOf.Warden })
                        if (!p.WorkTypeIsDisabled(def)) p.workSettings.SetPriority(def, 1);
                }
                var faction = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
                candidate = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, faction);
                candidate.guest.Recruitable = true;
                candidate.guest.resistance = 0;
                GenSpawn.Spawn(candidate, origin + new IntVec3(7, 0, 3), map);
                candidate.health.AddHediff(HediffDefOf.Anesthetic);
                candidate.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Bruise"), candidate.RaceProps.body.corePart).Severity = 3;
                candidate.needs.food.CurLevelPercentage = .2f;
                var friendly = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && !f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
                var visitor = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, friendly);
                GenSpawn.Spawn(visitor, origin + new IntVec3(7, 0, 5), map);
                visitor.health.AddHediff(HediffDefOf.Anesthetic);
                visitor.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Bruise"), visitor.RaceProps.body.corePart).Severity = 3;
                visitor.needs.food.CurLevelPercentage = .2f;
                return new { success = true, candidate = candidate.GetUniqueLoadID(), prison = prison.GetUniqueLoadID(),
                    visitor = visitor.GetUniqueLoadID(),
                    scope = "Prepared zero-resistance recruitable downed hostile; ordinary capture, feeding, recruitment and integration required",
                    state = PopulationTools.Person(candidate) };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
