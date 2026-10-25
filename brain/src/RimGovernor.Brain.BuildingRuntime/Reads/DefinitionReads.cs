namespace RimGovernor.Brain.BuildingRuntime;

internal static class DefinitionReads
{
    internal static async Task<DefinitionFacts?> ReadDefinitionFacts(object source, C.Identity id, IEnumerable<O.EdibleCrop> crops, CancellationToken cancellationToken)
    {
        if (source is not IDefinitionSource native)
        {
            return null;
        }

        var catalog = await native.DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false);
        var research = source as IResearchSource ?? throw new RequiredFactException("finished research");
        var read = await research.ReadResearchAsync(id, cancellationToken).ConfigureAwait(false);
        var finished = DefinitionsReads.FinishedSet(read.Finished);
        return new DefinitionFacts
        {
            Catalog = catalog,
            Finished = finished,
            Crops = DefinitionsReads.CropRows(crops)
        };
    }

}
