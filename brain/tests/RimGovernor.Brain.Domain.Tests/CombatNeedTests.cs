using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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

    private static RoundsResult Reviewed(Journal journal, GenerationSnapshot world, long hostiles, long tick, CancellationToken ct) => journal.ReviewRounds(new()
    {
        Current = world, Tick = new(tick), Enabled = true, Policy = DefaultRoundsPolicy(), Revision = journal.LoadRounds(ct).Revision,
        Facts = WorkResourceTestFacts.Create("a", "b") with { Hostiles = Known(hostiles), Tick = new(tick) },
    }, ct);

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
        _ = Assert.Throws<OpenMethodException>(() => journal.CommitCombatFight(World, new("subdue-b"), DraftPlan("fight-2"), new CombatMemory(), [new("b")], ct));

        // Clear with the fight still open: the standing stays filed so the defense step can wind the fight down.
        Assert.Equal(Situation.Clear, Reviewed(journal, World, 0, 120, ct).Review.Combat?.Situation);

        journal.CloseCombatFight(plan.Id, ct);
        Assert.Null(Reviewed(journal, World, 0, 130, ct).Review.Combat);
    }

    [Fact]
    public void AnOlderSavesIncidentOwnedFightClosesOnLoadAndItsRestorationIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open("rg-combat-old-save-", ct);
        _ = Reviewed(journal, World, 1, 100, ct);

        // An older save: the fight is an incident's Method and owns the saved restoration.
        var incident = journal.OpenIncident(new(EnsureMood, new("colony"), "observed_need", World, new(100)), ct);
        var plan = DraftPlan("old-fight");
        _ = journal.CommitIncidentMethod(incident.Incident.Id, new("combat-1"), "", plan, ct);
        var world = new Store.World(World.Colony, World.Load, World.Map);
        journal.OpenCombatFight(plan.Id, new CombatMemory(), world, [new("a")], ct);
        var restoration = new CombatRestoration(world, plan.Id, [new CombatDoorRestoration(new(3, 4), false, true)], []);
        journal.SaveCombatRestoration(restoration, ct);
        Assert.Throws<ConflictException>(() => journal.CloseCombatFight(plan.Id, ct));

        // The load sequence (GovernorState.WorldRebuild) on the same journal.
        var saved = journal.GovernorStateBlobs(ct);
        Assert.Contains(Journal.GovernorCombatRestorationKey, saved.Keys);
        journal.RebuildStandards(saved, null, ct);
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
        _ = Assert.Throws<OpenMethodException>(() => journal.CommitCombatFight(reloaded, new("engage-1"), fight, new CombatMemory(), [new("a")], ct));
        Assert.Contains(journal.LoadNeed(MilitaryFamily.Combat, reloaded, ct).Methods, m => m.Method.Value == "restore-restore");
    }
}
