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