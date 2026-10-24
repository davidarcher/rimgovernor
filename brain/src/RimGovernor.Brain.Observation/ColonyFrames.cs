namespace RimGovernor.Brain.Observation;

// The colony read's small sections: disaster, comfort, pollution, waste, blight and the medical reserve.

public static class ColonyFramesReads
{
    /// <summary>
    /// The frame's active game conditions, each with what its def row says (a condition the catalog has no row
    /// for throws); known is false when the frame could not read the environment.
    /// </summary>
    internal static (EquatableArray<DisasterCondition> Conditions, bool Known) ColonyConditions(O.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        if (ColonyDecodeReads.HasIssue(v.Issues, "environment"))
        {
            return ([], false);
        }
        var conditions = new List<DisasterCondition>(v.Environment.Count);
        foreach (var row in v.Environment)
        {
            var condition = new DisasterCondition
            {
                Id = row.Id ?? "",
                Definition = row.DefName ?? "",
                Permanent = row.Permanent.GetValueOrDefault(),
                DisablesPower = ColonyDecodeReads.RequireCatalog(catalog).DisablesElectricity((row.DefName ?? "")),
            };
            if (row.TicksLeft is not null && !condition.Permanent && row.TicksLeft.GetValueOrDefault() >= 0)
            {
                condition = condition with { TicksLeft = row.TicksLeft };
            }
            conditions.Add(condition);
        }
        return (EquatableArray.From(conditions), true);
    }

    /// <summary>
    /// Whether the frame's biome keeps the sky dark for good (its map conditions' class family). No catalog to
    /// read its conditions from throws, as does a biome the catalog has no row for: lit is never assumed.
    /// </summary>
    internal static Fact<bool> ColonyOutdoorsDark(O.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        if (catalog is null)
        {
            throw new OutdoorsDarkUnknownException();
        }
        return Known(catalog.OutdoorsPermanentlyDark(v.Biome));
    }

    internal static RoundsFacts ColonyDisaster(O.ColonyFactsSnapshot v, RoundsFacts facts, Buildings buildings, EquatableArray<DisasterCondition> conditions, bool conditionsKnown)
    {
        if (conditionsKnown)
        {
            facts = facts with { DisasterConditions = Known(conditions) };
        }
        facts = facts with { DisasterTick = new Tick((long)(v.Context?.Tick ?? 0)) };
        var recovery = v.Recovery;
        if (!ColonyDecodeReads.Resolved(buildings, recovery.Buildings, r => r))
        {
            return facts;
        }
        var restrictions = new List<RecoveryRestriction>();
        foreach (var restriction in recovery.Restrictions)
        {
            restrictions.Add(new RecoveryRestriction(new PawnId(restriction.Pawn.Id), restriction.AreaId ?? ""));
        }
        facts = facts with { RecoverySafety = Known(new RecoverySafety { Restrictions = EquatableArray.From(restrictions) }) };
        var rows = new List<RecoveryBuilding>(recovery.Buildings.Count);
        foreach (var reference in recovery.Buildings)
        {
            if (!buildings.TryGetRow(reference, out var row) || row is null)
            {
                throw new BridgeContractException($"recovery building {reference.Id}: the frame holds no row for it");
            }
            var s = row.Service;
            var b = new RecoveryBuilding
            {
                Id = reference.Id ?? "",
                UsesHitPoints = Known(row.HitPoints is O.BuildingHitPoints.Tracked),
                Broken = Known(s.BrokenDown),
                Forbidden = Known(row.Settings.Forbidden),
                Burning = Known(row.Burning),
                Fuel = ColonyDecodeReads.Optional(s.Fuel is not null, s.Fuel.GetValueOrDefault()),
                FuelTarget = ColonyDecodeReads.Optional(s.TargetFuel is not null, s.TargetFuel.GetValueOrDefault()),
                Refuelable = Known(s.Fuel is not null),
            };
            if (row.HitPoints is O.BuildingHitPoints.Tracked hitPoints)
            {
                b = b with { HitPoints = Known((long)hitPoints.Current), MaxHitPoints = Known((long)hitPoints.Maximum) };
            }

            rows.Add(b);
        }
        return facts with { RecoveryBuildings = Known(EquatableArray.From(rows)) };
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
    /// Decodes the colony read's corpse census (its embedded WasteReply) into the rows MorgueWaiting and the
    /// tomb census read. A missing observed snapshot, or a row whose corpse the frame's entity table does not
    /// hold, stays unknown.
    /// </summary>
    internal static Fact<EquatableArray<WasteItem>> ColonyWaste(O.ColonyFactsSnapshot v, Tables tables)
    {
        var snapshot = v.Waste?.Outcome;
        if (snapshot is null || !ColonyDecodeReads.Headed(tables, snapshot.Items, row => row.Thing))
        {
            return Unknown<EquatableArray<WasteItem>>();
        }
        var items = new List<WasteItem>(snapshot.Items.Count);
        foreach (var row in snapshot.Items)
        {
            items.Add(new WasteItem { CorpseOf = (CorpseOf)row.CorpseClass, Grave = row.Grave?.Id ?? "" });
        }
        return Known(EquatableArray.From(items));
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
            _ = cell.ZoneId.TryGetValue(out var zone);
            zone ??= "";
            if (!farms.Contains(zone))
            {
                zone = "";
            }
            _ = cell.InHome.TryGetValue(out var home);
            if (zone == "" && !home)
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
        if (!ColonyDecodeReads.HasIssue(v.Issues, "resources"))
        {
            var rows = new List<Amount>();
            foreach (var q in v.Resources)
            {
                rows.Add(new Amount(new Resource(q.DefName), q.Units));
            }
            r = r with { Resources = Known(EquatableArray.From(rows)) };
        }
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
