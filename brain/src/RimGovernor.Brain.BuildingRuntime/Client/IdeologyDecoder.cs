using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Decodes the frame's ideology section over the catalog's Ideology defs. A
/// section whose catalog has no Ideology defs is a contract failure.
/// </summary>
internal static class IdeologyDecoder
{
    internal static P.Ideoligion Decode(PbObservations.IdeologySnapshot v, BridgeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var defs = catalog.IdeologyDefs() ?? throw BridgeContractException.Contract("ideology section without catalog ideology defs");
        var facts = new P.IdeoligionFacts
        {
            Observed = v,
            Buildings = [.. v.Buildings.Select(row => new P.HeldBuilding(new P.BuildingDemand
            {
                Observed = row,
                ForbiddenDefs = catalog.ThingDefsWithBuildingTags([.. row.ForbiddenBuildingTags]),
            }))],
        };
        return new P.Ideoligion(defs, facts);
    }
    /// <summary>
    /// The frame's Ideology mode: the expansion's flag every routine frame carries, and with it installed the
    /// primary ideoligion's section. A frame without the flag, or an installed colony without its section, is a
    /// contract failure.
    /// </summary>
    internal static P.IdeologyMode Mode(PbObservations.IdeologyFrame? frame, BridgeCatalog catalog) =>
        frame switch
        {
            null => throw BridgeContractException.Contract("routine frame without its ideology flag"),
            PbObservations.IdeologyFrame.NotInstalled => new P.IdeologyMode.NotInstalled(),
            PbObservations.IdeologyFrame.Installed installed => new P.IdeologyMode.Installed(Decode(installed.Snapshot, catalog)),
            { } other => throw BridgeContractException.Contract("routine frame with an unknown ideology flag " + other.GetType().Name),
        };
}
