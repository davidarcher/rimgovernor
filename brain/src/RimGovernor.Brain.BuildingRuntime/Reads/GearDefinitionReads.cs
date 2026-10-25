namespace RimGovernor.Brain.BuildingRuntime;

public static class GearDefinitionReads
{
    public static async Task<GearDefinitions?> ReadGearDefinitions(object source, C.Identity id, CancellationToken cancellationToken) =>
        GearDefinitionsReads.GearDefinitionsOf(await DefinitionReads.ReadDefinitionFacts(source, id, [], cancellationToken).ConfigureAwait(false));

}
