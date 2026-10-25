namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Accumulates the consumption ledger per colony and load.</summary>
internal sealed class ConsumptionMemory : IDisposable
{
    private readonly SemaphoreSlim mu = new(1, 1);
    private ColonyId colony;
    private LoadId load;
    private ConsumptionLedger ledger = new();

    public async Task<ResourceConsumption?> WindowAsync(FacadeNativeClient reader, GenerationSnapshot snapshot, CancellationToken ct)
    {
        await mu.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (colony != snapshot.Colony || load != snapshot.Load)
            {
                (colony, load, ledger) = (snapshot.Colony, snapshot.Load, new ConsumptionLedger());
            }

            O.ConsumptionSnapshot page;
            try
            {
                page = await reader.ReadConsumptionAsync(ledger.Since(), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }

            ledger.Merge(page);
            return ledger.Window(ResourceRules.ResourceRateWindowDays);
        }
        finally
        {
            mu.Release();
        }
    }

    public void Dispose() => mu.Dispose();
}

public sealed partial class Rounder : IDisposable
{
    private readonly ConsumptionMemory consumption = new();

    /// <summary>Releases the consumption ledger's lock.</summary>
    public void Dispose() => consumption.Dispose();

    internal async Task<ResourceConsumption?> ResourceConsumptionAsync(GenerationSnapshot snapshot, CancellationToken ct) =>
        Native is FacadeNativeClient reader ? await consumption.WindowAsync(reader, snapshot, ct).ConfigureAwait(false) : null;

    /// <summary>The safe surface ore of each material the ring names; a material whose sources could not be read has no entry, which counts as no ore.</summary>
    internal async Task<IReadOnlyDictionary<Resource, long>> ResourceSurfaceOreAsync(GenerationSnapshot snapshot, ItemFacts items, ResourceConsumption? consumptionRead, CancellationToken ct)
    {
        var output = new Dictionary<Resource, long>();
        if (Native is not FacadeNativeClient reader)
        {
            return output;
        }

        // Every material's sources are read in one frame rather than one frame each.
        var reads = ResourceRules.MaterialRunwayKeys(items, consumptionRead).Select(async Task<(Resource Key, IReadOnlyList<ResourceSource>? Rows)> (resource) =>
        {
            try
            {
                var (rows, _) = await reader.ReadResourceSourcesAsync(Boundary.Identity(snapshot), resource.Value, ct).ConfigureAwait(false);
                return (resource, rows);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // an unreadable source leaves the resource without surface ore
                return (resource, null);
            }
        }).ToList();
        foreach (var (resource, rows) in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (rows is not null)
            {
                output[resource] = ResourceRules.SurfaceOre(rows);
            }
        }

        return output;
    }

    /// <summary>
    /// The value per unit of each construction material the priced planned rooms consume (#3653): each material's sources
    /// read once, in one frame, and priced by <see cref="ForecastValue.MaterialValueOf"/>. A failed read, or a native without
    /// the read, prices it at market value.
    /// </summary>
    internal Task<IReadOnlyDictionary<Resource, ConstructionMaterialValue>> MaterialValuesAsync(GenerationSnapshot snapshot, ForecastCoupledContext context,
        StationCatalog stations, ItemFacts items, CancellationToken ct) =>
        MaterialValuesAsync(snapshot, ForecastValue.ChargedMaterials(context), stations, items, ct);

    /// <summary>The value per unit of each of <paramref name="materials"/>, read and priced as the charged materials are (#3653, #3658).</summary>
    internal async Task<IReadOnlyDictionary<Resource, ConstructionMaterialValue>> MaterialValuesAsync(GenerationSnapshot snapshot, IEnumerable<Resource> materials,
        StationCatalog stations, ItemFacts items, CancellationToken ct)
    {
        var reader = Native as FacadeNativeClient;
        var reads = materials.Distinct().Select(async Task<(Resource Key, ConstructionMaterialValue Value)> (resource) =>
        {
            if (reader is null) return (resource, ForecastValue.MaterialValueOf(resource, null, null, stations.Haul, items));
            try
            {
                var (rows, storage) = await reader.ReadResourceSourcesAsync(Boundary.Identity(snapshot), resource.Value, ct).ConfigureAwait(false);
                return (resource, ForecastValue.MaterialValueOf(resource, rows, storage, stations.Haul, items));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // an unreadable source leaves the material at market value
                return (resource, ForecastValue.MaterialValueOf(resource, null, null, stations.Haul, items));
            }
        }).ToList();
        return (await Task.WhenAll(reads).ConfigureAwait(false)).ToDictionary(r => r.Key, r => r.Value);
    }
}
