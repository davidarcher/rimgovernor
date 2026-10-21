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

    internal static RoundsFacts ColonyDisaster(O.ColonyFactsSnapshot v, RoundsFacts facts, Buildings buildings, DefinitionCatalog? catalog, EquatableArray<DisasterCondition> conditions, bool conditionsKnown)
    {
        if (conditionsKnown)
        {
            facts = facts with { DisasterConditions = Known(conditions) };
        }
        facts = facts with { DisasterTick = new Tick((long)(v.Context?.Tick ?? 0)) };
        if (v.Recovery?.Observed is not { } recovery || !ColonyDecodeReads.Resolved(buildings, recovery.Buildings, r => r))
        {
            return facts;
        }
        var restrictions = new List<RecoveryRestriction>();
        foreach (var restriction in recovery.Restrictions)
        {
            restrictions.Add(new RecoveryRestriction(new PawnId(restriction.Pawn?.Id ?? ""), Known(restriction.AreaId ?? "")));
        }
        facts = facts with { RecoverySafety = Known(new RecoverySafety { Restrictions = EquatableArray.From(restrictions) }) };
        var rows = new List<RecoveryBuilding>(recovery.Buildings.Count);
        foreach (var reference in recovery.Buildings)
        {
            if (!buildings.TryGetRow(reference, out var row) || row is null)
            {
                throw new BridgeContractException($"recovery building {reference.Id}: the frame holds no row for it");
            }
            if (catalog is null)
            {
                throw new DomainException($"recovery building {reference.Id}: no definition catalog was loaded");
            }
            var usesHitPoints = catalog.UsesHitPoints(row.Building.DefName);
            var s = row.Service;
            var b = new RecoveryBuilding
            {
                Id = reference.Id ?? "",
                UsesHitPoints = Known(usesHitPoints),
                Broken = Known(s.BrokenDown),
                Forbidden = Known(row.Settings.Forbidden),
                Burning = Known(row.Burning),
                Fuel = ColonyDecodeReads.Optional(s.Fuel is not null, s.Fuel.GetValueOrDefault()),
                FuelTarget = ColonyDecodeReads.Optional(s.TargetFuel is not null, s.TargetFuel.GetValueOrDefault()),
                Refuelable = Known(s.Fuel is not null),
            };
            if (row.HitPoints is not null)
            {
                b = b with { HitPoints = Known((long)row.HitPoints) };
            }
            if (row.MaxHitPoints is not null)
            {
                b = b with { MaxHitPoints = Known((long)row.MaxHitPoints) };
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

    private static EquatableArray<PawnId> PawnIdArray(IEnumerable<string> values)
    {
        var rows = new List<PawnId>();
        foreach (var id in values)
        {
            rows.Add(new PawnId(id));
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Decodes the comfort census. The joy buildings, the recreation foothold and the watch-cell buildings are
    /// rules over the catalog rows; a catalog that cannot answer throws, never an unknown census.
    /// </summary>
    internal static Fact<ComfortObservation> ColonyComfort(O.ColonyFactsSnapshot v, DefinitionCatalog? catalog)
    {
        var value = v.Upkeep?.Outcome?.Comfort?.Observed;
        if (value is null)
        {
            return Unknown<ComfortObservation>();
        }
        EquatableArray<ComfortFacility> Facilities(IEnumerable<O.ComfortFacility> values)
        {
            var rows = new List<ComfortFacility>();
            foreach (var f in values)
            {
                rows.Add(new ComfortFacility
                {
                    Id = f.Id ?? "",
                    RoomId = f.Room?.Id ?? "",
                    Kind = f.Kind ?? "",
                    AccessibleTo = PawnIdArray(FactHelpers.RefIds(f.AccessibleTo)),
                    Users = PawnIdArray(FactHelpers.RefIds(f.Users)),
                });
            }
            return EquatableArray.From(rows);
        }
        var r = new ComfortObservation { People = PawnIdArray(value.People), Dining = Facilities(value.Dining), Recreation = Facilities(value.Recreation) };
        var required = ColonyDecodeReads.RequireCatalog(catalog);
        try
        {
            if (value.Joy is { } j)
            {
                var pawns = new List<JoyTolerance>();
                foreach (var p in j.Pawns)
                {
                    pawns.Add(new JoyTolerance { Pawn = new PawnId(p.Pawn), Tolerance = EquatableArray.From(p.Tolerance), Bored = EquatableArray.From(p.Bored) });
                }
                r = r with { Joy = new RecreationCensus { Kinds = EquatableArray.From(j.Kinds), Pawns = EquatableArray.From(pawns), Methods = EquatableArray.From(required.JoyBuildings()) } };
            }
            r = r with { RecreationFoothold = required.RecreationFoothold(), WatchBuildings = EquatableArray.From(required.WatchBuildings()), Furniture = required.DiningFurniture() };
        }
        catch (DomainException e)
        {
            throw new DomainException($"comfort census: {e.Message}");
        }
        var surfaces = new List<DiningSurface>();
        foreach (var s in value.Surfaces)
        {
            var adjacent = new List<Cell>();
            foreach (var c in s.Adjacent)
            {
                adjacent.Add(ColonyDecodeReads.CellOf(c));
            }
            surfaces.Add(new DiningSurface { Id = s.Id ?? "", RoomId = s.Room?.Id ?? "", Adjacent = EquatableArray.From(adjacent) });
        }
        return Known(r with { Surfaces = EquatableArray.From(surfaces) });
    }

    /// <summary>
    /// The biotech/pollution Need's read from the Biotech colony section: each wastepack with the game's verdicts and
    /// the count of polluted cells outside the pollution-clear area. Unknown without the section, so the Need is
    /// unknown on a colony without Biotech.
    /// </summary>
    internal static Fact<PollutionFacts> ColonyPollution(Fact<BiotechColony> section)
    {
        if (!section.TryGetValue(out var b))
        {
            return Unknown<PollutionFacts>();
        }
        return Known(new PollutionFacts { UncoveredCells = b.PollutedUncoveredCells, Wastepacks = b.Wastepacks });
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
            var known = true;
            foreach (var q in v.Resources)
            {
                if (q.Units is null)
                {
                    known = false;
                    break;
                }
                rows.Add(new Amount(new Resource((q.DefName ?? "")), q.Units.GetValueOrDefault()));
            }
            if (known)
            {
                r = r with { Resources = Known(EquatableArray.From(rows)) };
            }
        }
        var u = v.Upkeep?.Outcome;
        if (u is null || ColonyDecodeReads.HasIssue(u.Issues, "items") || !ColonyDecodeReads.Headed(tables, u.Items, item => item.Item))
        {
            return r;
        }
        var stacks = new List<MedicineStackRow>();
        foreach (var item in u.Items)
        {
            // Whether the def is medicine is the catalog's.
            bool medicine;
            try
            {
                medicine = ColonyDecodeReads.RequireCatalog(catalog).Medicine(tables.Entity(item.Item)?.DefName ?? "");
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
                Id = item.Item.Id ?? "",
                Definition = new Resource(tables.Entity(item.Item)?.DefName ?? ""),
                Count = item.Count,
                Forbidden = item.Forbidden,
                Perishable = Known(item.RotTicks is not null),
                RotTicks = ColonyDecodeReads.Optional(item.RotTicks is not null, item.RotTicks.GetValueOrDefault()),
            });
        }
        return r with { Items = Known(EquatableArray.From(stacks)) };
    }
}
