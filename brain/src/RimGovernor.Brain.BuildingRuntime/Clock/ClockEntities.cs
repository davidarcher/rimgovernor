namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native side of the buildings and bills sections (the bridge client's ReadBuildings and ReadBillStacks); the scheduler requires it. The zone census is <see cref="IZonesNative"/>'s.</summary>
public interface IEntityNative
{
    Task<EntityRows<PbObservations.BuildingState>> ReadBuildingsAsync(PbCommon.Identity identity, CancellationToken ct);

    Task<EntityRows<PbObservations.BillStackRow>> ReadBillStacksAsync(PbCommon.Identity identity, CancellationToken ct);
}

internal static class ClockEntities
{
    /// <summary>
    /// The review step's refresher for the buildings and bills sections. It runs once per full review step after
    /// the bundle has fixed the step's scope, and reads each section whole: the snapshot stream's frame serves
    /// them. A failed read keeps the held section: a plan may reason over stale state, apply refuses stale intent.
    /// </summary>
    public static async Task RefreshEntitySectionsAsync(IEntityNative native, ClockFacts f, PbCommon.Identity identity, FactScope scope, CancellationToken ct)
    {
        // The zone census is the policy zone refresher's, typed differently under the zones section; this
        // refresher never reads zones.
        await RefreshEntitySectionAsync(f, scope, FactSection.Buildings, "rimgovernor/observations_list_buildings", async () => await native.ReadBuildingsAsync(identity, ct).ConfigureAwait(false)).ConfigureAwait(false);
        await RefreshEntitySectionAsync(f, scope, FactSection.Bills, "rimgovernor/observations_read_bills", async () => await native.ReadBillStacksAsync(identity, ct).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task RefreshEntitySectionAsync<T>(ClockFacts f, FactScope scope, FactSection section, string source, Func<Task<EntityRows<T>>> read)
        where T : class
    {
        EntityRows<T> full;
        try
        {
            full = await read().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        StoreTables.PutKeyed(f.Store, scope, Wire.Name(section), new KeyedTableRows<T>(full.Rows), Watermark.At(full.AsOf()));
        FactStore.Put(f.Store, scope, section, new Held<Table<T>> { Value = full.Rows, AsOf = full.AsOf(), Complete = true, Source = source });
    }
}
