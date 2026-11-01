using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>What a consumer provides and what a day of its interruption costs.</summary>
public sealed record ConsumerValue(UtilityService Service, InterruptionLoss Loss);

/// <summary>
/// The prices the power comparison reads, valued from the power step's own reading: each consumer's interruption by building id,
/// each piece the alternatives build (a generator, the battery, a conduit, a shelter's wall, door and roof) by definition, and the
/// unpowered thermal provisions that could take over a powered temperature control. A consumer or piece it lacks is unassessed,
/// naming the fact (SelectPowerMethod).
/// </summary>
public sealed record PowerValuation
{
    public IReadOnlyDictionary<string, ConsumerValue> Consumers { get; init; } = new Dictionary<string, ConsumerValue>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ProvisionOption> Pieces { get; init; } = new Dictionary<string, ProvisionOption>(StringComparer.Ordinal);

    /// <summary>The unpowered thermal provisions that could replace a powered temperature control, with their steady temperature.</summary>
    public IReadOnlyList<PassiveProvision> Passive { get; init; } = [];
}

/// <summary>
/// Values the power network's consumers and pieces (task 6.4, RG-HABITAT-008), each consumer by the valuation its own milestone
/// prices it with:
/// <list type="bullet">
/// <item>a cooler holding perishable stock: the spoilage its cooling prevents (ForecastFreezer.SpoilageAvoided);</item>
/// <item>a bench: the bill throughput it loses unpowered (<see cref="WorkshopLoss"/>, the recipe-flow run value);</item>
/// <item>a bedroom's heater or cooler: its sleepers' mood leaving the comfortable band at the room's steady temperature without
/// it (<see cref="SleepLoss"/>, the housing valuation's flat mood gain);</item>
/// <item>a medical facility linked to an occupied medical bed: the patients' queued operations at the bed's factor without it
/// (<see cref="CareLoss"/>, CareCapacityRules.SurgeryLossOf).</item>
/// </list>
/// Every other consumer (a lamp, a turret) is unassessed, naming the service model the price lacks. A piece is its work and cost
/// list in its cheapest common stuff (LayoutClusterRules.PiecePrice), ready after its work over the colony's builders
/// (ProspectiveCapacityRules.PieceOption); material delivery time is not read.
/// </summary>
public static class PowerValuationRules
{
    /// <summary>The ticks a second a heat pusher's heatPerSecond is spread over (CompHeatPusher pushes it every second).</summary>
    private const double TicksPerSecond = 60;

    /// <summary>
    /// The materials <see cref="Valuation"/> prices: the pieces' and unpowered thermal burners' cost lists, the burners' fuel, the
    /// perishable stocks' definitions and the products of every bill on a bench.
    /// </summary>
    public static IReadOnlyList<Resource> Materials(ReviewFacts f, IEnumerable<string> pieces) =>
        [.. pieces.Concat(Burners(f).Select(d => d.Name)).Select(name => ColonyPlanningRules.HostPiece(f, name)).Where(piece => piece is not null).SelectMany(piece => piece!.Value.Costs.Select(a => a.Resource))
            .Concat(Burners(f).SelectMany(d => d.Fuels))
            .Concat(f.FoodStorageUpkeep.Stocks.Where(s => s.Stock.Rot is FoodRot.Rots).Select(s => new Resource(s.Stock.DefName)))
            .Concat(f.EssentialInputs.Benches.SelectMany(b => b.Observed.Bills).Select(b => b.Recipe).Where(f.ProductionRecipes.ContainsKey)
                .SelectMany(r => f.ProductionRecipes[r].Products.Select(p => p.Resource)))
            .Distinct()];

