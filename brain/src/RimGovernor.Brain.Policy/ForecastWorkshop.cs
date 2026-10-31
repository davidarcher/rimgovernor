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

    internal static IReadOnlyList<RoomGain> Credit(ForecastRoomService room, ForecastCoupledOutcome outcome,
        IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (!context.LayoutRooms.TryGetValue(room.Room, out var layout)) throw new RequiredFactException("workshop layout:" + room.Room);
        if (layout.Production.Count == 0) return [];
        var ledger = context.ProductionOrders ?? throw new RequiredFactException("workshop production orders");
        if (ledger.Status is not (WorkRules.LedgerViewReconciled or WorkRules.LedgerViewInactive)) throw new RequiredFactException("workshop production orders:" + ledger.Status);
        if (ledger.Orders.Count == 0) return [];
        // An order naming the bench kind runs on standing benches of it already; one naming none has no host but the planned bench (RG-HABITAT-005).
        var useful = layout.Production.Where(p => p.RunsPerDay > 0).Select(p =>
        {
            var hosted = UsefulRuns(p, room, outcome, context, [.. ledger.Orders.Where(o => o.BenchKind == p.Bench)]);
            return (Load: p, Hosted: hosted, Unhosted: Math.Min(p.RunsPerDay - hosted, UsefulRuns(p, room, outcome, context, [.. ledger.Orders.Where(o => o.BenchKind.Length == 0)])));
        }).Where(p => p.Hosted + p.Unhosted > 0).ToArray();
        if (useful.Length == 0) return [];
        if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { } closure) return [];
        var closed = Closed(context, closure);
        var shelter = context.Shelters.First(s => s.PlannedRoom == room.Room || s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == room.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var outside = ((ForecastShelterThermal.Estimated)shelter.Thermal).Outdoors;
        var gains = new List<RoomGain>();
        foreach (var (load, runs, unhosted) in useful)
        {
            if (!context.WorkBenchSpeeds.TryGetValue(load.Bench, out var model) || !double.IsFinite(model.Base) || model.Base <= 0) throw new RequiredFactException("workbench speed:" + load.Bench);
            var benches = closure.Prediction.WorkBenches.Where(b => b.Def == load.Bench).ToArray();
            if (benches.Length == 0) throw new RequiredFactException("workshop planned benches:" + load.Bench);
            double speedGain = 0, speed = 0;
            foreach (var bench in benches)
            {
                var powered = Powered(context, bench);
                var served = model.Working(closed with { Powered = powered }, bench.WorkOffset);
                speed += served / benches.Length;
                if (runs <= 0) continue;
                double now;
                if (bench.Placement is BenchPlacement.Standing standing) now = model.RequiresPower && !powered ? 0 : standing.WorkFactor;
                else if (bench.Placement is BenchPlacement.Planned) now = model.Working(BenchRoom.Outside(outside, powered));
                else throw new RequiredFactException("workbench placement:" + load.Bench);
                if (!double.IsFinite(now) || now < 0) throw new RequiredFactException("workbench factor:" + load.Bench);
                speedGain += (served - now) / model.Base / benches.Length;
            }
            double value = 0;
            foreach (var product in Products(context, load))
            {
                if (!context.MaterialValues.TryGetValue(product.Resource, out var price)) throw new RequiredFactException("workshop product value:" + product.Resource);
                value += product.Count * price.PerUnit;
            }
            // Useful work is already the demand owner's daily load. A speed gain cannot sell more than that unmet load.
            var gain = Math.Min(1, speedGain) * runs * value;
            if (unhosted > 0 && speed > 0)
            {
                // Nothing makes an unhosted load but this bench: its useful runs are produced whole, less the labor its
                // speed in this room asks (the recipe's work at unit speed over the factor), the same capability-days.
                var work = context.ProductionRecipes[load.Recipe].Work;
                if (!double.IsFinite(work) || work <= 0) throw new RequiredFactException("workshop recipe work:" + load.Recipe);
                gain += unhosted * (value - (work / speed / GameTime.TicksPerDay));
            }
            gains.Add(new(load.Bench + "/" + load.Recipe, "", gain));
        }
        return gains;
    }

    /// <summary>
    /// Whether <paramref name="bench"/> of a predicted room is powered (RG-HABITAT-005): it needs no power, or its network's
    /// read surplus covers the draw every predicted bench on that network adds up to it, the benches joined by network id
    /// once each (by def and anchor) in anchor x, z then def order. An unconnected bench is unpowered: a known zero for a def
    /// that runs no bills unpowered (<see cref="BenchSpeedModel.RequiresPower"/>), never an unread draw.
    /// </summary>
    internal static bool Powered(ForecastCoupledContext context, PlannedBenchSpeed bench)
    {
        if (bench.Power is BenchPower.Unneeded) return true;
        if (bench.Power is not BenchPower.Connected connected) return false; // Unconnected: no network reaches it.
        if (!double.IsFinite(connected.SurplusWatts)) throw new RequiredFactException("workbench power surplus:" + connected.Network);
        var draw = 0.0;
        foreach (var shared in context.Closures.SelectMany(c => c.Prediction.WorkBenches)
            .Where(b => b.Power is BenchPower.Connected c && c.Network == connected.Network).DistinctBy(b => (b.Def, b.Position))
            .OrderBy(b => b.Position.X).ThenBy(b => b.Position.Z).ThenBy(b => b.Def, StringComparer.Ordinal))
        {
            var watts = ((BenchPower.Connected)shared.Power).AdditionalWatts;
            if (!double.IsFinite(watts) || watts < 0) throw new RequiredFactException("workbench power draw:" + shared.Def);
            draw += watts;
            if (shared.Def == bench.Def && shared.Position == bench.Position) return draw <= connected.SurplusWatts;
        }
        throw new RequiredFactException("workbench power network:" + bench.Def);
    }

    private static double UsefulRuns(PlannedProduction load, ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, IReadOnlyList<OrderSpec> orders)
    {
        var matching = orders.Where(o => o.Recipe == load.Recipe && o.Target > 0).ToArray();
        if (matching.Length == 0) return 0;
        var products = Products(context, load);
        var batch = products.Sum(p => p.Count);
        if (!(batch > 0)) throw new RequiredFactException("workshop recipe products:" + load.Recipe);
        var terminal = outcome.Terminal ?? throw new RequiredFactException("workshop terminal stock");
        var finished = context.Bindings.Where(b => b.Value.Kind == PawnActivityKind.Recipe && b.Value.Recipe?.Recipe == load.Recipe && terminal.Finished.ContainsKey(b.Key.Task))
            .Select(b => b.Key.Task).Distinct(StringComparer.Ordinal).Count();
        var creditDays = RingClosure.CreditDays(context.InvestmentHorizonDays, room.ServedAt);
        if (creditDays <= 0) return 0;
        double runs = 0;
        foreach (var order in matching)
        {
            if (WorkRules.WireClass(order.Mode) == BillMode.StockTarget)
            {
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
        var total = context.LayoutRooms.Values.SelectMany(r => r.Production).Where(p => p.Bench == load.Bench && p.Recipe == load.Recipe).Sum(p => p.RunsPerDay);
        return Math.Min(load.RunsPerDay, runs * load.RunsPerDay / total);
    }
}
