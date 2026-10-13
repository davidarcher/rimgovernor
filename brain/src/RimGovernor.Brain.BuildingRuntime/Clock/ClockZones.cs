namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The step's refresher for the policy zone census: planners ask it through <c>ObservationApi.WithZones</c>, and
/// a review step asks it once up front. A review step reads the census whole once; any other ask serves the held
/// census until an invalidation marks it.
/// </summary>
internal sealed class ZoneRefresher(IZonesNative native, FactStore store, FactScope scope, long tick, bool review)
{
    // Set once a review step has refreshed the census.
    private bool _refreshed;

    public long Tick { get; } = tick;

    public async Task<Held<ZonesRead>> ZonesAsync(PbCommon.Identity id, CancellationToken ct)
    {
        var ok = FactsApi.TryRead<ZonesRead>(store, FactSection.Zones, out var held);
        ok = ok && store.Scope == scope
            && held.Value.Context?.Identity?.ColonyId == id.ColonyId
            && held.Value.Context?.Identity?.MapId == id.MapId;
        if (ok && (_refreshed || !review))
        {
            return held;
        }
        var read = await native.ReadZoneSectionAsync(id, ct).ConfigureAwait(false);
        _refreshed = review;
        var output = new Held<ZonesRead>(read, read.AsOf, true, "rimgovernor/observations_list_zones");
        FactsApi.Put(store, scope, FactSection.Zones, output);
        return output;
    }
}