    /// <summary>The valuation of every powered consumer on the map, every piece in <paramref name="pieces"/> and the passive provisions, at <paramref name="values"/>.</summary>
    public static PowerValuation Valuation(ReviewFacts f, IEnumerable<string> pieces, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values)
    {
        var builders = LayoutClusterRules.BuildersOf(f.ForecastObserved.WorkPawns);
        var priced = new Dictionary<string, ProvisionOption>(StringComparer.Ordinal);
        foreach (var name in pieces.Distinct(StringComparer.Ordinal))
        {
            if (ColonyPlanningRules.HostPiece(f, name) is { } piece
                && ProspectiveCapacityRules.PieceOption(name, piece.Work, LayoutClusterRules.PiecePrice(piece, values), builders) is { } option)
                priced[name] = option;
        }
        var thermal = f.ForecastObserved.Coupled?.Thermal;
        // A shelter's roof is its cells' roofing, worked at the fastest roofer's ticks a cell (no material).
        if (thermal is { Workers.Count: > 0 } && ProspectiveCapacityRules.PieceOption(PowerRules.RoofPiece, thermal.Workers.Min(w => w.RoofCellWorkTicks), 0, builders) is { } roof)
            priced[PowerRules.RoofPiece] = roof;
        var consumers = new Dictionary<string, ConsumerValue>(StringComparer.Ordinal);
        var buildings = f.EssentialInputs.Power.Buildings.Where(b => b.BaseW < 0).ToArray();
        if (buildings.Length == 0) return new() { Pieces = priced };
        var rooms = f.Upkeep.Rooms;
        var cellRoom = new Dictionary<Cell, RoomState>();
        foreach (var room in rooms.Rooms)
        {
            foreach (var c in rooms.CellsOf(room)) cellRoom[c] = room;
        }
        var coolers = RefrigerationRules.CoolerDefinitions(f.Stations).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var heaters = f.Stations.Temperature.Where(d => d.Heats).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var benches = f.EssentialInputs.Benches.ToDictionary(b => b.Observed.Bench, b => b.Observed, StringComparer.Ordinal);
        // The cell a thermal control acts on: a cooler's cold side, a heater's own.
        Cell ActsOn(PowerSite b) => coolers.Contains(b.Definition) ? RefrigerationRules.ColdSide(b.Cell, ConstructionOwnershipRules.BuildingOf(b, b.Definition).Rotation) : b.Cell;
        var controls = buildings.Where(b => coolers.Contains(b.Definition) || heaters.Contains(b.Definition)).ToArray();
        var thermalRooms = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in f.EssentialInputs.Power.Buildings.Where(b => coolers.Contains(b.Definition) || f.Stations.Temperature.Any(d => d.Name == b.Definition)))
        {
            if (!cellRoom.TryGetValue(ActsOn(b), out var room)) continue;
            if (!thermalRooms.TryGetValue(room.Id, out var ids)) thermalRooms[room.Id] = ids = [];
            ids.Add(b.Id);
        }
        var sleepers = (f.ForecastObserved.Needs?.Pawns ?? []).Where(p => p.OwnedBed.Length != 0).ToArray();
        var gains = BedroomGainFacts.Of(f);
        var stock = ResourceStockRules.Count(f.Census.Resources);
        var butchery = ForecastWorkshop.ButcheryRunValueOf(f.CombinedFoodSupply.Stocks, f.Acquisition, f.FoodWork);
        var passive = new List<PassiveProvision>();
        foreach (var b in buildings)
        {
            var def = b.Definition;
            if (controls.Contains(b))
            {
                if (!cellRoom.TryGetValue(ActsOn(b), out var room))
                {
                    consumers[b.Id] = Unassessed(UtilityService.Unmodelled, "room thermal loss:" + b.Id);
                    continue;
                }
                var alone = !thermalRooms.TryGetValue(room.Id, out var devices) || devices.All(id => id == b.Id);
                bool Removed(ThermalSource s) => s.Cell == b.Cell || (s.HeatPerTick < 0 && s.BuildCell == b.Cell);
                if (room.Role == RoomRole.Bedroom)
                {
                    var beds = room.BedIds().ToHashSet(StringComparer.Ordinal);
                    var owners = sleepers.Where(p => beds.Contains(p.OwnedBed)).OrderBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
                    var without = Steady(room, rooms, thermal, Removed, null, alone, f.Census.OutdoorTemperatureC);
                    consumers[b.Id] = new(UtilityService.Sleep, SleepLoss(owners, room.TemperatureC, without, gains));
                    if (owners.Length != 0) passive.AddRange(Passive(f, b, ActsOn(b), coolers.Contains(def), room, rooms, thermal, Removed, owners, values, builders));
                }
                else consumers[b.Id] = coolers.Contains(def) ? Cooling(f, room, values) : Unassessed(UtilityService.Unmodelled, "room thermal loss:" + room.Id);
            }
            else if (benches.TryGetValue(b.Id, out var bench))
            {
                consumers[b.Id] = new(UtilityService.Workshop, WorkshopLoss(bench, f.WorkBenchSpeeds, f.ProductionRecipes, values, stock, butchery, b.Powered));
            }
            else if (def == f.HospitalMonitor.Def && f.HospitalMonitor.Def.Length != 0)
            {
                consumers[b.Id] = new(UtilityService.Care, f.ForecastObserved.Coupled?.Support is { } support
                    ? CareLoss(b.Cell, f.HospitalMonitor, support, f.MedicalPawns, ForecastValuePolicy.For(f.Preferences), b.Powered)
                    : new InterruptionLoss.Unassessed("care census"));
            }
            else if (b.Turret is not null)
            {
                consumers[b.Id] = Unassessed(UtilityService.Unmodelled, "turret defence:" + def);
            }
        }
        return new() { Consumers = consumers, Pieces = priced, Passive = passive };
    }

    /// <summary>
    /// What a day of <paramref name="bench"/> standing unpowered costs, by milestone 3's recipe-flow valuation: its active bills
    /// (neither suspended nor finished) in stack order share the runs a worker makes there in a day (a day's ticks at the bench's
    /// speed over the recipe's work), each taking what it still asks that day: a forever bill all of it, a count bill its remaining
    /// iterations, a target bill the batches its product's stock is short of the target. A run is worth its products at their
    /// values (ForecastWorkshop.RunValueOf) less the labor it takes; a def that runs no bills unpowered loses that net value, one
    /// that runs slower unpowered (BenchPowerPart) the labor the slower speed adds. Unassessed naming an unread speed, recipe or value.
    /// </summary>
    public static InterruptionLoss WorkshopLoss(BillStackRow bench, IReadOnlyDictionary<string, BenchSpeedModel> speeds, IReadOnlyDictionary<string, RecipeFlow> recipes,
        IReadOnlyDictionary<Resource, ConstructionMaterialValue> values, IReadOnlyDictionary<Resource, long> stock, double butchery, bool poweredNow)
    {
        var active = bench.Bills.Where(b => !b.Suspended && !b.Finished).ToArray();
        if (active.Length == 0 || !bench.Usable || !bench.Reachable) return new InterruptionLoss.PerDay(0);
        if (!speeds.TryGetValue(bench.BenchDef, out var model)) return new InterruptionLoss.Unassessed("workbench speed:" + bench.BenchDef);
        var unpowered = model.RequiresPower ? 0 : model.Parts.OfType<BenchPowerPart>().Aggregate(1.0, (factor, part) => factor * part.Unpowered);
        // The read stat already carries the unpowered factor where the bench stands unpowered now.
        var speed = !poweredNow && unpowered > 0 ? bench.WorkSpeed / unpowered : bench.WorkSpeed;
        if (!double.IsFinite(speed) || speed <= 0) return new InterruptionLoss.Unassessed("workbench speed:" + bench.BenchDef);
        double loss = 0, day = 1; // The share of the worker's day the earlier bills leave.
        foreach (var bill in active)
        {
            if (!recipes.TryGetValue(bill.Recipe, out var recipe)) return new InterruptionLoss.Unassessed("workshop recipe:" + bill.Recipe);
            if (!(recipe.Work > 0) || !double.IsFinite(recipe.Work)) return new InterruptionLoss.Unassessed("workshop recipe work:" + bill.Recipe);
            var capacity = day * GameTime.TicksPerDay * speed / recipe.Work;
            double asked;
            if (bill.RepeatMode == RimGovernor.Brain.GameFacts.Operations.RepeatMode.Forever) asked = capacity;
            else if (bill.RepeatMode == RimGovernor.Brain.GameFacts.Operations.RepeatMode.Count) asked = Math.Max(0, bill.RepeatCount);
            else if (bill.RepeatMode == RimGovernor.Brain.GameFacts.Operations.RepeatMode.Target)
            {
                if (recipe.Products.Count != 1 || !(recipe.Products[0].Count > 0)) return new InterruptionLoss.Unassessed("workshop stock target product:" + bill.Recipe);
                var product = recipe.Products[0];
                asked = Math.Max(0, bill.TargetCount - stock.GetValueOrDefault(product.Resource)) / product.Count;
            }
            else throw new DomainException("bill repeat mode is not a bench mode: " + bill.RepeatMode);
            var runs = Math.Min(capacity, asked);
            if (runs <= 0) continue;
            day -= runs * recipe.Work / speed / GameTime.TicksPerDay;
            var unread = recipe.Butchery ? double.IsFinite(butchery) ? "" : "butchery_run_value"
                : recipe.Products.Where(p => !values.ContainsKey(p.Resource)).Select(p => "workshop product value:" + p.Resource).FirstOrDefault() ?? "";
            if (unread.Length != 0) return new InterruptionLoss.Unassessed(unread);
            var value = ForecastWorkshop.RunValueOf(recipe, values, butchery);
            var labor = recipe.Work / speed / GameTime.TicksPerDay;
            var net = Math.Max(0, value - labor);
            loss += runs * (unpowered > 0 ? Math.Min(net, (recipe.Work / (speed * unpowered) / GameTime.TicksPerDay) - labor) : net);
            if (day <= 0) break;
        }
        return new InterruptionLoss.PerDay(loss);
    }

    /// <summary>
    /// What a day of a bedroom's heater or cooler stopping costs, by milestone 2's housing valuation: each sleeper's sleep
    /// temperature memory (slept_in_cold or slept_in_heat, ApplyBedThoughts) at the room's steady temperature without it against
    /// the memory at its temperature now, priced through the flat mood gain (BedroomGainFacts.MoodGain); a sleeper it warms or
    /// cools out of its band only. Unassessed naming an unread steady temperature, memory row or mood.
    /// </summary>
    public static InterruptionLoss SleepLoss(IReadOnlyList<NeedsForecastPawn> sleepers, double now, PassiveSteady without, BedroomGainFacts gains)
    {
        if (sleepers.Count == 0) return new InterruptionLoss.PerDay(0);
        if (without is PassiveSteady.Unread { Fact: var fact }) return new InterruptionLoss.Unassessed(fact);
        var steady = ((PassiveSteady.Read)without).TemperatureC;
        double loss = 0;
        foreach (var p in sleepers)
        {
            if (!ForecastHousing.SleepTemperatureRead(p, now) || !ForecastHousing.SleepTemperatureRead(p, steady))
                return new InterruptionLoss.Unassessed("sleep temperature thought:" + p.Pawn);
            var delta = ForecastHousing.SleepTemperatureThought(p, steady) - ForecastHousing.SleepTemperatureThought(p, now);
            if (delta == 0) continue;
            if (gains.MoodGain(p.Pawn, delta) is not { } gain) return new InterruptionLoss.Unassessed("sleep mood:" + p.Pawn);
            loss += Math.Max(0, -gain);
        }
        return new InterruptionLoss.PerDay(loss);
    }

    /// <summary>
    /// What a medical facility at <paramref name="at"/> standing unpowered costs, by milestone 4's care valuation, once per pending
    /// surgery (OnceDays: each queued surgery bill on a patient is done once, and none queued costs nothing): for every
    /// patient lying in a medical bed within its link distance, the queued operations' loss there (CareCapacityRules.SurgeryLossOf)
    /// at the bed's SurgerySuccessChanceFactor without the facility's offset over the loss with it. CompFacility applies the offset
    /// only while the facility is powered, so the bed's read factor carries it where the facility is <paramref name="poweredNow"/>
    /// and lacks it where it stands unpowered now: the powered factor is then the read one plus the offset, and the unpowered one
    /// the read factor itself. Unassessed naming an unread bed factor, or the immunity gain a patient with an immunizable
    /// condition loses (its illness course is not priced here).
    /// </summary>
    public static InterruptionLoss CareLoss(Cell at, FacilityLink link, SupportActivityRead support, IReadOnlyList<CarePawn> pawns, ForecastValuePolicy value, bool poweredNow)
    {
        var request = new CareCapacityRequest { Support = support, Value = value };
        var conditions = pawns.GroupBy(p => p.Id.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Conditions, StringComparer.Ordinal);
        double loss = 0;
        foreach (var patient in support.Patients.OrderBy(p => p.Patient, StringComparer.Ordinal))
        {
            var bed = patient.Beds.FirstOrDefault(b => b.Medical && b.Occupants.Contains(patient.Patient)
                && Math.Sqrt(((b.Location.X - at.X) * (b.Location.X - at.X)) + ((b.Location.Z - at.Z) * (b.Location.Z - at.Z))) <= link.MaxDistance);
            if (bed is null) continue;
            if (link.ImmunityGainSpeedOffset > 0 && conditions.TryGetValue(patient.Patient, out var hediffs) && hediffs.Any(h => h.Immunizable is not null))
                return new InterruptionLoss.Unassessed("immunity gain:" + patient.Patient);
            var surgeries = support.Demands.Where(d => d.Kind == "surgery" && d.Target == patient.Patient).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            if (surgeries.Length == 0) continue;
            if (bed.SurgerySuccessFactor is CareSurgeryFactor.Unread { Fact: var fact }) return new InterruptionLoss.Unassessed(fact);
            var factor = ((CareSurgeryFactor.Read)bed.SurgerySuccessFactor).Factor;
            var (withOffset, without) = poweredNow ? (factor, Math.Max(0, factor - link.SurgerySuccessChanceOffset)) : (factor + link.SurgerySuccessChanceOffset, factor);
            var (powered, _) = CareCapacityRules.SurgeryLossOf(request, surgeries, bed with { SurgerySuccessFactor = new CareSurgeryFactor.Read(withOffset) });
            var (unpowered, _) = CareCapacityRules.SurgeryLossOf(request, surgeries, bed with { SurgerySuccessFactor = new CareSurgeryFactor.Read(without) });
            loss += Math.Max(0, unpowered - powered);
        }
        return new InterruptionLoss.PerDay(0) { OnceDays = loss };
    }

    /// <summary>
    /// <paramref name="room"/>'s steady temperature (ForecastShelterPolicy.SteadyTemperature over the observed thermal read) with
    /// its supplied complete sources less the <paramref name="removed"/> ones, plus <paramref name="added"/>. A room with no other
    /// source and nothing added settles at the outdoors (the balance's no-source limit) whatever its exchange; otherwise unread
    /// naming the room when the read does not sample it as a rectangle.
    /// </summary>
    internal static PassiveSteady Steady(RoomState room, RoomObservation rooms, CoupledThermalRead? read, Func<ThermalSource, bool> removed, ThermalSource? added, bool alone, double outdoors)
    {
        var interior = rooms.CellsOf(room);
        var fallback = alone && added is null ? new PassiveSteady.Read(read?.Outdoors ?? outdoors) : (PassiveSteady)new PassiveSteady.Unread("room thermal:" + room.Id);
        if (read is null || interior.Count == 0) return fallback;
        var x = interior.Min(c => c.X); var z = interior.Min(c => c.Z);
        var bounds = new Rectangle(x, z, interior.Max(c => c.X) - x + 1, interior.Max(c => c.Z) - z + 1);
        if (bounds.Width * bounds.Height != interior.Count) return fallback;
        var observed = read.Cells.ToDictionary(c => c.Cell);
        if (Methods.Generation.CoupledShelterCandidates.Estimate(interior.ToArray(), Methods.Generation.CoupledShelterCandidates.Ring(bounds), observed, read)
            is not ForecastShelterThermal.Estimated estimated) return fallback;
        var inside = interior.ToHashSet();
        ThermalSource[] sources = [.. read.Sources.Where(s => s.Complete && inside.Contains(s.Cell) && !removed(s) && HeatSupplies.Supplied(s.Supply, out _)), .. added is null ? [] : new[] { added }];
        var plan = new ForecastShelterPlan(new(ForecastShelterSource.Observed, room.Id), EquatableArray.From(interior), [], estimated, EquatableArray.From(sources), []);
        return new PassiveSteady.Read(ForecastShelterPolicy.SteadyTemperature(plan));
    }

    // The unpowered thermal burners the catalog admits (a campfire, a passive cooler): no power, no thermostat, a fuel.
    private static IEnumerable<TemperatureDefinition> Burners(ReviewFacts f) => f.Stations.Temperature.Where(d => !d.Controlled && d.PowerW == 0 && d.Fuels.Count > 0);

    /// <summary>
    /// Each available unpowered burner that pushes the way a bedroom's powered control does, standing where the control acts:
    /// the room's steady temperature with it in place of the control (<see cref="Steady"/>), the sleepers' band (the tightest of
    /// their comfortable ranges), and its price: the piece, plus its fuel over the comparison's horizon at the fuel's value.
    /// </summary>
    private static IEnumerable<PassiveProvision> Passive(ReviewFacts f, PowerSite control, Cell at, bool cooling, RoomState room, RoomObservation rooms, CoupledThermalRead? read,
        Func<ThermalSource, bool> removed, IReadOnlyList<NeedsForecastPawn> sleepers, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values, int builders)
    {
        var low = sleepers.Max(p => p.SleepComfortableMin);
        var high = sleepers.Min(p => p.SleepComfortableMax);
        foreach (var d in Burners(f).Where(d => d.Heats != cooling && ColonyPlanningRules.DefinitionAvailable(f, d.Name)).OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var fuel = d.Fuels[0];
            var source = new ThermalSource(d.Name, at, true, d.HeatPerSecond / TicksPerSecond, d.Heats ? d.MaximumC : d.MinimumC,
                new HeatSupply.Burning(fuel.Value, d.FuelItemsPerDay / GameTime.TicksPerDay)) { Definition = d.Name };
            PassiveSteady steady;
            double cost = 0, lead = 0;
            if (ColonyPlanningRules.HostPiece(f, d.Name) is not { } piece
                || ProspectiveCapacityRules.PieceOption(d.Name, piece.Work, LayoutClusterRules.PiecePrice(piece, values), builders) is not { } option)
                steady = new PassiveSteady.Unread(PowerRules.BuildPriceFact + d.Name);
            else if (!values.TryGetValue(fuel, out var price)) steady = new PassiveSteady.Unread("fuel value:" + fuel.Value);
            else
            {
                steady = Steady(room, rooms, read, removed, source, false, f.Census.OutdoorTemperatureC);
                cost = option.CostDays + (d.FuelItemsPerDay * price.PerUnit * ColonyModelPolicy.DefaultHorizonDays);
                lead = option.LeadDays;
            }
            yield return new PassiveProvision(control.Id, steady, low, high, cost, lead) { Definition = d.Name };
        }
    }

    // A cooler's interruption outside a bedroom: the spoilage its cooling prevents in the room its cold side faces, every
    // perishable stock there warming from its current rot rate to the outdoor one.
    private static ConsumerValue Cooling(ReviewFacts f, RoomState room, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values)
    {
        var stocks = f.FoodStorageUpkeep.Stocks.Select(s => s.Stock).Where(s => s.Place is FoodPlace.Stands { Room: var at } && at == room.Id).ToArray();
        if (stocks.Length == 0) return Unassessed(UtilityService.Unmodelled, "room thermal loss:" + room.Id);
        var outdoorRate = ForecastFreezer.RotRate(f.RotRates, f.Census.OutdoorTemperatureC);
        return new(UtilityService.Refrigeration, new InterruptionLoss.PerDay(stocks.Sum(s => ForecastFreezer.SpoilageAvoided(s, outdoorRate, s.RotRate, values))));
    }

    private static ConsumerValue Unassessed(UtilityService service, string fact) => new(service, new InterruptionLoss.Unassessed(fact));
}
