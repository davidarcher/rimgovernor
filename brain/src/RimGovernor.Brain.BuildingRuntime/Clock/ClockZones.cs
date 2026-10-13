namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The step's refresher for the policy zone census: planners ask it through <c>ObservationApi.WithZones</c>, and
/// a review step asks it once up front. A review step reads the census whole once; any other ask serves the held
/// census until an invalidation marks it.
/// </summary>
internal sealed class ZoneRefresher(IZonesNative native, FactStore store) : IZonesSource
{
    // Set once a review step has refreshed the census.
    private bool _refreshed;

    /// <summary>The store scope the step fixed once its bundle was read.</summary>
    public FactScope Scope { get; set; }

    public long Tick { get; set; }

    /// <summary>Whether the step reviews: a review step reads the census whole once.</summary>
    public bool Review { get; set; }

    public async Task<Held<ZonesRead>> ZonesAsync(PbCommon.Identity id, CancellationToken ct)
    {
        var ok = FactStore.Read<ZonesRead>(store, FactSection.Zones, out var held);
        ok = ok && store.Scope == Scope
            && (held.Value.Context?.Identity?.ColonyId ?? "") == (id.ColonyId ?? "")
            && (held.Value.Context?.Identity?.MapId).GetValueOrDefault() == id.MapId.GetValueOrDefault();
        if (ok && (_refreshed || !Review))
        {
            return held;
        }
        var (read, _) = await native.ReadZoneSectionAsync(id, ct).ConfigureAwait(false);
        _refreshed = Review;
        var output = new Held<ZonesRead> { Value = read, AsOf = read.AsOf, Complete = true, Source = "rimgovernor/observations_list_zones" };
        FactStore.Put(store, Scope, FactSection.Zones, output);
        return output;
    }
}
