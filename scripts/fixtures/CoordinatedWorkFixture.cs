#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools;

// Prepared physical facts and fault injection only. All selected tasks come from the ordinary brain/Hands.
public sealed partial class PawnControlFixture
{
    private static bool coordinationActive, coordinationPatched, coordinationInterrupt, coordinationInterrupted;
    private static string coordinationMode = "";
    private static string coordinationMethod = "", coordinationInterruptedPawn = "", coordinationInterruptedAssignment = "";
    private static readonly Dictionary<string, PawnActivityOrder> coordinationOrders = new(StringComparer.Ordinal);
    private static readonly HashSet<string> coordinationCompleted = new(StringComparer.Ordinal);
    private static readonly HashSet<string> coordinationParticipants = new(StringComparer.Ordinal);
    private static readonly HashSet<string> coordinationReleased = new(StringComparer.Ordinal);
    private static readonly HashSet<string> coordinationFinishedKinds = new(StringComparer.Ordinal);
    private static bool coordinationJoint, coordinationRecovered, coordinationForced;
    private static int coordinationDelivered, coordinationRevision;
    // The staged barracks (construction): its interior's south-west cell and its door.
    private static IntVec3 barracks, barracksDoor;

    [Tool("test/coordinated_work_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Prepare a quiet food or shelter deficit and specialist/helper physical facts; construction also clears a 5x5 barracks site with a lit campfire at its interior centre and replies its interior and door cells. No plans, orders or task assignments are installed.")]
    public async Task<object> PrepareCoordination(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description = "food or construction", DefaultValue = "food")] string mode = "food",
        [ToolParameter(Description = "Interrupt one carrying delivery once", DefaultValue = false)] bool interrupt = false) =>
        await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (mode != "food" && mode != "construction") throw new ArgumentException("Unknown coordination fixture.");
            coordinationActive = false; (barracks, barracksDoor) = (default, default);
            var map = Find.CurrentMap;
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToArray();
            if (people.Length < 2) throw new InvalidOperationException("Two prepared participants required.");
            foreach (var p in people)
            {
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                p.needs.food.CurLevelPercentage = 1; p.needs.rest.CurLevelPercentage = 1;
                for (var hour = 0; hour < 24; hour++) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !p.WorkTypeIsDisabled(t))) p.workSettings.SetPriority(type, 0);
                p.skills.GetSkill(SkillDefOf.Plants).Level = p == people[0] ? 20 : 0;
                p.skills.GetSkill(SkillDefOf.Construction).Level = p == people[0] ? 20 : 0;
            }
            var anchor = people[0].Position;
            foreach (var p in people.Skip(1)) p.Position = CellFinder.RandomClosewalkCellNear(anchor, map, 3);
            // A slower expert makes the haul-only alternative economically useful without selecting it.
            var leg = people[0].RaceProps.body.AllParts.FirstOrDefault(b => b.def == BodyPartDefOf.Leg && !people[0].health.hediffSet.PartIsMissing(b));
            if (leg != null) people[0].health.AddHediff(HediffDefOf.MissingBodyPart, leg);
            if (mode == "food")
            {
                foreach (var t in map.listerThings.AllThings.Where(t => t.def.IsNutritionGivingIngestible && t.def.category == ThingCategory.Item).ToArray()) t.Destroy();
                foreach (var p in map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant?.harvestedThingDef?.IsNutritionGivingIngestible == true).ToArray()) p.Destroy();
                var origin = GenRadial.RadialCellsAround(anchor, 5, false).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null);
                foreach (var plant in origin.GetThingList(map).OfType<Plant>().ToArray()) plant.Destroy();
                crop = GenSpawn.Spawn(ThingDefOf.Plant_Potato, origin, map); ((Plant)crop).Growth = 1;
                map.areaManager.Home[origin] = true;
                deliveryCell = GenRadial.RadialCellsAround(origin, 35, false).First(c => c.InBounds(map) && c.Standable(map)
                    && c.GetEdifice(map) == null && c.DistanceTo(origin) > 28 && c.GetZone(map) == null);
                var stockpile = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(stockpile); stockpile.GetStoreSettings().filter.SetDisallowAll();
                harvestProduct = crop.def.plant.harvestedThingDef;
                stockpile.GetStoreSettings().filter.SetAllow(harvestProduct, true); stockpile.GetStoreSettings().Priority = StoragePriority.Critical;
                stockpile.AddCell(deliveryCell);
            }
            else
            {
                foreach (var b in map.listerThings.AllThings.Where(t => t.Faction == Faction.OfPlayer &&
                    (t is Building_Bed || t is Frame || t is Blueprint || t.def == ThingDefOf.Wall || t.def.defName == "Door")).ToArray()) b.Destroy();
                foreach (var cell in map.AllCells.Where(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed)) map.roofGrid.SetRoof(cell, null);
                foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).ToArray()) t.Destroy();
                // The barracks the case's layout plan names (#3537): a clear unroofed 5x5 square away from the colonists and
                // the wood, its 3x3 interior heated by a lit campfire, door mid south. Its ring is the urgent build.
                bool Clear(CellRect r) => r.InBounds(map) && r.Cells.All(c => c.DistanceTo(anchor) > 5 && c.Standable(map) && c.GetEdifice(map) == null
                    && c.GetZone(map) == null && c.GetRoof(map) == null && !c.GetTerrain(map).IsWater);
                var origin = GenRadial.RadialCellsAround(anchor, 25, false).Select(c => new CellRect(c.x, c.z, 5, 5)).First(Clear).Min;
                foreach (var cell in new CellRect(origin.x, origin.z, 5, 5).Cells)
                {
                    foreach (var t in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToArray()) t.Destroy();
                    map.areaManager.Home[cell] = true;
                }
                var fire = FixtureKit.Spawn(map, ThingDefOf.Campfire, null, origin + new IntVec3(2, 0, 2), Faction.OfPlayer, Rot4.North);
                var fuel = fire.TryGetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
                (barracks, barracksDoor) = (origin + new IntVec3(1, 0, 1), origin + new IntVec3(2, 0, 0));
                // The ring takes 75 logs and the door 25: two full stacks.
                for (var stack = 0; stack < 2; stack++)
                    GenSpawn.Spawn(ThingDefOf.WoodLog, CellFinder.RandomClosewalkCellNear(anchor, map, 3), map).stackCount = 75;
            }
            coordinationOrders.Clear(); coordinationCompleted.Clear(); coordinationParticipants.Clear(); coordinationReleased.Clear(); coordinationFinishedKinds.Clear();
            coordinationMethod = coordinationInterruptedPawn = coordinationInterruptedAssignment = "";
            coordinationMode = mode; coordinationInterrupt = interrupt; coordinationInterrupted = false;
            coordinationJoint = coordinationRecovered = coordinationForced = false;
            coordinationDelivered = coordinationRevision = 0;
            if (!coordinationPatched)
            {
                var h = new Harmony("rimgovernor.fixture.coordinated-work");
                h.Patch(AccessTools.Method(typeof(NativePawnActivities), "Apply"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(CoordinationApplied)));
                h.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(CoordinationTick)));
                coordinationPatched = true;
            }
            coordinationActive = true;
            return FixtureKit.Reply(new Fx.CoordinationPrepared { Success = true, X = barracks.x, Z = barracks.z, DoorX = barracksDoor.x, DoorZ = barracksDoor.z,
                OutdoorTemperature = map.mapTemperature.OutdoorTemp,
                Comfort = people.Select(p => new Fx.CoordinationComfort(p.GetUniqueLoadID(), p.ComfortableTemperatureRange().min, p.SafeTemperatureRange().min)).ToArray() });
        }, ct).ConfigureAwait(false);

    private static void CoordinationApplied(PawnActivities batch)
    {
        if (!coordinationActive) return;
        var map = Find.CurrentMap;
        bool Installed(PawnActivityOrder order) => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == order.Pawn) is { } pawn
            && (order.Release ? NativePawnActivities.Read(pawn) == null : NativePawnActivities.Read(pawn) is { } execution
                && execution.Assignment == order.Assignment && execution.Method == order.Method && execution.Revision == order.Revision);
        if (batch.Orders.Any(o => !Installed(o))) return;
        if (coordinationMethod.Length == 0 && batch.Tasks.Any(t => coordinationMode == "food" ? t.Step.Job == "Harvest" : t.Step.Kind == PawnActivityKind.Supply))
            coordinationMethod = batch.Orders[0].Method;
        if (coordinationMethod.Length == 0 || batch.Orders.Any(o => o.Method != coordinationMethod)) return;
        coordinationJoint |= batch.Tasks.Select(t => t.Pawn).Distinct().Count() > 1;
        foreach (var order in batch.Orders)
        {
            if (order.Task.Length == 0 && !coordinationParticipants.Contains(order.Pawn)) continue;
            coordinationParticipants.Add(order.Pawn); coordinationOrders[order.Pawn] = order;
            if (order.Release) coordinationReleased.Add(order.Pawn);
            if (coordinationInterrupted && order.Pawn == coordinationInterruptedPawn && order.Assignment == coordinationInterruptedAssignment
                && order.Revision > coordinationRevision && order.Steps.Any(s => s.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply)) coordinationRecovered = true;
        }
    }

    private static void CoordinationTick(Pawn ___pawn)
    {
        if (!coordinationActive || !coordinationOrders.TryGetValue(___pawn.GetUniqueLoadID(), out var order) || order.Release) return;
        var execution = NativePawnActivities.Read(___pawn);
        if (execution == null || execution.Assignment != order.Assignment || execution.Method != order.Method || execution.Revision != order.Revision) return;
        coordinationForced |= ___pawn.CurJob?.playerForced == true;
        if (execution.State == "completed" && !order.Preparing && coordinationCompleted.Add(order.Assignment + "/" + order.Revision))
        {
            coordinationDelivered += order.Steps.Where(s => s.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply).Sum(s => s.Count);
            foreach (var step in order.Steps) coordinationFinishedKinds.Add(step.Kind + ":" + step.Job);
        }
        if (coordinationInterrupt && !coordinationInterrupted && ___pawn.carryTracker.CarriedThing != null
            && order.Steps.Any(s => s.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply))
        {
            coordinationInterrupted = true; coordinationRevision = checked((int)order.Revision);
            coordinationInterruptedPawn = order.Pawn; coordinationInterruptedAssignment = order.Assignment;
            ___pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
        }
    }

    [Tool("test/coordinated_work_read", Description = "UNSAFE FOR MODEL EXECUTION. Observe planner-selected participant/task evidence, physical output, carried interruption/rebinding and released ownership.")]
    public async Task<object> ReadCoordination(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
    {
        var map = Find.CurrentMap;
        return FixtureKit.Reply(new Fx.CoordinationRead
        {
            Tick = Find.TickManager.TicksGame, Joint = coordinationJoint, Interrupted = coordinationInterrupted, Recovered = coordinationRecovered,
            Forced = coordinationForced, Delivered = coordinationDelivered,
            Participants = coordinationParticipants.OrderBy(s => s).ToArray(), Released = coordinationReleased.OrderBy(s => s).ToArray(),
            FinishedKinds = coordinationFinishedKinds.OrderBy(s => s).ToArray(),
            PhysicalFood = coordinationMode == "food" ? deliveryCell.GetThingList(map).Where(t => t.def == harvestProduct).Sum(t => t.stackCount) : 0,
            PhysicalBuildings = map.listerThings.AllThings.OfType<Verse.Building>().Count(b => b.Faction == Faction.OfPlayer && (b is Building_Bed || b.def == ThingDefOf.Wall)),
            Active = coordinationParticipants.Count(id => map.mapPawns.AllPawnsSpawned.Any(p => p.GetUniqueLoadID() == id && NativePawnActivities.Read(p)?.Method == coordinationMethod)),
        });
    }, ct).ConfigureAwait(false);
}
