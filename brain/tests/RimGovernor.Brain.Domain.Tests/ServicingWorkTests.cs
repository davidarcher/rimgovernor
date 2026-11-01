using RimGovernor.Brain.GameFacts.Observations;
using BuildingState = RimGovernor.Brain.GameFacts.Observations.BuildingState;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-008/010 (task 5.1): repair, breakdown fix, refuel and door-to-wall replacement are measured activities priced
/// from the native servicing read (JobDriver_Repair's warmup and per-hit-point work, JobDriver_Refuel's per-load wait, the
/// carry legs and capacity, the wall's cost and build work, Deconstruct's refund) and joined to the canonical building row for
/// the deficit's service effect. The bug was the categorical servicing holds: repair and replacement were never priced, so a
/// damaged wall, an empty generator and a door that should be a wall could not be compared, and an unread input was never named.
/// </summary>
public sealed class ServicingWorkTests
{
    private static readonly Cell At = new(4, 7);

    private static BuildingState Row(string id = "Thing_Wall1", string def = "Wall", BuildingHitPoints? hp = null, BuildingPower? power = null,
        BuildingFuel? fuel = null, bool brokenDown = false, string[]? fuelDefs = null, Cell? cell = null) =>
        PowerBuildingFixtures.Trader(id, def, cell ?? At, 0, 0, false) with
        {
            HitPoints = hp ?? new BuildingHitPoints.Tracked { Current = 150, Maximum = 300 },
            Service = BuildingServiceWire.NoService with { Power = power ?? new BuildingPower.NoPower(), Fuel = fuel ?? new BuildingFuel.NotRefuelable(), BrokenDown = brokenDown },
            FuelDefinitions = [.. fuelDefs ?? []],
        };

    private static ServicingTarget Target(string id = "Thing_Wall1", string def = "Wall", ServicingFuel? fuel = null, ServicingRemoval? removal = null,
        ServicingReplacement? replacement = null, Cell? cell = null, bool repairable = true) => new(id, def, cell ?? At)
    {
        Home = true, Repairable = repairable, Fuel = fuel ?? new ServicingFuel.NotRefuelable(),
        Removal = removal ?? new ServicingRemoval.Read([new("Steel", 5)], 0.5, 135),
        Replacement = replacement ?? new ServicingReplacement.None("not_a_door"),
    };

    private static ServicingWorker Worker(ServicingKind kind, ServicingAbility ability, ServicingSupply? supply = null, string target = "Thing_Wall1", string pawn = "Thing_Human1") =>
        new(target, kind, pawn, 30) { WorkActive = true, Ability = ability, Supply = supply };

    private static ServicingActivityRead Read(ServicingTarget target, ServicingWorker[] workers, ServicingStock[]? stock = null, string[]? findings = null) =>
        CoupledFixtures.NoServicing with { Targets = [target], Workers = [.. workers], Stock = [.. stock ?? []], Findings = [.. findings ?? []] };

    private static ServicingWork Assessed(ServicingActivityRead read, BuildingState row) =>
        Assert.IsType<ServicingOption.Assessed>(Assert.Single(ServicingRules.OptionsOf(read, row))).Work;

    private static string Unassessed(ServicingActivityRead read, BuildingState row) =>
        Assert.IsType<ServicingOption.Unassessed>(Assert.Single(ServicingRules.OptionsOf(read, row))).Fact;

    private static string Excluded(ServicingActivityRead read, BuildingState row) =>
        Assert.IsType<ServicingOption.Excluded>(Assert.Single(ServicingRules.OptionsOf(read, row))).Reason;

    private static FuelBurn Burn(double perDay, bool useDriven = false) =>
        new() { PerDay = perDay, UnitsPerItem = 1, UseDriven = useDriven, InRain = false, WhenPowered = false };

    private static readonly BuildingHitPoints Whole = new BuildingHitPoints.Tracked { Current = 100, Maximum = 100 };

