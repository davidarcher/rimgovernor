using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Saved <c>standard/&lt;id&gt;</c> rows across a save and reload (#3249): the review after a load mints new
/// load-token-hashed ids, so a routine row is never re-bound and is no longer re-inserted. Trade missions and
/// Record-carrying rows still load.
/// </summary>
public sealed class OrphanStandardTests
{
    [Fact]
    public void ReloadNeverRebindsSavedRoutineRowsAndKeepsOnlyRecordsAndMissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var facts = WorkResourceTestFacts.Create("a", "b");
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-orphan-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var review = source.ReviewRounds(new() { Current = world, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        Assert.NotEmpty(review.Standards);
        var ledger = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainMechs);
        var delivery = DomainAction.NewPawnActivitiesAction(new("deliver"), new(1, [new("a", "assignment", "record-method", 0, 1, false,
            [new(PawnActivityKind.Deliver, "stack", "", "", 1, 500) { Destination = new(2, 2) }])]));
        source.CommitMethodRecord(ledger.Standard.Id, ledger.Revision, new("record-method"), "",
            new PlanSpec(new("record-plan"), new(1), [delivery]), "{\"kept\":true}", ct);
        var mission = new TradeMission
        {
            HomeColony = world.Colony, HomeMap = world.Map, HomeTile = 1, Settlement = "settlement", SettlementTile = 2,
            Crew = [new("a")], Negotiator = new("a"), SilverBudget = 10, Demand = [new("Rice", 5)], Pack = [new("Silver", 10)],
            Phase = TradeMissionPhase.Planned, ReturnHome = true,
        };
        source.SeedStandard(Standard.NewTradeMissionStandard(new("trade-mission-1"), world, mission), ct);

        var saved = source.GovernorStateBlobs(ct);
        var savedIds = saved.Keys.Where(k => k.StartsWith(Journal.GovernorStandardKeyPrefix, StringComparison.Ordinal))
            .Select(k => k[Journal.GovernorStandardKeyPrefix.Length..]).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ledger.Standard.Id.Value, savedIds);
        Assert.Contains("trade-mission-1", savedIds);
        Assert.True(savedIds.Count > 2);

        // The load sequence (GovernorState.WorldRebuild): standards, families, then rounds reset, then a review.
        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-orphan-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildStandards(saved, null, ct);
        loaded.RebuildFamilies(saved, ct);
        loaded.ResetRounds(ct);
        var reloaded = world with { Load = new("load-2") };
        var after = loaded.ReviewRounds(new() { Current = reloaded, Tick = new(200), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts with { Tick = new(200) } }, ct);

        // No saved row is ever bound again: every concern got a freshly minted id.
        Assert.NotEmpty(after.Standards);
        Assert.DoesNotContain(after.Standards, s => savedIds.Contains(s.Standard.Id.Value));
        Assert.All(after.Standards, s => Assert.Equal(reloaded.Load, s.Standard.Snapshot.Load));

        // Only the trade mission and the Record row were re-inserted.
        var kept = loaded.GovernorStateBlobs(ct).Keys.Where(k => k.StartsWith(Journal.GovernorStandardKeyPrefix, StringComparison.Ordinal))
            .Select(k => k[Journal.GovernorStandardKeyPrefix.Length..]).Where(savedIds.Contains).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { ledger.Standard.Id.Value, "trade-mission-1" }.Order(StringComparer.Ordinal), kept);
        Assert.Equal("{\"kept\":true}", loaded.LoadStandard(ledger.Standard.Id, ct).Standard.Record);
        Assert.Single(loaded.OpenStandards(Concerns.TradeMission, ct));
    }
}
