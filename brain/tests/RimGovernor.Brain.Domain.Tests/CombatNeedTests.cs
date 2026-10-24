using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The <c>military/combat</c> Need replaces the ActiveCombat incident (#3311): the review files a combat standing
/// while hostiles stand or the Need holds a fight, its fights are Need Methods, and an older save's incident-owned
/// fight closes on load while its saved restoration is kept.
/// </summary>
public sealed class CombatNeedTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static RoundsResult Reviewed(Journal journal, GenerationSnapshot world, long hostiles, long tick, CancellationToken ct, Fact<FoodPlan> foodPlan = default) => journal.ReviewRounds(new()
    {
        Current = world, Tick = new(tick), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Revision = journal.LoadRounds(ct).Revision,
        Facts = WorkResourceTestFacts.Create("a", "b") with { Hostiles = Known(hostiles), Tick = new(tick), FoodPlan = foodPlan },
    }, ct);

    /// <summary>
    /// A roster hunt is the Need's hunt origin: the plan's open formation channel files its prey and gunners on the
    /// standing, and once the formation is released (the plan opens none) the Need is met again with no hostile standing (#3514).
    /// </summary>
    [Fact]
    public void ARosterHuntFilesItsPreyAndGunnersAndAReleasedOneLeavesTheNeedMet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open("rg-combat-hunt-", ct);
        var formation = CandidateRules.FoodCandidate(CandidateKind.Hunt, "roster:a>boar", new YieldSupply.Steady(0), CandidateState.Closed, new CandidateLead.Days(0), new CandidateWork.PerDay(0)) with { Prey = ["boar"], Hunters = ["a"] };
        var opened = Known(new FoodPlan { Portfolio = [new() { Channel = formation, Decision = FoodPlanDecision.Open }] });
        var standing = Reviewed(journal, World, 0, 100, ct, opened).Review.Combat;
        Assert.Equal(Situation.Active, standing?.Situation);
        Assert.Equal(["boar"], standing!.Prey.Select(p => p.Value));
        Assert.Equal(["a"], standing.Hunters.Select(p => p.Value));

        Assert.Null(Reviewed(journal, World, 0, 110, ct, Known(new FoodPlan())).Review.Combat);
    }

    private static PlanSpec DraftPlan(string id) => new(new(id), new(1), [DomainAction.NewOwnedDraftAction(new(id + "-draft"), new(new("a")))]);

    private static Journal Open(string name, CancellationToken ct) =>
        Journal.Open(Path.Combine(Path.GetTempPath(), name + Guid.NewGuid().ToString("N") + ".db"), ct);

    [Fact]
    public void TheReviewFilesTheStandingWhileHostilesStandOrTheNeedHoldsAFight()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open("rg-combat-need-", ct);
        Assert.Null(Reviewed(journal, World, 0, 100, ct).Review.Combat);
        Assert.Equal(Situation.Active, Reviewed(journal, World, 1, 110, ct).Review.Combat?.Situation);

        var plan = DraftPlan("fight");
        var owner = journal.CommitCombatFight(World, new("engage-1"), plan, new CombatMemory(), [new("a")], ct);
        Assert.Equal(MilitaryFamily.Combat, owner.Need);
        Assert.Equal(("engage-1", plan.Id), (Assert.Single(owner.Methods).Method.Value, owner.Methods[0].Plan));
        Assert.True(journal.LoadCombatFight(plan.Id, ct)!.Open);

        // A second fight while the first is held is refused.
        _ = Assert.Throws<CombatWorkOpenException>(() => journal.CommitCombatFight(World, new("subdue-b"), DraftPlan("fight-2"), new CombatMemory(), [new("b")], ct));

        // Clear with the fight still open: the standing stays filed so the defense step can wind the fight down.
        Assert.Equal(Situation.Clear, Reviewed(journal, World, 0, 120, ct).Review.Combat?.Situation);

        journal.CloseCombatFight(plan.Id, ct);
        Assert.Null(Reviewed(journal, World, 0, 130, ct).Review.Combat);
    }

    /// <summary>
    /// A --resume restart reopens the same journal file (no game load): the open fight and its drafted roster are read
    /// back as they were committed, so the restarted controller keeps the defenders on it (#916).
    /// </summary>
    [Fact]
    public void AnOpenFightAndItsRosterSurviveReopeningTheJournal()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-combat-resume-" + Guid.NewGuid().ToString("N") + ".db");
        var plan = DraftPlan("resumed-fight");
        using (var journal = Journal.Open(path, ct))
        {
            _ = Reviewed(journal, World, 1, 100, ct);
            _ = journal.CommitCombatFight(World, new("engage-1"), plan, new CombatMemory(), [new("a"), new("b")], ct);
        }

        using var reopened = Journal.Open(path, ct);
        var fight = Assert.Single(reopened.OpenCombatFights(ct));
        Assert.Equal(plan.Id, fight.Key);
        Assert.True(fight.Value.Open);
        Assert.Equal(["a", "b"], fight.Value.Roster.Select(p => p.Value).Order(StringComparer.Ordinal));
        Assert.True(fight.Value.Roster.SetEquals(reopened.LoadCombatFight(plan.Id, ct)!.Roster));
    }

    [Fact]
    public void AnOpenFightClosesOnLoadAndItsRestorationIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open("rg-combat-old-save-", ct);
        _ = Reviewed(journal, World, 1, 100, ct);

        // The fight owns the saved restoration.
        var plan = DraftPlan("old-fight");
        _ = journal.CommitCombatFight(World, new("engage-1"), plan, new CombatMemory(), [new("a")], ct);
        var world = new Store.World(World.Colony, World.Load, World.Map);
        var restoration = new CombatRestoration(world, plan.Id, [new CombatDoorRestoration(new(3, 4), false, true)], []);
        journal.SaveCombatRestoration(restoration, ct);
        Assert.Throws<ConflictException>(() => journal.CloseCombatFight(plan.Id, ct));

        // The load sequence (GovernorState.WorldRebuild) on the same journal.
        var saved = journal.GovernorStateBlobs(ct);
        Assert.Contains(Journal.GovernorCombatRestorationKey, saved.Keys);
        journal.RebuildOwners(null, ct);
        journal.RebuildFamilies(saved, ct);
        journal.ResetRounds(ct);

        Assert.False(journal.LoadCombatFight(plan.Id, ct)!.Open);
        Assert.Empty(journal.OpenCombatFights(ct));
        Assert.Equal(restoration, journal.LoadCombatRestoration(ct));

        // The new load's review files the Need, which restores the saved settings and fights again.
        var reloaded = World with { Load = new("load-2") };
        Assert.Equal(Situation.Active, Reviewed(journal, reloaded, 1, 200, ct).Review.Combat?.Situation);
        var restore = new PlanSpec(new("restore"), new(1), [.. Journal.CombatRestorationActions(restoration, new("restore"))]);
        journal.CommitCombatRestoration(reloaded, restore, ct);
        var fight = DraftPlan("new-fight");
        _ = Assert.Throws<CombatWorkOpenException>(() => journal.CommitCombatFight(reloaded, new("engage-1"), fight, new CombatMemory(), [new("a")], ct));
        Assert.Contains(journal.LoadNeed(MilitaryFamily.Combat, reloaded, ct).Methods, m => m.Method.Value == "restore-restore");
    }
}
