using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// The threat section of one colony census as facts, the wealth split and the raid
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

    /// <summary>The def names of the hostile factions that can raid; unknown when the threat section was not observed.</summary>
    public Fact<EquatableArray<string>> HostileFactions { get; init; }

    public Fact<string> Storyteller { get; init; }

    public Fact<double> DaysPassed { get; init; }

    /// <summary>The hostile lords seen on the map since load.</summary>
    public IReadOnlyList<RaidTrack> Raids { get; init; } = [];

    /// <summary>The colony's disease facts (#3824), as native read them; null when unread.</summary>
    public DiseaseFacts? Disease { get; init; }

    /// <summary>Reads the threat section of a census.</summary>
    public static ColonyThreat ProjectColonyThreat(ColonyFactsSnapshot census)
    {
        var facts = census.Threat?.Observed;
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
            HostileFactions = Known(facts.HostileFactions),
            Storyteller = string.IsNullOrEmpty(facts.Storyteller) ? Unknown<string>() : Known(facts.Storyteller),
            DaysPassed = Number(facts.DaysPassed),
            Raids = DefenseSiteRules.DecodeRaidTracks(facts.Raids),
            Disease = facts.Disease,
        };
    }
}
