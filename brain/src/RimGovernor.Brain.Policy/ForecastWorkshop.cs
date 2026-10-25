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
        var useful = layout.Production.Where(p => p.RunsPerDay > 0).Select(p => (Load: p, Runs: UsefulRuns(p, room, outcome, context, ledger.Orders))).Where(p => p.Runs > 0).ToArray();
        if (useful.Length == 0) return [];
        if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { } closure) return [];
        var closed = Closed(context, closure);
        var shelter = context.Shelters.First(s => s.PlannedRoom == room.Room || s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == room.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var outside = ((ForecastShelterThermal.Estimated)shelter.Thermal).Outdoors;
        var gains = new List<RoomGain>();
        foreach (var (load, runs) in useful)
        {
            if (!context.WorkBenchSpeeds.TryGetValue(load.Bench, out var model) || !double.IsFinite(model.Base) || model.Base <= 0) throw new RequiredFactException("workbench speed:" + load.Bench);
            var benches = closure.Prediction.WorkBenches.Where(b => b.Def == load.Bench).ToArray();
            if (benches.Length == 0) throw new RequiredFactException("workshop planned benches:" + load.Bench);
            double speedGain = 0;
            foreach (var bench in benches)
            {
                double now;
                if (bench.Placement is BenchPlacement.Standing standing) now = standing.WorkFactor;
                else if (bench.Placement is BenchPlacement.Planned) now = model.At(BenchRoom.Outside(outside, bench.Powered));
                else throw new RequiredFactException("workbench placement:" + load.Bench);
                if (!double.IsFinite(now) || now < 0) throw new RequiredFactException("workbench factor:" + load.Bench);
                speedGain += (model.At(closed with { Powered = bench.Powered }, bench.WorkOffset) - now) / model.Base / benches.Length;
            }
            double value = 0;
            foreach (var product in Products(context, load))
            {
                if (!context.MaterialValues.TryGetValue(product.Resource, out var price)) throw new RequiredFactException("workshop product value:" + product.Resource);
                value += product.Count * price.PerUnit;
            }
            // Useful work is already the demand owner's daily load. A speed gain cannot sell more than that unmet load.
            gains.Add(new(load.Bench + "/" + load.Recipe, "", Math.Min(1, speedGain) * runs * value));
        }
        return gains;
    }

    private static double UsefulRuns(PlannedProduction load, ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, IReadOnlyList<OrderSpec> orders)
    {
        var matching = orders.Where(o => o.Recipe == load.Recipe && o.Target > 0 && (o.BenchKind.Length == 0 || o.BenchKind == load.Bench)).ToArray();
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
