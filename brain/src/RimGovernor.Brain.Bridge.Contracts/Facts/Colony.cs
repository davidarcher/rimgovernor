using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ColonyThreat</c>: the threat section of one colony census as facts, the wealth split and the raid
/// points the storyteller would draw now. A section native did not observe (absent, unavailable, or a field it
/// withheld) leaves the fact unknown, never zero.
/// </summary>
public sealed record ColonyThreat
{
    public Fact<double> RaidPoints { get; init; }

    public Fact<double> WealthTotal { get; init; }

    public Fact<double> WealthItems { get; init; }

    public Fact<double> WealthBuildings { get; init; }

    public Fact<double> WealthPawns { get; init; }

    public Fact<double> StorytellerWealth { get; init; }

    public Fact<double> AdaptationFactor { get; init; }

    public Fact<double> DifficultyThreatScale { get; init; }

    /// <summary>The hostile lords seen on the map since load.</summary>
    public IReadOnlyList<RaidTrack> Raids { get; init; } = [];
}

// Go colony.go. The colony facts read is wire plumbing and not ported; ValidateColonyFacts is (ColonyValidation.cs and its section files);
// so are the threat projection and the not-requested planning answer.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>ProjectColonyThreat</c>: reads the threat section of a validated census.</summary>
    public static ColonyThreat ProjectColonyThreat(ColonyFactsSnapshot census)
    {
        var facts = census.Threat?.Outcome?.Observed;
        if (facts is null)
        {
            return new ColonyThreat();
        }

        static Fact<double> Number(double? value) => value is { } v && double.IsFinite(v) ? Known(v) : Unknown<double>();

        return new ColonyThreat
        {
            RaidPoints = Number(facts.RaidPoints),
            WealthTotal = Number(facts.WealthTotal),
            WealthItems = Number(facts.WealthItems),
            WealthBuildings = Number(facts.WealthBuildings),
            WealthPawns = Number(facts.WealthPawns),
            StorytellerWealth = Number(facts.StorytellerWealth),
            AdaptationFactor = Number(facts.AdaptationFactor),
            DifficultyThreatScale = Number(facts.DifficultyThreatScale),
            Raids = ValidateRaidTracks(facts.Raids), // validated with the census
        };
    }

    /// <summary>
    /// Go's <c>NotRequestedPlanning</c>: the planning section of a colony facts read that did not ask for planning,
    /// as the native answers it.
    /// </summary>
    public static PlanningSection NotRequestedPlanning() =>
        new()
        {
            Outcome = new Unavailable { Reason = UnavailableReason.NotRequested, Detail = "Planning was not requested." },
        };
}
