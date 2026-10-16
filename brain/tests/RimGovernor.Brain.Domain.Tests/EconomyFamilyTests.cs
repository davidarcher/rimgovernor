using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The economy family's research Need (#3263): a pure met, unmet and unknown predicate over the recorded research
/// needs and the native census, a pure next step (select, hold, bench), and a selection admitted under the Need.
/// </summary>
public sealed class EconomyFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static ResearchProjectFacts Project(string name, string[]? prerequisites = null, string[]? locks = null, string category = "", double cost = 1) => new()
    {
        Name = new(name),
        Hidden = Known(false),
        KnowledgeCategory = category,
        Prerequisites = Known(EquatableArray.From((prerequisites ?? []).Select(p => new ResearchProjectId(p)))),
        HiddenPrerequisites = Known<EquatableArray<ResearchProjectId>>([]),
        LockReasons = EquatableArray.From(locks ?? []),
        Census = true,
        Cost = cost,
    };

    private static EconomyFacts Facts(string[] needs, string current = "", string[]? finished = null, KnowledgeSlot[]? slots = null, params ResearchProjectFacts[] projects) => new()
    {
        ResearchNeeds = EquatableArray.From(needs),
        Research = Known(new ResearchCensus
        {
            Current = current,
            Projects = projects.ToDictionary(p => p.Name.Value, p => p, StringComparer.Ordinal),
            Finished = EquatableArray.From(finished ?? []),
            Knowledge = EquatableArray.From(slots ?? []),
        }),
    };

    [Fact]
    public void ResearchIsMetUnmetOrUnknownByTheNeedsAndTheCensus()
    {
        var none = EconomyFamily.ResearchNeed(new EconomyFacts());
        Assert.Equal((EconomyFamily.Research, NeedTier.Development, Satisfaction.Met, 0.0), (none.Id, none.Tier, none.Satisfaction, none.Deficit.Value));

        var finished = EconomyFamily.ResearchNeed(Facts(["Smithing"], finished: ["Smithing"], projects: Project("Smithing")));
        Assert.Equal(Satisfaction.Met, finished.Satisfaction);

        var idle = EconomyFamily.ResearchNeed(Facts(["Smithing"], projects: Project("Smithing")));
        Assert.Equal((Satisfaction.Unmet, 1.0), (idle.Satisfaction, idle.Deficit.Value));
        Assert.Contains(idle.Evidence, e => e.Fact == "research_target" && e.Value.Value == "Smithing");

        // A derived target stays owed while its project is current: the ladder waits on it finishing.
        Assert.Equal(Satisfaction.Unmet, EconomyFamily.ResearchNeed(Facts(["Smithing"], current: "Smithing", projects: Project("Smithing"))).Satisfaction);

        // No census is unknown, never met; a need the census does not list is no target at all.
        var unread = EconomyFamily.ResearchNeed(new EconomyFacts { ResearchNeeds = ["Smithing"] });
        Assert.Equal(Satisfaction.Unknown, unread.Satisfaction);
        Assert.False(unread.Deficit.IsKnown);
        Assert.Equal(Satisfaction.Met, EconomyFamily.ResearchNeed(Facts(["Smithing"], projects: Project("Other"))).Satisfaction);
    }

    [Fact]
    public void AnEmptyKnowledgeSlotWithAProjectToFundIsUnmet()
    {
        var facts = Facts(["Smithing"], finished: ["Smithing"], slots: [new KnowledgeSlot { Category = "Basic" }],
            projects: [Project("Smithing"), Project("Study", category: "Basic")]);
        var need = EconomyFamily.ResearchNeed(facts);
        Assert.Equal((Satisfaction.Unmet, 1.0), (need.Satisfaction, need.Deficit.Value));
        Assert.Equal(new ResearchStep("Study", null, false, false), ResearchStepOf(facts));

        // A slot already funding is no spending need.
        var funding = facts with { Research = Known(facts.Research.Value! with { Knowledge = [new KnowledgeSlot { Category = "Basic", Current = new("Other") }] }) };
        Assert.Equal(Satisfaction.Met, EconomyFamily.ResearchNeed(funding).Satisfaction);
    }

    [Fact]
    public void TheStepSelectsThePrerequisiteFirstAndHoldsForTheBenchOrTheCurrentProject()
    {
        var next = ResearchStepOf(Facts(["Smithing"], projects: [Project("Smithing", ["Stonecutting"]), Project("Stonecutting")]));
        Assert.Equal(new ResearchStep("Stonecutting", null, false, true), next);

        var bench = ResearchStepOf(Facts(["Smithing"], projects: Project("Smithing", locks: [ResearchLockBench])));
        Assert.Equal(("", "research_bench", "Smithing", true, true), (bench.Select, bench.Hold!.Subject, bench.Hold.Detail, bench.Bench, bench.BuildAhead));

        var current = ResearchStepOf(Facts(["Smithing"], current: "Smithing", projects: Project("Smithing")));
        Assert.Equal(("current_research_project", true, true), (current.Hold!.Subject, current.Bench, current.BuildAhead));

        var broken = ResearchStepOf(Facts(["Smithing"], projects: Project("Smithing", ["Missing"])));
        Assert.Equal(("field_unavailable", "research_prerequisites", false), (broken.Hold!.Subject, broken.Hold.Detail, broken.BuildAhead));

        Assert.Equal("research", ResearchStepOf(new EconomyFacts { ResearchNeeds = ["Smithing"] }).Hold!.Detail);
    }

    [Fact]
    public void TheFamilyProposesTheSelectionOrHoldsTheNeed()
    {
        var family = new EconomyFamily();
        var ready = new[] { new Need { Id = EconomyFamily.Research, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet } };
        var select = Assert.Single(family.Propose(ready, Facts(["Smithing"], projects: Project("Smithing"))));
        Assert.Equal((EconomyFamily.Research, "research-Smithing"), (select.Need, select.Method.Value));
        var plan = select.Plan(new PlanId("p"));
        Assert.Equal("Smithing", Assert.Single(plan.Actions).ResearchSelect!.Project);

        var held = Assert.Single(family.Propose(ready, Facts(["Smithing"], current: "Smithing", projects: Project("Smithing"))));
        Assert.Equal("current_research_project", held.Hold!.Subject);
        Assert.Empty(family.Propose([], Facts(["Smithing"], projects: Project("Smithing"))));
    }

    [Fact]
    public void AResearchSelectionClaimsItsProject()
    {
        var action = DomainAction.NewResearchSelectAction(new("r"), new ResearchSelect("Smithing"));
        var claims = MethodClaimsOf(new WorkAdmissionEvidence(), [new WorkAction(new("p"), action, new() { Action = action.Id, Stage = Stage.Pending })]);
        Assert.Equal(["research/Smithing"], claims.Targets);
    }

    [Fact]
    public void TheSelectionIsAdmittedUnderItsNeedAndContinuedWhileOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-economy-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var economy = Facts(["Smithing"], projects: [Project("Smithing", ["Stonecutting"]), Project("Stonecutting")]);
        var runner = new FamilyRunner<EconomyFacts>(new EconomyFamily(), (_, _) => Task.FromResult(economy));
        FamilyRun Run() => runner.RunAsync(new FamilyStep { Journal = journal, World = World }, ct).GetAwaiter().GetResult();

        var run = Run();
        Assert.Equal(Outcome.Admitted, Assert.Single(run.Reports).Verdict.Outcome);
        var method = Assert.Single(run.Methods);
        Assert.Equal("research-Stonecutting", method.Method.Value);
        Assert.Equal(EconomyFamily.Research.FilingKey, journal.PlanMethod(method.Plan, ct)!.Concern);
        Assert.Equal("Research: research Stonecutting", OperationIntent.Of(journal.PlanMethod(method.Plan, ct)!));

        var again = Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Assert.Single(again.Reports).Verdict);
        Assert.Equal(ClaimOutcome.Continued, Assert.Single(again.Methods).Outcome);

        // Met: nothing more is proposed.
        economy = Facts(["Smithing"], finished: ["Smithing", "Stonecutting"], projects: [Project("Smithing", ["Stonecutting"]), Project("Stonecutting")]);
        Assert.Equal(BuildingRuntimePackage.BuildingReasonNoDeficit, Assert.Single(Run().Reports).Verdict);
    }
}
