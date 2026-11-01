using System.Globalization;
using RimGovernor.Brain.GameFacts.Observations;
using BuildingRow = RimGovernor.Brain.GameFacts.Observations.BuildingState;

namespace RimGovernor.Brain.Policy;

// Servicing work as measured activities (task 5.1 of redefine-habitat-requirements): each free colonist's repair, breakdown
// fix, refuel or door-to-wall replacement of a building that needs it, priced from the native servicing read's mechanics,
// supplies and per-pawn abilities and joined to the building's canonical row for its condition and the service it provides.
// The maintenance choice (ServicingChoiceRules, task 5.2) and power fuel/repair chains (task 5.3) compare these; nothing here orders work.

/// <summary>A resource a servicing work consumes, or a replacement recovers, and its expected count.</summary>
public sealed record ServicingInput(Resource Resource, double Count);

/// <summary>
/// The service a target's deficit costs, read from its canonical building row. <see cref="Condition"/>: hit points only, the
/// building still serves (RimWorld buildings work at any hit points above zero) and the deficit exposes it to destruction.
/// <see cref="PowerOutput"/>: a producer's rated watts (BuildingPower.Trader.RatedW) its network lacks. <see cref="Pending"/>: the
/// fuel left lasts <c>Ticks</c> at the row's burn rate, then <c>Then</c> is lost. <see cref="Enclosure"/>: a replacement leaves
/// the cell open for <c>Ticks</c> between the door's removal and the wall's completion. <see cref="Unmodelled"/>: the building's
/// service is not modelled, its state named ("service:&lt;def&gt;", "turret service:&lt;def&gt;", "fuel use rate:&lt;def&gt;").
/// </summary>
public abstract record ServiceLoss
{
    private protected ServiceLoss() { }

    public sealed record Condition(int MissingHitPoints, int MaximumHitPoints) : ServiceLoss;

    public sealed record PowerOutput(string Network, double Watts) : ServiceLoss;

    public sealed record Pending(double Ticks, ServiceLoss Then) : ServiceLoss;

    public sealed record Enclosure(Cell Cell, double Ticks) : ServiceLoss;

    public sealed record Unmodelled(string State) : ServiceLoss;
}

/// <summary>
/// One pawn's servicing of one target: its <see cref="Duration"/> in ticks (walks, carries and native work at the pawn's
/// measured rate), the <see cref="Inputs"/> it consumes and the <see cref="ServiceLoss"/> it restores (or, for a replacement,
/// the transition loss it causes). <see cref="WorkActive"/> is whether native-led labor serves it under the pawn's current work
/// settings; <see cref="InHomeArea"/> whether the native giver accepts the target (repair and fix require the home area).
/// </summary>
public abstract record ServicingWork(string Target, string Pawn, bool WorkActive, bool InHomeArea)
{
    public abstract ServicingKind Kind { get; }
    public abstract double Duration { get; }
    public abstract EquatableArray<ServicingInput> Inputs { get; }
    public required ServiceLoss ServiceLoss { get; init; }
}

/// <summary>Restoring <paramref name="HitPoints"/> by JobDriver_Repair: the walk, then <paramref name="WorkTicks"/>; consumes nothing.</summary>
public sealed record RepairWork(string Target, string Pawn, bool WorkActive, bool InHomeArea, int HitPoints, double ApproachTicks, double WorkTicks)
    : ServicingWork(Target, Pawn, WorkActive, InHomeArea)
{
    public override ServicingKind Kind => ServicingKind.Repair;
    public override double Duration => ApproachTicks + WorkTicks;
    public override EquatableArray<ServicingInput> Inputs => [];
}

