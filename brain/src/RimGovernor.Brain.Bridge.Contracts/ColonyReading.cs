using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>A canonical colony frame and its reviewed planning inputs, with observation provenance.</summary>
public sealed record ColonyReading
{
    public O.ColonyFactsSnapshot Snapshot => Facts.Census;
    public required Tables Tables { get; init; }
    public required ObservationIdentity Identity { get; init; }
    public required RoundsFacts Facts { get; init; }
    public Held<PlanningCells>? Window { get; init; }
    public Held<O.ZonesSnapshot>? Zones { get; init; }
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