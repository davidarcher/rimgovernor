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
        // The Policy reform state spells a fixed ideoligion as not fluid, with no points and no reform cost.
        var development = v.Development is PbObservations.IdeoDevelopment.Fluid fluid
            ? new P.IdeoDevelopment(true, fluid.CanReform, fluid.ReformCount, fluid.Points, fluid.NextReformPoints)
            : P.IdeoDevelopment.Fixed;
        var facts = new P.IdeoligionFacts
        {
            IdeoId = v.IdeoId,
            Memes = [.. v.Memes],
            Believers = v.Believers,
            StyleCategories = [.. v.StyleCategories],
            VeneratedAnimals = [.. v.VeneratedAnimals],
            BelieverPawns = [.. v.BelieverPawns],
            Development = development,
        };

        var precepts = new List<P.HeldPrecept>();
        foreach (var row in v.Precepts)
        {
            precepts.Add(new P.HeldPrecept(row.Id, row.DefName));
        }
        var roles = new List<P.HeldRole>();
        foreach (var row in v.Roles)
        {
            roles.Add(new P.HeldRole(row.Id, row.DefName, row.Active, [.. row.Pawns]) { Converts = row.Converts, ConvertReady = row.ConvertReady });
        }
        var rituals = new List<P.HeldRitual>();
        foreach (var row in v.Rituals)
        {
            rituals.Add(new P.HeldRitual
            {
                Id = row.Id,
                Pattern = row.Pattern,
                LastFinishedTick = row.LastFinishedTick,
                ActiveObligations = row.ActiveObligations,
                RepeatPenaltyActive = row.RepeatPenaltyActive,
                Running = row.Running,
            });
        }
        var buildings = new List<P.HeldBuilding>();
        foreach (var row in v.Buildings)
        {
            buildings.Add(new P.HeldBuilding(row.Building)
            {
                Demand = new P.BuildingDemand
                {
                    Applies = row.Applies,
                    Present = row.Present,
                    Satisfied = row.Satisfied,
                    MinArea = row.MinArea,
                    MinImpressiveness = row.MinImpressiveness,
                    FloorTags = [.. row.FloorTags],
                    Counts = [.. row.Things.Select(t => new P.ThingCount { Def = t.Def, Count = t.Count })],
                    ForbiddenDefs = catalog.ThingDefsWithBuildingTags([.. row.ForbiddenBuildingTags]),
                    Unread = [.. row.Unread],
                },
            });
        }
        return new P.Ideoligion(defs, facts with { Precepts = precepts, Roles = roles, Rituals = rituals, Buildings = buildings });
    }

    /// <summary>
    /// The frame's Ideology mode: the expansion's flag every routine frame carries, and with it installed the
    /// primary ideoligion's section. A frame without the flag, or an installed colony without its section, is a
    /// contract failure.
    /// </summary>
    internal static P.IdeologyMode Mode(bool? active, PbObservations.IdeologySnapshot? section, BridgeCatalog catalog) =>
        active switch
        {
            null => throw BridgeContractException.Contract("routine frame without its ideology flag"),
            false => section is null ? new P.IdeologyMode.NotInstalled() : throw BridgeContractException.Contract("ideology section with ideology_active false"),
            true => new P.IdeologyMode.Installed(Decode(section ?? throw BridgeContractException.Contract("ideology installed without its primary ideoligion section"), catalog)),
        };
}
