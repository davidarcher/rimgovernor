namespace RimGovernor.Brain.Runtime;

/// <summary>The native saved storage history shared by ordinary and direct planning sources.</summary>
public interface IReviewStorageSource
{
    Task<RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot> ReadStorageOccupancyAsync(IReadOnlyCollection<string> groups, int sinceHour, CancellationToken cancellationToken);
}
