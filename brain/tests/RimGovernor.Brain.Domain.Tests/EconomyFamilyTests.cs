using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;
using Obs = RimGovernor.Contracts.Wire.Observations;

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

    private static KeyValuePair<string, ResearchProjectFacts> Project(string name, string[]? prerequisites = null, string[]? locks = null, string category = "", double cost = 1) => new(name, new()
    {
        KnowledgeCategory = category,
        Prerequisites = EquatableArray.From((prerequisites ?? []).Select(p => new ResearchProjectId(p))),
        LockReasons = EquatableArray.From(locks ?? []),
        Census = true,
        Cost = cost,
    });

    private static EconomyFacts Facts(string[] needs, string current = "", string[]? finished = null, KnowledgeSlot[]? slots = null, params KeyValuePair<string, ResearchProjectFacts>[] projects) => new()
    {
        ResearchNeeds = EquatableArray.From(needs),
        Research = Known(new ResearchCensus
        {
            Current = current,
            Projects = projects.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Finished = EquatableArray.From(finished ?? []),
            Knowledge = EquatableArray.From(slots ?? []),
        }),
    };

    [Fact]
    public void ResearchIsMetUnmetOrUnknownByTheNeedsAndTheCensus()
    {
        var none = EconomyFamily.ResearchNeed(new EconomyFacts());
        Assert.Equal((EconomyFamily.Research, NeedTier.Development, Satisfaction.Met, 0.0), (none.Id, none.Tier, none.Satisfaction, none.Deficit));

        var finished = EconomyFamily.ResearchNeed(Facts(["Smithing"], finished: ["Smithing"], projects: Project("Smithing")));
        Assert.Equal(Satisfaction.Met, finished.Satisfaction);

        var idle = EconomyFamily.ResearchNeed(Facts(["Smithing"], projects: Project("Smithing")));
        Assert.Equal((Satisfaction.Unmet, 1.0), (idle.Satisfaction, idle.Deficit));

        // A derived target stays owed while its project is current: the ladder waits on it finishing.
        Assert.Equal(Satisfaction.Unmet, EconomyFamily.ResearchNeed(Facts(["Smithing"], current: "Smithing", projects: Project("Smithing"))).Satisfaction);

        // No census is unknown, never met; a need the census does not list is no target at all.
        Assert.Equal("research census", Assert.Throws<RequiredFactException>(() => EconomyFamily.ResearchNeed(new EconomyFacts { ResearchNeeds = ["Smithing"] })).Fact);
        Assert.Equal(Satisfaction.Met, EconomyFamily.ResearchNeed(Facts(["Smithing"], projects: Project("Other"))).Satisfaction);
    }

    [Fact]
    public void AnEmptyKnowledgeSlotWithAProjectToFundIsUnmet()
    {
        var facts = Facts(["Smithing"], finished: ["Smithing"], slots: [new KnowledgeSlot { Category = "Basic" }],
            projects: [Project("Smithing"), Project("Study", category: "Basic")]);
        var need = EconomyFamily.ResearchNeed(facts);
        Assert.Equal((Satisfaction.Unmet, 1.0), (need.Satisfaction, need.Deficit));
        Assert.Equal(new ResearchStep("Study", null, false, false), EconomyRules.ResearchStepOf(facts));

        // A slot already funding is no spending need.
        var funding = facts with { Research = Known(facts.Research.Value! with { Knowledge = [new KnowledgeSlot { Category = "Basic", Current = new("Other") }] }) };
        Assert.Equal(Satisfaction.Met, EconomyFamily.ResearchNeed(funding).Satisfaction);
    }

    [Fact]
    public void TheStepSelectsThePrerequisiteFirstAndHoldsForTheBenchOrTheCurrentProject()
    {
        var next = EconomyRules.ResearchStepOf(Facts(["Smithing"], projects: [Project("Smithing", ["Stonecutting"]), Project("Stonecutting")]));
        Assert.Equal(new ResearchStep("Stonecutting", null, false, true), next);

        var bench = EconomyRules.ResearchStepOf(Facts(["Smithing"], projects: Project("Smithing", locks: [ResearchRules.ResearchLockBench])));
        Assert.Equal(("", "research_bench", "Smithing", true, true), (bench.Select, bench.Hold!.Subject, bench.Hold.Detail, bench.Bench, bench.BuildAhead));

        var current = EconomyRules.ResearchStepOf(Facts(["Smithing"], current: "Smithing", projects: Project("Smithing")));
        Assert.Equal(("current_research_project", true, true), (current.Hold!.Subject, current.Bench, current.BuildAhead));

        var broken = EconomyRules.ResearchStepOf(Facts(["Smithing"], projects: Project("Smithing", ["Missing"])));
        Assert.Equal(("field_unavailable", "research_prerequisites", false), (broken.Hold!.Subject, broken.Hold.Detail, broken.BuildAhead));

        Assert.Equal("research", EconomyRules.ResearchStepOf(new EconomyFacts { ResearchNeeds = ["Smithing"] }).Hold!.Detail);
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
        var claims = MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(), [new WorkAction(new("p"), action, new() { Action = action.Id, Stage = Stage.Pending })]);
        Assert.Equal(["research/Smithing"], claims.Targets);
    }

    [Fact]
    public void TheSelectionIsAdmittedUnderItsNeedAndContinuedWhileOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-economy-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
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
        Assert.Equal(Verdict.BuildingReasonExistingWork, Assert.Single(again.Reports).Verdict);
        Assert.Equal(ClaimOutcome.Continued, Assert.Single(again.Methods).Outcome);

        // Met: nothing more is proposed.
        economy = Facts(["Smithing"], finished: ["Smithing", "Stonecutting"], projects: [Project("Smithing", ["Stonecutting"]), Project("Stonecutting")]);
        Assert.Equal(Verdict.BuildingReasonNoDeficit, Assert.Single(Run().Reports).Verdict);
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
        var met = EconomyRules.ResourceNeedOf(Stock(150));
        Assert.Equal((EconomyFamily.Resource, Satisfaction.Met, NeedTier.Wellbeing), (met.Id, met.Satisfaction, met.Tier));

        var unmet = EconomyRules.ResourceNeedOf(Stock(25));
        Assert.Equal(Satisfaction.Unmet, unmet.Satisfaction);
        Assert.True(unmet.Deficit > 0, unmet.Deficit.ToString());

        // A runway known short is unmet with targets covered; its missing horizon share is the deficit.
        var runway = EconomyRules.ResourceNeedOf(Stock(150, Runway(Known(true), Known(1.0))));
        Assert.Equal(Satisfaction.Unmet, runway.Satisfaction);
        Assert.True(runway.Deficit > 0, runway.Deficit.ToString());

        // An unread census, or an unread runway with nothing short, is unknown: no latch holds the last answer.
        Assert.Equal("resource stock or runways", Assert.Throws<RequiredFactException>(() => EconomyRules.ResourceNeedOf(Stock(150) with { Stock = Unknown<EquatableArray<Amount>>() })).Fact);
        Assert.Equal("resource stock or runways", Assert.Throws<RequiredFactException>(() => EconomyRules.ResourceNeedOf(Stock(150, Runway(Unknown<bool>(), Unknown<double>())))).Fact);
        Assert.Equal(Satisfaction.Unmet, EconomyRules.ResourceNeedOf(Stock(25, Runway(Unknown<bool>(), Unknown<double>()))).Satisfaction);
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
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
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

    [Fact]
    public void TradeNeedsAreMetUnmetOrUnknownByTheReviewAndTheMissionRecord()
    {
        // The trade Need (#3316): the silver gap, unknown while unread.
        Assert.Equal("silver gap", Assert.Throws<RequiredFactException>(() => EconomyFamily.TradeNeed(new TradeReview())).Fact);
        Assert.Equal(Satisfaction.Met, EconomyFamily.TradeNeed(new TradeReview { SilverGap = Known(0.0) }).Satisfaction);
        var gap = EconomyFamily.TradeNeed(new TradeReview { SilverGap = Known(120.0) });
        Assert.Equal((EconomyFamily.Trade, NeedTier.Development, Satisfaction.Unmet, 1.0), (gap.Id, gap.Tier, gap.Satisfaction, gap.Deficit));

        // The caravan Need: met once every caravan present is traded out.
        Assert.Equal("caravan trade", Assert.Throws<RequiredFactException>(() => EconomyFamily.CaravanNeed(new TradeReview())).Fact);
        Assert.Equal(Satisfaction.Met, EconomyFamily.CaravanNeed(new TradeReview { CaravanRecovered = Known(true) }).Satisfaction);
        var caravan = EconomyFamily.CaravanNeed(new TradeReview { CaravanRecovered = Known(false) });
        Assert.Equal((EconomyFamily.Caravan, NeedTier.Wellbeing, Satisfaction.Unmet), (caravan.Id, caravan.Tier, caravan.Satisfaction));

        // The trade-mission Need: a mission under way or a proposed trip; unknown while the record is unread.
        Assert.Equal("trade missions", Assert.Throws<RequiredFactException>(() => EconomyFamily.TradeMissionNeed(new TradeMissionFacts { Proposed = true })).Fact);
        Assert.Equal(Satisfaction.Met, EconomyFamily.TradeMissionNeed(new TradeMissionFacts { Active = Known(false) }).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, EconomyFamily.TradeMissionNeed(new TradeMissionFacts { Active = Known(true) }).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, EconomyFamily.TradeMissionNeed(new TradeMissionFacts { Active = Known(false), Proposed = true }).Satisfaction);

        // The toggles compose them: the trade Need alone, or the caravan and trade-mission Needs together.
        var facts = new EconomyFacts { Trade = new TradeReview { SilverGap = Known(1.0), CaravanRecovered = Known(false) }, Missions = new TradeMissionFacts { Active = Known(false) } };
        Assert.Equal([EconomyFamily.Trade], new EconomyFamily(false, false, trade: true).Derive(facts).Select(n => n.Id));
        Assert.Equal([EconomyFamily.Caravan, EconomyFamily.TradeMission], new EconomyFamily(false, false, caravan: true).Derive(facts).Select(n => n.Id));
        Assert.Empty(new EconomyFamily(false, false).Derive(facts));
        Assert.Empty(new EconomyFamily(false, false, true, true).Propose(new EconomyFamily(false, false, true, true).Derive(facts), facts));
    }

    [Fact]
    public void CaravanTradePhasesAreTheNeedsMethodsHeldOnTheParticipantClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-caravan-claim-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
        var participant = new TradeParticipant(TradeParticipantKind.Settlement, "settlement", "caravan");
        PlanSpec Open(string plan, string pawn) => new(new(plan), new(1), [DomainAction.NewTradeAction(new(plan + "-trade"),
            Trade.NewTradeOpen(participant.Key(), new(pawn), false).WithParticipant(participant))]);

        // The phase's MethodId names the trader and phase; another Need's open with the same participant is held on its claim.
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(EconomyFamily.Caravan, World, new("trade-open-a"), "", Open("p1", "a"), ct).Outcome);
        var held = journal.CommitClaimedMethod(EconomyFamily.TradeMission, World, new("trade-open-m"), "", Open("p2", "b"), ct);
        Assert.Equal(ClaimOutcome.Conflict, held.Outcome);
        Assert.Equal("trade/" + participant.Key(), held.Conflict!.Claim);
        Assert.Empty(journal.LoadNeed(EconomyFamily.TradeMission, World, ct).Methods);
        Assert.Single(journal.OwnerMethodAttempts(journal.LoadNeed(EconomyFamily.Caravan, World, ct), new("trade-open-a"), ct));
    }

    /// <summary>A research read with no project listed, nothing finished and both slots empty.</summary>
    private static Obs.ResearchSnapshot NoResearch => new()
    {
        Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 1 },
        Slots = [new() { Category = "", CurrentProject = "" }],
        Projects = [],
        Finished = [],
    };

    [Fact]
    public void TheResearchReadOverlaysTheCatalogAndRoundTripsOnTheWire()
    {
        var read = NoResearch with
        {
            Slots = [new() { Category = "", CurrentProject = "Smithing" }, new() { Category = "Basic", CurrentProject = "" }],
            Projects = [new() { Project = new() { DefName = "Smithing" }, ProgressFraction = .25, Finished = false, Current = true, LockReasons = ["hidden"] }],
            Finished = ["Stonecutting"],
        };
        Assert.Equal(read, RimGovernor.Contracts.Wire.WireJson.Deserialize<Obs.ResearchSnapshot>(RimGovernor.Contracts.Wire.WireJson.Serialize(read)));
        var catalog = new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal)
        {
            ["Smithing"] = new() { Cost = 700 }, ["Stonecutting"] = new() { Cost = 300 }, ["Brewing"] = new() { Cost = 500 },
        };
        var overflow = new Dictionary<string, string>(StringComparer.Ordinal) { ["Basic"] = "" };
        var census = EconomyRules.ResearchCensusOf(read, catalog, overflow);
        Assert.Equal("Smithing", census.Current);
        Assert.Equal((.25, true, true), (census.Projects["Smithing"].Progress, census.Projects["Smithing"].Census, census.Projects["Smithing"].Hidden));
        // A project the read does not list is finished or unstarted.
        Assert.Equal((1.0, false), (census.Projects["Stonecutting"].Progress, census.Projects["Stonecutting"].Census));
        Assert.Equal(0.0, census.Projects["Brewing"].Progress);
        Assert.Equal(("Basic", ""), (Assert.Single(census.Knowledge).Category, census.Knowledge[0].Overflow));
        Assert.Throws<DomainException>(() => EconomyRules.ResearchCensusOf(read, new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal), overflow));
        // A knowledge slot of a category the catalog does not hold is a contract violation too.
        Assert.Throws<DomainException>(() => EconomyRules.ResearchCensusOf(read, catalog, new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Empty(EconomyRules.ResearchCensusOf(NoResearch, catalog, overflow).Current);
    }

    /// <summary>A world read of the home tile with no settlement in range.</summary>
    private static Obs.WorldSnapshot NoWorld => new()
    {
        Tile = new() { Tile = 7, Hilliness = "Flat" },
        Settlements = [],
    };

    [Fact]
    public void TheWorldReadRoundTripsOnTheWire()
    {
        var read = NoWorld with
        {
            Settlements = [new() { Id = "Settlement_1", Tile = 9, Relation = Obs.SettlementRelation.Neutral, CanTrade = true, TraderKind = "Base_Outlander_Standard" },
                new() { Id = "Settlement_2", Tile = 7, Relation = Obs.SettlementRelation.Player, CanTrade = false, TraderKind = "" }],
        };
        var json = RimGovernor.Contracts.Wire.WireJson.Serialize(read);
        Assert.Contains("\"relation\":\"neutral\"", json, StringComparison.Ordinal);
        Assert.Equal(read, RimGovernor.Contracts.Wire.WireJson.Deserialize<Obs.WorldSnapshot>(json));
        Assert.Equal(NoWorld, RimGovernor.Contracts.Wire.WireJson.Deserialize<Obs.WorldSnapshot>(RimGovernor.Contracts.Wire.WireJson.Serialize(NoWorld)));
    }
}
