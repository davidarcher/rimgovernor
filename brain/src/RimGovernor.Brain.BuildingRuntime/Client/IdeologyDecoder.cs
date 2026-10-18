using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
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
            IdeoId = v.IdeoId ?? "",
            Memes = [.. v.Memes],
            Believers = v.Believers.GetValueOrDefault(),
            StyleCategories = [.. v.StyleCategories],
            VeneratedAnimals = [.. v.VeneratedAnimals],
        };
        if (v.Fluid is not null || v.CanReform is not null || v.DevelopmentPoints is not null || v.ReformCount is not null || v.NextReformPoints is not null)
        {
            facts = facts with
            {
                Development = Known(new P.IdeoDevelopment(
                    v.Fluid.GetValueOrDefault(),
                    v.CanReform.GetValueOrDefault(),
                    v.ReformCount.GetValueOrDefault(),
                    v.DevelopmentPoints.GetValueOrDefault(),
                    v.NextReformPoints.GetValueOrDefault())),
            };
        }

        var precepts = new List<P.HeldPrecept>();
        foreach (var row in v.Precepts)
        {
            precepts.Add(new P.HeldPrecept(row.Id ?? "", row.DefName ?? ""));
        }
        var roles = new List<P.HeldRole>();
        foreach (var row in v.Roles)
        {
            var held = new List<Domain.PawnId>();
            foreach (var pawn in row.Pawns)
            {
                held.Add(new Domain.PawnId(pawn.Id ?? ""));
            }
            roles.Add(new P.HeldRole(row.Id ?? "", row.DefName ?? "", row.Active.GetValueOrDefault(), held));
        }
        var rituals = new List<P.HeldRitual>();
        foreach (var row in v.Rituals)
        {
            rituals.Add(new P.HeldRitual
            {
                Id = row.Id ?? "",
                Pattern = row.Pattern ?? "",
                LastFinishedTick = row.LastFinishedTick.GetValueOrDefault(),
                ActiveObligations = row.ActiveObligations.GetValueOrDefault(),
                RepeatPenaltyActive = row.RepeatPenaltyActive.GetValueOrDefault(),
                Running = row.Running.GetValueOrDefault(),
            });
        }
        var buildings = new List<P.HeldBuilding>();
        foreach (var row in v.Buildings)
        {
            buildings.Add(new P.HeldBuilding(row.Building ?? ""));
        }
        return new P.Ideoligion(defs, facts with { Precepts = precepts, Roles = roles, Rituals = rituals, Buildings = buildings });
    }
}
