using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;
using Pol = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

// #3764: the census's threat section is required and every ThreatFacts member is read (#3539 added the storyteller
// facts); a threat read the native cannot complete fails the census, so a section missing a member does not decode.
public sealed class ThreatFactsTests
{
    private static readonly ThreatFacts Raided = ColonyFactsCoreTests.QuietThreat with
    {
        WealthItems = 1200, WealthBuildings = 3400, WealthPawns = 900, WealthTotal = 5500, RaidPoints = 310.5,
        Storyteller = "Randy", DaysPassed = 12.5, HostileFactions = ["Pirate"],
        Cycles = [new(3, 75, [.25, 12.5])],
        BiomeIncidents = [new("native-allowed", true), new("native-blocked", false)],
        Raids =
        [
            new RaidTrack
            {
                LordId = "lord-1", SpawnTick = new Tick(60000), Spawn = new Cell(0, 40), Ground = true, Range = 24.9,
                Trail = [new Cell(0, 40), new Cell(3, 41)],
            },
        ],
    };

    [Fact]
    public void TheThreatSectionRoundTripsExactly()
    {
        var facts = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = Raided } };
        var json = OrdersJson.Serialize(facts);
        Assert.Contains("\"trail\":[{\"x\":0,\"z\":40},{\"x\":3,\"z\":41}]", json);
        var back = OrdersJson.Deserialize<ColonyFactsSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(Raided, back.Threat.Observed);
        Assert.Equal(Raided, FactsJson.Deserialize<ThreatFacts>(FactsJson.Serialize(Raided)));
    }

    /// <summary>
    /// RG-HABITAT-009: the native saves its raid tracks with the map (RaidTrackLog, the native-raid-track-log probe), so the census
    /// after a load carries the ground raid seen before it and its arrival edge is priced; a save that never saw a raid names none.
    /// </summary>
    [Fact]
    public void ASavedGroundRaidPricesItsArrivalEdgeAfterLoadAndAFreshSaveNamesNoneObserved()
    {
        var loaded = FactsJson.Deserialize<ThreatFacts>(FactsJson.Serialize(Raided));
        var threat = Pol.DefenseCandidateRules.ThreatOf(new Pol.ReviewFacts { Census = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = loaded } }, DefenseCapacity = 621 });
        Assert.Equal([new Cell(0, 40)], threat.ArrivalEdges);
        Assert.Equal([new Cell(0, 40), new Cell(0, 40), new Cell(3, 41)], Assert.Single(threat.Trails));
        Assert.Equal(24.9, threat.Range);
        Assert.DoesNotContain(Pol.DefenseProtectionRules.ArrivalUnobserved, threat.Missing);

        var fresh = Pol.DefenseCandidateRules.ThreatOf(new Pol.ReviewFacts { Census = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = ColonyFactsCoreTests.QuietThreat } }, DefenseCapacity = 621 });
        Assert.Empty(fresh.ArrivalEdges);
        Assert.Contains("arrival_edges:none_observed", fresh.Missing);
    }

    [Fact]
    public void PlanningHerdAndMonolithKeepTheSameNativeThreatOwner()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = Raided } };
        var facts = new Pol.ReviewFacts { Census = snapshot, DefenseCapacity = 621 };
        var herd = Pol.FactsHerdPlanExtensions.HerdPlanInput(facts);
        var monolith = Pol.MonolithRules.MonolithGate(facts);

        Assert.Same(snapshot.Threat.Observed, facts.Census.Threat.Observed);
        Assert.Same(facts.Census.Threat.Observed, herd.Threat);
        Assert.Same(facts.Census.Threat.Observed, monolith.Threat);
        Assert.Equal(new Pol.RaidDefense(310.5, 621), Pol.RaidDefense.Of(facts));
        Assert.Equal(5500, herd.Budget);
    }

    [Fact]
    public void NativeCensusRetainsPopulationTemperatureAndWorkerInputsUnchanged()
    {
        var worker = new WorkResourcePawn("pawn", "held", 17, "started", "carried", "Rice", 4) { LocalHour = 6.75 };
        var temperature = new SleepingTemperature.Indoor(-7.25, 32.5);
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            ColonistCount = 7, WorkerCount = 3, BedCapacity = 5, IndoorSleepingCapacity = 4,
            OutdoorTemperatureC = -31.125, SleepingTemperature = temperature,
            WorkResources = [worker], Threat = new() { Observed = Raided },
        };
        var facts = WorkResourceTestFacts.Create("pawn") with { Census = snapshot };
        var shelter = Pol.ShelterFacts.From(facts, Pol.Rules.DefaultPlannerSettings());
        var evidence = Pol.ProductionRules.WorkAdmissionEvidenceOf(facts, new Pol.ProductionBatchCensus.Read([]));

        Assert.Same(snapshot, facts.Census);
        Assert.Equal((7U, 3U, 5U, 4U), (facts.Census.ColonistCount, facts.Census.WorkerCount, facts.Census.BedCapacity, facts.Census.IndoorSleepingCapacity));
        Assert.Equal((7L, 2), Pol.ShelterRules.StandInsOwed(facts));
        Assert.Equal((-7.25, 32.5), (shelter.SleepingMin, shelter.SleepingMax));
        Assert.Equal(-31.125, facts.Census.OutdoorTemperatureC);
        Assert.Same(temperature, facts.Census.SleepingTemperature);
        Assert.Same(worker, Assert.Single(evidence.WorkResources));
        Assert.Equal(6.75, Assert.Single(evidence.Pawns).LocalHour);
        Assert.Same(Raided, facts.Census.Threat.Observed);
    }

    [Fact]
    public void MissingNativeCensusRefusesEveryPlanningConsumer()
    {
        var facts = new Pol.ReviewFacts();

        Assert.Equal("colony census", Assert.Throws<Pol.RequiredFactException>(() => facts.Census.Threat.Observed).Fact);
        Assert.Throws<Pol.RequiredFactException>(() => Pol.RaidDefense.Of(facts));
        Assert.Throws<Pol.RequiredFactException>(() => Pol.FactsHerdPlanExtensions.HerdPlanInput(facts));
        Assert.Throws<Pol.RequiredFactException>(() => Pol.MonolithRules.MonolithGate(facts));
        Assert.Equal("colony census", Assert.Throws<Pol.RequiredFactException>(() => Pol.ShelterRules.StandInsOwed(facts)).Fact);
        Assert.Equal("colony census", Assert.Throws<Pol.RequiredFactException>(() =>
            Pol.ProductionRules.WorkAdmissionEvidenceOf(facts, new Pol.ProductionBatchCensus.Read([]))).Fact);
    }

    [Theory]
    [InlineData("storyteller")]
    [InlineData("daysPassed")]
    [InlineData("raidPoints")]
    [InlineData("raids")]
    [InlineData("disease")]
    [InlineData("trade")]
    [InlineData("biomeIncidents")]
    public void AThreatMissingAMemberDoesNotDecode(string member)
    {
        var facts = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = Raided } };
        var node = JsonNode.Parse(OrdersJson.Serialize(facts))!.AsObject();
        Assert.True(node["threat"]!["observed"]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }
}
