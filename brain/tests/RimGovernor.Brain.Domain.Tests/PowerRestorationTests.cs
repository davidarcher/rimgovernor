using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using BuildingState = RimGovernor.Brain.GameFacts.Observations.BuildingState;
using Ex = RimGovernor.Brain.Executor;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT power restoration (task 5.3): a producer an empty tank or a breakdown took off a short network is compared by the
/// maintenance choice (refuel, fix, or acquire-then-refuel) and counts toward the network's capacity only while that choice
/// restores it. The bug was a categorical wait on fuel or repair: an empty wood generator held power/basic forever whether or not
/// any wood existed, and nothing ordered the refuel or the cut.
/// </summary>
public sealed class PowerRestorationTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PowerPlanning Planning = new()
    {
        Battery = new PowerBattery(600, 0.5),
        Sources = new Dictionary<string, PowerSourceProfile> { ["WoodFiredGenerator"] = PowerRules.ConstantPowerProfile },
    };

    private static FuelBurn Burn => new() { PerDay = 2.2, UnitsPerItem = 1, UseDriven = false, InRain = false, WhenPowered = false };

    private static BuildingFuel.Refuelable Tank(double fuel, double percent = 0.3) => new()
    {
        AutoRefuel = new AutoRefuelControl.Switch { Allowed = true }, Fuel = fuel, TargetFuel = 75, AutoRefuelPercent = percent, OutOfFuel = fuel == 0, Burn = Burn,
    };

    internal static BuildingState Generator(string id, bool empty = true, bool broken = false, int x = 2)
    {
        var row = PowerBuildingFixtures.Trader(id, "WoodFiredGenerator", new(x, 3), 1000, 0, false);
        return row with
        {
            HitPoints = new BuildingHitPoints.Tracked { Current = 100, Maximum = 100 },
            Service = row.Service with { Fuel = empty ? Tank(0) : new BuildingFuel.NotRefuelable(), BrokenDown = broken },
            FuelDefinitions = empty ? ["WoodLog"] : [],
        };
    }

    internal static BuildingState Stove => PowerBuildingFixtures.Trader("stove", "ElectricStove", new(8, 2), -350, 0, false);

    internal static PowerTopology Topology(params BuildingState[] producers) => new() { Buildings = [.. producers, Stove] };

    internal static ServicingOption.Assessed Refueling(string target, string pawn, int items = 75, double fetch = 100) =>
        new(new RefuelWork(target, pawn, true, true, new Resource("WoodLog"), items, 3, fetch, 50, 40, 240, true)
            { ServiceLoss = new ServiceLoss.PowerOutput("net", 1000) });

    internal static ServicingOption.Assessed Fixing(string target, string pawn) =>
        new(new FixBreakdownWork(target, pawn, true, true, new Resource("ComponentIndustrial"), 10, 10, 1000, 0.5) { ServiceLoss = new ServiceLoss.PowerOutput("net", 1000) });

    internal static ServicingCensus Census(int wood, int components, params ServicingOption[] options) => new([.. options])
    {
        Components = new ServicingComponents.Counted(components), Component = "ComponentIndustrial",
        Stock = new Dictionary<string, int>(StringComparer.Ordinal) { ["WoodLog"] = wood, ["ComponentIndustrial"] = components },
    };

    private static AcquisitionSource Tree(string id, double yield = 40) => new()
    {
        Observed = new() { Source = new() { Id = id }, Token = id + "-cas", Resource = "WoodLog", Yield = yield,
            Designation = new AcquisitionDesignation.Open(), Taken = false, Kind = new AcquisitionKind.Plant(false, 1, false) },
        Entity = new() { Id = id, DefName = "Plant_TreeOak", Label = "oak", Place = new EntityPlace.OnMap(0, new(12, 12)) },
        Tree = true,
    };

    internal static PowerRestorationRequest Request(PowerTopology topology, ServicingCensus census, params AcquisitionSource[] sources) =>
        new() { Topology = topology, Census = census, Sources = [.. sources] };

    private static PowerProposal Select(PowerTopology topology, PowerRestoration restoration) =>
        PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), [], [], Planning with { Restorable = restoration.Restorable });

    private static ServicingStanding StandingOf(ServicingDecision d, ServicingChoiceKind kind, string pawn = "") =>
        d.Alternatives.Single(a => a.Kind == kind && a.Pawn == pawn).Standing;

    internal sealed class Clock : IClock { public DateTimeOffset Now() => DateTimeOffset.UnixEpoch; }

    internal sealed class Boundary : Ex.IBoundary
    {
        public readonly List<DomainAction> Written = [];
        public Task<Ex.IntentInspection> InspectIntentAsync(Ex.Target target, CancellationToken ct) =>
            Task.FromResult(new Ex.IntentInspection { StartedAt = DateTimeOffset.UnixEpoch, ObservedAt = DateTimeOffset.UnixEpoch, Current = target.Snapshot, Tick = new(101) });
        public Task<IReadOnlyList<Ex.Receipt>> WriteIntentsAsync(IReadOnlyList<Ex.Placement> placements, CancellationToken ct)
        {
            Written.AddRange(placements.Select(p => p.Action));
            return Task.FromResult<IReadOnlyList<Ex.Receipt>>(placements.Select(p => new Ex.Receipt
                { Action = p.Action.Id, Attempt = p.Attempt, Snapshot = p.Snapshot, Kind = Receipt.Accepted }).ToArray());
        }
    }

    /// <summary>A journal and the power family's runner over facts whose servicing history is re-derived from it, as the reader does.</summary>
    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public PowerFacts Facts { get; set; } = new();
        public FamilyRunner<PowerFacts> Runner => new(new PowerFamily(), (_, _) => Task.FromResult(Observed()),
            new FamilySchedule { Kinds = [ActionKind.RecoveryService, ActionKind.Acquisition] });

        public PowerFacts Observed()
        {
            var (underway, refused) = HomeUpkeepReader.ServicingHistory(Journal, World, PowerFamily.Basic, [PowerRestorationRules.RefuelMethod], [],
                [(HomeUpkeepReader.RefuelJob, ServicingKind.Refuel)], TestContext.Current.CancellationToken);
            var r = Facts.Restoration;
            return Facts with { Restoration = r with { Underway = [.. underway, .. r.Underway], Refused = [.. refused] } };
        }

        public FamilyRun Run() => Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == PowerFamily.Basic).Verdict;

        public void Dispose() => Journal.Dispose();

        public static Colony Open()
        {
            IntentActionRules.RegisterIntentKinds();
            var ct = TestContext.Current.CancellationToken;
            var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-power-restoration-" + Guid.NewGuid().ToString("N") + ".db"), ct);
            journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
            return new Colony { Journal = journal };
        }
    }

    // (a) An adequately sized empty generator with wood in stock is refuelled, not answered with a second generator; the accepted
    // order restores nothing by itself and only fresh powered facts close the Need.
    [Fact]
    public async Task AnEmptyGeneratorWithWoodInStockIsRefuelledAndTheNeedClosesOnlyOnFreshPoweredFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var topology = Topology(Generator("gen"));
        var request = Request(topology, Census(500, 1, Refueling("gen", "a")));
        var decision = Assert.Single(PowerRestorationRules.Decide(request).Decisions);
        Assert.Equal((ServicingChoiceKind.Refuel, "a"), (decision.Chosen.Kind, decision.Chosen.Pawn));
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.FuelStocked), StandingOf(decision, ServicingChoiceKind.Acquire));
        Assert.Equal(PowerMethod.Restore, Select(topology, PowerRestorationRules.Decide(request)).Method);

        using var colony = Colony.Open();
        colony.Facts = new PowerFacts { Standing = false, Restoration = request };
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        var method = Assert.Single(run.Methods);
        Assert.Equal("refuel-gen-a", method.Method.Value);
        var progress = Assert.Single(colony.Journal.LoadPlan(method.Plan, ct).Progress);
        Assert.Equal(("a", "gen", RecoveryMethod.Refuel), (progress.Action.RecoveryService!.Pawn.Value, progress.Action.RecoveryService.Thing, progress.Action.RecoveryService.Method));
        // The refuel claims its producer and its pawn.
        Assert.Equal(new MethodClaims(["a"], ["gen"], []), MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(), [new WorkAction(method.Plan, progress.Action, progress.View)]));
        Assert.Equal(ClaimOutcome.Continued, Assert.Single(colony.Run().Methods).Outcome);

        var boundary = new Boundary();
        using var hands = new Ex.Executor(new ExecutorJournal(colony.Journal), boundary, new Clock(), new Ex.Limits
            { MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10) },
            new PlanAuthorizer(colony.Journal, true));
        hands.UpdateAuthority(new() { Snapshot = World, Enabled = true });
        await hands.RunBatchAsync(method.Plan, [progress.Action.Id], ct);
        Assert.Equal(ActionKind.RecoveryService, Assert.Single(boundary.Written).Kind);

        // Accepted restores nothing: the Need stays open on the same facts.
        Assert.Equal(Satisfaction.Unmet, Assert.Single(new PowerFamily().Derive(colony.Observed())).Satisfaction);
        // The pawn's running native refuel is observed: no second order, and the producer still counts as restoring.
        colony.Facts = colony.Facts with { Restoration = request with { Underway = [new("gen", ServicingKind.Refuel, "a", false)] } };
        Assert.Empty(new PowerFamily().Propose([Assert.Single(new PowerFamily().Derive(colony.Facts))], colony.Facts));
        Assert.Equal(PowerMethod.Restore, Select(topology, PowerRestorationRules.Decide(colony.Facts.Restoration)).Method);
        // Fresh powered facts close it.
        Assert.Equal(Satisfaction.Met, Assert.Single(new PowerFamily().Derive(new PowerFacts { Standing = true })).Satisfaction);
    }

    // (b) No wood in stock but a tree to cut: the acquire-then-refuel chain is bound (the cut is ordered), re-derived each review;
    // once wood is stocked the next review refuels.
    [Fact]
    public void WithNoStockAndATreeToCutTheChainIsBoundAndALaterReviewWithStockRefuels()
    {
        var topology = Topology(Generator("gen"));
        var noStock = new ServicingOption.Excluded("gen", ServicingKind.Refuel, "a", ServicingRules.NoSupply);
        var request = Request(topology, Census(0, 1, noStock), Tree("oak1"), Tree("oak2"));
        var restoration = PowerRestorationRules.Decide(request);
        var decision = Assert.Single(restoration.Decisions);
        Assert.Equal(ServicingChoiceKind.Acquire, decision.Chosen.Kind);
        var chain = decision.Chosen.Chain!;
        Assert.Equal((new Resource("WoodLog"), 75.0, 0.0), (chain.Resource, chain.Deficit, chain.Pending));
        Assert.Equal(["oak1", "oak2"], chain.Sources.Select(s => s.Id));
        // The chain restores: the planner counts the generator and builds nothing.
        Assert.Equal(["gen"], restoration.Restorable);
        Assert.Equal(PowerMethod.Restore, Select(topology, restoration).Method);
        var method = Assert.Single(PowerRestorationRules.Methods(PowerFamily.Basic, restoration));
        Assert.Equal(PowerRestorationRules.FuelMethodId(chain), method.Method);
        Assert.StartsWith("fuel-WoodLog-", method.Method.Value, StringComparison.Ordinal);
        var plan = method.Plan(new PlanId("p"));
        Assert.Equal(["oak1", "oak2"], plan.Actions.Select(a => a.Acquisition!.Thing));

        // A designated tree is pending yield, and a held one is not selected again.
        var held = Request(topology, Census(0, 1, noStock), Tree("oak1"), Tree("oak2")) with { Held = new Dictionary<string, bool>(StringComparer.Ordinal) { ["oak1"] = true } };
        Assert.Equal(["oak2"], Assert.Single(PowerRestorationRules.Decide(held).Decisions).Chosen.Chain!.Sources.Select(s => s.Id));

        // A later review: the wood is stocked and a refuel is measured. The chain is excluded and the refuel is ordered.
        var stocked = Request(topology, Census(80, 1, Refueling("gen", "a")), Tree("oak1"), Tree("oak2"));
        var refuel = Assert.Single(PowerRestorationRules.Decide(stocked).Decisions);
        Assert.Equal(ServicingChoiceKind.Refuel, refuel.Chosen.Kind);
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.FuelStocked), StandingOf(refuel, ServicingChoiceKind.Acquire));
        Assert.Equal("refuel-gen-a", Assert.Single(PowerRestorationRules.Methods(PowerFamily.Basic, PowerRestorationRules.Decide(stocked))).Method.Value);
    }

    // (c) No wood and no source that yields it: the chain is the named state fuel acquisition:WoodLog, the generator is not
    // restorable, and generation is priced as if it were not there.
    [Fact]
    public void WithNoStockAndNoPathTheChainIsANamedStateAndGenerationWins()
    {
        var topology = Topology(Generator("gen"));
        var request = Request(topology, Census(0, 1, new ServicingOption.Excluded("gen", ServicingKind.Refuel, "a", ServicingRules.NoSupply)));
        var restoration = PowerRestorationRules.Decide(request);
        var decision = Assert.Single(restoration.Decisions);
        Assert.Equal(ServicingChoiceKind.Defer, decision.Chosen.Kind);
        Assert.Equal(new ServicingStanding.Unassessed(ServicingChoiceRules.FuelAcquisition + "WoodLog"), StandingOf(decision, ServicingChoiceKind.Acquire));
        Assert.Contains("Acquire unassessed fuel acquisition:WoodLog", ServicingChoiceRules.Describe(decision), StringComparison.Ordinal);
        Assert.Empty(restoration.Restorable);
        Assert.Empty(PowerRestorationRules.Methods(PowerFamily.Basic, restoration));
        // The generator counts for nothing: the network is short exactly as if it had no producer.
        var bare = PowerRules.SelectPowerMethod(new PowerTopology { Buildings = [Stove] }, new Bounds(20, 20), [], [], Planning);
        var dead = Select(topology, restoration);
        Assert.NotEqual(PowerMethod.Restore, dead.Method);
        Assert.Equal(bare.Method, dead.Method);
    }

    // (d) A broken generator with a component in stock: the fix restores it, so the planner builds nothing (the fix itself is the
    // home-upkeep repairs Need's Method). With no component, the fix is excluded and the acquisition is the named state.
    [Fact]
    public void ABrokenGeneratorWithAComponentIsFixedAndWithoutOneTheAcquisitionIsANamedState()
    {
        var topology = Topology(Generator("gen", empty: false, broken: true));
        var restoration = PowerRestorationRules.Decide(Request(topology, Census(0, 1, Fixing("gen", "a"))));
        var decision = Assert.Single(restoration.Decisions);
        Assert.Equal(ServicingChoiceKind.FixBreakdown, decision.Chosen.Kind);
        Assert.Equal(["gen"], restoration.Restorable);
        Assert.Equal(PowerMethod.Restore, Select(topology, restoration).Method);
        Assert.Empty(PowerRestorationRules.Methods(PowerFamily.Basic, restoration));

        var none = PowerRestorationRules.Decide(Request(topology, Census(0, 0, Fixing("gen", "a"))));
        var deferred = Assert.Single(none.Decisions);
        Assert.Equal(ServicingChoiceKind.Defer, deferred.Chosen.Kind);
        Assert.Equal(new ServicingStanding.Excluded(ServicingRules.NoStock), StandingOf(deferred, ServicingChoiceKind.FixBreakdown, "a"));
        Assert.Equal(new ServicingStanding.Unassessed(ServicingChoiceRules.ComponentAcquisition + "ComponentIndustrial"), StandingOf(deferred, ServicingChoiceKind.Acquire));
        Assert.Empty(none.Restorable);
        Assert.NotEqual(PowerMethod.Restore, Select(topology, none).Method);
    }

    // (e) Two empty generators and wood for one: the first refuel takes the wood and the pawn, the second cannot count it again.
    [Fact]
    public void TwoEmptyGeneratorsCannotClaimTheSameFuelTwice()
    {
        var topology = Topology(Generator("gen1"), Generator("gen2", x: 5));
        var restoration = PowerRestorationRules.Decide(Request(topology,
            Census(100, 1, Refueling("gen1", "a"), Refueling("gen1", "b", fetch: 200), Refueling("gen2", "a"), Refueling("gen2", "b", fetch: 200))));
        Assert.Equal(("gen1", ServicingChoiceKind.Refuel, "a"), (restoration.Decisions[0].Target, restoration.Decisions[0].Chosen.Kind, restoration.Decisions[0].Chosen.Pawn));
        var second = restoration.Decisions[1];
        Assert.Equal(ServicingChoiceKind.Defer, second.Chosen.Kind);
        Assert.Equal((new ServicingStanding.Excluded(ServicingChoiceRules.PawnTaken), new ServicingStanding.Excluded(ServicingChoiceRules.FuelTaken)),
            (StandingOf(second, ServicingChoiceKind.Refuel, "a"), StandingOf(second, ServicingChoiceKind.Refuel, "b")));
        Assert.Equal(["gen1"], restoration.Restorable);
        Assert.Equal("refuel-gen1-a", Assert.Single(PowerRestorationRules.Methods(PowerFamily.Basic, restoration)).Method.Value);

        // An issued refuel not yet running still holds the wood it will carry: the next review cannot give it to gen2.
        var issued = PowerRestorationRules.Decide(Request(topology, Census(100, 1, Refueling("gen1", "a"), Refueling("gen2", "b", fetch: 200))) with
            { Underway = [new("gen1", ServicingKind.Refuel, "a", true, "WoodLog", 75)] });
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.FuelTaken), StandingOf(issued.Decisions[1], ServicingChoiceKind.Refuel, "b"));
    }

    // (e2) The issued refuel carries its items on the underway row: it holds them although the census no longer measures its option.
    [Fact]
    public void AnIssuedRefuelStillHoldsItsFuelWhenItsCensusOptionIsGone()
    {
        var topology = Topology(Generator("gen1"), Generator("gen2", x: 5));
        var issued = PowerRestorationRules.Decide(Request(topology, Census(100, 1, Refueling("gen2", "b", fetch: 200))) with
            { Underway = [new("gen1", ServicingKind.Refuel, "a", true, "WoodLog", 75)] });
        Assert.Equal(ServicingChoiceKind.Refuel, issued.Decisions[0].Chosen.Kind);
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.FuelTaken), StandingOf(issued.Decisions[1], ServicingChoiceKind.Refuel, "b"));
    }

    // (e3) One review, one ledger: a component or fuel item a choice took is not counted again by another request of the review.
    [Fact]
    public void TwoRequestsOfOneReviewSharingALedgerSpendTheSameStockOnce()
    {
        ServicingRequest Over(ServicingCensus census, ServicingLedger? ledger, params BuildingState[] targets) =>
            new() { Targets = targets, Census = census, Unneeded = new HashSet<string>(StringComparer.Ordinal), Ledger = ledger };

        // One component, a broken generator (power) and a broken cooler (home upkeep): the power choice takes it, home upkeep cannot.
        var broken = Generator("gen", empty: false, broken: true);
        var cooler = Generator("cooler", empty: false, broken: true, x: 6);
        var fixes = Census(0, 1, Fixing("gen", "a"), Fixing("cooler", "b"));
        var ledger = new ServicingLedger();
        var power = Assert.Single(PowerRestorationRules.Decide(Request(Topology(broken), fixes) with { Ledger = ledger }).Decisions);
        var home = Assert.Single(ServicingChoiceRules.Decide(Over(fixes, ledger, cooler)));
        Assert.Equal(ServicingChoiceKind.FixBreakdown, power.Chosen.Kind);
        Assert.Equal(ServicingChoiceKind.Defer, home.Chosen.Kind);
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.ComponentTaken), StandingOf(home, ServicingChoiceKind.FixBreakdown, "b"));
        // Deciding either again on the review's ledger takes nothing more and changes no answer.
        Assert.Equal(ServicingChoiceKind.FixBreakdown, Assert.Single(PowerRestorationRules.Decide(Request(Topology(broken), fixes) with { Ledger = ledger }).Decisions).Chosen.Kind);
        Assert.Equal(ServicingChoiceKind.Defer, Assert.Single(ServicingChoiceRules.Decide(Over(fixes, ledger, cooler))).Chosen.Kind);
        // Without the shared ledger each request counts the stock for itself.
        Assert.Equal(ServicingChoiceKind.FixBreakdown, Assert.Single(ServicingChoiceRules.Decide(Over(fixes, null, cooler))).Chosen.Kind);

        // Wood for one refuel, an empty generator (power) and an empty turret barrel (defense rearm).
        var wood = Census(100, 1, Refueling("gen", "a"), Refueling("turret", "b", fetch: 200));
        var empty = Generator("gen");
        var barrel = Generator("turret", x: 7);
        var fuel = new ServicingLedger();
        var refuel = Assert.Single(PowerRestorationRules.Decide(Request(Topology(empty), wood) with { Ledger = fuel }).Decisions);
        var rearm = Assert.Single(DefenseRearm.Refuels([barrel], wood, [], [], fuel));
        Assert.Equal((ServicingChoiceKind.Refuel, ServicingChoiceKind.Defer), (refuel.Chosen.Kind, rearm.Chosen.Kind));
        Assert.Equal(new ServicingStanding.Excluded(ServicingChoiceRules.FuelTaken), StandingOf(rearm, ServicingChoiceKind.Refuel, "b"));
    }

    // (f) The native auto-refuel threshold is the row's Props.autoRefuelPercent (0.3 for the wood generator), not a constant .25.
    [Theory]
    [InlineData(22, 0.3, true)]
    [InlineData(23, 0.3, false)]
    [InlineData(30, 0.5, true)]
    public void TheNativeAutoRefuelPercentGatesNativeStarts(double fuel, double percent, bool starts)
    {
        var at = new Cell(2, 3);
        var row = Generator("gen") with { Service = Generator("gen").Service with { Fuel = Tank(fuel, percent), Power = new BuildingPower.Trader { PowerOn = true, OutputW = 1000, RatedW = 1000, Network = "net" } } };
        var target = new ServicingTarget("gen", "WoodFiredGenerator", at)
        {
            Home = true, Repairable = true, Fuel = new ServicingFuel.Refuel(false, (int)(75 - fuel), true),
            Removal = new ServicingRemoval.Read([], 0.5, 135), Replacement = new ServicingReplacement.None("not_a_door"),
        };
        var read = CoupledFixtures.NoServicing with
        {
            Targets = [target], Stock = [new("WoodLog", 500)],
            Workers = [new("gen", ServicingKind.Refuel, "a", 30) { WorkActive = true, Ability = new ServicingAbility.Carrying(), Supply = new("WoodLog", 75, 100, 50, 40) }],
        };
        var work = Assert.IsType<RefuelWork>(Assert.IsType<ServicingOption.Assessed>(Assert.Single(ServicingRules.OptionsOf(read, row))).Work);
        Assert.Equal(starts, work.NativeStarts);
    }

    // (g) A refused refuel spends the order's budget: the next review re-derives the refusal from the journal and re-selects.
    [Fact]
    public void ARefusedRefuelSpendsTheBudgetAndTheNextReviewReselects()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Colony.Open();
        var request = Request(Topology(Generator("gen")), Census(500, 1, Refueling("gen", "a"), Refueling("gen", "b", fetch: 300)));
        colony.Facts = new PowerFacts { Standing = false, Restoration = request };
        var first = Assert.Single(colony.Run().Methods);
        Assert.Equal("refuel-gen-a", first.Method.Value);

        var action = new ActionId(first.Plan.Value + "-0");
        var scope = World with { Plan = first.Plan };
        colony.Journal.Prepare(first.Plan, action, scope, new(110), ct);
        var sent = colony.Journal.Dispatch(first.Plan, action, scope, new(110), ct);
        colony.Journal.RecordReceipts([new BatchReceipt(first.Plan, action, sent.View.Attempt, Receipt.Refused)
        {
            Refusal = new NativeRefusal("refused", "no path", RefusalClass.Permanent),
        }], ct);

        var observed = colony.Observed();
        Assert.Equal(new RefusedServicing("gen", ServicingKind.Refuel, "a", "no_path"), Assert.Single(observed.Restoration.Refused));
        Assert.Equal(new ServicingStanding.Excluded("refused:no_path"),
            StandingOf(Assert.Single(PowerRestorationRules.Decide(observed.Restoration).Decisions), ServicingChoiceKind.Refuel, "a"));
        var next = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(next).Outcome);
        Assert.Equal("refuel-gen-b", Assert.Single(next.Methods).Method.Value);
    }
}
