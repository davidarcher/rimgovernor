namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Accumulates the consumption ledger per colony and load.</summary>
internal sealed class ConsumptionMemory : IDisposable
{
    private readonly SemaphoreSlim mu = new(1, 1);
    private ColonyId colony;
    private LoadId load;
    private ConsumptionLedger ledger = new();

    public async Task<Fact<ResourceConsumption>> WindowAsync(FacadeNativeClient reader, GenerationSnapshot snapshot, CancellationToken ct)
    {
        await mu.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (colony != snapshot.Colony || load != snapshot.Load)
            {
                (colony, load, ledger) = (snapshot.Colony, snapshot.Load, new ConsumptionLedger());
            }

            ConsumptionPage page;
            try
            {
                page = await reader.ReadConsumptionAsync(ledger.Since(), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Unknown<ResourceConsumption>();
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

    internal async Task<Fact<ResourceConsumption>> ResourceConsumptionAsync(GenerationSnapshot snapshot, CancellationToken ct) =>
        Native is FacadeNativeClient reader ? await consumption.WindowAsync(reader, snapshot, ct).ConfigureAwait(false) : Unknown<ResourceConsumption>();

    internal async Task<IReadOnlyDictionary<Resource, Fact<long>>> ResourceSurfaceOreAsync(GenerationSnapshot snapshot, ItemFacts items, Fact<ResourceConsumption> consumptionFact, CancellationToken ct)
    {
        var output = new Dictionary<Resource, Fact<long>>();
        if (Native is not FacadeNativeClient reader)
        {
            return output;
        }

        foreach (var resource in ResourceRules.MaterialRunwayKeys(items, consumptionFact))
        {
            try
            {
                var (rows, _) = await reader.ReadResourceSourcesAsync(Boundary.Identity(snapshot), resource.Value, ct).ConfigureAwait(false);
                output[resource] = ResourceRules.SurfaceOre(rows);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // an unreadable source leaves the resource's surface ore unknown
            }
        }

        return output;
    }
}
