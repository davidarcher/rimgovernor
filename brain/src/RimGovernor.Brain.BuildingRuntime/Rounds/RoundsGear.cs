namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsGearSource : Observation.IColonySource, IColonyFactsReader
{
    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);
}

internal interface IFrameThingsSource
{
    Task<Things> FrameThingsAsync(C.Identity identity, CancellationToken ct);
}

internal static partial class BuildingRuntimePackage
{
    internal static (GearObservation Observation, bool Known) GearObservationFacts(O.ColonyFactsSnapshot observed, Tables tables, GearDefinitions defs)
    {
        var fact = ObservationPackage.GearFacts(observed, tables, defs);
        return (fact.ValueOrDefault!, fact.IsKnown);
    }

    internal static async Task<GearDefinitions> GearDefinitionsOfAsync(object source, C.Identity identity, CancellationToken call) => await ObservationPackage.ReadGearDefinitions(source, identity, call).ConfigureAwait(false);

    internal static async Task<Things> FrameThingsOfAsync(object native, C.Identity identity, CancellationToken ct) =>
        native is IFrameThingsSource source ? await source.FrameThingsAsync(identity, ct).ConfigureAwait(false) : new Things();

    internal static string GearDef(Things things, C.Ref? reference) =>
        things.TryRow(reference, out var row) ? row.Entity?.DefName ?? "" : "";
}
