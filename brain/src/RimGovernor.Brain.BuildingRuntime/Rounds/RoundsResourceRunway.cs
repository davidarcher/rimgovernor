namespace RimGovernor.Brain.BuildingRuntime;

public interface IResourceRunwaySource
{
    (IReadOnlyList<ResourceSource> Sources, ResourceStorage Storage, BridgeResult Result) ReadResourceSources(CancellationToken ct, C.Identity identity, string resource);
}

public interface IConsumptionSource
{
    ConsumptionPage ReadConsumption(CancellationToken ct, int sinceHour);
}

/// <summary>Accumulates the consumption ledger per colony and load.</summary>
internal sealed class ConsumptionMemory
{
    private readonly object mu = new();
    private ColonyId colony;
    private LoadId load;
    private ConsumptionLedger ledger = new();

    public Fact<ResourceConsumption> Window(CancellationToken ct, IConsumptionSource reader, GenerationSnapshot snapshot)
    {
        lock (mu)
        {
            if (colony != snapshot.Colony || load != snapshot.Load)
            {
                (colony, load, ledger) = (snapshot.Colony, snapshot.Load, new ConsumptionLedger());
            }

            ConsumptionPage page;
            try
            {
                page = reader.ReadConsumption(ct, ledger.Since);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Unknown<ResourceConsumption>();
            }

            ledger.Merge(page);
            return ledger.Window(PolicyFunctions.ResourceRateWindowDays);
        }
    }
}

public sealed partial class Rounder
{
    private readonly ConsumptionMemory consumption = new();

    internal Fact<ResourceConsumption> ResourceConsumption(CancellationToken ct, GenerationSnapshot snapshot) =>
        Native is IConsumptionSource reader ? consumption.Window(ct, reader, snapshot) : Unknown<ResourceConsumption>();

    internal IReadOnlyDictionary<Resource, Fact<long>> ResourceSurfaceOre(CancellationToken ct, GenerationSnapshot snapshot, ItemFacts items, Fact<ResourceConsumption> consumptionFact)
    {
        var output = new Dictionary<Resource, Fact<long>>();
        if (Native is not IResourceRunwaySource reader)
        {
            return output;
        }

        foreach (var resource in PolicyFunctions.MaterialRunwayKeys(items, consumptionFact))
        {
            try
            {
                var (rows, _, _) = reader.ReadResourceSources(ct, Boundary.Identity(snapshot), resource.Value);
                output[resource] = PolicyFunctions.SurfaceOre(rows);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // an unreadable source leaves the resource's surface ore unknown
            }
        }

        return output;
    }
}
