namespace RimGovernor.Brain.Runtime;

public static class ZonesReads
{
    private static readonly AsyncLocal<IZonesSource?> ZonesScope = new();

    /// <summary>Attaches <paramref name="source"/> to the current async flow; dispose the result to detach it.</summary>
    public static IDisposable WithZones(IZonesSource source)
    {
        var previous = ZonesScope.Value;
        ZonesScope.Value = source;
        return new ZonesRestore(previous);
    }

    /// <summary>
    /// Serves the scheduler's zone section, or a full census outside a scheduler step (for standalone planners
    /// and inspection).
    /// </summary>
    public static async Task<Held<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>> ReadZoneSection(IZonesNative? native, RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken)
    {
        if (ZonesScope.Value is { } zones)
        {
            return await zones.ZonesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        if (native is null)
        {
            throw new BridgeUnavailableException();
        }
        var read = await native.ReadZoneSectionAsync(id, cancellationToken).ConfigureAwait(false);
        return new Held<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> { Value = read, AsOf = read.Context.Tick, Complete = true, Source = "observations_list_zones" };
    }

    private sealed class ZonesRestore(IZonesSource? previous) : IDisposable
    {
        public void Dispose() => ZonesScope.Value = previous;
    }
}
