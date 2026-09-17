using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Prepared starting conditions only. Capture, care and recruitment run normally afterward.
    public sealed class PopulationFixture
    {
        private static Pawn candidate;

        [Tool("test/population_setup", Description = "Disposable population fixture, excluded from production and model access. Prepares a downed hostile candidate, prison, spare housing and supplies. Does not capture or recruit.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Native starting candidate kind: Villager for resistance work or SpaceRefugee_Clothed for bounded admission.")] string candidateKind = "Villager")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (candidate != null || Find.CurrentMap == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("One setup on a paused disposable map is required");
                var map = Find.CurrentMap;
                var workers = map.mapPawns.FreeColonistsSpawned.ToList();
                if (candidateKind != "Villager" && candidateKind != "SpaceRefugee_Clothed")
                    throw new ArgumentException("Unsupported fixture candidate kind");
                var anchor = workers.First().Position;
                var origins = map.AllCells.OrderBy(c => c.DistanceToSquared(anchor)).Where(c =>
                    CellRect.FromLimits(c, c + new IntVec3(15, 0, 9)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    && workers.Any(p => p.CanReach(c + new IntVec3(7, 0, 3), PathEndMode.OnCell, Danger.Deadly))).Take(1).ToList();
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
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                if (!prison.ForPrisoners || prison.GetRoom().PsychologicallyOutdoors)
                    throw new InvalidOperationException("Fixture prison bed must be indoors and configured for prisoners");
                for (int x = 8; x <= 14; x++) for (int z = 0; z <= 6; z++)
                {
                    if (x == 8 || x == 14 || z == 0 || z == 6) spawn(x == 11 && z == 0 ? "Door" : "Wall", x, z);
                    var cell = origin + new IntVec3(x, 0, z);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[cell] = true;
                }
                for (int i = 0; i <= workers.Count + 1; i++) spawn("SleepingSpot", 9 + i % 5, 1 + i / 5 * 2);
                for (int i = 0; i < 40; i++)
                {
                    var food = spawn("MealSurvivalPack", i % 14, 7 + i / 14);
                    food.stackCount = food.def.stackLimit;
                    map.roofGrid.SetRoof(food.Position, RoofDefOf.RoofConstructed);
                }
                spawn("MedicineIndustrial", 10, 4).stackCount = 20;
                spawn("Gun_Revolver", 11, 4);
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                foreach (var p in workers)
                {
                    if (p.drafter != null) p.drafter.Drafted = false;
                    foreach (var def in new[] { WorkTypeDefOf.Doctor, WorkTypeDefOf.Warden })
                        if (!p.WorkTypeIsDisabled(def)) p.workSettings.SetPriority(def, 1);
                }
                var faction = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
                candidate = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed(candidateKind), faction);
                candidate.guest.Recruitable = true;
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
                    candidateKind,
                    visitor = visitor.GetUniqueLoadID(),
                    prisonCell = prison.Position.IsInPrisonCell(map),
                    bedChecks = workers.Select(p => new { pawn = p.GetUniqueLoadID(),
                        usable = RestUtility.CanUseBedNow(prison, candidate, false, guestStatusOverride: GuestStatus.Prisoner),
                        reachable = p.CanReach(prison, PathEndMode.OnCell, Danger.Some),
                        reservable = p.CanReserve(prison),
                        nativeBed = RestUtility.FindBedFor(candidate, p, false, false, GuestStatus.Prisoner)?.GetUniqueLoadID() }).ToArray(),
                    scope = "Prepared recruitable downed hostile; native capture assigns resistance, ordinary care and recruitment required",
                    state = PopulationTools.Person(candidate) };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Forces the one native outcome a prisoner-interaction acceptance run
        // needs to observe without playing days of recruitment: the game's
        // own InteractionWorker_RecruitAttempt.DoRecruit path, exactly what a
        // successful warden recruit interaction calls.
        [Tool("test/population_recruit", Description = "Disposable population fixture, excluded from production and model access. Recruits the prepared candidate through the native recruit-success path so a later progress read observes an actual recruited outcome.")]
        public async Task<object> Recruit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (candidate == null || Find.CurrentMap == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("A prepared candidate on a paused disposable map is required");
                if (!candidate.IsPrisonerOfColony) throw new InvalidOperationException("The candidate must be a colony prisoner before recruitment");
                var recruiter = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != candidate)
                    ?? throw new InvalidOperationException("A free colonist recruiter is required");
                InteractionWorker_RecruitAttempt.DoRecruit(recruiter, candidate, false);
                return new { success = true, candidate = candidate.GetUniqueLoadID(), recruiter = recruiter.GetUniqueLoadID(),
                    freeColonist = candidate.IsFreeColonist, prisoner = candidate.IsPrisonerOfColony };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
