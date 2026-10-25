namespace RimGovernor.Brain.Runtime;

public static class GearDefinitionReads
{
    public static async Task<GearDefinitions?> ReadGearDefinitions(object source, RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken) =>
        GearDefinitionsReads.GearDefinitionsOf(await DefinitionReads.ReadDefinitionFacts(source, id, [], cancellationToken).ConfigureAwait(false));

}