    // JobDriver_Repair's tick action, as decompiled: ticksToNextRepair starts at WarmupTicks, falls by ConstructionSpeed x 1.7
    // a tick, and each time it reaches zero it gains TicksBetweenRepairs and one hit point comes. Its float accumulation lands
    // within a tick of the exact count the policy derives.
    private static int NativeRepairTicks(int hitPoints, float rate)
    {
        float next = 80; var ticks = 0;
        while (hitPoints > 0)
        {
            ticks++; next -= rate;
            if (next <= 0) { next += 20; hitPoints--; }
        }
        return ticks;
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.0)]
    [InlineData(11.0)]
    [InlineData(15.0)]
    [InlineData(40.0)]
    public void RepairingAHalfWallTakesTheNativeDriversTicksAtThePawnsConstructionSpeed(double speed)
    {
        var read = Read(Target(), [Worker(ServicingKind.Repair, new ServicingAbility.Construction(speed))]);
        var work = Assert.IsType<RepairWork>(Assessed(read, Row()));
        Assert.Equal(150, work.HitPoints);
        var native = NativeRepairTicks(150, (float)speed * 1.7f);
        Assert.InRange(work.WorkTicks, native - 1, native + 1);
        Assert.Equal(30 + work.WorkTicks, work.Duration);
        Assert.Empty(work.Inputs);
        Assert.Equal(new ServiceLoss.Condition(150, 300), work.ServiceLoss);
    }

    [Fact]
    public void RefuelingCarriesTheItemsToFullInLoadsOfTheCarryCapacityAndWaitsOnceALoad()
    {
        var fuel = new BuildingFuel.Refuelable { AutoRefuel = new AutoRefuelControl.Switch { Allowed = true }, Fuel = 6, TargetFuel = 75, AutoRefuelPercent = 0.3, OutOfFuel = false, Burn = Burn(2.2) };
        var row = Row("Thing_Gen1", "WoodFiredGenerator", Whole, new BuildingPower.Trader { PowerOn = true, OutputW = 1000, RatedW = 1000, Network = "net" }, fuel, fuelDefs: ["WoodLog"]);
        var read = Read(Target("Thing_Gen1", "WoodFiredGenerator", new ServicingFuel.Refuel(false, 69, true)),
            [Worker(ServicingKind.Refuel, new ServicingAbility.Carrying(), new("WoodLog", 30, 100, 50, 40), "Thing_Gen1")], [new("WoodLog", 500)]);
        var work = Assert.IsType<RefuelWork>(Assessed(read, row));
        Assert.Equal((69, 3), (work.Items, work.Trips));
        Assert.Equal(100 + 3 * (50 + 240) + 2 * 40, work.Duration);
        Assert.Equal([new ServicingInput(new("WoodLog"), 69)], work.Inputs);
        Assert.True(work.NativeStarts);
        var pending = Assert.IsType<ServiceLoss.Pending>(work.ServiceLoss);
        Assert.Equal(6 / 2.2 * GameTime.TicksPerDay, pending.Ticks, 6);
        Assert.Equal(new ServiceLoss.PowerOutput("net", 1000), pending.Then);

        var scarce = Assert.IsType<RefuelWork>(Assessed(read with { Stock = [new("WoodLog", 20)] }, row));
        Assert.Equal((20, 1), (scarce.Items, scarce.Trips));
        Assert.Equal(100 + 50 + 240, scarce.Duration);

        var switchedOff = row with { Service = row.Service with { Fuel = fuel with { AutoRefuel = new AutoRefuelControl.Switch { Allowed = false } } } };
        Assert.False(Assert.IsType<RefuelWork>(Assessed(read, switchedOff)).NativeStarts);
        var aboveThreshold = row with { Service = row.Service with { Fuel = fuel with { Fuel = 30 } } };
        Assert.False(Assert.IsType<RefuelWork>(Assessed(read, aboveThreshold)).NativeStarts);
    }

    [Fact]
    public void ReplacingADoorWithAWallCostsTheWallLessTheDoorRefundAndLeavesTheCellOpenWhileItIsBuilt()
    {
        var target = Target("Thing_Door1", "Door", removal: new ServicingRemoval.Read([new("Steel", 25)], 0.5, 850),
            replacement: new ServicingReplacement.Wall("Wall", [new("Steel", 5)], 135, 2.0));
        var read = Read(target, [Worker(ServicingKind.Replace, new ServicingAbility.Construction(1.0), new("Steel", 75, 100, 60, 60), "Thing_Door1")]);
        var work = Assert.IsType<ReplacementWork>(Assessed(read, Row("Thing_Door1", "Door", Whole)));
        Assert.Equal("Wall", work.Replacement);
        Assert.Equal([new ServicingInput(new("Steel"), 5)], work.Inputs);
        Assert.Equal([new ServicingInput(new("Steel"), 12.5)], work.Recovered);
        Assert.Equal([new ServicingInput(new("Steel"), -7.5)], work.NetInputs);
        Assert.Equal(850 / 1.7, work.DeconstructTicks, 6);
        Assert.Equal(135 / (1.7 * 2.0), work.BuildTicks, 6);
        Assert.Equal(100 + 60, work.DeliveryTicks);
        Assert.Equal(30 + 850 / 1.7 + 160 + 135 / 3.4, work.Duration, 6);
        Assert.Equal(new ServiceLoss.Enclosure(At, 135 / 3.4), work.ServiceLoss);
    }

    [Fact]
    public void ABrokenGeneratorFixConsumesOneComponentAndRestoresItsRatedWattsWhileOtherServiceIsNamed()
    {
        var target = Target("Thing_Gen1", "SolarGenerator");
        var worker = Worker(ServicingKind.FixBreakdown, new ServicingAbility.Fixing(0.9), new("ComponentIndustrial", 10, 100, 50, 40), "Thing_Gen1");
        var generator = Row("Thing_Gen1", "SolarGenerator", Whole, new BuildingPower.Trader { PowerOn = false, OutputW = 0, RatedW = 1700, Network = "net" }, brokenDown: true);
        var fix = Assert.IsType<FixBreakdownWork>(Assessed(Read(target, [worker]), generator));
        Assert.Equal(100 + 50 + 1000, fix.Duration);
        Assert.Equal([new ServicingInput(new("ComponentIndustrial"), 1)], fix.Inputs);
        Assert.Equal(0.9, fix.SuccessChance);
        Assert.Equal(new ServiceLoss.PowerOutput("net", 1700), fix.ServiceLoss);

        var consumer = generator with { Service = generator.Service with { Power = new BuildingPower.Trader { PowerOn = false, OutputW = 0, RatedW = -200, Network = "net" } } };
        Assert.Equal(new ServiceLoss.Unmodelled("service:SolarGenerator"), Assessed(Read(target, [worker]), consumer).ServiceLoss);
    }

    [Fact]
    public void TheTargetJoinsItsCanonicalRowByIdAndAMismatchedDefOrCellIsAContractViolation()
    {
        var read = Read(Target(), [Worker(ServicingKind.Repair, new ServicingAbility.Construction(1))]);
        Assert.Same(read.Targets[0], ServicingRules.Join(read, Row())!.Target);
        Assert.Throws<DomainException>(() => ServicingRules.Join(read, Row(def: "Door")));
        Assert.Throws<DomainException>(() => ServicingRules.Join(read, Row(cell: new Cell(5, 7))));
        Assert.Throws<DomainException>(() => ServicingRules.Join(read with { Targets = [Target(), Target()] }, Row()));
        Assert.Equal("servicing target:Thing_Other", Assert.Throws<RequiredFactException>(() => ServicingRules.Join(read, Row("Thing_Other"))).Fact);
        Assert.Equal("servicing read", Assert.Throws<RequiredFactException>(() => ServicingRules.Join(null, Row())).Fact);
        var truncated = read with { Findings = [ServicingRules.Truncated] };
        Assert.Null(ServicingRules.Join(truncated, Row("Thing_Other")));
        Assert.Equal(ServicingRules.Truncated, Unassessed(truncated, Row("Thing_Other")));
    }

    [Fact]
    public void EachMissingFactIsNamedNeverDefaulted()
    {
        var repair = Worker(ServicingKind.Repair, new ServicingAbility.Unread("construction speed:Thing_Human1"));
        Assert.Equal("repair rate:Wall (construction speed:Thing_Human1)", Unassessed(Read(Target(), [repair]), Row()));
        Assert.Equal("hit points:Wall", Assert.Throws<RequiredFactException>(() => ServicingRules.WorkOf(Read(Target(), [repair]),
            new(Target(), Row(hp: new BuildingHitPoints.Untracked())), repair with { Ability = new ServicingAbility.Construction(1) })).Fact);

        var fix = Worker(ServicingKind.FixBreakdown, new ServicingAbility.Unread("fix success chance:Thing_Human1"), new("ComponentIndustrial", 10, 1, 1, 1));
        Assert.Equal("fix success chance:Wall (fix success chance:Thing_Human1)", Unassessed(Read(Target(), [fix]), Row(hp: Whole, brokenDown: true)));

        var door = Target("Thing_Door1", "Door", replacement: new ServicingReplacement.Wall("Wall", [new("Steel", 5)], 135, 1));
        var swap = Worker(ServicingKind.Replace, new ServicingAbility.Construction(1), new("Steel", 75, 1, 1, 1), "Thing_Door1");
        var doorRow = Row("Thing_Door1", "Door", Whole);
        Assert.Equal("cost list:Door", Unassessed(Read(door with { Removal = new ServicingRemoval.Unread("cost list:Door") }, [swap]), doorRow));
        Assert.Equal("construction rate:Door (construction speed:Thing_Human1)",
            Unassessed(Read(door, [swap with { Ability = new ServicingAbility.Unread("construction speed:Thing_Human1") }]), doorRow));
        Assert.Equal("wall delivery:Steel", Unassessed(Read(door, [swap with { Supply = new("WoodLog", 75, 1, 1, 1) }]), doorRow));

        var tank = new BuildingFuel.Refuelable { AutoRefuel = new AutoRefuelControl.NoSwitch(), Fuel = 0, TargetFuel = 75, AutoRefuelPercent = 0.3, OutOfFuel = true, Burn = Burn(2.2) };
        var refuelTarget = Target(fuel: new ServicingFuel.Refuel(false, 75, true));
        var refuel = Worker(ServicingKind.Refuel, new ServicingAbility.Carrying(), new("WoodLog", 30, 1, 1, 1));
        Assert.Equal("fuel stock:WoodLog", Unassessed(Read(refuelTarget, [refuel]), Row(hp: Whole, fuel: tank, fuelDefs: ["WoodLog"])));
        Assert.Equal("fuel def:Wall", Unassessed(Read(refuelTarget, [refuel], [new("WoodLog", 100)]), Row(hp: Whole, fuel: tank)));
        Assert.Equal("fuel:Wall", Unassessed(Read(refuelTarget, [refuel], [new("WoodLog", 100)]), Row(hp: Whole)));
    }

    [Fact]
    public void AUseDrivenFuelBurnIsNamedAndAnEmptyTankLosesTheBuildingsService()
    {
        var useDriven = new BuildingFuel.Refuelable { AutoRefuel = new AutoRefuelControl.NoSwitch(), Fuel = 20, TargetFuel = 50, AutoRefuelPercent = 0.3, OutOfFuel = false, Burn = Burn(0, useDriven: true) };
        var stove = Row("Thing_Stove1", "FueledStove", Whole, fuel: useDriven, fuelDefs: ["WoodLog"]);
        var read = Read(Target("Thing_Stove1", "FueledStove", new ServicingFuel.Refuel(false, 40, true)),
            [Worker(ServicingKind.Refuel, new ServicingAbility.Carrying(), new("WoodLog", 30, 1, 1, 1), "Thing_Stove1")], [new("WoodLog", 100)]);
        var work = Assert.IsType<RefuelWork>(Assessed(read, stove));
        Assert.Equal(new ServiceLoss.Unmodelled("fuel use rate:FueledStove"), work.ServiceLoss);
        Assert.False(work.NativeStarts);

        var empty = stove with { Service = stove.Service with { Fuel = useDriven with { Fuel = 0, OutOfFuel = true } } };
        var emptyWork = Assert.IsType<RefuelWork>(Assessed(read, empty));
        Assert.Equal(new ServiceLoss.Unmodelled("service:FueledStove"), emptyWork.ServiceLoss);
        Assert.True(emptyWork.NativeStarts);
    }

    [Fact]
    public void AKnownRefusalExcludesTheOptionWithItsReason()
    {
        Assert.Equal(ServicingRules.NotRepairable, Excluded(Read(Target(repairable: false), [Worker(ServicingKind.Repair, new ServicingAbility.Construction(1))]), Row()));
        Assert.Equal(ServicingRules.ZeroRate, Excluded(Read(Target(), [Worker(ServicingKind.Repair, new ServicingAbility.Construction(0))]), Row()));
        Assert.Equal(ServicingRules.NoWorker, Excluded(Read(Target(), []), Row()));
        Assert.Equal(ServicingRules.NoSupply, Excluded(Read(Target(), [Worker(ServicingKind.FixBreakdown, new ServicingAbility.Fixing(1))]), Row(hp: Whole, brokenDown: true)));

        var tank = new BuildingFuel.Refuelable { AutoRefuel = new AutoRefuelControl.NoSwitch(), Fuel = 3, TargetFuel = 10, AutoRefuelPercent = 0.3, OutOfFuel = false, Burn = Burn(1) };
        var tankRow = Row(hp: Whole, fuel: tank, fuelDefs: ["Chemfuel"]);
        var carrier = Worker(ServicingKind.Refuel, new ServicingAbility.Carrying(), new("Chemfuel", 75, 1, 1, 1));
        Assert.Equal(ServicingRules.RefuelNotEmpty, Excluded(Read(Target(fuel: new ServicingFuel.Refuel(false, 7, false)), [carrier], [new("Chemfuel", 9)]), tankRow));
        var refuelable = Target(fuel: new ServicingFuel.Refuel(false, 7, true));
        Assert.Equal(ServicingRules.NoStock, Excluded(Read(refuelable, [carrier], [new("Chemfuel", 0)]), tankRow));
        Assert.Equal(ServicingRules.CannotCarry, Excluded(Read(refuelable, [carrier with { Supply = new("Chemfuel", 0, 1, 1, 1) }], [new("Chemfuel", 9)]), tankRow));
        Assert.Empty(ServicingRules.OptionsOf(Read(Target(fuel: new ServicingFuel.Refuel(true, 0, true)), []), tankRow));
    }

    [Fact]
    public void TheMaintenanceChoiceComparesTheSurveyedWorkAndPicksTheShortestRepair()
    {
        var read = Read(Target(), [Worker(ServicingKind.Repair, new ServicingAbility.Construction(1)), Worker(ServicingKind.Repair, new ServicingAbility.Construction(15), pawn: "Thing_Human2")]);
        var options = ServicingRules.Survey(read, id => id == "Thing_Wall1" ? Row() : null);
        Assert.Equal("building row:Thing_Wall1", Assert.Throws<RequiredFactException>(() => ServicingRules.Survey(read, _ => null)).Fact);
        var decision = Assert.Single(ServicingChoiceRules.Decide(new ServicingRequest { Targets = [Row()], Census = new([.. options]) { Components = new ServicingComponents.Unread("component stock:ComponentIndustrial") }, Unneeded = new HashSet<string>() }));
        Assert.Equal((ServicingChoiceKind.Repair, "Thing_Human2"), (decision.Chosen.Kind, decision.Chosen.Pawn));
        Assert.Equal(183.0, Assert.IsType<RepairWork>(decision.Chosen.Work).Duration);
    }
}