/// <summary>
/// One attempt of JobDriver_FixBrokenDownBuilding: fetch a component, carry it to the target and work; the component is
/// consumed whether the attempt succeeds (<paramref name="SuccessChance"/>) or not.
/// </summary>
public sealed record FixBreakdownWork(string Target, string Pawn, bool WorkActive, bool InHomeArea, Resource Component, double FetchTicks,
    double CarryTicks, double WorkTicks, double SuccessChance) : ServicingWork(Target, Pawn, WorkActive, InHomeArea)
{
    public override ServicingKind Kind => ServicingKind.FixBreakdown;
    public override double Duration => FetchTicks + CarryTicks + WorkTicks;
    public override EquatableArray<ServicingInput> Inputs => [new(Component, 1)];
}

/// <summary>
/// Carrying <paramref name="Items"/> of <paramref name="Fuel"/> in <paramref name="Trips"/> loads (JobDriver_Refuel: fetch, carry,
/// wait <paramref name="RefuelTicks"/> per load, walk back for the next). <paramref name="NativeStarts"/> is whether native
/// auto-refuel takes it now (auto refuel allowed and the fuel at or below Props.autoRefuelPercent of its target).
/// </summary>
public sealed record RefuelWork(string Target, string Pawn, bool WorkActive, bool InHomeArea, Resource Fuel, int Items, int Trips,
    double FetchTicks, double CarryTicks, double ReturnTicks, double RefuelTicks, bool NativeStarts) : ServicingWork(Target, Pawn, WorkActive, InHomeArea)
{
    public override ServicingKind Kind => ServicingKind.Refuel;
    public override double Duration => FetchTicks + Trips * (CarryTicks + RefuelTicks) + (Trips - 1) * ReturnTicks;
    public override EquatableArray<ServicingInput> Inputs => [new(Fuel, Items)];
}

/// <summary>
/// Swapping the target for <paramref name="Replacement"/>: the walk, the deconstruction (JobDriver_Deconstruct's work), the
/// delivery of its <see cref="Inputs"/> and the build (WorkToBuild in the stuff at the stuff's ConstructionSpeedFactor).
/// <paramref name="Recovered"/> is the expected Deconstruct leavings (each cost times resourcesFractionWhenDeconstructed);
/// <see cref="NetInputs"/> what the swap costs once they are back.
/// </summary>
public sealed record ReplacementWork(string Target, string Pawn, bool WorkActive, string Replacement, EquatableArray<ServicingInput> Costs,
    EquatableArray<ServicingInput> Recovered, double ApproachTicks, double DeconstructTicks, double DeliveryTicks, double BuildTicks)
    : ServicingWork(Target, Pawn, WorkActive, true)
{
    public override ServicingKind Kind => ServicingKind.Replace;
    public override double Duration => ApproachTicks + DeconstructTicks + DeliveryTicks + BuildTicks;
    public override EquatableArray<ServicingInput> Inputs => Costs;
    public EquatableArray<ServicingInput> NetInputs => [.. Costs.Select(c => c with
        { Count = c.Count - Recovered.Where(r => r.Resource == c.Resource).Sum(r => r.Count) })];
}

/// <summary>
/// One way to service a target: <see cref="Assessed"/> with its work, <see cref="Excluded"/> for a known reason it cannot be
/// done (no capable worker, no supply, a native refusal), or <see cref="Unassessed"/> naming the required fact left unread.
/// </summary>
public abstract record ServicingOption(string Target, ServicingKind Kind, string Pawn)
{
    public sealed record Assessed(ServicingWork Work) : ServicingOption(Work.Target, Work.Kind, Work.Pawn);

    public sealed record Excluded(string Target, ServicingKind Kind, string Pawn, string Reason) : ServicingOption(Target, Kind, Pawn);

    public sealed record Unassessed(string Target, ServicingKind Kind, string Pawn, string Fact) : ServicingOption(Target, Kind, Pawn);
}

/// <summary>A servicing read's target joined to its canonical building row by id (the same id, def and cell in both).</summary>
public sealed record ServicingTargetJoin(ServicingTarget Target, BuildingRow Building);

