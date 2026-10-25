using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// An older save's <c>standard/&lt;id&gt;</c> blob (governor state schema 6) as the retired Standards wrote it: the
/// review raises no Standard since #3265, so tests that load an older save write the blob themselves.
/// </summary>
internal static class LegacyStandardBlob
{
    internal static JsonObject Of(string id, string kind, GenerationSnapshot world, string? record = null)
    {
        var standard = new JsonObject
        {
            ["ID"] = id, ["Kind"] = kind, ["Snapshot"] = JsonNode.Parse(StoreJson.Serialize(world)), ["Episode"] = 1,
            ["Status"] = "open", ["Finding"] = "unmet", ["RecoveryObserved"] = false, ["Once"] = false,
        };
        if (record is not null)
        {
            standard["Record"] = record;
        }
        return new JsonObject { ["schemaVersion"] = 6, ["standard"] = standard, ["revision"] = 1 };
    }
}

/// <summary>
/// Saved trade missions across a save and reload: an older save's trade-mission Standard is dropped, its mission now
/// the <c>family/economy/trade_missions</c> record (#3316, decision 12).
/// </summary>
public sealed class OrphanStandardTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static TradeMission Mission(GenerationSnapshot world) => new()
    {
        HomeColony = world.Colony, HomeMap = world.Map, HomeTile = 1, Settlement = "settlement", SettlementTile = 2,
        Crew = [new("a")], Negotiator = new("a"), SilverBudget = 10, Demand = [new("Rice", 5)], Pack = [new("Silver", 10)],
        Phase = TradeMissionPhase.Outbound, ReturnHome = true,
    };

    [Fact]
    public void TradeMissionsRecordSurvivesSaveAndReload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-missions-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var home = new Store.World(World.Colony, World.Load, World.Map);
        source.SaveTradeMissions(new TradeMissionsRecord { World = home }.With("m-1", Mission(World)), ct);

        var saved = source.GovernorStateBlobs(ct);
        Assert.Contains(Journal.GovernorTradeMissionsKey, saved.Keys);

        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-missions-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);
        var reloaded = home with { Load = new("load-2") };
        var record = loaded.LoadTradeMissions(reloaded, ct);
        var entry = Assert.Single(record.Missions);
        Assert.Equal("m-1", entry.Id);
        Assert.Equal(Mission(World), entry.Mission);
        Assert.Equal(reloaded, record.World);

        // Another colony's save holds none of this colony's missions; a mission that is over leaves the record.
        Assert.Empty(loaded.LoadTradeMissions(home with { Colony = new("other") }, ct).Missions);
        loaded.SaveTradeMissions(record.With("m-1", Mission(World) with { Phase = TradeMissionPhase.Ended }), ct);
        Assert.Empty(loaded.LoadTradeMissions(reloaded, ct).Missions);
        Assert.DoesNotContain(Journal.GovernorTradeMissionsKey, loaded.GovernorStateBlobs(ct).Keys);
    }

    [Fact]
    public void OldSaveTradeMissionStandardBlobLoadsAndIsDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-old-mission-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        source.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RimGovernor.Brain.Policy.RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        var saved = new Dictionary<string, string>(source.GovernorStateBlobs(ct), StringComparer.Ordinal);

        // An older save's trade-mission Standard: Kind trade-mission, its intent in a Mission field the codec no longer reads.
        var blob = LegacyStandardBlob.Of("trade-mission-1", "trade-mission", World);
        var standard = blob["standard"]!;
        standard["Mission"] = new JsonObject { ["HomeColony"] = "colony", ["Settlement"] = "settlement", ["Phase"] = "outbound" };
        saved[Journal.LegacyStandardKeyPrefix + "trade-mission-1"] = blob.ToJsonString();

        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-old-mission-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);

        Assert.DoesNotContain(loaded.GovernorStateBlobs(ct).Keys, k => k == Journal.LegacyStandardKeyPrefix + "trade-mission-1");
        Assert.Empty(loaded.LoadTradeMissions(new Store.World(World.Colony, new("load-2"), World.Map), ct).Missions);
    }
}
