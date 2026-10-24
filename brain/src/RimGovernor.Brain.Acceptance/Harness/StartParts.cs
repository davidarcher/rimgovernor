using RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The scenario parts a harness start adds to the base scenario; every list empty keeps the base's.</summary>
public sealed record StartParts
{
    public IReadOnlyList<StartingThing> StartingThings { get; init; } = [];

    public IReadOnlyList<ForcedTrait> ForcedTraits { get; init; } = [];

    public IReadOnlyList<ForcedHediff> ForcedHediffs { get; init; } = [];

    public IReadOnlyList<NakedPart> Naked { get; init; } = [];

    public IReadOnlyList<SetNeedLevel> SetNeedLevels { get; init; } = [];
}
