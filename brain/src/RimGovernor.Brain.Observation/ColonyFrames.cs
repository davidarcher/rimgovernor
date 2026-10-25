namespace RimGovernor.Brain.Observation;

// The colony read's small sections: disaster, comfort, pollution, waste, blight and the medical reserve.

public static class ColonyFramesReads
{
    /// <summary>
    /// The frame's active game conditions, each with what its def row says (a condition the catalog has no row
    /// for throws).
    /// </summary>
    internal static EquatableArray<DisasterCondition> ColonyConditions(O.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        var conditions = new List<DisasterCondition>(v.Environment.Count);
        foreach (var row in v.Environment)
        {
            conditions.Add(new DisasterCondition
            {
                Id = row.Id,
                Definition = row.DefName,
                Duration = row.Duration,
                DisablesPower = ColonyDecodeReads.RequireCatalog(catalog).DisablesElectricity(row.DefName),
            });
        }
        return EquatableArray.From(conditions);
    }

    /// <summary>
    /// Whether the frame's biome keeps the sky dark for good (its map conditions' class family). No catalog to
    /// read its conditions from throws, as does a biome the catalog has no row for: lit is never assumed.
    /// </summary>
    internal static bool ColonyOutdoorsDark(O.ColonyFactsSnapshot v, DefinitionCatalog? catalog) =>
        (catalog ?? throw new OutdoorsDarkUnknownException()).OutdoorsPermanentlyDark(v.Biome);

    internal static RoundsFacts ColonyDisaster(O.ColonyFactsSnapshot v, RoundsFacts facts, Buildings buildings, EquatableArray<DisasterCondition> conditions)
    {
        facts = facts with { DisasterConditions = conditions, DisasterTick = new Tick((long)(v.Context?.Tick ?? 0)) };
        var recovery = v.Recovery;
        var restrictions = new List<RecoveryRestriction>();
        foreach (var restriction in recovery.Restrictions)
        {
            restrictions.Add(new RecoveryRestriction(new PawnId(restriction.Pawn.Id), restriction.AreaId ?? ""));
        }
        facts = facts with { RecoverySafety = new RecoverySafety { Restrictions = EquatableArray.From(restrictions) } };
        var rows = new List<RecoveryBuilding>(recovery.Buildings.Count);
        foreach (var reference in recovery.Buildings)
        {
            if (!buildings.TryGetRow(reference, out var row) || row is null)
            {
                throw new BridgeContractException($"recovery building {reference.Id}: the frame holds no row for it");
            }
            var s = row.Service;
            rows.Add(new RecoveryBuilding
            {
                Id = reference.Id ?? "",
                HitPoints = row.HitPoints,
                Broken = s.BrokenDown,
                Forbidden = row.Settings.Forbidden,
                Burning = row.Burning,
                Fuel = s.Fuel is O.BuildingFuel.Refuelable fuel
                    ? new RecoveryFuel.Refuelable(fuel.Fuel, fuel.TargetFuel)
                    : new RecoveryFuel.NotRefuelable(),
            });
        }
        return facts with { RecoveryBuildings = EquatableArray.From(rows) };
    }

