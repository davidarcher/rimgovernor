using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

// Task 6.5 (RG-HABITAT-008): the power family's switch orders under Auto. A chosen configuration is a PowerSwitch per
// building, re-derived each review from the observed switches; who set a switch is no exemption, the player's preferences
// keep their veto, and the Need closes only on observed facts.
public sealed class PowerSwitchTests
{
    private const string Wood = "WoodFiredGenerator";
    private static readonly Tick Now = new(600_000);
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PowerPlanning Base = new()
    {
        Battery = new PowerBattery(600, 0.5),
        Sources = new Dictionary<string, PowerSourceProfile> { [Wood] = PowerRules.ConstantPowerProfile },
        Light = new LightTerrains(new Dictionary<string, bool> { ["Soil"] = true }),
        DaylightHours = 12,
        Now = Now,
        Generators =
        [
            new GeneratorOption
            {
                Definition = Wood, Available = true, Profile = PowerRules.ConstantPowerProfile, NominalW = 1000,
                Fuel = new Resource("WoodLog"), FuelStock = 500, FuelRenewable = true, FuelPerDay = 20,
            },
        ],
        Valuation = new PowerValuation
        {
            Consumers = new Dictionary<string, ConsumerValue>(StringComparer.Ordinal)
            {
                ["freezer"] = new(UtilityService.Refrigeration, new InterruptionLoss.PerDay(20)),
                ["art"] = new(UtilityService.Workshop, new InterruptionLoss.PerDay(0)),
            },
            Pieces = new Dictionary<string, ProvisionOption>(StringComparer.Ordinal) { [Wood] = new(Wood, false, 1, 5) },
        },
    };

    // One building on "net" at (x, 2): its rated watts, whether it is powered and its switch.
    private static PowerSite At(string id, string definition, int x, double rated, bool powered, BuildingSwitch? sw = null)
    {
        var b = PowerBuildingFixtures.Trader(id, definition, new Cell(x, 2), rated, rated > 0 && powered ? rated : 0, powered);
        return b with { Service = b.Service with { Switch = sw ?? BuildingServiceWire.Flick(true) } };
    }

    private static SiteCell[] Row() => [.. Enumerable.Range(0, 9).Select(x => SiteCellFixtures.At(new Cell(x, 2)))];

    private static PowerProposal Select(PowerTopology v, PowerPlanning planning) => PowerRules.SelectPowerMethod(v, new Bounds(20, 20), Row(), [], planning);

    // (a) A short network switches the idle bench off; once the flick holds the configuration holds (nothing re-ordered) and the
    // power Need stays open while the bench is unpowered; a later surplus switches the bench back on.
    [Fact]
    public void TheIdleBenchIsSwitchedOffHeldAndSwitchedBackOnInSurplus()
    {
        var gen = At("gen", Wood, 2, 500, true);
        var freezer = At("freezer", "Cooler", 4, -400, true);

        var order = Select(new PowerTopology { Buildings = [gen, freezer, At("art", "TableSculpting", 6, -300, true)] }, Base);
        Assert.Equal(PowerMethod.Configure, order.Method);
        Assert.Equal(new PowerConfiguration(["art"], []), order.Configuration);
        Assert.StartsWith(BuildingPlanner.PowerSwitchMethod, order.Key.Value, StringComparison.Ordinal);

        // Ordered, not yet flicked: the network waits for the pawn.
        var flicking = Select(new PowerTopology { Buildings = [gen, freezer, At("art", "TableSculpting", 6, -300, true, BuildingServiceWire.Flick(true, false))] }, Base);
        Assert.Equal(PowerMethod.WaitSwitch, flicking.Method);

        // Flicked off: the configuration holds and orders nothing; the bench is unpowered, so the power Need is not closed.
        var off = At("art", "TableSculpting", 6, -300, false, BuildingServiceWire.Flick(false));
        var held = Select(new PowerTopology { Buildings = [gen, freezer, off] }, Base);
        Assert.Equal(PowerMethod.Configured, held.Method);
        Assert.Equal(new PowerConfiguration(["art"], []), held.Configuration);
        Assert.False(off.Powered);

        // A second generator covers both: the bench is switched back on.
        var surplus = Select(new PowerTopology { Buildings = [gen, At("gen2", Wood, 0, 500, true), freezer, off] }, Base);
        Assert.Equal(PowerMethod.Configure, surplus.Method);
        Assert.Equal(new PowerConfiguration([], ["art"]), surplus.Configuration);
    }

    // (b) A generator the player switched off is switched on under Auto like any other; one the player's preferences protect is
    // never ordered and the network compares building instead.
    [Fact]
    public void APlayerSwitchedOffGeneratorIsSwitchedOnUnlessProtected()
    {
        var v = new PowerTopology { Buildings = [At("gen", Wood, 2, 500, false, BuildingServiceWire.Flick(false)), At("freezer", "Cooler", 4, -400, false)] };

        var on = Select(v, Base);
        Assert.Equal(PowerMethod.Configure, on.Method);
        Assert.Equal(new PowerConfiguration([], ["gen"]), on.Configuration);

        var vetoed = Select(v, Base with { Protected = new HashSet<string>(["gen"], StringComparer.Ordinal) });
        Assert.Equal((PowerMethod.Generate, Wood), (vetoed.Method, vetoed.Definition));
        Assert.Null(typeof(PowerMethod).GetField("WaitPlayer"));
    }

