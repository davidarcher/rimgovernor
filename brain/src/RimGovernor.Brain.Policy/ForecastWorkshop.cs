using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Useful production gained by the stations the served workshop houses (#3801).</summary>
internal static class ForecastWorkshop
{
    internal static IReadOnlyList<Amount> Products(ForecastCoupledContext context, PlannedProduction load) =>
        context.ProductionRecipes.TryGetValue(load.Recipe, out var recipe) ? recipe.Products
            : throw new RequiredFactException("workshop recipe:" + load.Recipe);

    internal static BenchRoom Closed(ForecastCoupledContext context, ForecastPlannedClosure closure)
    {
        if (!context.LayoutRooms.TryGetValue(closure.Room, out var planned)) throw new RequiredFactException("planned room layout:" + closure.Room);
        var shelter = context.Shelters.FirstOrDefault(s => s.PlannedRoom == closure.Room
            || s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == closure.Room.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new RequiredFactException("bench room shelter:" + closure.Room);
        var role = Wire.TryParse<RoomRole>(closure.Prediction.Role, out var named) ? named : RoomRole.Room;
        return BenchRoom.Closed(role, PlannedRoomCleanlinessRules.Steady(closure.Prediction, planned.FilthRuns.Values, context.Filth).Dirty,
            ForecastShelterPolicy.SteadyTemperature(shelter), closure.Prediction.ReadingBonus, false);
    }

    /// <summary>
    /// The useful output the room's stations gain (#3801, RG-HABITAT-005). An order naming the bench kind runs on standing
    /// benches of it already, so only the room's speed gain is credited; one naming none has no host but the planned bench,
    /// whose whole output less its labor and haul is. Every non-bridge production of a recipe shares its one demand, split
    /// by planned runs, so a second host (<see cref="HostStep.Share"/>) or a second bench never doubles it. A chain's bridge
    /// (<see cref="HostStep.Bridge"/>) is priced with its replacement: an unhosted load's useful units are spread from the
    /// bridge's readiness (it stands, or its planned piece finishes) and the bridge makes them alone until the replacement's
    /// room serves; a kind-named load the replacement takes over from its standing bridge earns the labor the faster bench saves.
    /// </summary>
    internal static IReadOnlyList<RoomGain> Credit(ForecastRoomService room, ForecastCoupledOutcome outcome,
        IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (!context.LayoutRooms.TryGetValue(room.Room, out var layout)) throw new RequiredFactException("workshop layout:" + room.Room);
        if (layout.Production.Count == 0) return [];
        var ledger = context.ProductionOrders ?? throw new RequiredFactException("workshop production orders");
        if (ledger.Status is not (WorkRules.LedgerViewReconciled or WorkRules.LedgerViewInactive)) throw new RequiredFactException("workshop production orders:" + ledger.Status);
        if (ledger.Orders.Count == 0) return [];
        var all = context.LayoutRooms.OrderBy(r => r.Key).SelectMany(r => r.Value.Production.Select(p => (Room: r.Key, Load: p))).ToArray();
        PlannedProduction? Host(string recipe) => all.Select(p => p.Load).FirstOrDefault(p => p.Recipe == recipe && p.Step == HostStep.Host);
        (int Room, PlannedProduction Load)? BridgeOf(PlannedProduction p) => p.Step != HostStep.Host ? null
            : all.Where(b => b.Load.Recipe == p.Recipe && b.Load.Step == HostStep.Bridge && b.Load.Bench != p.Bench).Select(b => ((int, PlannedProduction)?)b).FirstOrDefault();
        bool Bridged(PlannedProduction p) => p.Step == HostStep.Bridge && Host(p.Recipe) is { } h && h.Bench != p.Bench;
        string[] Kinds(PlannedProduction p) => p.Step == HostStep.Share && Host(p.Recipe) is { } h ? [p.Bench, h.Bench] : [p.Bench];
        var creditDays = RingClosure.CreditDays(context.InvestmentHorizonDays, room.ServedAt);
        var useful = layout.Production.Where(p => p.RunsPerDay > 0 && !Bridged(p)).Select(p =>
        {
            var kinds = Kinds(p);
            var sharing = all.Select(q => q.Load).Where(q => q.Recipe == p.Recipe && !Bridged(q)).ToArray();
            var hostedTotal = sharing.Where(q => Kinds(q).Intersect(kinds).Any()).Sum(q => q.RunsPerDay);
            var hosted = UsefulRuns(p, outcome, context, [.. ledger.Orders.Where(o => kinds.Contains(o.BenchKind) && (p.Step != HostStep.Share || o.Stocked()))], creditDays, hostedTotal);
            var bridge = BridgeOf(p);
            var ready = bridge is { } b ? Readiness(context, outcome, b.Room, b.Load) : null;
            // An unhosted load's useful units spread over the days from the bridge's readiness to the horizon: the bridge makes them until this room serves.
            var lead = ready is { } r ? Math.Max(0, Math.Min(room.ServedAt / GameTime.TicksPerDay, context.InvestmentHorizonDays) - (r / GameTime.TicksPerDay)) : 0;
            var total = sharing.Sum(q => q.RunsPerDay);
            var unhosted = Math.Min(p.RunsPerDay - hosted, UsefulRuns(p, outcome, context, [.. ledger.Orders.Where(o => o.BenchKind.Length == 0)], creditDays + lead, total));
            var upgraded = bridge is { } k && !kinds.Contains(k.Load.Bench)
                ? Math.Min(p.RunsPerDay - hosted - unhosted, UsefulRuns(p, outcome, context, [.. ledger.Orders.Where(o => o.BenchKind == k.Load.Bench)], creditDays, total)) : 0;
            return (Load: p, Hosted: hosted, Unhosted: unhosted, Upgraded: upgraded, Bridge: bridge, Lead: lead);
        }).Where(p => p.Hosted + p.Unhosted + p.Upgraded > 0).ToArray();
        if (useful.Length == 0) return [];
        if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { } closure) return [];
        var closed = Closed(context, closure);
        var shelter = context.Shelters.First(s => s.PlannedRoom == room.Room || s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == room.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var outside = ((ForecastShelterThermal.Estimated)shelter.Thermal).Outdoors;
        var gains = new List<RoomGain>();
        var joined = new HashSet<(string, Cell)>();
        foreach (var (load, runs, unhosted, upgraded, bridge, lead) in useful)
        {
            var model = SpeedModel(context, load.Bench);
            var benches = closure.Prediction.WorkBenches.Where(b => b.Def == load.Bench).ToArray();
            if (benches.Length == 0) throw new RequiredFactException("workshop planned benches:" + load.Bench);
            double speedGain = 0, speed = 0, fetch = 0, connection = 0;
            foreach (var bench in benches)
            {
                var powered = Powered(context, bench);
                if (powered && bench.Power is BenchPower.Unconnected { Nearest: { } reach } && joined.Add((bench.Def, bench.Position)))
                {
                    // Powered only through the line its nearest network needs: that line is the bench's to lay.
                    connection += ConnectionDays(context, reach);
                }
                fetch += (bench.Storage?.Cells ?? 0) / (double)benches.Length; // No storage cell: nothing to haul to.
                var served = model.Working(closed with { Powered = powered }, bench.WorkOffset);
                speed += served / benches.Length;
                if (runs <= 0) continue;
                speedGain += (served - Now(model, bench, outside, powered)) / model.Base / benches.Length;
            }
            var value = RunValue(context, load);
            // Useful work is already the demand owner's daily load. A speed gain cannot sell more than that unmet load.
            var gain = Math.Min(1, speedGain) * runs * value;
            if ((unhosted > 0 || upgraded > 0) && speed > 0)
            {
                var work = RecipeWork(context, load.Recipe);
                // Nothing makes an unhosted load but this bench: its useful runs are produced whole, less the labor its
                // speed in this room asks (the recipe's work at unit speed over the factor) and the walk to the nearest
                // storage and back over which a run's ingredients and product are hauled, the same capability-days.
                gain += unhosted * (value - (work / speed / GameTime.TicksPerDay) - HaulDays(fetch));
                if (upgraded > 0 && bridge is { } k)
                {
                    // The standing bridge makes these runs now: the replacement saves the labor its speed spares.
                    var (from, _) = BridgeBench(context, k.Load, outside);
                    if (!(from > 0)) throw new RequiredFactException("workbench factor:" + k.Load.Bench);
                    gain += upgraded * ((work / from) - (work / speed)) / GameTime.TicksPerDay;
                }
            }
            gains.Add(new(load.Bench + "/" + load.Recipe, "", gain));
            if (connection > 0) gains.Add(new(load.Bench + "/" + load.Recipe + "/power_connection", "", 0) { Once = -connection });
            if (unhosted > 0 && lead > 0 && bridge is { } bridged)
            {
                // The bridge makes the same daily runs alone over the lead, at its own speed where it works before its room closes.
                var (at, haul) = BridgeBench(context, bridged.Load, outside);
                if (at > 0)
                {
                    var work = RecipeWork(context, load.Recipe);
                    gains.Add(new(load.Bench + "/" + load.Recipe + "/bridge/" + bridged.Load.Bench, "", 0)
                        { Once = unhosted * lead * (value - (work / at / GameTime.TicksPerDay) - HaulDays(haul)) });
                }
            }
        }
        return gains;
    }

    private static BenchSpeedModel SpeedModel(ForecastCoupledContext context, string def) =>
        context.WorkBenchSpeeds.TryGetValue(def, out var model) && double.IsFinite(model.Base) && model.Base > 0 ? model : throw new RequiredFactException("workbench speed:" + def);

    private static double RecipeWork(ForecastCoupledContext context, string recipe) =>
        context.ProductionRecipes[recipe].Work is var work && double.IsFinite(work) && work > 0 ? work : throw new RequiredFactException("workshop recipe work:" + recipe);

    /// <summary>What the bench makes now: a standing one its read factor (none unpowered where it needs power), a planned one what it would make outside.</summary>
    private static double Now(BenchSpeedModel model, PlannedBenchSpeed bench, double outside, bool powered)
    {
        double now;
        if (bench.Placement is BenchPlacement.Standing standing) now = model.RequiresPower && !powered ? 0 : standing.WorkFactor;
        else if (bench.Placement is BenchPlacement.Planned) now = model.Working(BenchRoom.Outside(outside, powered));
        else throw new RequiredFactException("workbench placement:" + bench.Def);
        return double.IsFinite(now) && now >= 0 ? now : throw new RequiredFactException("workbench factor:" + bench.Def);
    }

    /// <summary>A chain's bridge bench (any predicted room's row of its definition): its speed now and its storage walk. No row throws naming it.</summary>
    private static (double Speed, double Fetch) BridgeBench(ForecastCoupledContext context, PlannedProduction bridge, double outside)
    {
        var bench = context.Closures.SelectMany(c => c.Prediction.WorkBenches).FirstOrDefault(b => b.Def == bridge.Bench)
            ?? throw new RequiredFactException("workshop planned benches:" + bridge.Bench);
        return (Now(SpeedModel(context, bridge.Bench), bench, outside, Powered(context, bench)), bench.Storage?.Cells ?? 0);
    }

    /// <summary>
    /// The tick a chain's bridge first serves at: at once where a bench of it stands (a predicted room reads it standing),
    /// else when its planned piece's site finishes in the outcome; null when neither happens within the forecast, so it
    /// bridges nothing.
    /// </summary>
    private static double? Readiness(ForecastCoupledContext context, ForecastCoupledOutcome outcome, int room, PlannedProduction bridge)
    {
        if (context.Closures.SelectMany(c => c.Prediction.WorkBenches).Any(b => b.Def == bridge.Bench && b.Placement is BenchPlacement.Standing)) return 0;
        var terminal = outcome.Terminal ?? throw new RequiredFactException("workshop terminal stock");
        var prefix = PlannedRules.PlannedPieceSitePrefix(room, bridge.Bench);
        var finishes = context.PlannedSites.Where(s => s.Room == room && s.Site.StartsWith(prefix, StringComparison.Ordinal))
            .SelectMany(s => s.Finishes).Where(terminal.Finished.ContainsKey).Select(f => terminal.Finished[f]).ToArray();
        return finishes.Length == 0 ? null : finishes.Min();
    }

    /// <summary>The capability-days of one run's fetch and haul: to the nearest storage and back over <paramref name="cells"/>, at a cardinal step a cell.</summary>
    internal static double HaulDays(double cells) => 2 * cells * DefenseCorridorRoutes.PathMoveCardinal / GameTime.TicksPerDay;

    /// <summary>
    /// What one run of <paramref name="load"/> yields in capability-days: its products at their material values, or a
    /// butchering's meat (<see cref="ForecastCoupledContext.ButcheryRunValue"/>, whose recipe lists no product).
    /// </summary>
    internal static double RunValue(ForecastCoupledContext context, PlannedProduction load)
    {
        var products = Products(context, load);
        if (context.ProductionRecipes[load.Recipe].Butchery) return double.IsFinite(context.ButcheryRunValue) ? context.ButcheryRunValue : throw new RequiredFactException("butchery_run_value");
        double value = 0;
        foreach (var product in products)
        {
            if (!context.MaterialValues.TryGetValue(product.Resource, out var price)) throw new RequiredFactException("workshop product value:" + product.Resource);
            value += product.Count * price.PerUnit;
        }
        return value;
    }

    /// <summary>
    /// The mean meat a butchering run yields (<see cref="ForecastCoupledContext.ButcheryRunValue"/>): over the unforbidden animal
    /// corpses in stock, else over the huntable food prey the hunt channels are offered on (a corpse to come), at the crop work a
    /// nutrition costs. NaN (unread) with neither, or with no crop to price nutrition.
    /// </summary>
    internal static double ButcheryRunValueOf(IReadOnlyList<FoodStock> stocks, IReadOnlyList<AcquisitionSource> prey, FoodWorkFacts food)
    {
        if (!(food.GrowTicksPerNutrition > 0)) return double.NaN;
        var corpses = stocks.Where(s => s.IsCorpse() && !s.IsHumanlike && !s.Forbidden).Select(s => s.Nutrition).ToArray();
        var meat = corpses.Length != 0 ? corpses : prey.Where(s => s.Hunt && s.Food && !s.Tree).Select(s => s.NutritionYield).ToArray();
        return meat.Length == 0 ? double.NaN : meat.Average() * food.GrowTicksPerNutrition / GameTime.TicksPerDay;
    }

    /// <summary>
    /// The capability-days of joining an unconnected bench to its nearest network: <see cref="NetworkReach.Cells"/> conduits,
    /// each its work to build and its cost at the materials' values. An unread conduit or price throws naming it.
    /// </summary>
    internal static double ConnectionDays(ForecastCoupledContext context, NetworkReach reach)
    {
        if (reach.Cells == 0) return 0;
        var conduit = context.PowerConduit ?? throw new RequiredFactException("power_conduit_construction");
        var cell = conduit.WorkToBuild / GameTime.TicksPerDay;
        foreach (var cost in conduit.Costs)
        {
            if (!context.MaterialValues.TryGetValue(cost.Resource, out var price)) throw new RequiredFactException("power conduit material value:" + cost.Resource);
            cell += cost.Count * price.PerUnit;
        }
        return reach.Cells * cell;
    }

    /// <summary>The conduit's materials, charged where a predicted bench is joined through a line to its nearest network.</summary>
    internal static IEnumerable<Resource> ConnectionMaterials(ForecastCoupledContext context) =>
        context.PowerConduit is { } conduit && context.Closures.SelectMany(c => c.Prediction.WorkBenches).Any(b => b.Power is BenchPower.Unconnected { Nearest.Cells: > 0 })
            ? conduit.Costs.Select(c => c.Resource) : [];

    /// <summary>
    /// Whether <paramref name="bench"/> of a predicted room is powered (RG-HABITAT-005): it needs no power, or the network it
    /// is on (or, unconnected, the nearest one a conduit line would join it to, whose line the credit charges) has a read
    /// surplus covering the draw every predicted bench on that network adds up to it, the benches joined by network id once
    /// each (by def and anchor) in anchor x, z then def order. A bench with no transmitting network on the map is unpowered:
    /// a known zero for a def that runs no bills unpowered (<see cref="BenchSpeedModel.RequiresPower"/>), never an unread draw.
    /// </summary>
    internal static bool Powered(ForecastCoupledContext context, PlannedBenchSpeed bench)
    {
        if (bench.Power is BenchPower.Unneeded) return true;
        if (Supply(bench.Power) is not var (network, _, surplus)) return false; // No network on the map.
        if (!double.IsFinite(surplus)) throw new RequiredFactException("workbench power surplus:" + network);
        var draw = 0.0;
        foreach (var shared in context.Closures.SelectMany(c => c.Prediction.WorkBenches)
            .Where(b => Supply(b.Power)?.Network == network).DistinctBy(b => (b.Def, b.Position))
            .OrderBy(b => b.Position.X).ThenBy(b => b.Position.Z).ThenBy(b => b.Def, StringComparer.Ordinal))
        {
            var watts = Supply(shared.Power)!.Value.Watts;
            if (!double.IsFinite(watts) || watts < 0) throw new RequiredFactException("workbench power draw:" + shared.Def);
            draw += watts;
            if (shared.Def == bench.Def && shared.Position == bench.Position) return draw <= surplus;
        }
        throw new RequiredFactException("workbench power network:" + bench.Def);
    }

    /// <summary>The network a bench draws from, the draw it adds and the network's surplus: its own, or the nearest one an unconnected bench would join.</summary>
    private static (string Network, double Watts, double Surplus)? Supply(BenchPower power)
    {
        if (power is BenchPower.Connected c) return (c.Network, c.AdditionalWatts, c.SurplusWatts);
        if (power is BenchPower.Unconnected { Nearest: { } r }) return (r.Network, r.AdditionalWatts, r.SurplusWatts);
        return null; // Needs none, or no network on the map.
    }

    /// <summary>
    /// The useful daily runs of <paramref name="load"/>'s share of <paramref name="orders"/> over <paramref name="days"/>: the
    /// demand spread over those days, split by planned runs over the <paramref name="total"/> of every production sharing it.
    /// </summary>
    private static double UsefulRuns(PlannedProduction load, ForecastCoupledOutcome outcome, ForecastCoupledContext context, IReadOnlyList<OrderSpec> orders, double days, double total)
    {
        var matching = orders.Where(o => o.Recipe == load.Recipe && o.Target > 0).ToArray();
        if (matching.Length == 0) return 0;
        var products = Products(context, load);
        var terminal = outcome.Terminal ?? throw new RequiredFactException("workshop terminal stock");
        var finished = context.Bindings.Where(b => b.Value.Kind == PawnActivityKind.Recipe && b.Value.Recipe?.Recipe == load.Recipe && terminal.Finished.ContainsKey(b.Key.Task))
            .Select(b => b.Key.Task).Distinct(StringComparer.Ordinal).Count();
        var creditDays = days;
        if (creditDays <= 0 || !(total > 0)) return 0;
        double runs = 0;
        foreach (var order in matching)
        {
            if (WorkRules.WireClass(order.Mode) == BillMode.StockTarget)
            {
                if (!(products.Sum(p => p.Count) > 0)) throw new RequiredFactException("workshop recipe products:" + load.Recipe);
                // Native target-count bills without a declarer's resource context count their recipe's sole product.
                var product = !order.Product.IsEmpty ? order.Product : products.Count == 1 ? products[0].Resource
                    : throw new RequiredFactException("workshop stock target product:" + order.Recipe);
                var productBatch = products.Where(p => p.Resource == product).Sum(p => p.Count);
                if (!(productBatch > 0)) throw new RequiredFactException("workshop stock target yield:" + order.Recipe);
                var usefulUnits = Math.Max(0, order.Target - terminal.Stocks.GetValueOrDefault(product))
                    + terminal.Drawn.GetValueOrDefault(product);
                var bounded = usefulUnits / creditDays;
                runs += (order.Class == ResourceClass.None ? bounded : Math.Min(BenchRateRules.DemandRate(usefulUnits, order.Class), bounded)) / productBatch;
            }
            else runs += Math.Max(0, order.Target - finished) / creditDays;
        }
        return Math.Min(load.RunsPerDay, runs * load.RunsPerDay / total);
    }
}
