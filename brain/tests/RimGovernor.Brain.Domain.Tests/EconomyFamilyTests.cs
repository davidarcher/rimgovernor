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
/// Its resource Need (#3303): a plain threshold over the targets, the census and the runways, held on the research
/// gate, with independent Methods admitted side by side by their claims.
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

    private static readonly Resource Steel = new("Steel");

    private static ResourceFacts Stock(long steel, params ResourceRunway[] runways) => new()
    {
        Targets = new Dictionary<Resource, long> { [Steel] = 100 },
        Stock = Known<EquatableArray<Amount>>([new(Steel, steel)]),
        Runways = EquatableArray.From(runways),
    };

    private static ResourceRunway Runway(Fact<bool> deficit, Fact<double> daysLeft) =>
        new() { Resource = Steel, WindowDays = 2, Reserve = 10, Deficit = deficit, DaysLeft = daysLeft };

    [Fact]
    public void TheResourceNeedIsMetUnmetOrUnknownByTheTargetsTheCensusAndTheRunways()
    {
        var met = ResourceNeedOf(Stock(150));
        Assert.Equal((EconomyFamily.Resource, Satisfaction.Met, NeedTier.Wellbeing), (met.Id, met.Satisfaction, met.Tier));
        Assert.Contains(met.Evidence, e => e.Fact == "resource_targets");

        var unmet = ResourceNeedOf(Stock(25));
        Assert.Equal(Satisfaction.Unmet, unmet.Satisfaction);
        Assert.True(unmet.Deficit.TryGetValue(out var deficit) && deficit > 0, unmet.Deficit.ToString());

        // A runway known short is unmet with targets covered; its missing horizon share is the deficit.
        var runway = ResourceNeedOf(Stock(150, Runway(Known(true), Known(1.0))));
        Assert.Equal(Satisfaction.Unmet, runway.Satisfaction);
        Assert.True(runway.Deficit.TryGetValue(out var share) && share > 0, runway.Deficit.ToString());

        // An unread census, or an unread runway with nothing short, is unknown: no latch holds the last answer.
        var unread = ResourceNeedOf(Stock(150) with { Stock = Unknown<EquatableArray<Amount>>() });
        Assert.Equal(Satisfaction.Unknown, unread.Satisfaction);
        Assert.False(unread.Deficit.IsKnown);
        Assert.Equal(Satisfaction.Unknown, ResourceNeedOf(Stock(150, Runway(Unknown<bool>(), Unknown<double>()))).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, ResourceNeedOf(Stock(25, Runway(Unknown<bool>(), Unknown<double>()))).Satisfaction);
    }

    [Fact]
    public void TheResourceNeedIsHeldOnTheResearchGateUntilTheWorksDemandStock()
    {
        var family = new EconomyFamily(false, true);
        var facts = new EconomyFacts { Resource = Stock(25) with { ResearchGate = "Smithing" } };
        var ready = family.Derive(facts);
        Assert.Equal([EconomyFamily.Resource], ready.Select(n => n.Id));
        var held = Assert.Single(family.Propose(ready, facts));
        Assert.Equal((EconomyFamily.Resource, "research", "Smithing"), (held.Need, held.Hold!.Subject, held.Hold!.Detail));

        Assert.Empty(family.Propose(ready, facts with { Resource = facts.Resource with { Works = true } }));
        Assert.DoesNotContain(new EconomyFamily(true, false).Derive(facts), n => n.Id == EconomyFamily.Resource);
    }

    private static DomainAction Zone(string id, params (int X, int Z)[] cells) =>
        DomainAction.NewZoneCreateAction(new(id), new ZoneCreate(ZoneKind.Growing, "Plant_Haygrass", cells.Select(c => new Cell(c.X, c.Z))));

    private static PlanSpec Plan(DomainAction action)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new(1), [action]);
    }

    [Fact]
    public void ResourceMethodsAreAdmittedSideBySideUnderTheNeedAndAnOverlapIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-economy-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var methods = new List<(string Method, DomainAction Action)>
        {
            ("fields-hay", Zone("z1", (10, 10), (10, 11))),
            ("fields-social", Zone("z2", (12, 10), (12, 11))),
        };
        var economy = new EconomyFacts { Resource = Stock(25) };
        var runner = new FamilyRunner<EconomyFacts>(new EconomyFamily(false, true), (_, _) => Task.FromResult(economy), EconomyRounds.Schedule,
            (work, _, call) =>
            {
                var verdicts = methods.Select(m => RoundsBuildingPlanner.OwnerCommitVerdict(journal.CommitOwnerWork(work.Owner, new MethodId(m.Method), "", Plan(m.Action), call))).ToList();
                return Task.FromResult(verdicts.Count == 0 ? Verdict.AwaitingPlan(work.Need.Id.Value, "no_method") : verdicts[^1]);
            });
        FamilyRun Run() => runner.RunAsync(new FamilyStep { Journal = journal, World = World }, ct).GetAwaiter().GetResult();

        var run = Run();
        Assert.Equal(2, run.Methods.Count);
        Assert.All(run.Methods, m => Assert.Equal((EconomyFamily.Resource, ClaimOutcome.Admitted), (m.Need, m.Outcome)));
        Assert.All(run.Methods, m => Assert.Equal(EconomyFamily.Resource.FilingKey, journal.PlanMethod(m.Plan, ct)!.Concern));

        // The same MethodIds continue; nothing new is committed.
        Run();
        Assert.Equal(2, journal.LoadNeed(EconomyFamily.Resource, World, ct).Methods.Count);

        // A third Method on a cell the first holds is refused on that claim; the open Methods stand.
        methods.Clear();
        methods.Add(("fields-third", Zone("z3", (10, 11), (10, 12))));
        var refused = Assert.Single(Run().Reports, r => r.Need == EconomyFamily.Resource).Verdict;
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Equal(2, journal.LoadNeed(EconomyFamily.Resource, World, ct).Methods.Count);
    }
}