public static class ServicingRules
{
    /// <summary>The excluded reasons.</summary>
    public const string NoWorker = "no_worker", NoSupply = "no_supply", NoStock = "no_stock", CannotCarry = "cannot_carry",
        ZeroRate = "zero_rate", NotRepairable = "not_repairable", RefuelNotEmpty = "refuel_not_empty";

    /// <summary>The unread fact of a relocation haul the game has not walked yet (ReconcileRules.UnprobedHauls names the leg to read).</summary>
    public const string HaulUnprobed = "haul_unprobed";

    /// <summary>
    /// The building's servicing target in the read, joined by id: a building the read does not list is a required fact
    /// unread (<c>servicing target:&lt;id&gt;</c>); a listed target whose def or cell is not the row's is a contract violation.
    /// </summary>
    public static ServicingTargetJoin Join(ServicingActivityRead? read, BuildingRow building)
    {
        if (read is null) throw new RequiredFactException("servicing read");
        var id = building.Building.Id;
        var listed = read.Targets.Where(t => t.Id == id).ToArray();
        if (listed.Length > 1) throw new DomainException($"servicing target {id} is listed {listed.Length} times");
        if (listed.Length == 0) throw new RequiredFactException("servicing target:" + id);
        var target = listed[0];
        var cell = building.Building.Place is EntityPlace.OnMap onMap ? onMap.Cell : (Cell?)null;
        if (target.Definition != building.Building.DefName || target.Cell != cell)
            throw new DomainException($"servicing target {id} is {target.Definition} at {target.Cell}, its row {building.Building.DefName} at {cell}");
        return new(target, building);
    }

    /// <summary>
    /// Every way to service the building now: one option per worker row of each kind it needs (a repair for missing hit
    /// points, a fix when broken down, a refuel when not full) or offers (a replacement where one is read), and an
    /// <see cref="NoWorker"/> exclusion for a kind with no worker row.
    /// </summary>
    public static IReadOnlyList<ServicingOption> OptionsOf(ServicingActivityRead? read, BuildingRow building)
    {
        var join = Join(read, building);
        var options = new List<ServicingOption>();
        foreach (var kind in KindsOf(join))
        {
            var rows = read!.Workers.Where(w => w.Target == join.Target.Id && w.Kind == kind).OrderBy(w => w.Pawn, StringComparer.Ordinal).ToArray();
            if (rows.Length == 0) options.Add(new ServicingOption.Excluded(join.Target.Id, kind, "", NoWorker));
            foreach (var worker in rows) options.Add(OptionOf(read, join, worker));
        }
        return options;
    }

    /// <summary>
    /// Every option of every target the read lists, each joined to its canonical row by <paramref name="row"/>; a listed
    /// target without a row is a required fact unread (<c>building row:&lt;id&gt;</c>).
    /// </summary>
    public static IReadOnlyList<ServicingOption> Survey(ServicingActivityRead read, Func<string, BuildingRow?> row) =>
        [.. read.Targets.SelectMany(t => OptionsOf(read, row(t.Id) ?? throw new RequiredFactException("building row:" + t.Id)))];

