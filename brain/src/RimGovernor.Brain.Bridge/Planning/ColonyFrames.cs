using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using Cell = RimGovernor.Brain.Domain.Cell;
using PawnId = RimGovernor.Brain.Domain.PawnId;
namespace RimGovernor.Brain.Bridge;

// The colony read's small sections: disaster, comfort, pollution, waste, blight and the medical reserve.

public static class ColonyFramesReads
{
    /// <summary>
    /// The frame's active game conditions, each with what its def row says (a condition the catalog has no row
    /// for throws).
    /// </summary>
    internal static EquatableArray<DisasterCondition> ColonyConditions(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        var conditions = new List<DisasterCondition>(v.Environment.Count);
        foreach (var row in v.Environment)
        {
            conditions.Add(new DisasterCondition
            {
                Observed = row,
                DisablesPower = ColonyPlanningReads.RequireCatalog(catalog).DisablesElectricity(row.DefName),
            });
        }
        return EquatableArray.From(conditions);
    }

    /// <summary>
    /// Whether the frame's biome keeps the sky dark for good (its map conditions' class family). No catalog to
    /// read its conditions from throws, as does a biome the catalog has no row for: lit is never assumed.
    /// </summary>
    internal static bool ColonyOutdoorsDark(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, DefinitionCatalog? catalog) =>
        (catalog ?? throw new OutdoorsDarkUnknownException()).OutdoorsPermanentlyDark(v.Biome);

    internal static ReviewFacts ColonyDisaster(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, ReviewFacts facts, Buildings buildings, EquatableArray<DisasterCondition> conditions)
    {
        facts = facts with { DisasterConditions = conditions, DisasterTick = new Tick(v.Context.Tick) };
        var recovery = v.Recovery;
        facts = facts with { RecoverySafety = new RecoverySafety { Restrictions = recovery.Restrictions } };
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.BuildingState>(recovery.Buildings.Count);
        foreach (var reference in recovery.Buildings)
        {
            if (!buildings.TryGetRow(reference, out var row) || row is null)
            {
                throw new BridgeContractException($"recovery building {reference.Id}: the frame holds no row for it");
            }
            rows.Add(row);
        }
        return facts with { RecoveryBuildings = EquatableArray.From(rows) };
    }

    /// <summary>
    /// Joins the native comfort census to the catalog rows the comfort rules plan from: the joy buildings, the recreation
    /// foothold, the watch-cell buildings and the dining furniture. A catalog that cannot answer throws.
    /// </summary>
    internal static ComfortObservation ColonyComfort(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        var census = v.Upkeep.Comfort ?? throw new BridgeContractException("comfort census is absent");
        var required = ColonyPlanningReads.RequireCatalog(catalog);
        try
        {
            return new ComfortObservation
            {
                Census = census,
                JoyMethods = EquatableArray.From(required.JoyBuildings()),
                RecreationFoothold = required.RecreationFoothold(),
                WatchBuildings = EquatableArray.From(required.WatchBuildings()),
                Furniture = required.DiningFurniture(),
            };
        }
        catch (DomainException e)
        {
            throw new DomainException($"comfort census: {e.Message}");
        }
    }
    /// <summary>
    /// Decodes the total corpse census into the rows MorgueWaiting and the tomb census read.
    /// An absent census or a corpse missing from the frame's entity table is a contract failure.
    /// </summary>
    internal static EquatableArray<RimGovernor.Brain.GameFacts.Observations.WasteItem> ColonyWaste(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables)
    {
        var snapshot = v.Waste ?? throw new BridgeContractException("waste census is absent");
        foreach (var row in snapshot.Items)
        {
            if (tables.Entity(row.Thing) is null)
            {
                throw new BridgeContractException($"waste corpse {row.Thing.Id}: the frame holds no row for it");
            }
        }
        return snapshot.Items;
    }

    /// <summary>Decodes the total visible blight census; absent sections and unresolved plants fail the contract.</summary>
    internal static EquatableArray<BlightedPlant> BlightCensus(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot snapshot, Tables tables)
    {
        var census = snapshot.Blight ?? throw new BridgeContractException("blight census is absent");
        var plants = new List<BlightedPlant>(census.Plants.Count);
        foreach (var row in census.Plants)
        {
            var entity = tables.Entity(row.Thing) ?? throw new BridgeContractException($"blighted plant {row.Thing.Id}: the frame holds no row for it");
            if (entity.Place is not RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)
            {
                throw new BridgeContractException($"blighted plant {entity.Id}: the plant is not on the map");
            }
            plants.Add(new BlightedPlant { Observed = row, Entity = entity });
        }
        return EquatableArray.From(plants);
    }

    /// <summary>
    /// Decodes the medicine-reserve facts of one colony read. A stocked def the catalog has no row for throws
    /// naming the def, never an unknown census.
    /// </summary>
    public static MedicalReserveObservation ColonyMedicalReserve(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog)
    {
        var r = new MedicalReserveObservation { Census = v };
        var stacks = new List<RimGovernor.Brain.GameFacts.Observations.UpkeepItem>();
        foreach (var item in v.Upkeep.Items)
        {
            // Whether the def is medicine is the catalog's.
            bool medicine;
            try
            {
                medicine = ColonyPlanningReads.RequireCatalog(catalog).Medicine(item.Definition);
            }
            catch (DomainException e)
            {
                throw new DomainException($"medical reserve census: {e.Message}");
            }
            if (!medicine)
            {
                continue;
            }
            stacks.Add(item);
        }
        return r with { Items = EquatableArray.From(stacks) };
    }
}
