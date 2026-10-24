using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public static class ColonyDecodeReads
{
    /// <summary>Whether <paramref name="buildings"/> holds the building every row refers to.</summary>
    internal static bool Resolved<TRow>(Buildings buildings, IEnumerable<TRow> rows, Func<TRow, C.Ref?> reference)
    {
        foreach (var row in rows)
        {
            if (!buildings.TryGetRow(reference(row), out _))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether <paramref name="tables"/> hold the head every row refers to: the def and position a family reads from the table.</summary>
    internal static bool Headed<TRow>(Tables tables, IEnumerable<TRow> rows, Func<TRow, C.Ref?> reference)
    {
        foreach (var row in rows)
        {
            if (tables.Entity(reference(row)) is null)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the building table holds the head every row refers to.</summary>
    internal static bool Headed<TRow>(Buildings buildings, IEnumerable<TRow> rows, Func<TRow, C.Ref?> reference)
    {
        foreach (var row in rows)
        {
            if (buildings.Entity(reference(row)) is null)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A proto3 optional scalar as a fact: unknown when the field is absent.</summary>
    internal static Fact<T> Optional<T>(bool has, T value) => has ? Known(value) : Unknown<T>();

    /// <summary>A reference's id, unknown without a reference.</summary>
    internal static Fact<string> OptionalRef(C.Ref? r) => r is null ? Unknown<string>() : Optional(r.Id is not null, (r.Id ?? ""));

    internal static bool HasIssue(IEnumerable<O.ReadIssue> issues, string field)
    {
        foreach (var issue in issues)
        {
            if ((issue.Field ?? "") == field)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The accessible colony stock census by definition, summed over the rows that carry units; a definition absent from
    /// it is known zero. Unknown when the read reports a resources issue.
    /// </summary>
    internal static Fact<IReadOnlyDictionary<Resource, long>> StockCensus(O.ColonyFactsSnapshot v)
    {
        if (HasIssue(v.Issues, "resources"))
        {
            return Unknown<IReadOnlyDictionary<Resource, long>>();
        }
        var stock = new Dictionary<Resource, long>();
        foreach (var q in v.Resources)
        {
            var key = new Resource(q.DefName);
            stock[key] = stock.GetValueOrDefault(key) + q.Units;
        }
        return Known<IReadOnlyDictionary<Resource, long>>(stock);
    }

    internal static Fact<bool> NativePresence(string? value, IEnumerable<O.ReadIssue> issues, string field) =>
        PlanningWindowRules.CellPresence(value, issues, field, false);

    internal static Cell CellOf(C.Cell? cell) => new(cell?.X ?? 0, cell?.Z ?? 0);

    /// <summary>The cell of a map census row's head: a map census holds only placed things, so an unplaced head breaks the contract.</summary>
    internal static Cell MapCellOf(O.EntityRef head) =>
        head.Place is O.EntityPlace.OnMap at ? CellOf(at.Cell) : throw new ObservationContractException($"{head.Id} is on no map");

    /// <summary>The load id of the allowed area labelled <paramref name="label"/>, "" when the map has none.</summary>
    internal static string AllowedAreaID(O.Policies policies, string label)
    {
        foreach (var a in policies.AllowedAreas)
        {
            if (a.Label == label)
            {
                return a.Id;
            }
        }
        return "";
    }

    /// <summary>
    /// The food policy holders outside the work census, as the diet planner reads them: a prisoner's traits
    /// resolved against the catalog, an animal with the foods its race can ever eat. An animal whose pawn row or
    /// race the frame does not hold is dropped, so no diet is written for it.
    /// </summary>
    internal static IReadOnlyList<FoodEater> FoodEaters(IEnumerable<O.FoodEater> eaters, Pawns pawns, AnimalRaceCatalog races, DefinitionCatalog? catalog)
    {
        var output = new List<FoodEater>();
        foreach (var e in eaters)
        {
            var traits = new List<PawnTrait>(e.Traits.Count);
            foreach (var t in e.Traits)
            {
                traits.Add(RoundsWorkReads.ResolveTrait(catalog, new PawnTrait { Name = t.DefName, Degree = t.Degree }));
            }
            var eater = new FoodEater { Pawn = e.Pawn, Animal = e.Kind == O.FoodEaterKind.Animal, Precepts = [.. e.Precepts], Traits = traits };
            if (eater.Animal)
            {
                if (!pawns.TryGetValue(e.Pawn.Value, out var row) || row is null || !races.Race(new Resource(row.Pawn.DefName ?? ""), out var race))
                {
                    continue;
                }
                eater = eater with { Edible = race.Edible };
            }
            output.Add(eater);
        }
        return output;
    }

    /// <summary>
    /// Projects only same-tick validated facts. Native raw food runway cannot stand in for the
    /// diet/rot/competing-feed forecast needed by FoodDays.
    ///
    /// <paramref name="tables"/> are the frame's keyed tables every building and pawn reference resolves
    /// against; a family with a reference they do not hold is unknown until a later frame.
    /// </summary>
    public static ColonyProjection DecodeColony(O.ColonyFactsSnapshot v, Identity expected, Tables tables, DefinitionCatalog? catalog)
    {
        var buildings = tables.Buildings;
        expected.Validate();
        var identity = DecodeReads.ContextIdentity(v.Context);
        // The colony facts row may come from the step's fact cache at any earlier tick; only another generation
        // changes its world.
        if (expected.NativeGeneration.TryGetValue(out var generation)
            && identity.NativeGeneration.TryGetValue(out var actual) && actual != generation)
        {
            throw new ObservationChangedException();
        }
        // The race rows are static for a load and come from the catalog; an unbuildable race table fails the reading.
        var races = RequireCatalog(catalog).AnimalRaces();
        var r = new ColonyProjection
        {
            Identity = identity,
            Bounds = new Bounds((int)v.MapSize.Width, (int)v.MapSize.Height),
            PlayerTechLevel = v.PlayerTechLevel,
            Workers = (int)v.WorkerCount,
            DeliveryLedger = v.DeliveryLedger,
            PendingFoodNutrition = v.PendingFoodNutrition,
            PendingHunts = (int)v.PendingHunts,
            Threat = v.Threat.Observed,
            DeepResources = v.DeepResources,
            Policies = v.Policies,
            Books = catalog!.Books(),
            Foods = catalog.Foods(),
            FoodEaters = FoodEaters(v.Policies.FoodEaters, tables.Pawns, races, catalog),
            RoyaltySection = v.Royalty,
            BiotechSection = v.Biotech,
        };
        r = r with { FoodChannels = FoodChannelsReads.ColonyFoodChannels(v.FoodChannels, tables.Pawns, races) };
        var conditions = ColonyFramesReads.ColonyConditions(v, catalog);
        if (catalog is not null)
        {
            r = r with
            {
                RoofSupport = Verse.RoofCollapseUtility.RoofMaxSupportDistance,
                Packable = catalog.Packable(),
                LightTerrains = new LightTerrains(catalog.LightTerrains().ToDictionary(terrain => terrain, _ => true)),
                Impressiveness = catalog.ImpressivenessLevels(),
                PowerSources = catalog.PowerSources(),
            };
            var generatorRows = new Dictionary<string, GeneratorRow>(StringComparer.Ordinal);
            foreach (var name in PowerRules.GeneratorDefinitions)
            {
                if (catalog.FindThing(name) is null)
                {
                    continue;
                }
                generatorRows[name] = catalog.GeneratorRow(name);
            }
            r = r with
            {
                GeneratorRows = generatorRows,
                ComponentSteel = catalog.ProductInputCost(TradePolicyRules.ComponentResource.Value, SiteRules.SteelResource.Value),
            };
            if (catalog.FindThing(PowerRules.BatteryDefinition) is not null)
            {
                r = r with { PowerBattery = catalog.PowerBattery(PowerRules.BatteryDefinition) };
            }
        }
        // The biome's permanent darkness is its map conditions': a frame without a biome row or a catalog fails, never counts as lit.
        var outdoorsDark = ColonyFramesReads.ColonyOutdoorsDark(v, catalog);
        // No eligible indoor bed (SleepingTemperature.NoIndoorBed) leaves the rounds' indoor sleeping range unknown.
        var (indoor, sleepingMin, sleepingMax) = v.SleepingTemperature is O.SleepingTemperature.Indoor(var min, var max) ? (true, min, max) : (false, 0d, 0d);
        var f = new RoundsFacts
        {
            Tick = identity.Tick,
            WorkResources = Known(v.WorkResources),
            Colonists = v.ColonistCount,
            BedCapacity = v.BedCapacity,
            IndoorCapacity = v.IndoorSleepingCapacity,
            SleepingMin = Optional(indoor, sleepingMin),
            SleepingMax = Optional(indoor, sleepingMax),
            OutdoorTemperature = v.OutdoorTemperatureC,
        };
        f = f with
        {
            MapBounds = Known(r.Bounds),
            ShelterArea = AllowedAreaID(r.Policies, ShelterRules.SafeAreaLabel),
            NoDangerArea = AllowedAreaID(r.Policies, ShelterRules.NoDangerAreaLabel),
            AnimalWard = f.AnimalWard with { Area = AllowedAreaID(r.Policies, HerdWardRules.HospitalAreaLabel) },
            IsolationArea = AllowedAreaID(r.Policies, IsolationRoomRules.IsolationAreaLabel),
            RaidPoints = r.Threat.RaidPoints,
            HostileFactions = r.Threat.HostileFactions,
            Storyteller = r.Threat.Storyteller,
            DaysPassed = r.Threat.DaysPassed,
            Wealth = Known(new WealthFacts(r.Threat.WealthItems, r.Threat.WealthTotal)),
            Disease = r.Threat.Disease,
        };
        r = r with { Raids = r.Threat.Raids };
        f = f with { PenGrazing = r.FoodChannels.Grazing, Pasture = r.FoodChannels.Pasture };
        if (v.Development?.Outcome is { } development && Resolved(buildings, development.Power, row => row.Building))
        {
            var power = new List<PowerBuilding>(development.Power.Count);
            var topology = new PowerTopology();
            var geometryKnown = true;
            var turrets = new List<DefenseTurretFacts>();
            var fuel = new List<FuelConsumer>();
            // A solar flare holds every network off.
            topology = topology with
            {
                Blackout = conditions.Any(condition => condition.DisablesPower),
                Eclipse = conditions.Any(condition => condition.Definition == DisasterRules.ConditionEclipse),
            };
            var sites = new List<PowerSite>();
            var defs = RequireCatalog(catalog);
            foreach (var row in development.Power)
            {
                buildings.TryGetRow(row.Building, out var b);
                var s = b!.Service;
                var def = b.Building.DefName;
                var reference = b.Building;
                // Every development power row is a power trader or a battery: a battery is on while connected and
                // outputs nothing.
                var (output, powered, network) = s.Power is O.BuildingPower.Trader trader ? (trader.OutputW, trader.PowerOn, trader.Network)
                    : s.Power is O.BuildingPower.Battery battery ? (0d, battery.Network is not null, battery.Network)
                    : throw new ObservationContractException($"development power row {reference.Id} has no power comp");
                var site = new PowerSite
                {
                    Id = reference.Id,
                    Definition = reference.DefName,
                    Cell = reference.Place is O.EntityPlace.OnMap placed ? CellOf(placed.Cell) : default,
                    Occupied = CellsRules.RectCells(b.Occupied),
                    BaseW = defs.PowerBaseW(def, tables.FinishedResearch),
                    OutputW = output,
                    Powered = powered,
                    Connected = network is not null,
                    Network = network ?? "",
                    Forbidden = b.Settings.Forbidden,
                    SwitchedOn = s.SwitchedOn,
                    Fuel = s.Fuel,
                    OutOfFuel = Optional(s.Fuel is O.BuildingFuel.Refuelable, s.Fuel is O.BuildingFuel.Refuelable { OutOfFuel: true }),
                    BrokenDown = s.BrokenDown,
                    FuelDefinitions = defs.RefuelFuels(def),
                    StorageWD = defs.BatteryCapacityWd(def),
                    // Whether rain shorts the building is its def's, not the frame's.
                    RainVulnerable = defs.RainVulnerable(def),
                    Roofed = row.Roofed,
                    Turret = row.TurretDps is { } dps ? new TurretGun(dps) : null,
                };
                power.Add(site);
                var consumer = FuelReads.FuelConsumerOf(catalog, site.Id, site.Definition, site, out var refuelable);
                if (refuelable)
                {
                    fuel.Add(consumer);
                }
                geometryKnown = geometryKnown && reference.Place is O.EntityPlace.OnMap && site.Occupied.Count > 0;
                sites.Add(site);
                if (site.Turret is { } gun)
                {
                    turrets.Add(new DefenseTurretFacts { Id = site.Id, Cell = site.Cell, Powered = site.Powered, Dps = gun.Dps, OutOfFuel = site.Unfueled, FuelDefinitions = [.. site.FuelDefinitions.Select(d => new Resource(d))] });
                }
            }
            topology = topology with { Buildings = sites };
            // A conduit is a built plain building of a def whose power comp only transmits: the def rows say
            // which, the building table says where.
            var conduits = new List<Cell>();
            var unsafeConduits = new List<Cell>();
            foreach (var row in buildings.Table.Sorted())
            {
                if (row.Status != O.BuildingStatus.Built || !defs.PlainConduit(row.Building.DefName))
                {
                    continue;
                }
                if (row.Building.Place is not O.EntityPlace.OnMap at)
                {
                    geometryKnown = false;
                    continue;
                }
                var cell = CellOf(at.Cell);
                conduits.Add(cell);
                if (row.Building.DefName == "PowerConduit")
                {
                    unsafeConduits.Add(cell);
                }
            }
            var networks = new List<PowerNetworkFact>();
            foreach (var row in development.Networks)
            {
                networks.Add(new PowerNetworkFact
                {
                    Id = row.Id,
                    GenerationW = row.GenerationW,
                    ConsumptionW = row.ConsumptionW,
                    StoredWD = row.StoredWattDays,
                });
            }
            var geysers = new List<PowerGeyser>();
            foreach (var row in development.Geysers)
            {
                var reference = tables.Entity(row.Geyser);
                if (reference?.Place is not O.EntityPlace.OnMap at)
                {
                    geometryKnown = false;
                    continue;
                }
                var geyserCells = new List<Cell>();
                foreach (var cell in row.Cells)
                {
                    geyserCells.Add(CellOf(cell));
                }
                geysers.Add(new PowerGeyser { Id = reference.Id ?? "", Cell = CellOf(at.Cell), Occupied = row.Occupied, Cells = geyserCells });
            }
            topology = topology with { Conduits = conduits, UnsafeConduits = unsafeConduits, Networks = networks, Geysers = geysers };
            if (geometryKnown)
            {
                r = r with { PowerPlanning = Known(topology) };
            }
            r = r with { DefenseTurrets = Known(EquatableArray.From(turrets)) };
            f = f with { Fuel = Known(EquatableArray.From(fuel)) };
            var coverage = PowerRules.PowerCoverageOf(power);
            f = f with
            {
                PowerRequired = Known(coverage.Required),
                PowerHeadroom = Known(coverage.HeadroomW),
                DisabledConsumers = Known(coverage.Disabled),
                PowerWeatherSafe = Known(unsafeConduits.Count == 0 && PowerRules.PowerWeatherSafe(power)),
            };
            f = f with { ShortCircuit = development.ShortCircuitTick is { } shorted ? new DisasterShortCircuit.Seen(new Tick(shorted)) : new DisasterShortCircuit.NoneSeen() };
        }
        // The biome is read on every frame; the season facts only with a clean food climate read.
        r = r with { CropClimate = new CropClimate { Biome = v.Biome } };
        if (v.FoodClimate is { } climate && !HasIssue(v.Issues, "food_climate"))
        {
            r = r with
            {
                CropClimate = r.CropClimate with
                {
                    Sowing = Optional(climate.SowingNow is not null, climate.SowingNow.GetValueOrDefault()),
                    DaysRemaining = Optional(climate.GrowingDaysRemaining is not null, climate.GrowingDaysRemaining.GetValueOrDefault()),
                    OutdoorsDark = Known(outdoorsDark),
                },
            };
            f = f with { Calendar = ColonyProductionReads.ColonyCalendar(climate) };
        }
        EquatableArray<double>? curve = v.Planning is O.Planning.Requested { Facts.Gear: { } planningGear } ? planningGear.Climate.Temperatures : null;
        if (curve is { } curveTemps && curveTemps.Count == 12)
        {
            var temps = new double[curveTemps.Count];
            for (var i = 0; i < curveTemps.Count; i++)
            {
                temps[i] = curveTemps[i];
            }
            r = r with
            {
                ColdMap = Known(LayoutRoofRules.ColdMapCurve(temps)),
                HotMap = Known(LayoutRoofRules.HotMapCurve(temps)),
                OutdoorCurve = Known(EquatableArray.From(temps)),
                FullRotRateC = RequireCatalog(catalog).Derived.FullRotRateC,
            };
        }
        AcquisitionReads.ApplyAcquisition(v, tables, catalog, ref r);
        f = ColonyProductionReads.ColonyProduction(v, f);
        r = r with { ProductionBenches = ColonyProductionReads.ColonyProductionBenches(v, buildings, tables.Pawns, catalog) };
        f = f with { TradeMealIngredients = TradePolicyRules.TradeMealIngredients(r.ProductionBenches) };
        if (!HasIssue(v.Issues, "butchering") && Headed(buildings, v.Butchering, b => b.Bench))
        {
            var benches = new List<CookingBench>();
            foreach (var b in v.Butchering)
            {
                benches.Add(new CookingBench
                {
                    Id = b.Bench.Id,
                    Definition = buildings.Entity(b.Bench)?.DefName ?? "",
                    Usable = b.Usable,
                    Room = OptionalRef(b.Room),
                });
            }
            var standing = new List<ButcherBench>();
            foreach (var b in benches)
            {
                standing.Add(new ButcherBench { Definition = b.Definition, Room = b.Room });
            }
            f = f with { ButcherBenches = Known(EquatableArray.From(standing)) };
            r = r with { ButcheringBenches = Known(EquatableArray.From(benches)) };
        }
        f = ColonyFramesReads.ColonyDisaster(v, f, buildings, conditions);
        var comfort = ColonyFramesReads.ColonyComfort(v, catalog);
        f = f with { Comfort = comfort, BasicComfort = comfort, HomeCoverage = v.Upkeep.HomeCoverage };
        f = f with { Sleeping = SleepingUpkeepReads.ColonySleeping(v, buildings, catalog) };
        f = f with
        {
            AnimalUpkeep = f.AnimalUpkeep with
            {
                AnimalRaces = races,
                Animals = AnimalUpkeepReads.MergeHerdFoodFacts(AnimalUpkeepReads.ColonyAnimals(v, tables.Pawns, races), r.FoodChannels),
                WildAnimals = AnimalUpkeepReads.ColonyWildAnimals(v, tables.Pawns, races),
                Roamers = AnimalUpkeepReads.ColonyRoamers(v),
            },
        };
        f = f with { Waste = ColonyFramesReads.ColonyWaste(v, tables), Biotech = v.Biotech };
        f = f with { Upkeep = ColonyUpkeepReads.ColonyUpkeep(v, tables, catalog) };
        f = f with { OutdoorsDark = outdoorsDark };
        f = f with { MedicalReserve = ColonyFramesReads.ColonyMedicalReserve(v, tables, catalog) };
        f = f with { JoinerLetters = Known(v.JoinerLetters) };
        if (!HasIssue(v.Issues, "cooking") && Headed(buildings, v.Cooking, b => b.Bench))
        {
            var benches = new List<CookingBench>();
            foreach (var bench in v.Cooking)
            {
                benches.Add(new CookingBench
                {
                    Id = bench.Bench.Id,
                    Definition = buildings.Entity(bench.Bench)?.DefName ?? "",
                    Usable = bench.Usable,
                    Room = OptionalRef(bench.Room),
                    AutoRefuel = Optional(bench.AutoRefuel is not null, bench.AutoRefuel.GetValueOrDefault()),
                });
            }
            r = r with { CookingBenches = Known(EquatableArray.From(benches)) };
        }
        // A food supply whose stock the things table misses stays unknown until a later frame.
        if (FoodSupplyReads.DecodeFoodSupply(v.FoodSupply, tables.Things, catalog) is { } supply)
        {
            r = r with { FoodSupply = Known(supply) };
            f = f with { FoodStorageUpkeep = FoodStorageUpkeepRules.FoodStorageStocks(supply, RequireCatalog(catalog).Derived.FullRotRateC) };
        }
        var combined = FoodSupplyReads.DecodeFoodSupply(v.Forecast.CombinedFoodSupply, tables.Things, catalog);
        if (combined is not null)
        {
            r = r with { CombinedFoodSupply = Known(combined) };
        }
        if (combined is null || !r.FoodSupply.TryGetValue(out var human))
        {
            throw new ObservationContractException("the food runway needs the colonists' food supply and the combined food supply");
        }
        if (human.Consumers.Count == 0)
        {
            f = f with { FoodDays = new FoodRunway.NoEaters() };
        }
        else
        {
            var selected = new List<PawnId>(human.Consumers.Count);
            foreach (var consumer in human.Consumers)
            {
                selected.Add(consumer.Id);
            }
            // The forecast over the colonists who eat; one the policy refuses fails the reading.
            try
            {
                var forecastFood = FoodForecastRules.ForecastFood(combined, selected);
                f = f with { FoodDays = forecastFood.RunwayDays };
                r = r with { FoodAtRiskNutrition = Known(forecastFood.AtRiskNutrition) };
            }
            catch (DomainException e)
            {
                throw new ObservationContractException("the food forecast refused the colony's food supply: " + e.Message);
            }
        }
        f = f with { AnimalUpkeep = f.AnimalUpkeep with { Food = r.CombinedFoodSupply } };
        if (!HasIssue(v.Issues, "resources"))
        {
            var rows = new List<Amount>(v.Resources.Count);
            var wood = 0L;
            foreach (var q in v.Resources)
            {
                if (q.DefName == "WoodLog")
                {
                    wood = q.Units;
                }
                rows.Add(new Amount(new Resource(q.DefName), q.Units));
            }
            f = f with { Wood = Known(wood), Resources = Known(EquatableArray.From(rows)), ResourceThings = Known(v.ResourceThings) };
        }
        r = r with { Resources = StockCensus(v) };
        if (v.EventLoot?.Outcome is { } loot && Headed(tables, loot.Items, row => row.Item))
        {
            var rows = new List<LootItem>(loot.Items.Count);
            var spawnForbidden = RequireCatalog(catalog).SpawnForbiddenProducts();
            foreach (var row in loot.Items)
            {
                var head = tables.Entity(row.Item);
                var item = new LootItem
                {
                    Supply = new StartingSupply { Thing = row.Item?.Id ?? "", Definition = head?.DefName ?? "", Cell = MapCellOf(head!) },
                    Forbidden = row.Forbidden.GetValueOrDefault(),
                    SafeToHaul = row.SafeToHaul.GetValueOrDefault(),
                    SafetyKnown = row.SafeToHaul is not null,
                    Count = row.Count.GetValueOrDefault(),
                    SpawnForbidden = spawnForbidden.Contains(head?.DefName ?? ""),
                    AcceptedBy = EquatableArray.From(row.AcceptedBy.Select(r => r.Id ?? "")),
                    Deterioration = row.Deterioration,
                };
                if (row.PathLength is not null)
                {
                    item = item with { PathLength = Known(row.PathLength.GetValueOrDefault()) };
                }
                if (row.StorageHeadroom is not null)
                {
                    item = item with { StorageHeadroom = Known(row.StorageHeadroom.GetValueOrDefault()) };
                }
                rows.Add(item);
            }
            f = f with { EventLoot = Known(EquatableArray.From(rows)) };
        }
        if (v.Planning is O.Planning.Requested { Facts: var planningObserved }
            && Headed(buildings, planningObserved.Environment.Lights, l => l.Building) && Headed(buildings, planningObserved.Environment.Growers, g => g.Building))
        {
            var environment = ColonyEnvironment(planningObserved.Environment, v.OutdoorTemperatureC, buildings, catalog);
            r = r with { Environment = Known(environment) };
        }
        // Facts.Gear needs the catalog: ObserveColony fills it.
        return r with { Facts = f };
    }

    internal static DefinitionCatalog RequireCatalog(DefinitionCatalog? catalog) =>
        catalog ?? throw new BridgeContractException("no definition catalog is held for this load");

    /// <summary>
    /// Decodes the native controlled-growing census whole. Rows keep their native order (ids ascending); a
    /// grower's fertility and sow tag are its def row's in the catalog.
    /// </summary>
    internal static ControlledEnvironment ColonyEnvironment(O.ControlledEnvironment v, double outdoor, Buildings buildings, DefinitionCatalog? catalog)
    {
        var lights = new List<GrowLight>();
        foreach (var row in v.Lights)
        {
            lights.Add(new GrowLight
            {
                Id = HeadedEntity(buildings, row.Building).Id,
                Room = row.Room?.Id,
                Network = row.PowerNetId,
                LitNow = row.LitNow,
                GrowthCells = row.GrowthCells,
            });
        }
        var growers = new List<PlantGrower>();
        foreach (var row in v.Growers)
        {
            var reference = HeadedEntity(buildings, row.Building);
            var (fertility, sowTag) = GrowerRows(catalog, reference.DefName);
            growers.Add(new PlantGrower
            {
                Id = reference.Id,
                Fertility = fertility,
                SowTag = sowTag,
                Crop = row.CropDefName,
                CanSow = row.CanSow,
            });
        }
        var rooms = new List<GrowRoom>();
        foreach (var row in v.Rooms)
        {
            rooms.Add(new GrowRoom { Id = row.Room.Id, GridRoom = row.GridRoom, TemperatureC = row.TemperatureC });
        }
        var networks = new List<PowerHeadroom>();
        foreach (var row in v.Networks)
        {
            networks.Add(new PowerHeadroom
            {
                Id = row.Id,
                GenerationW = row.GenerationW,
                SolarW = row.SolarW,
                WindW = row.WindW,
                ConsumptionW = row.ConsumptionW,
                CapacityWattDays = row.CapacityWattDays,
                ActiveSource = row.HasActiveSource,
            });
        }
        return new ControlledEnvironment
        {
            Lights = [.. lights],
            Growers = [.. growers],
            Rooms = [.. rooms],
            Networks = [.. networks],
            OutdoorTemperatureC = outdoor,
        };
    }

    /// <summary>
    /// A plant grower def's own numbers: its fixed fertility, or the ground's for a def that sets none, and the
    /// sow tag of its building properties (null when it names none). A def the catalog has no row for is a contract break.
    /// </summary>
    /// <summary>The building row a census row refers to; the caller checked <see cref="Headed{TRow}(Buildings, IEnumerable{TRow}, Func{TRow, C.Ref?})"/> first.</summary>
    private static O.EntityRef HeadedEntity(Buildings buildings, C.Ref reference) =>
        buildings.Entity(reference) ?? throw new ObservationContractException($"building {reference.Id} is not in the building table");

    internal static (GrowerFertility Fertility, string? SowTag) GrowerRows(DefinitionCatalog? catalog, string def)
    {
        var row = RequireCatalog(catalog).FindThing(def)
            ?? throw new ObservationContractException($"plant grower {def} has no definition row");
        GrowerFertility fertility = row.fertility >= 0
            ? new GrowerFertility.Fixed(double.IsFinite(row.fertility) ? row.fertility : throw new ObservationContractException($"plant grower {def} has a nonfinite fertility"))
            : new GrowerFertility.Ground();
        var tag = row.building?.sowTag;
        return (fertility, string.IsNullOrEmpty(tag) ? null : tag);
    }

    /// <summary>
    /// The wire string as its enum member. A
    /// name the enum does not declare is a producer the brain was not built for, a contract break.
    /// </summary>
    internal static TEnum WireParse<TEnum>(string? name)
        where TEnum : struct, Enum =>
        Wire.TryParse<TEnum>(name ?? "", out var value)
            ? value
            : throw new ObservationContractException($"{typeof(TEnum).Name} {name} is not a defined member");

    /// <summary>
    /// <see cref="WireParse{TEnum}"/> for an open set (a def another mod adds): a name the enum does not declare reads as
    /// its <paramref name="unrecognized"/> member rather than failing the whole observation.
    /// </summary>
    internal static TEnum WireParseOpen<TEnum>(string? name, TEnum unrecognized)
        where TEnum : struct, Enum =>
        Wire.TryParse<TEnum>(name ?? "", out var value) ? value : unrecognized;
}

/// <summary>Null-tolerant row lookups over the bridge tables, which take a non-null reference.</summary>
internal static class TableLookups
{
    public static bool TryGetRow(this Buildings buildings, C.Ref? reference, out O.BuildingState row)
    {
        row = null!;
        return reference is not null && buildings.TryRow(reference, out row);
    }

    public static bool TryGetRow(this Things things, C.Ref? reference, out O.Thing row)
    {
        row = null!;
        return reference is not null && things.TryRow(reference, out row);
    }

    public static bool TryGetRow(this Pawns pawns, C.Ref? reference, out O.PawnState row)
    {
        row = null!;
        return reference is not null && pawns.TryRow(reference, out row);
    }
}