    internal static Fact<EquatableArray<RecoveryWorker>> RecoveryWorkers(Fact<EquatableArray<MoodPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<EquatableArray<RecoveryWorker>>();
        }
        var workers = new List<RecoveryWorker>(rows.Count);
        foreach (var p in rows)
        {
            workers.Add(new RecoveryWorker { Pawn = p.Id, Dead = p.Dead, Downed = p.Downed, Drafted = p.Drafted, Mental = p.Mental, PlayerForced = p.PlayerForced });
        }
        return Known(EquatableArray.From(workers));
    }

    /// <summary>
    /// Joins the native comfort census to the catalog rows the comfort rules plan from: the joy buildings, the recreation
    /// foothold, the watch-cell buildings and the dining furniture. A catalog that cannot answer throws.
    /// </summary>
    internal static Fact<ComfortObservation> ColonyComfort(O.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        var required = ColonyDecodeReads.RequireCatalog(catalog);
        try
        {
            return Known(new ComfortObservation
            {
                Census = v.Upkeep.Comfort,
                JoyMethods = EquatableArray.From(required.JoyBuildings()),
                RecreationFoothold = required.RecreationFoothold(),
                WatchBuildings = EquatableArray.From(required.WatchBuildings()),
                Furniture = required.DiningFurniture(),
            });
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
    internal static EquatableArray<WasteItem> ColonyWaste(O.ColonyFactsSnapshot v, Tables tables)
    {
        var snapshot = v.Waste ?? throw new BridgeContractException("waste census is absent");
        var items = new List<WasteItem>(snapshot.Items.Count);
        foreach (var row in snapshot.Items)
        {
            if (tables.Entity(row.Thing) is null)
            {
                throw new BridgeContractException($"waste corpse {row.Thing.Id}: the frame holds no row for it");
            }
            items.Add(new WasteItem { CorpseOf = (CorpseOf)row.CorpseClass, Grave = row.Grave?.Id ?? "" });
        }
        return EquatableArray.From(items);
    }

    /// <summary>
    /// Lists the blighted plants on colony ground from the planning window's thing lists: a plant thing with
    /// blighted state on a growing zone or the home area. It is unknown (never "no blight") until the window and
    /// the zone section are both held. A cell the window does not hold (fogged) has no plant to see.
    /// </summary>
    internal static Fact<EquatableArray<BlightedPlant>> BlightCensus(ColonyProjection p)
    {
        if (p.Region == default || p.Zones is not { Complete: true })
        {
            return Unknown<EquatableArray<BlightedPlant>>();
        }
        var farms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var farm in p.Farms)
        {
            farms.Add(farm.Id);
        }
        var plants = new List<BlightedPlant>();
        foreach (var cell in p.Cells)
        {
            var zone = cell.ZoneId is { } id && farms.Contains(id) ? id : "";
            if (zone == "" && !cell.InHome)
            {
                continue;
            }
            foreach (var thing in cell.Things)
            {
                if (thing.Category != ThingCategory.Plant || !thing.Plant.Blighted || thing.Id == 0)
                {
                    continue;
                }
                plants.Add(new BlightedPlant { ID = thing.LoadId(), Definition = thing.Def, Cell = cell.Cell, Designated = thing.Has(ThingMarks.Designated), Eligible = true });
            }
        }
        return Known(EquatableArray.From(plants));
    }

    /// <summary>
    /// Decodes the medicine-reserve facts of one colony read. A stocked def the catalog has no row for throws
    /// naming the def, never an unknown census.
    /// </summary>
    public static MedicalReserveObservation ColonyMedicalReserve(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog)
    {
        var r = new MedicalReserveObservation { Colonists = v.ColonistCount };
        var rows = new List<Amount>();
        foreach (var q in v.Resources)
        {
            rows.Add(new Amount(new Resource(q.DefName), q.Units));
        }
        r = r with { Resources = EquatableArray.From(rows) };
        var stacks = new List<MedicineStackRow>();
        foreach (var item in v.Upkeep.Items)
        {
            // Whether the def is medicine is the catalog's.
            bool medicine;
            try
            {
                medicine = ColonyDecodeReads.RequireCatalog(catalog).Medicine(item.Definition);
            }
            catch (DomainException e)
            {
                throw new DomainException($"medical reserve census: {e.Message}");
            }
            if (!medicine)
            {
                continue;
            }
            stacks.Add(new MedicineStackRow
            {
                Id = item.Id,
                Definition = new Resource(item.Definition),
                Count = item.Count,
                Forbidden = item.Forbidden,
                Perishable = Known(item.Rot is O.ItemRot.Rotting),
                RotTicks = ColonyDecodeReads.Optional(item.Rot is O.ItemRot.Rotting, item.Rot is O.ItemRot.Rotting rot ? rot.Ticks : 0),
            });
        }
        return r with { Items = Known(EquatableArray.From(stacks)) };
    }
}
