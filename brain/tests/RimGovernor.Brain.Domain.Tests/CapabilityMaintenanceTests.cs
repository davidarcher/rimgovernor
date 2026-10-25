using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Domain.Tests;

public sealed class CapabilityMaintenanceTests
{
    internal static SkillLearning Learning() => new()
    {
        Disabled = false, TrackerAllowsLearning = true, StoredLevel = 1, Aptitude = 0,
        Xp = -95d, XpSinceMidnight = 0d, DirectFactor = 1d, UnsaturatedFactor = 1d,
        SaturationThreshold = 4000d, SaturatedFactor = .2, LossThreshold = -100d,
        DecayInterval = 200, NextDecay = 200, ValidForTicks = 1000, NextMidnightReset = 1000,
        TrackerIntervalTicks = 1, TrackerPhase = 0, TrackerElapsedTicks = 0,
        Levels = [new(0, 100, 0), new(1, 200, 10), new(2, 300, 20)],
        Practice = new SkillPractice.Range(true, true, 2, 3000d, 3000d)
    };
    internal static Ideoligion Ideology(params string[] holders) => new(new() { Roles = new Dictionary<string, RoleDef>
    { ["soldier"] = new(2, [new([new("Shooting", 1), new("Melee", 1)])]) } },
        new() { Development = RimGovernor.Brain.Policy.IdeoDevelopment.Fixed, Roles = [new("role", "soldier", true, holders.Select(h => new PawnId(h)).ToArray())] });

    [Fact]
    public void OnlyAssignedActiveRequirementsAuthorizeMaintenanceAndPreserveOr()
    {
        Assert.Empty(CapabilityMaintenanceRules.AssignedCombatCapabilities(new IdeologyMode.Installed(Ideology())));
        var requirement = Assert.Single(CapabilityMaintenanceRules.AssignedCombatCapabilities(new IdeologyMode.Installed(Ideology("pawn"))));
        Assert.Equal(2, requirement.Alternatives.Count);
        Assert.Empty(CapabilityMaintenanceRules.AssignedCombatCapabilities(new IdeologyMode.NotInstalled()));
        var inactive = Ideology("pawn") with { Facts = new() { Development = RimGovernor.Brain.Policy.IdeoDevelopment.Fixed, Roles = [new("role", "soldier", false, [new("pawn")])] } };
        Assert.Empty(CapabilityMaintenanceRules.AssignedCombatCapabilities(new IdeologyMode.Installed(inactive)));
    }

    [Fact]
    public void NormalWorkSuppressesPracticeAndUnknownOrBranchDoesNotProveFailure()
    {
        var requirement = Assert.Single(CapabilityMaintenanceRules.AssignedCombatCapabilities(new IdeologyMode.Installed(Ideology("pawn"))));
        WorkSkill[] skills = [new() { Name = "Shooting", Level = 1, Learning = Learning() }, new() { Name = "Melee", Disabled = true }];
        var completed = new Dictionary<string, bool> { ["work"] = true };
        ForecastMaintenanceConsequence Forecast(EquatableArray<WorkSkill> observed, ForecastActivityPlan plan)
            => ForecastMaintenancePolicy.ForecastMaintenance([new(requirement, observed)], [], plan, completed, 200)[0];
        Assert.True(Assert.IsType<ForecastMaintenanceGap.Projected>(Forecast(EquatableArray.From(skills), new([], 0, 0)).MissingXp).Xp > 0);
        var projection = ForecastSkillTests.Projected(ForecastSkillPolicy.ForecastSkill(Learning(), [], 0));
        var work = new ForecastActivityPlan([new("work", "pawn", 0, 0, 0, 1, 0, 0, 1, 1, 0)
        { Learning = new("Shooting", [new("work", 1, 20d, SkillXpPath.NormalTracker)], projection, projection, 1, 1, "native") }], 1, 1);
        Assert.Equal(new ForecastMaintenanceGap.Projected(0), Forecast(EquatableArray.From(skills), work).MissingXp);
        Assert.IsType<ForecastMaintenanceGap.Unprojectable>(Forecast([skills[0]], new([], 0, 0)).MissingXp);
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
        var roster = ((WorkPawnsRead.Read)facts.ForecastObserved.WorkPawns).Pawns.Select(p => p with { Skills = [new() { Name = skill, Level = 1, Learning = Learning() }] }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, skill, "target/" + i, new(i + 1, 1),
            melee ? "RimGovernor_Spar" : "RimGovernor_TrainRange", melee ? "RimGovernor_Spar" : "RimGovernor_TrainShooting",
            0d, 100d, Learning()) { Company = [], CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = 0, Thoughts = [], Equipment = [], Findings = [] }).ToArray();
        facts = facts with { Hostiles = Known(0L), CriticalPatients = Known(0L), Ideology = new IdeologyMode.Installed(ideology), ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = new WorkPawnsRead.Read(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!;
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated },  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.NotEmpty(selected.Selection.Selected);
        Assert.True(Assert.IsType<ForecastMaintenanceGap.Projected>(selected.Selected.Maintenance[0].MissingXp).Xp < Assert.IsType<ForecastMaintenanceGap.Projected>(selected.Baseline.Maintenance[0].MissingXp).Xp);
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
        var absent = ConstructionFixtures.NoTasks();
        Assert.Equal(ActivityNeed.Owed, ForecastServiceInvestments.ReviewServiceActivityNeed(batch, ActivityNeed.Owed, absent));
        var satisfied = facts with { ForecastObserved = facts.ForecastObserved with
        { WorkPawns = new WorkPawnsRead.Read(EquatableArray.From(roster.Select(p => p with { Skills = [new() { Name = skill, Level = 1, Learning = Learning() with { Xp = 100d } }] }))) } };
        var recovered = ForecastCoupledCandidates.GenerateCoupledActivities(satisfied, 300)!;
        Assert.Empty(ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = recovered.Context }, recovered).Selection.Selected);
    }
}
