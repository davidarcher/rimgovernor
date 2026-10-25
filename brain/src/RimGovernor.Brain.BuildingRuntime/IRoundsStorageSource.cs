namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native saved storage history shared by ordinary and direct planning sources.</summary>
public interface IRoundsStorageSource
{
    Task<RimGovernor.Contracts.Wire.Observations.StorageOccupancySnapshot> ReadStorageOccupancyAsync(IReadOnlyCollection<string> groups, int sinceHour, CancellationToken cancellationToken);
}
