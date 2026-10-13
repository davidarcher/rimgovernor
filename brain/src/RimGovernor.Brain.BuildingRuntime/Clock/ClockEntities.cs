using Google.Protobuf;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native side of the buildings and bills sections (the bridge client's ReadBuildings and ReadBillStacks); the scheduler requires it. The zone census is <see cref="IZonesNative"/>'s.</summary>
public interface IEntityNative
{
    Task<EntityRows<PbObservations.BuildingState>> ReadBuildingsAsync(PbCommon.Identity identity, CancellationToken ct);

    Task<EntityRows<PbObservations.BillStack>> ReadBillStacksAsync(PbCommon.Identity identity, CancellationToken ct);
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
        await RefreshEntitySectionAsync(f, scope, FactSection.Buildings, "rimgovernor/observations_list_buildings", () => native.ReadBuildingsAsync(identity, ct)).ConfigureAwait(false);
        await RefreshEntitySectionAsync(f, scope, FactSection.Bills, "rimgovernor/observations_read_bills", () => native.ReadBillStacksAsync(identity, ct)).ConfigureAwait(false);
    }

    private static async Task RefreshEntitySectionAsync<T>(ClockFacts f, FactScope scope, FactSection section, string source, Func<Task<EntityRows<T>>> read)
        where T : class, IMessage<T>
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
        FactsApi.PutKeyed(f.Store, scope, Wire.Name(section), full.Rows, FactsApi.At(full.AsOf));
        FactsApi.Put(f.Store, scope, section, new Held<Table<T>>(full.Rows, full.AsOf, true, source));
    }
}
