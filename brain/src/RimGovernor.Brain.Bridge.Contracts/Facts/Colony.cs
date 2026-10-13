using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;

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

// Go colony.go. The colony facts read and its validators (ValidateColonyFacts) are wire plumbing and not ported;
// the threat projection and the not-requested planning answer are.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>ProjectColonyThreat</c>: reads the threat section of a validated census.</summary>
    public static ColonyThreat ProjectColonyThreat(ColonyFactsSnapshot census)
    {
        var facts = census.Threat?.Observed;
        if (facts is null)
        {
            return new ColonyThreat();
        }

        static Fact<double> Number(bool present, double value) => present && double.IsFinite(value) ? Known(value) : Unknown<double>();

        return new ColonyThreat
        {
            RaidPoints = Number(facts.HasRaidPoints, facts.RaidPoints),
            WealthTotal = Number(facts.HasWealthTotal, facts.WealthTotal),
            WealthItems = Number(facts.HasWealthItems, facts.WealthItems),
            WealthBuildings = Number(facts.HasWealthBuildings, facts.WealthBuildings),
            WealthPawns = Number(facts.HasWealthPawns, facts.WealthPawns),
            StorytellerWealth = Number(facts.HasStorytellerWealth, facts.StorytellerWealth),
            AdaptationFactor = Number(facts.HasAdaptationFactor, facts.AdaptationFactor),
            DifficultyThreatScale = Number(facts.HasDifficultyThreatScale, facts.DifficultyThreatScale),
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
            Unavailable = new Unavailable { Reason = UnavailableReason.NotRequested, Detail = "Planning was not requested." },
        };
}
