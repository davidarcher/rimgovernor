namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>A native source that serves the definition catalog.</summary>
public interface IDefinitionCatalogSource
{
    DefinitionCatalog DefinitionCatalog(C.Identity identity, CancellationToken ct);
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The dining furniture and the joy buildings of the definition catalog, for the recreation foothold.</summary>
    internal static (DiningFurniture Furniture, List<string> Joy) RecreationDefinitions(CancellationToken ct, object native, C.Identity identity)
    {
        if (native is not IDefinitionCatalogSource source)
        {
            throw new InvalidOperationException("native serves no definition catalog for the recreation foothold");
        }
        var catalog = source.DefinitionCatalog(identity, ct);
        var furniture = catalog.DiningFurniture();
        var joy = catalog.JoyBuildings().Select(m => m.Definition).ToList();
        return (furniture, joy);
    }

    /// <summary>The definition catalog of the native source; null when the source serves none.</summary>
    internal static DefinitionCatalog? PawnCatalog(CancellationToken ct, object native, C.Identity identity) =>
        native is IDefinitionCatalogSource source ? source.DefinitionCatalog(identity, ct) : null;
}
