namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsGear
{
    internal static async Task<GearDefinitions?> GearDefinitionsOfAsync(object source, C.Identity identity, CancellationToken call) => await GearDefinitionReads.ReadGearDefinitions(source, identity, call).ConfigureAwait(false);

    internal static async Task<Things> FrameThingsOfAsync(object native, C.Identity identity, CancellationToken ct) =>
        native is FacadeNativeClient source ? await source.FrameThingsAsync(identity, ct).ConfigureAwait(false) : new Things();

    internal static string GearDef(Things things, C.Ref? reference) =>
        things.TryRow(reference, out var row) ? row.Entity.DefName : "";
}
