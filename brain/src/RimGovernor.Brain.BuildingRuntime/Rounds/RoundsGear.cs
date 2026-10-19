namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsGear
{
    /// <summary>The gear census of a step's own colony facts read, which reads no zones: a growing field offers no stuff arrival here.</summary>
    internal static (GearObservation Observation, bool Known) GearObservationFacts(O.ColonyFactsSnapshot observed, Tables tables, GearDefinitions defs)
    {
        var fact = ColonyGearReads.GearFacts(observed, tables, defs, Unknown<EquatableArray<FoodField>>(), []);
        return (fact.ValueOrDefault!, fact.IsKnown);
    }

    internal static async Task<GearDefinitions> GearDefinitionsOfAsync(object source, C.Identity identity, CancellationToken call) => await GearDefinitionsReads.ReadGearDefinitions(source, identity, call).ConfigureAwait(false);

    internal static async Task<Things> FrameThingsOfAsync(object native, C.Identity identity, CancellationToken ct) =>
        native is FacadeNativeClient source ? await source.FrameThingsAsync(identity, ct).ConfigureAwait(false) : new Things();

    internal static string GearDef(Things things, C.Ref? reference) =>
        things.TryRow(reference, out var row) ? row.Entity?.DefName ?? "" : "";
}