    // (e) A consumer without a flick switch is never a configuration's order: the comparison builds instead.
    [Fact]
    public void ANonFlickableConsumerIsNeverSwitched()
    {
        var v = new PowerTopology { Buildings = [At("gen", Wood, 2, 500, true), At("freezer", "Cooler", 4, -400, true), At("art", "TableSculpting", 6, -300, true, new BuildingSwitch.Fixed())] };
        var proposal = Select(v, Base);
        Assert.Equal((PowerMethod.Generate, Wood), (proposal.Method, proposal.Definition));
        var configure = proposal.Decision!.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Configure).Alternative.Standing;
        Assert.DoesNotContain("art", Assert.IsType<UtilityStanding.Assessed>(configure).Effect.SwitchedOff);
    }

    // The switch orders' plan: one PowerSwitch per building, off first (each claims switch/<thing>).
    [Fact]
    public void TheSwitchPlanOrdersEachSwitchOffFirst()
    {
        var spec = BuildingPlanner.PowerSwitchPlan(new PlanId("p"), new PowerConfiguration(["art"], ["gen"]));
        Assert.Equal([new PowerSwitch("art", false), new PowerSwitch("gen", true)], spec.Actions.Select(a => a.PowerSwitch));
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public PowerConfiguration Orders { get; } = new(["art"], []);
        public MethodId Key { get; } = new(BuildingPlanner.PowerSwitchMethod + "net-art");

        private FamilyRunner<PowerFacts> Runner => new(new PowerFamily(), (_, _) => Task.FromResult(new PowerFacts { Standing = false }),
            new FamilySchedule { Kinds = [ActionKind.PowerSwitch] },
            (work, _, call) => Task.FromResult(BuildingPlanner.PowerSwitchHold(Journal, work.Owner, Key, World, call)
                ?? BuildingPlanner.OwnerCommitVerdict(Journal.CommitOwnerWork(work.Owner, Key, "", BuildingPlanner.PowerSwitchPlan(PlanIds.MintPlanId(), Orders), call))));

        public FamilyRun Run() => Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == PowerFamily.Basic).Verdict;

        public void Dispose() => Journal.Dispose();

        public void Receive(PlanId plan, Receipt receipt, NativeRefusal? refusal = null)
        {
            var ct = TestContext.Current.CancellationToken;
            var action = new ActionId(plan.Value + "-0");
            var scope = World with { Plan = plan };
            Journal.Prepare(plan, action, scope, new(110), ct);
            var sent = Journal.Dispatch(plan, action, scope, new(110), ct);
            Journal.RecordReceipts([new BatchReceipt(plan, action, sent.View.Attempt, receipt) { Refusal = refusal }], ct);
        }

        public static Colony Open()
        {
            IntentActionRules.RegisterIntentKinds();
            var ct = TestContext.Current.CancellationToken;
            var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-power-switch-" + Guid.NewGuid().ToString("N") + ".db"), ct);
            journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
            return new Colony { Journal = journal };
        }
    }

    // (c) An accepted switch whose building still reads unchanged is Unconfirmed: the next review holds the same orders instead of
    // re-issuing them, and the Need stays open on the facts.
    [Fact]
    public void AnAcceptedButUnchangedSwitchIsUnconfirmedAndNotReissued()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Colony.Open();
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        var first = Assert.Single(run.Methods);
        Assert.Equal(colony.Key, first.Method);

        Assert.Equal("power_switch_method", Colony.Report(colony.Run()).Refusal.Subject);

        colony.Receive(first.Plan, Receipt.Accepted);
        var unconfirmed = colony.Run();
        Assert.Equal("power_switch_unconfirmed", Colony.Report(unconfirmed).Refusal.Subject);
        Assert.Empty(unconfirmed.Methods);
        Assert.Single(colony.Journal.OwnerMethodHistory(colony.Journal.LoadNeed(PowerFamily.Basic, World, ct), ct));
    }

    // (e) A refused switch ("not flickable") spends the order's refusal budget: the next review is not admitted blindly.
    [Fact]
    public void ARefusedSwitchSpendsTheRefusalBudget()
    {
        using var colony = Colony.Open();
        var first = Assert.Single(colony.Run().Methods);
        colony.Receive(first.Plan, Receipt.Refused, new NativeRefusal("refused", "Building is not flickable.", RefusalClass.Permanent));

        var next = colony.Run();
        Assert.NotEqual(Outcome.Admitted, Colony.Report(next).Outcome);
        Assert.Empty(next.Methods);
    }
}
