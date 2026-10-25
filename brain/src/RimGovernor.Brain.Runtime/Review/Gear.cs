namespace RimGovernor.Brain.Runtime;

internal static class Gear
{
    internal static async Task<GearDefinitions?> GearDefinitionsOfAsync(object source, RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken call) => await GearDefinitionReads.ReadGearDefinitions(source, identity, call).ConfigureAwait(false);

    internal static async Task<Things> FrameThingsOfAsync(object native, RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        native is GameClient source ? await source.FrameThingsAsync(identity, ct).ConfigureAwait(false) : new Things();

    internal static string GearDef(Things things, RimGovernor.Brain.GameFacts.Common.Ref? reference) =>
        things.TryRow(reference, out var row) ? row.Entity.DefName : "";
}
