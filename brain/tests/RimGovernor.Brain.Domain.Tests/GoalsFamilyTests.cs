using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The goals family (#3494): each player goal is a Development Need with a pure met, unmet and unknown predicate; an
/// unmet goal is held under what pursues or blocks it; the demand of the goals rides the existing channels.
/// </summary>
public sealed class GoalsFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static GoalRecord Goal(int id, GoalKind kind, string target = "", int count = 0, string qualifier = "") =>
        new() { Id = id, Kind = kind, Target = target, Count = count, Qualifier = qualifier };

    private static ResearchFacts Research(params string[] finished) => new()
    {
        Projects = [new("Smithing"), new("Electricity"), new("Stonecutting")],
        Finished = [.. finished.Select(f => new ResearchProjectId(f))],
    };

    private static GoalsFacts Facts(params GoalRecord[] goals) => new()
    {
        Goals = goals,
        Research = Known(Research()),
        Tier = Known(TechTier.Camp),
        Stock = Known<EquatableArray<Amount>>([new Amount(new Resource("Steel"), 40)]),
        Colonists = 5L,
        Animals = Known<EquatableArray<UpkeepAnimal>>([new UpkeepAnimal { Definition = new Resource("Muffalo") }]),
    };

    private static Need Derive(GoalsFacts facts, GoalRecord goal) => Assert.Single(new GoalsFamily().Derive(facts), n => n.Id == GoalRules.NeedOf(goal));

    private static Ideoligion Ideo(bool fluid, params string[] precepts) => new(new IdeologyDefs { Precepts = new Dictionary<string, PreceptDef> { ["Slavery_Acceptable"] = new([]) } }, new IdeoligionFacts
    {
        Precepts = [.. precepts.Select((p, i) => new HeldPrecept("p" + i, p))],
        Development = Known(new IdeoDevelopment(fluid, fluid, 0, 0, 0)),
    });

    [Fact]
    public void AGoalIsADevelopmentNeedNamedForItsKindAndId()
    {
        var goal = Goal(7, GoalKind.ResearchProject, "Smithing");
        var need = Derive(Facts(goal), goal);
        Assert.Equal("goals/research-project/7", need.Id.Value);
        Assert.Equal((NeedTier.Development, Satisfaction.Unmet, 1.0), (need.Tier, need.Satisfaction, need.Deficit.Value));
    }

    [Fact]
    public void AResearchProjectGoalIsUnmetAndWantedUntilTheProjectIsFinished()
    {
        var goal = Goal(1, GoalKind.ResearchProject, "Smithing");
        var open = Facts(goal);
        Assert.Equal(Satisfaction.Unmet, Derive(open, goal).Satisfaction);
        Assert.Equal(["Smithing"], GoalRules.DemandOf(open).Research);

        var done = open with { Research = Known(Research("Smithing")) };
        Assert.Equal((Satisfaction.Met, 0.0), (Derive(done, goal).Satisfaction, Derive(done, goal).Deficit.Value));
        Assert.Empty(GoalRules.DemandOf(done).Research);

        // A project the census does not list, or an unread census, is unknown, never met.
        var unlisted = Goal(1, GoalKind.ResearchProject, "Nope");
        Assert.Equal(Satisfaction.Unknown, Derive(Facts(unlisted), unlisted).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Derive(open with { Research = default }, goal).Satisfaction);
    }

    [Fact]
    public void AResearchTierGoalWantsTheProjectsOfEachTierAboveTheOneReached()
    {
        var goal = Goal(1, GoalKind.ResearchTier, "Powered");
        var facts = Facts(goal);
        Assert.Equal(["Electricity", "Stonecutting"], GoalRules.DemandOf(facts).Research);
        Assert.Equal((Satisfaction.Unmet, 2.0), (Derive(facts, goal).Satisfaction, Derive(facts, goal).Deficit.Value));
        Assert.Equal(Satisfaction.Met, Derive(facts with { Tier = Known(TechTier.Industrial) }, goal).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Derive(facts with { Tier = default }, goal).Satisfaction);
    }

    private static GoalsFacts WithLevels(GoalsFacts facts, string[] finished, params (string Project, RimGovernor.Contracts.Wire.Defs.TechLevel Level)[] levels) => facts with
    {
        Research = Known(new ResearchFacts
        {
            Projects = [.. levels.Select(l => new ResearchProjectId(l.Project))],
            Finished = [.. finished.Select(f => new ResearchProjectId(f))],
            Levels = levels.ToDictionary(l => new ResearchProjectId(l.Project), l => l.Level),
        }),
    };

    [Fact]
    public void AResearchTierGoalListsTheUnfinishedProjectsAtThatTechLevelBesideTheUnlockChain()
    {
        var goal = Goal(1, GoalKind.ResearchTier, "Industrial");
        var facts = WithLevels(Facts(goal), ["Smithing", "Machining"],
            ("Smithing", RimGovernor.Contracts.Wire.Defs.TechLevel.Medieval),
            ("Machining", RimGovernor.Contracts.Wire.Defs.TechLevel.Industrial),
            ("Fabrication", RimGovernor.Contracts.Wire.Defs.TechLevel.Industrial),
            ("Geothermal", RimGovernor.Contracts.Wire.Defs.TechLevel.Industrial),
            ("Cryptosleep", RimGovernor.Contracts.Wire.Defs.TechLevel.Spacer));
        Assert.Equal(["Electricity", "Fabrication", "Geothermal", "Machining", "Stonecutting"], GoalRules.DemandOf(facts).Research);
        Assert.DoesNotContain("Cryptosleep", GoalRules.DemandOf(facts).Research);
        Assert.DoesNotContain("Smithing", GoalRules.DemandOf(facts).Research);
        Assert.Equal(Satisfaction.Met, Derive(facts with { Tier = Known(TechTier.Industrial) }, goal).Satisfaction);
        Assert.Empty(GoalRules.DemandOf(facts with { Tier = Known(TechTier.Industrial) }).Research);
    }

    [Fact]
    public void ATechLevelThatIsNoConstructionTierIsMetOnceItsProjectsAreFinished()
    {
        var goal = Goal(1, GoalKind.ResearchTier, "Medieval");
        var medieval = RimGovernor.Contracts.Wire.Defs.TechLevel.Medieval;
        var open = WithLevels(Facts(goal), ["Smithing"], ("Smithing", medieval), ("Smelting", medieval), ("Machining", RimGovernor.Contracts.Wire.Defs.TechLevel.Industrial));
        Assert.Equal(["Smelting"], GoalRules.DemandOf(open).Research);
        Assert.Equal((Satisfaction.Unmet, 1.0), (Derive(open, goal).Satisfaction, Derive(open, goal).Deficit.Value));
        var done = WithLevels(open, ["Smithing", "Smelting"], ("Smithing", medieval), ("Smelting", medieval));
        Assert.Equal(Satisfaction.Met, Derive(done, goal).Satisfaction);
        Assert.Empty(GoalRules.DemandOf(done).Research);
        Assert.Equal(Satisfaction.Unknown, Derive(open with { Research = default }, goal).Satisfaction);
    }

    [Fact]
    public void AStockCountGoalAddsExactlyItsFloorToTheResourceNeeds()
    {
        var goal = Goal(2, GoalKind.StockCount, "Steel", 100);
        var facts = Facts(goal);
        var need = Derive(facts, goal);
        Assert.Equal((Satisfaction.Unmet, 60.0), (need.Satisfaction, need.Deficit.Value));
        Assert.Equal(Satisfaction.Met, Derive(facts with { Stock = Known<EquatableArray<Amount>>([new Amount(new Resource("Steel"), 100)]) }, goal).Satisfaction);

        var demand = GoalRules.DemandOf(facts);
        Assert.Equal(new Dictionary<Resource, long> { [new Resource("Steel")] = 100 }, demand.Stock);
        var applied = GoalRules.Apply(new RoundsFacts { ResourceNeeds = new Dictionary<Resource, long> { [new Resource("Steel")] = 30, [new Resource("Wood")] = 50 } }, demand);
        Assert.Equal(new Dictionary<Resource, long> { [new Resource("Steel")] = 100, [new Resource("Wood")] = 50 }, applied.ResourceNeeds);
    }

    [Fact]
    public void AStockGoalIsPursuedByTheResourceNeedOnceItHasAMethod()
    {
        var goal = Goal(2, GoalKind.StockCount, "Steel", 100);
        var facts = Facts(goal) with { Owners = new Dictionary<NeedId, OwnerProgress> { [EconomyFamily.Resource] = new OwnerProgress(true, "") } };
        var status = GoalRules.Evaluate(goal, facts);
        Assert.Equal((GoalState.Pursuing, EconomyFamily.Resource), (status.State, status.Owner));

        var held = Assert.Single(new GoalsFamily().Propose([Derive(facts, goal)], facts));
        Assert.Equal("pursuing:economy/resource", held.Hold!.Subject);

        // A blocked owner with no method blocks the goal; a waiting one does not (the reader leaves its reason empty).
        var blocked = facts with { Owners = new Dictionary<NeedId, OwnerProgress> { [EconomyFamily.Resource] = new OwnerProgress(false, "no_worker") } };
        Assert.Equal((GoalState.Blocked, "no_worker"), (GoalRules.Evaluate(goal, blocked).State, GoalRules.Evaluate(goal, blocked).Reason));
        Assert.Equal("blocked:no_worker", Assert.Single(new GoalsFamily().Propose([Derive(blocked, goal)], blocked)).Hold!.Subject);
    }

    [Fact]
    public void AColonistsGoalAboveTheRosterRaisesTheJoinerCapacity()
    {
        var goal = Goal(3, GoalKind.Colonists, count: 150);
        var facts = Facts(goal);
        Assert.Equal((Satisfaction.Unmet, 145.0), (Derive(facts, goal).Satisfaction, Derive(facts, goal).Deficit.Value));
        Assert.Equal(150, GoalRules.DemandOf(facts).Colonists);

        // A full hundred hosted people: the bot's own target admits no more; the goal does.
        var full = new RoundsFacts
        {
            Custody = Known<EquatableArray<CustodyFacts>>([.. Enumerable.Range(0, 100).Select(i => new CustodyFacts
            {
                Pawn = new("c" + i),
                Dead = Known(false),
                Admitted = Known(true),
                Guest = Known(false),
            })]),
            FoodDays = Known(30.0),
        };
        Assert.False(PopulationRules.JoinerCapacity(full.JoinerCapacity()).Value);
        var raised = GoalRules.Apply(full, GoalRules.DemandOf(facts));
        Assert.Equal(150, raised.ColonistGoal);
        Assert.True(PopulationRules.JoinerCapacity(raised.JoinerCapacity()).Value);

        // Without a goal the target is today's.
        Assert.Equal(100, PopulationRules.PopulationTargetFor(0));
        Assert.Equal(100, PopulationRules.PopulationTargetFor(50));
    }

    [Fact]
    public void AnAnimalsGoalSetsTheHerdTargetOfItsRace()
    {
        var goal = Goal(4, GoalKind.Animals, "Muffalo", 6);
        var facts = Facts(goal);
        Assert.Equal((Satisfaction.Unmet, 5.0), (Derive(facts, goal).Satisfaction, Derive(facts, goal).Deficit.Value));
        var applied = GoalRules.Apply(new RoundsFacts(), GoalRules.DemandOf(facts));
        Assert.Equal(new Dictionary<Resource, long> { [new Resource("Muffalo")] = 6 }, applied.AnimalGoals);
        Assert.Equal(applied.AnimalGoals, applied.HerdPlanInput().Goals);
    }

    [Fact]
    public void AKindNothingPursuesIsAcceptedAndReportedWithAReason()
    {
        var goal = Goal(5, GoalKind.Caravan);
        var facts = Facts(goal);
        Assert.Equal(Satisfaction.Unknown, Derive(facts, goal).Satisfaction);
        var status = GoalRules.Evaluate(goal, facts);
        Assert.Equal((GoalState.NoMethod, "no_pursuit"), (status.State, status.Reason));
        Assert.Equal(GoalDemand.None.Colonists, GoalRules.DemandOf(facts).Colonists);
    }

    [Fact]
    public void APreceptGoalOnANonFluidIdeoligionReportsNotFluid()
    {
        var goal = Goal(6, GoalKind.Precept, "Slavery_Acceptable");
        var rigid = Facts(goal) with { Ideology = Known(Ideo(fluid: false)) };
        var status = GoalRules.Evaluate(goal, rigid);
        Assert.Equal((GoalState.NoMethod, "not fluid", Satisfaction.Unmet), (status.State, status.Reason, status.Satisfaction));
        Assert.Equal("no_method:not fluid", Assert.Single(new GoalsFamily().Propose([Derive(rigid, goal)], rigid)).Hold!.Subject);

        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, rigid with { Ideology = Known(Ideo(fluid: false, "Slavery_Acceptable")) }).State);
        Assert.Equal("facts_unread", GoalRules.Evaluate(goal, rigid with { Ideology = Known(Ideo(fluid: true)) }).Reason);
        Assert.Equal("missing_dlc", GoalRules.Evaluate(goal, rigid with { IdeologyInstalled = Known(false) }).Reason);
        Assert.Equal((GoalState.Blocked, "facts_unread"), (GoalRules.Evaluate(goal, Facts(goal)).State, GoalRules.Evaluate(goal, Facts(goal)).Reason));
    }

    private static PrisonerFacts Held(string pawn, bool recruitable = true) => new()
    {
        Pawn = new(pawn),
        Dead = Known(false),
        Prisoner = Known(true),
        Recruitable = Known(recruitable),
    };

    private static CustodyFacts Colonist(string pawn) => new() { Pawn = new(pawn), Dead = Known(false), Admitted = Known(true) };

    private static GoalsFacts PrisonersCensus(IEnumerable<PrisonerFacts> prisoners, params CustodyFacts[] custody) =>
        new() { Rounds = new RoundsFacts { Prisoners = Known<EquatableArray<PrisonerFacts>>([.. prisoners]), Custody = Known<EquatableArray<CustodyFacts>>([.. custody]) } };

    [Fact]
    public void ANamedPrisonerIsPursuedWhileHeldMetOnceAColonistAndHasNoMethodOtherwise()
    {
        var goal = Goal(1, GoalKind.NamedPrisoner, "p1");
        Assert.Equal((GoalState.Pursuing, Satisfaction.Unmet), Pair(GoalRules.Evaluate(goal, PrisonersCensus([Held("p1")]))));
        Assert.Equal(PrisonersFamily.Recruit, GoalRules.Evaluate(goal, PrisonersCensus([Held("p1")])).Owner);
        Assert.Equal((GoalState.Met, Satisfaction.Met), Pair(GoalRules.Evaluate(goal, PrisonersCensus([], Colonist("p1")))));

        var stranger = GoalRules.Evaluate(goal, PrisonersCensus([], Colonist("other")));
        Assert.Equal((GoalState.NoMethod, "not a prisoner"), (stranger.State, stranger.Reason));
        var unrecruitable = GoalRules.Evaluate(goal, PrisonersCensus([Held("p1", recruitable: false)]));
        Assert.Equal((GoalState.NoMethod, "not_recruitable"), (unrecruitable.State, unrecruitable.Reason));
        Assert.Equal(GoalState.Blocked, GoalRules.Evaluate(goal, new GoalsFacts()).State);

        var demand = GoalRules.DemandOf(PrisonersCensus([Held("p1")]) with { Goals = [goal] });
        Assert.Equal([new PawnId("p1")], demand.Prisoners);
        var stamped = GoalRules.Stamp(new RoundsFacts { PrisonerColony = Known(new PrisonerColony()) }, demand);
        Assert.Equal(demand.Prisoners, stamped.PrisonerColony.Value.NamedPrisoners);
    }

    private static (GoalState, Satisfaction) Pair(GoalStatus s) => (s.State, s.Satisfaction);

    private static JoinerOffer Offer(string quest, string state = "NotYetAccepted", QuestCost cost = QuestCost.Free) => new()
    {
        Quest = new(quest),
        ScriptDef = "MechanoidSignal",
        State = state,
        CanAccept = true,
        Profile = Known(new QuestProfile { Family = QuestFamily.OdysseyGround, Cost = cost, Disposition = QuestDisposition.Decide }),
    };

    private static GoalsFacts QuestFacts(params JoinerOffer[] offers) =>
        new() { Rounds = new RoundsFacts { QuestOffers = Known<EquatableArray<JoinerOffer>>([.. offers]) } };

    [Fact]
    public void ANamedQuestIsConsideredFirstAndAcceptedWhenFeasible()
    {
        var facts = QuestFacts(Offer("q1"), Offer("q9"));
        Assert.Equal(new QuestId("q1"), QuestSelection.SelectQuestMethod(facts.Rounds).Quest);

        var named = GoalRules.Stamp(facts.Rounds, GoalRules.DemandOf(facts with { Goals = [Goal(1, GoalKind.NamedQuest, "q9")] }));
        var choice = QuestSelection.SelectQuestMethod(named);
        Assert.Equal((JoinerPlanReason.None, new QuestId("q9")), (choice.Reason, choice.Quest));

        // An infeasible named quest does not displace the others.
        var blocked = GoalRules.Stamp(QuestFacts(Offer("q1"), Offer("q9", cost: QuestCost.Pawns)).Rounds, new GoalDemand { Quests = [new QuestId("q9")] });
        Assert.Equal(new QuestId("q1"), QuestSelection.SelectQuestMethod(blocked).Quest);
    }

    [Fact]
    public void ANamedQuestReportsTheFailingCheckAndItsEndedStates()
    {
        var goal = Goal(1, GoalKind.NamedQuest, "q9");
        var infeasible = GoalRules.Evaluate(goal, QuestFacts(Offer("q9", cost: QuestCost.Pawns)));
        Assert.Equal((GoalState.Blocked, "capacity_unknown", JoinersFamily.Quests), (infeasible.State, infeasible.Reason, infeasible.Owner));

        Assert.Equal(GoalState.Pursuing, GoalRules.Evaluate(goal, QuestFacts(Offer("q9"))).State);
        Assert.Equal(GoalState.Pursuing, GoalRules.Evaluate(goal, QuestFacts(Offer("q9", "Ongoing"))).State);
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, QuestFacts(Offer("q9", "EndedSuccess"))).State);
        var expired = GoalRules.Evaluate(goal, QuestFacts(Offer("q9", "EndedOfferExpired")));
        Assert.Equal((GoalState.NoMethod, "expired"), (expired.State, expired.Reason));
        var lapsed = GoalRules.Evaluate(goal, QuestFacts(Offer("q9") with { ExpiresInTicks = Known(0L) }));
        Assert.Equal((GoalState.NoMethod, "expired"), (lapsed.State, lapsed.Reason));
        Assert.Equal(GoalState.NoMethod, GoalRules.Evaluate(goal, QuestFacts(Offer("q1"))).State);
        Assert.Equal(GoalState.Blocked, GoalRules.Evaluate(goal, new GoalsFacts()).State);
    }

    [Fact]
    public void EveryGoalIsOneReviewRowWithItsThemeProgress()
    {
        var met = Goal(1, GoalKind.Colonists, count: 3) with { Theme = 9 };
        var open = Goal(2, GoalKind.StockCount, "Steel", 100) with { Theme = 9 };
        var loose = Goal(3, GoalKind.Caravan);
        var facts = Facts(met, open, loose) with { Themes = [new ThemeRecord { Id = 9, Name = "Start", Members = [1, 2] }] };

        var (statuses, themeStatuses) = GoalsRounds.Evaluate(facts);
        var rows = GoalsRounds.ReviewDecisions(statuses, themeStatuses);
        Assert.Equal(["1", "2", "3"], rows.Select(r => r.Target));
        Assert.All(rows, r => Assert.Equal("goal_review", r.Kind));
        Assert.Equal(["met", "pursuing", "no_method"], rows.Select(r => r.Verdict));
        Assert.Equal("no_pursuit", rows[2].Reason);
        Assert.Equal("economy/resource", rows[1].Attrs["owner"]);
        Assert.Equal(60.0, rows[1].Attrs["deficit"]);
        Assert.Equal(1, rows[1].Attrs["theme_met"]);
        Assert.Equal(2, rows[1].Attrs["theme_total"]);
        Assert.False(rows[2].Attrs.ContainsKey("theme"));
    }

    [Fact]
    public async Task TheFamilyDeclaresItsReadsAndRunsThroughTheJournal()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-goals-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a");
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);

        var skill = Goal(5, GoalKind.Skill, "pawn1", 10, "Shooting");
        var stock = Goal(2, GoalKind.StockCount, "Steel", 100);
        var done = Goal(1, GoalKind.Colonists, count: 3);
        var current = Facts(skill, stock, done) with { Owners = new Dictionary<NeedId, OwnerProgress> { [EconomyFamily.Resource] = new OwnerProgress(true, "") } };
        var runner = new FamilyRunner<GoalsFacts>(new GoalsFamily(), (_, _) => Task.FromResult(current), GoalsRounds.Schedule);
        var run = await runner.RunAsync(new FamilyStep { Journal = journal, World = World }, ct);

        Assert.Empty(run.UndeclaredReads);
        Assert.Empty(run.Methods);
        Assert.Equal(Verdict.BuildingReasonNoDeficit, Assert.Single(run.Reports, r => r.Need == GoalRules.NeedOf(done)).Verdict);
        Assert.True(Assert.Single(run.Reports, r => r.Need == GoalRules.NeedOf(skill)).Verdict.Is(Cause.FieldUnavailable));
        var pursued = Assert.Single(run.Reports, r => r.Need == GoalRules.NeedOf(stock)).Verdict;
        Assert.True(pursued.Is(Cause.AwaitingPlan));
        Assert.Equal("pursuing:economy/resource", pursued.Refusal.Subject);
    }

    [Fact]
    public void TheFamilyIsOneOptionalQueueEntryWokenByBuildings()
    {
        var entry = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<GoalsFacts>(new GoalsFamily(), (_, _) => Task.FromResult(new GoalsFacts()), GoalsRounds.Schedule));
        Assert.Equal(GoalsFamily.FamilyId, entry.Name);
        Assert.Equal([ActionKind.Building], entry.Kinds);
        Assert.False(entry.Immediate);
    }
}
