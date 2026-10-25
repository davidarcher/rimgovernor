using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Prepared starting conditions only. Capture, care and recruitment run normally afterward.
    public sealed class PopulationFixture
    {
        private static Pawn? candidate;

        [Tool("test/population_setup", Description = "Disposable population fixture, excluded from production and model access. Prepares a downed hostile candidate, prison, spare housing and supplies. Does not capture or recruit.")]
        public async Task<object> Setup(INativeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (candidate != null || Find.CurrentMap == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("One setup on a paused disposable map is required");
                var map = Find.CurrentMap;
                var workers = map.mapPawns.FreeColonistsSpawned.ToList();
                // The case starts on the blank lab: its colonists stand
                // on the centre row, so the 16x10 site sits a few rows north
                // of it on bare Soil, with no site search.
                var origin = map.Center + new IntVec3(-8, 0, 4);
                if (!CellRect.FromLimits(origin, origin + new IntVec3(15, 0, 9)).Cells.All(p => p.InBounds(map) && p.Standable(map) && p.GetEdifice(map) == null))
                    throw new InvalidOperationException($"The fixture site at {origin} is not clear: this fixture needs the blank lab start");
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
                candidate = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, faction);
                candidate.guest.Recruitable = true;
                GenSpawn.Spawn(candidate, origin + new IntVec3(7, 0, 3), map);
                candidate.health.AddHediff(HediffDefOf.Anesthetic);
                candidate.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Bruise"), candidate.RaceProps.body.corePart).Severity = 3;
                candidate.needs.food.CurLevelPercentage = .2f;
                if (!candidate.Downed || !candidate.CanBeCaptured() || !candidate.HostileTo(Faction.OfPlayer))
                    throw new InvalidOperationException("The candidate must satisfy ordinary downed hostile capture eligibility");
                if (ModsConfig.AnomalyActive)
                {
                    // Regression: humans have the holding comp too; only the
                    // game's platform-study targets take the entity route.
                    if (candidate.GetComp<CompHoldingPlatformTarget>() == null || NativeCustodyOperations.EntityTarget(candidate) != null)
                        throw new InvalidOperationException("Ordinary human capture must use a prisoner bed despite its holding comp");
                    var entity = PawnGenerator.GeneratePawn(PawnKindDefOf.Sightstealer, Faction.OfEntities);
                    try
                    {
                        var holding = entity.GetComp<CompHoldingPlatformTarget>();
                        if (holding == null || !ReferenceEquals(NativeCustodyOperations.EntityTarget(entity), holding))
                            throw new InvalidOperationException("A platform-study entity must retain the holding-platform capture route");
                    }
                    finally { entity.Destroy(DestroyMode.Vanish); }
                }
                var friendly = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && !f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
                var visitor = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, friendly);
                GenSpawn.Spawn(visitor, origin + new IntVec3(7, 0, 5), map);
                visitor.health.AddHediff(HediffDefOf.Anesthetic);
                visitor.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Bruise"), visitor.RaceProps.body.corePart).Severity = 3;
                visitor.needs.food.CurLevelPercentage = .2f;
                return new { success = true, candidate = candidate.GetUniqueLoadID(), prison = prison.GetUniqueLoadID(),
                    visitor = visitor.GetUniqueLoadID(),
                    bedChecks = workers.Select(p => new { pawn = p.GetUniqueLoadID(),
                        usable = RestUtility.CanUseBedNow(prison, candidate, false, guestStatusOverride: GuestStatus.Prisoner),
                        reachable = p.CanReach(prison, PathEndMode.OnCell, Danger.Some),
                        reservable = p.CanReserve(prison),
                        nativeBed = RestUtility.FindBedFor(candidate, p, false, false, GuestStatus.Prisoner)?.GetUniqueLoadID() }).ToArray() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
