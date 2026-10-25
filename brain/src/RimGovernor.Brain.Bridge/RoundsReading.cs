using Identity = RimGovernor.Brain.Bridge.Contracts.ObservationIdentity;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using C = RimGovernor.Contracts.Wire.Common;
namespace RimGovernor.Brain.Bridge;

public interface IRoundsSource : IColonySource, IDefinitionSource
{
    Task<RoundsFrame> ReadRoundsFrameAsync(C.Identity identity, CancellationToken cancellationToken);
}

public sealed record RoundsReading
{
    /// <summary>The colony reading the frame decoded to.</summary>
    public required ColonyReading Reading { get; init; }

    /// <summary>When the embedded reading began.</summary>
    public DateTimeOffset StartedAt => Reading.StartedAt;

    /// <summary>When the embedded reading finished.</summary>
    public DateTimeOffset ObservedAt => Reading.ObservedAt;


    public required EmergencyFacts Emergency { get; init; }

    /// <summary>The reading's decoded census, per section with the tick each reply described, for the step's fact store.</summary>
    public RoundsSections Sections { get; init; } = new();

    /// <summary>The frame the reading decoded.</summary>
    public required RoundsFrame Frame { get; init; }

    /// <summary>The load's definition catalog the reading decoded against, null for a source without one.</summary>
    public DefinitionCatalog? Catalog { get; init; }
}

