using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.GameAccess;

/// <summary>A canonical colony frame and its reviewed planning inputs, with observation provenance.</summary>
public sealed record ColonyReading
{
    public RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot Snapshot => Facts.Census;
    public required Tables Tables { get; init; }
    public required ObservationIdentity Identity { get; init; }
    public required ReviewFacts Facts { get; init; }

    /// <summary>
    /// What the transition removals this review admitted so far rely on (TransitionBridgingRules): one ledger for the reading,
    /// shared by every proposing path that compares a standing building against its facts, so a spare bed slot or a net's
    /// remaining generation serves one removal, not each.
    /// </summary>
    public TransitionBridgingLedger Bridging { get; init; } = new();
    public Held<PlanningCells>? Window { get; init; }
    public Held<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>? Zones { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>A caller that plans geometry must have requested the planning window.</summary>
    public PlanningCells PlanningWindow => Window?.Value ?? throw new RequiredFactException("planning window");
}

/// <summary>The planning window's region and classified site cells, retained with its producing read.</summary>
public sealed record PlanningCells
{
    public Rectangle Region { get; init; }
    public EquatableArray<SiteCell> Cells { get; init; }
}