using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The squad's armor is a named state (rated or unmirrored) and a quest departure's defense points come from the
/// defender roster (#3887).
/// </summary>
public sealed class SquadArmorAndDeparturePointsTests
{
    private static SquadDefenderFacts Brawler(string id, DefenderArmor armor) => new()
    {
        Id = new PawnId(id),
        ViolenceCapable = true,
        HealthFraction = 1.0,
        Armor = armor,
    };

    [Fact]
    public void BrawlersRankRatedArmorBestFirstAndUnmirroredArmorLast()
    {
        var pool = new[]
        {
            Brawler("a", new DefenderArmor.Unmirrored()),
            Brawler("b", new DefenderArmor.Rated(0.4)),
            Brawler("c", new DefenderArmor.Rated(0.9)),
            Brawler("d", new DefenderArmor.Unmirrored()),
        };

        Assert.Equal(["c", "b", "a", "d"], CombatBlockingRules.Brawlers(pool).Select(d => d.Id.Value));
    }

    [Fact]
    public void ARangedDefenderIsNoBrawler()
    {
        var pool = new[] { Brawler("a", new DefenderArmor.Rated(0.5)) with { RangedEquipped = true }, Brawler("b", new DefenderArmor.Unmirrored()) };

        Assert.Equal(["b"], CombatBlockingRules.Brawlers(pool).Select(d => d.Id.Value));
    }

    private static O.QuestWorker Worker(string id, bool canFight) => new()
    {
        PawnId = new(id),
        HealthyAdult = true,
        CanFight = canFight,
        Rates = EquatableArray.From(Array.Empty<O.QuestWorkRate>()),
        CarryCapacity = 35,
        CarriedMass = 0,
        FactionLeader = false,
        Negotiation = new O.QuestNegotiation.Incapable(),
    };

    private static WorldProgressionRead Read(params O.QuestWorker[] workers) => new()
    {
        Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
        Maps = [new O.WorldMap { Id = 0, Tile = 7, Home = true, Pawns = [], QuestWorkers = [.. workers] }],
    };

    private static DefenderStrength Strength(string id, double melee, double ranged) => new()
    {
        Id = new PawnId(id),
        Dead = false,
        Downed = false,
        ViolenceCapable = true,
        MeleePower = melee,
        RangedDps = ranged,
    };

    [Fact]
    public void ADeparturesDefensePointsAreTheFightersRosterStrengthAndZeroForANonFighter()
    {
        var roster = Known(EquatableArray.From(new[] { Strength("fighter", 3, 11) }));

        var (_, departures) = QuestReads.FrameQuestWorkerCapacity(Read(Worker("fighter", true), Worker("pacifist", false)), new MapId(0), roster);

        Assert.True(departures.TryGetValue(out var rows));
        Assert.Equal((3 + 11) * DefenseCapacityPolicy.DefensePointsPerDps, rows.Single(r => r.Id.Value == "fighter").DefensePoints, 9);
        Assert.Equal(0, rows.Single(r => r.Id.Value == "pacifist").DefensePoints);
    }

    [Fact]
    public void AFighterMissingFromTheRosterLeavesTheDeparturesUnread()
    {
        var roster = Known(EquatableArray.From(Array.Empty<DefenderStrength>()));

        var (capacity, departures) = QuestReads.FrameQuestWorkerCapacity(Read(Worker("fighter", true)), new MapId(0), roster);

        Assert.True(capacity.IsKnown);
        Assert.False(departures.IsKnown);
    }
}