    /// <summary>
    /// One measured work in words for a Method's reason or a hold's detail: its ticks, inputs, the service it restores (or the
    /// transition it opens), and its kind's own measurement.
    /// </summary>
    public static string DescribeWork(ServicingWork work)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{work.Kind} by {work.Pawn}: {work.Duration:0} ticks, inputs {DescribeInputs(work.Inputs)}, {DescribeLoss(work.ServiceLoss)}");
        if (!work.WorkActive) text += ", work type inactive";
        if (!work.InHomeArea) text += ", outside home area";
        if (work is RepairWork repair) text += string.Create(CultureInfo.InvariantCulture, $", {repair.HitPoints} hit points");
        if (work is FixBreakdownWork fix) text += string.Create(CultureInfo.InvariantCulture, $", success {fix.SuccessChance:0.##}");
        if (work is RefuelWork refuel && !refuel.NativeStarts) text += ", above the native auto-refuel threshold";
        if (work is ReplacementWork swap) text += $", becomes {swap.Replacement}, net {DescribeInputs(swap.NetInputs)}";
        return text;
    }

    private static string DescribeInputs(EquatableArray<ServicingInput> inputs) => inputs.Count == 0 ? "none"
        : string.Join(" ", inputs.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Count:0.##} {i.Resource}")));

    private static string DescribeLoss(ServiceLoss loss)
    {
        if (loss is ServiceLoss.Condition c) return string.Create(CultureInfo.InvariantCulture, $"restores {c.MissingHitPoints}/{c.MaximumHitPoints} hit points");
        if (loss is ServiceLoss.PowerOutput p) return string.Create(CultureInfo.InvariantCulture, $"restores {p.Watts:0} W on {p.Network}");
        if (loss is ServiceLoss.Pending pending) return string.Create(CultureInfo.InvariantCulture, $"fuel lasts {pending.Ticks:0} ticks, then {DescribeLoss(pending.Then)}");
        if (loss is ServiceLoss.Enclosure e) return string.Create(CultureInfo.InvariantCulture, $"opens {e.Cell.X},{e.Cell.Z} for {e.Ticks:0} ticks");
        if (loss is ServiceLoss.Unmodelled u) return "unmodelled " + u.State;
        throw new DomainException("unknown service loss");
    }

    /// <summary>The kinds the joined target needs or offers, from its row's condition and the read's replacement.</summary>
    public static IReadOnlyList<ServicingKind> KindsOf(ServicingTargetJoin join)
    {
        var kinds = new List<ServicingKind>();
        if (join.Building.HitPoints is BuildingHitPoints.Tracked hp && hp.Current < hp.Maximum) kinds.Add(ServicingKind.Repair);
        if (join.Building.Service.BrokenDown) kinds.Add(ServicingKind.FixBreakdown);
        if (join.Target.Fuel is ServicingFuel.Refuel { Full: false }) kinds.Add(ServicingKind.Refuel);
        if (join.Target.Replacement is ServicingReplacement.Wall) kinds.Add(ServicingKind.Replace);
        return kinds;
    }

    /// <summary>One worker row's option: its work, or why it is excluded, or the required fact it lacks.</summary>
    public static ServicingOption OptionOf(ServicingActivityRead read, ServicingTargetJoin join, ServicingWorker worker)
    {
        try
        {
            return Excluded(read, join, worker) is { } reason
                ? new ServicingOption.Excluded(join.Target.Id, worker.Kind, worker.Pawn, reason)
                : new ServicingOption.Assessed(WorkOf(read, join, worker));
        }
        catch (RequiredFactException e)
        {
            return new ServicingOption.Unassessed(join.Target.Id, worker.Kind, worker.Pawn, e.Fact);
        }
    }

    private static string? Excluded(ServicingActivityRead read, ServicingTargetJoin join, ServicingWorker worker)
    {
        if (worker.Kind == ServicingKind.Repair && !join.Target.Repairable) return NotRepairable;
        if (worker.Kind != ServicingKind.Repair && worker.Supply is null) return NoSupply;
        if (worker.Ability is ServicingAbility.Construction { Speed: <= 0 }) return ZeroRate;
        if (worker.Kind == ServicingKind.Refuel)
        {
            var fuel = FuelOf(join);
            if (join.Target.Fuel is ServicingFuel.Refuel { AllowRefuelIfNotEmpty: false } && fuel.Fuel > 0) return RefuelNotEmpty;
            if (worker.Supply!.Capacity <= 0) return CannotCarry;
            if (StockOf(read, worker.Supply.Resource) <= 0) return NoStock;
        }
        if (worker.Kind == ServicingKind.Replace && worker.Supply!.Capacity <= 0) return CannotCarry;
        return null;
    }

    /// <summary>
    /// The worker row's measured work. Repair: the native's hit points come one per <c>RepairWorkPerHitPoint</c> of work after
    /// <c>RepairWarmupWork</c>, at ConstructionSpeed x <c>WorkPerSpeed</c> a tick and at most one a tick. Fix: one attempt's
    /// fetch, carry and <c>FixTicks</c>. Refuel: the items to full (at most the stock) in loads of the pawn's carry capacity.
    /// Replacement: deconstruct, deliver the wall's cost and build it. Throws <see cref="RequiredFactException"/> for an unread
    /// fact the work needs: "repair rate:&lt;def&gt;", "fix success chance:&lt;def&gt;", "construction rate:&lt;def&gt;",
    /// "hit points:&lt;def&gt;", "fuel:&lt;def&gt;", "fuel def:&lt;def&gt;", "fuel stock:&lt;resource&gt;", "cost list:&lt;def&gt;",
    /// "wall delivery:&lt;resource&gt;".
    /// </summary>
    public static ServicingWork WorkOf(ServicingActivityRead read, ServicingTargetJoin join, ServicingWorker worker)
    {
        var mechanics = read.Mechanics;
        var target = join.Target;
        var building = join.Building;
        var def = target.Definition;
        switch (worker.Kind)
        {
            case ServicingKind.Repair:
            {
                var hp = building.HitPoints as BuildingHitPoints.Tracked ?? throw new RequiredFactException("hit points:" + def);
                var rate = SpeedOf(worker, "repair rate:" + def) * mechanics.WorkPerSpeed;
                var missing = hp.Maximum - hp.Current;
                return new RepairWork(target.Id, worker.Pawn, worker.WorkActive, target.Home, missing, worker.ApproachTicks,
                    RepairTicks(mechanics, missing, rate)) { ServiceLoss = new ServiceLoss.Condition(missing, hp.Maximum) };
            }
            case ServicingKind.FixBreakdown:
            {
                if (worker.Ability is ServicingAbility.Unread unread) throw new RequiredFactException($"fix success chance:{def} ({unread.Fact})");
                var chance = (worker.Ability as ServicingAbility.Fixing ?? throw new DomainException("fix row without a fixing ability")).SuccessChance;
                var supply = worker.Supply!;
                return new FixBreakdownWork(target.Id, worker.Pawn, worker.WorkActive, target.Home, new(mechanics.Component), supply.FetchTicks,
                    supply.CarryTicks, mechanics.FixTicks, chance) { ServiceLoss = FunctionOf(building) };
            }
            case ServicingKind.Refuel:
            {
                var fuel = FuelOf(join);
                if (building.FuelDefinitions.Count == 0) throw new RequiredFactException("fuel def:" + def);
                var need = target.Fuel as ServicingFuel.Refuel ?? throw new RequiredFactException("fuel:" + def);
                var supply = worker.Supply!;
                var items = Math.Min(need.Items, StockOf(read, supply.Resource));
                var trips = (items + supply.Capacity - 1) / supply.Capacity;
                var allowed = fuel.AutoRefuel is not AutoRefuelControl.Switch { Allowed: false };
                return new RefuelWork(target.Id, worker.Pawn, worker.WorkActive, target.Home, new(supply.Resource), items, trips, supply.FetchTicks,
                    supply.CarryTicks, supply.ReturnTicks, mechanics.RefuelTicks, allowed && fuel.Fuel <= fuel.AutoRefuelPercent * fuel.TargetFuel)
                { ServiceLoss = fuel.OutOfFuel ? FunctionOf(building) : RunwayOf(building, fuel) };
            }
            case ServicingKind.Replace:
            {
                var wall = (ServicingReplacement.Wall)target.Replacement;
                if (target.Removal is ServicingRemoval.Unread unread) throw new RequiredFactException(unread.Fact);
                var removal = (ServicingRemoval.Read)target.Removal;
                var speed = SpeedOf(worker, "construction rate:" + def) * mechanics.WorkPerSpeed;
                var supply = worker.Supply!;
                var delivered = wall.Costs.Sum(c => c.Resource == supply.Resource ? c.Count
                    : throw new RequiredFactException("wall delivery:" + c.Resource));
                var trips = (delivered + supply.Capacity - 1) / supply.Capacity;
                var delivery = trips == 0 ? 0 : supply.FetchTicks + trips * supply.CarryTicks + (trips - 1) * supply.ReturnTicks;
                var build = wall.WorkToBuild / (speed * wall.StuffSpeedFactor);
                return new ReplacementWork(target.Id, worker.Pawn, worker.WorkActive, wall.Definition,
                    [.. wall.Costs.Select(c => new ServicingInput(new(c.Resource), c.Count))],
                    [.. removal.Costs.Select(c => new ServicingInput(new(c.Resource), Math.Min(c.Count * removal.RefundFraction, c.Count)))],
                    worker.ApproachTicks, removal.DeconstructWork / speed, delivery, build) { ServiceLoss = new ServiceLoss.Enclosure(target.Cell, build) };
            }
        }
        throw new DomainException("unknown servicing kind " + worker.Kind);
    }

    /// <summary>
    /// JobDriver_Repair's ticks for <paramref name="hitPoints"/> at <paramref name="rate"/> work a tick: the n-th hit point comes
    /// once <c>RepairWarmupWork + (n - 1) x RepairWorkPerHitPoint</c> work is done, and at most one hit point comes a tick.
    /// </summary>
    public static double RepairTicks(ServicingMechanics mechanics, int hitPoints, double rate)
    {
        if (hitPoints <= 0) return 0;
        var first = Math.Ceiling(mechanics.RepairWarmupWork / rate);
        return rate <= mechanics.RepairWorkPerHitPoint
            ? Math.Ceiling((mechanics.RepairWarmupWork + (hitPoints - 1) * mechanics.RepairWorkPerHitPoint) / rate)
            : first + hitPoints - 1;
    }

    /// <summary>
    /// What the building provides that a breakdown or empty tank stops: a producer's rated watts on its network; a turret's or
    /// any other building's service is not modelled and is named.
    /// </summary>
    public static ServiceLoss FunctionOf(BuildingRow building)
    {
        if (building.Service.Power is BuildingPower.Trader { RatedW: > 0 } producer) return new ServiceLoss.PowerOutput(producer.Network ?? "", producer.RatedW);
        return new ServiceLoss.Unmodelled((building.Turret is null ? "service:" : "turret service:") + building.Building.DefName);
    }

    /// <summary>The service a refuelable still holding fuel keeps until its burn at the row's rate empties it.</summary>
    public static ServiceLoss RunwayOf(BuildingRow building, BuildingFuel.Refuelable fuel)
    {
        if (fuel.Burn is { UseDriven: false, PerDay: > 0 }) return new ServiceLoss.Pending(fuel.Fuel / fuel.Burn.PerDay * GameTime.TicksPerDay, FunctionOf(building));
        return new ServiceLoss.Unmodelled("fuel use rate:" + building.Building.DefName);
    }

    private static BuildingFuel.Refuelable FuelOf(ServicingTargetJoin join) =>
        join.Building.Service.Fuel as BuildingFuel.Refuelable ?? throw new RequiredFactException("fuel:" + join.Target.Definition);

    private static double SpeedOf(ServicingWorker worker, string fact)
    {
        if (worker.Ability is ServicingAbility.Unread unread) throw new RequiredFactException($"{fact} ({unread.Fact})");
        return (worker.Ability as ServicingAbility.Construction ?? throw new DomainException("construction row without a construction ability")).Speed;
    }

    private static int StockOf(ServicingActivityRead read, string resource) =>
        read.Stock.Where(s => s.Resource == resource).Select(s => (int?)s.Count).SingleOrDefault() ?? throw new RequiredFactException("fuel stock:" + resource);
}
