using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
namespace RimGovernor.Brain.Domain.Tests;

public sealed class CapabilityMaintenanceTests
{
    internal static SkillLearning Learning() => new()
    {
        Disabled = Known(false), TrackerAllowsLearning = Known(true), StoredLevel = Known(1), Aptitude = Known(0),
        Xp = Known(-95d), XpSinceMidnight = Known(0d), DirectFactor = Known(1d), UnsaturatedFactor = Known(1d),
        SaturationThreshold = Known(4000d), SaturatedFactor = Known(.2), LossThreshold = Known(-100d),
        DecayInterval = Known(200), NextDecay = Known(200), ValidForTicks = Known(1000),
        Levels = [new(0, 100, 0), new(1, 200, 10), new(2, 300, 20)],
        PracticeEligible = Known(true), PracticeCeiling = Known(2), RangeAppliedBudget = Known(3000d)
    };
    internal static Ideoligion Ideology(params string[] holders) => new(new() { Roles = new Dictionary<string, RoleDef>
    { ["soldier"] = new(2, [new([new("Shooting", 1), new("Melee", 1)])]) } },
        new() { Roles = [new("role", "soldier", true, holders.Select(h => new PawnId(h)).ToArray())] });

    [Fact]
    public void OnlyAssignedActiveRequirementsAuthorizeMaintenanceAndPreserveOr()
    {
        Assert.Empty(AssignedCombatCapabilities(Known(Ideology())));
        var requirement = Assert.Single(AssignedCombatCapabilities(Known(Ideology("pawn"))));
        Assert.Equal(CapabilityDevelopmentKind.Maintenance, requirement.Kind);
        Assert.Equal(2, requirement.Alternatives.Count);
        Assert.Empty(AssignedCombatCapabilities(default));
        var inactive = Ideology("pawn") with { Facts = new() { Roles = [new("role", "soldier", false, [new("pawn")])] } };
        Assert.Empty(AssignedCombatCapabilities(Known(inactive)));
    }

    [Fact]
    public void NormalWorkSuppressesPracticeAndUnknownOrBranchDoesNotProveFailure()
    {
        var requirement = Assert.Single(AssignedCombatCapabilities(Known(Ideology("pawn"))));
        WorkSkill[] skills = [new() { Name = "Shooting", Level = 1, Learning = Learning() }, new() { Name = "Melee", Disabled = true }];
        var completed = new Dictionary<string, Fact<bool>> { ["work"] = Known(true) };
        ForecastMaintenanceConsequence Forecast(EquatableArray<WorkSkill> observed, ForecastActivityPlan plan)
            => ForecastMaintenancePolicy.ForecastMaintenance([new(requirement, observed)], [], plan, completed, 200)[0];
        Assert.True(Forecast(EquatableArray.From(skills), new([], 0, 0)).MissingXp.Value > 0);
        var projection = ForecastSkillPolicy.ForecastSkill(Learning(), [], 0);
        var work = new ForecastActivityPlan([new("work", "pawn", 0, 0, 0, 1, 0, 0, 1, 1, 0)
        { Learning = new("Shooting", new("work", 1, Known(20d), SkillXpPath.NormalTracker), projection, projection, 1, 1, "native") }], 1, 1);
        Assert.Equal(Known(0d), Forecast(EquatableArray.From(skills), work).MissingXp);
        Assert.False(Forecast([skills[0]], new([], 0, 0)).MissingXp.IsKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalUnfilteredComparisonChoosesAssignedPracticeAndBindsExactParticipants(bool melee)
    {
        var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: melee);
        var skill = melee ? "Melee" : "Shooting";
        var requirement = new RoleDef(1,[new([new(skill, 1)])]);
        var ideology = Ideology("pawn") with { Defs = new() { Roles = new Dictionary<string, RoleDef> { ["soldier"] = requirement } } };
        var roster = facts.ForecastObserved.WorkPawns.Value.Select(p => p with { Skills = Known<EquatableArray<WorkSkill>>([new() { Name = skill, Level = 1, Learning = Learning() }]) }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, skill, "target/" + i, new(i + 1, 1),
            melee ? "RimGovernor_Spar" : "RimGovernor_TrainRange", melee ? "RimGovernor_Spar" : "RimGovernor_TrainShooting",
            Known(0d), Known(100d), Learning()) { CallbackIntervalTicks = Known(10d), RawXpPerCallback = Known(10d) }).ToArray();
        facts = facts with { Hostiles = Known(0L), CriticalPatients = Known(0L), Ideology = Known(ideology), ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = Known(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = Known(1d), FallPerTick = Known(0d) })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Known = true, Workers = EquatableArray.From(workers) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated },  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.NotEmpty(selected.Selection.Selected);
        Assert.True(selected.Selected.Maintenance[0].MissingXp.Value < selected.Baseline.Maintenance[0].MissingXp.Value);
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "maintenance-test", 100));
        batch.Validate(); Assert.Equal(melee ? 2 : 1, batch.Tasks.Count);
        var wire = RimGovernor.Contracts.Wire.WireJson.Deserialize<RimGovernor.Contracts.Wire.Operations.PawnActivitiesIntent>(
            RimGovernor.Contracts.Wire.WireJson.Serialize(new RimGovernor.Contracts.Wire.Operations.PawnActivitiesIntent { Batch = batch }));
        Assert.Equal(batch.Tasks[0].Step.Practice, wire.Batch!.Tasks[0].Step.Practice);
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            RimGovernor.Brain.Store.Codec.Codecs.Default.Write(writer, DomainAction.NewPawnActivitiesAction(new("practice-action"), batch));
        using var document = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        var stored = RimGovernor.Brain.Store.Codec.Codecs.Default.Read<DomainAction>(document.RootElement).PawnActivities!;
        Assert.Equal(batch.Tasks[0].Step.Practice, stored.Tasks[0].Step.Practice);
        Assert.Equal(batch.Orders[0].Steps[0].Practice, stored.Orders[0].Steps[0].Practice);
        if (melee) Assert.Throws<DomainException>(() => (batch with { Tasks = [batch.Tasks[0]] }).Validate());
        var emergency = facts with { Hostiles = Known(1L) };
        var postponed = ForecastCoupledCandidates.GenerateCoupledActivities(emergency, 300)!;
        Assert.Empty(ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = postponed.Context }, postponed).Selection.Selected);
        var revoked = new ActivityTaskRead { Practice = [new(batch.Tasks[0].Id, Known(false), [])] };
        Assert.Equal(Known(false), ForecastServiceInvestments.ReviewServiceActivityNeed(batch, Known(true), revoked));
        var absent = new ActivityTaskRead();
        Assert.Equal(Known(true), ForecastServiceInvestments.ReviewServiceActivityNeed(batch, Known(true), absent));
        Assert.All(batch.Tasks, t => Assert.Equal("pawn", t.Step.Practice!.Beneficiary));
        Assert.All(batch.Tasks, t => Assert.Equal("role", t.Step.Practice!.Role));
        var satisfied = facts with { ForecastObserved = facts.ForecastObserved with
        { WorkPawns = Known(EquatableArray.From(roster.Select(p => p with { Skills = Known<EquatableArray<WorkSkill>>([new() { Name = skill, Level = 1, Learning = Learning() with { Xp = Known(100d) } }]) }))) } };
        var recovered = ForecastCoupledCandidates.GenerateCoupledActivities(satisfied, 300)!;
        Assert.Empty(ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = recovered.Context }, recovered).Selection.Selected);
    }
}
