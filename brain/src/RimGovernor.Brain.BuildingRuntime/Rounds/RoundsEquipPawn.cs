namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>A native source that serves the definition catalog.</summary>
internal static partial class BuildingRuntimePackage
{
    /// <summary>The dining furniture and the joy buildings of the definition catalog, for the recreation foothold.</summary>
    internal static async Task<(DiningFurniture Furniture, List<string> Joy)> RecreationDefinitionsAsync(object native, C.Identity identity, CancellationToken ct)
    {
        if (native is not Observation.IDefinitionSource source)
        {
            throw new InvalidOperationException("native serves no definition catalog for the recreation foothold");
        }
        var catalog = await source.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("native serves no definition catalog for the recreation foothold");
        var furniture = catalog.DiningFurniture();
        var joy = catalog.JoyBuildings().Select(m => m.Definition).ToList();
        return (furniture, joy);
    }

    /// <summary>The definition catalog of the native source; null when the source serves none.</summary>
    internal static async Task<DefinitionCatalog?> PawnCatalogAsync(object native, C.Identity identity, CancellationToken ct) =>
        native is Observation.IDefinitionSource source ? await source.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false) : null;
}
