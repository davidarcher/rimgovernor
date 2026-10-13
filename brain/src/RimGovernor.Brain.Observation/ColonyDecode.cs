namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
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
    internal static Fact<string> OptionalRef(C.Ref? r) => r is null ? Unknown<string>() : Optional(r.HasId, r.Id);

    internal static Fact<long> CountFact(bool has, uint value) => has ? Known((long)value) : Unknown<long>();

    internal static bool HasIssue(IEnumerable<O.ReadIssue> issues, string field)
    {
        foreach (var issue in issues)
        {
            if (issue.Field == field)
            {
                return true;
            }
        }
        return false;
    }

    internal static Fact<bool> NativePresence(string? value, IEnumerable<O.ReadIssue> issues, string field) =>
        CellPresence(value, issues, field, false);

    private static Cell CellOf(C.Cell? cell) => new(cell?.X ?? 0, cell?.Z ?? 0);

    /// <summary>
    /// Projects only same-tick validated facts. Native raw food runway cannot stand in for the
    /// diet/rot/competing-feed forecast needed by FoodDays.
    ///
    /// <paramref name="tables"/> are the frame's keyed tables every building and pawn reference resolves
    /// against; a family with a reference they do not hold is unknown until a later frame.
    /// </summary>
    public static ColonyProjection DecodeColony(O.ColonyFactsReply? reply, Identity expected, Tables tables, DefinitionCatalog? catalog)
    {
        var buildings = tables.Buildings;
        expected.Validate();
        if (reply is null)
        {
            throw new ObservationContractException("missing colony facts reply");
        }
        if (reply.Unavailable is not null)
        {
            throw new BridgeUnavailableException();
        }
        if (reply.Failure is not null)
        {
            throw new BridgeRefusedException();
        }
        var v = reply.Observed ?? throw new ObservationContractException("missing colony facts");
        BridgeFunctions.ValidateColonyFacts(v, new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value });
        var identity = ContextIdentity(v.Context);
        // The colony facts row may come from the step's fact cache at any earlier tick; only another generation
        // changes its world.
        if (expected.NativeGeneration.TryGetValue(out var generation)
            && identity.NativeGeneration.TryGetValue(out var actual) && actual != generation)
        {
            throw new ObservationChangedException();
        }
        var r = new ColonyProjection
        {
            Identity = identity,
            Bounds = new Bounds((int)(v.MapSize?.Width ?? 0), (int)(v.MapSize?.Height ?? 0)),
            PlayerTechLevel = Optional(v.HasPlayerTechLevel, v.PlayerTechLevel),
            Threat = ProjectColonyThreat(v),
        };
        // The race rows are static for a load and come from the catalog; an unbuildable race table fails the reading.
        var races = RequireCatalog(catalog).AnimalRaces();
        r = r with
        {
            FoodChannels = ColonyFoodChannels(v.FoodChannels, tables.Pawns, races),
            DeliveryLedger = ColonyDeliveryLedger(v.DeliveryLedger, v.Context?.Tick ?? 0),
            DeepResources = ColonyDeepResources(v.DeepResources, catalog),
            Policies = ColonyPolicies(v.Policies),
        };
        if (r.Policies.TryGetValue(out var policies))
        {
            policies = policies with { Books = catalog!.Books(), Foods = catalog.Foods() };
            if (v.Biome is not null)
            {
                policies = policies with { BiomeDiseases = catalog.BiomeDiseases(v.Biome) };
            }
            r = r with { Policies = Known(policies) };
        }
        var (conditions, conditionsKnown) = ColonyConditions(v, catalog);
        if (catalog is not null)
        {
            r = r with
            {
                RoofSupport = Verse.RoofCollapseUtility.RoofMaxSupportDistance,
                BedPrice = MarketBedPrice(catalog),
                Packable = catalog.Packable(),
                LightTerrains = new LightTerrains(catalog.LightTerrains().ToDictionary(terrain => terrain, _ => true)),
                Impressiveness = catalog.ImpressivenessLevels(),
                PowerSources = catalog.PowerSources(),
            };
            var generatorRows = new Dictionary<string, GeneratorRow>(StringComparer.Ordinal);
            foreach (var name in PolicyConstants.GeneratorDefinitions)
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
                ComponentSteel = catalog.ProductInputCost(ComponentResource.Value, SteelResource.Value),
            };
            if (catalog.FindThing(PolicyConstants.BatteryDefinition) is not null)
            {
                r = r with { PowerBattery = catalog.PowerBattery(PolicyConstants.BatteryDefinition) };
            }
        }
        // The biome's permanent darkness is its map conditions', read only where something decides on it
        // (sowing climate, a lighting census): there a frame without a biome read or a catalog fails, never counts as lit.
        var outdoorsDark = default(Fact<bool>);
        r = r with
        {
            Biotech = ColonyBiotech(v.Biotech),
            Odyssey = ColonyOdyssey(v.Odyssey),
            Anomaly = ColonyAnomaly(v.Anomaly),
            RoyaltyColony = ColonyRoyalty(v.Royalty),
        };
        var f = new RoundsFacts
        {
            Colonists = CountFact(v.HasColonistCount, v.ColonistCount),
            BedCapacity = CountFact(v.HasBedCapacity, v.BedCapacity),
            IndoorCapacity = CountFact(v.HasIndoorSleepingCapacity, v.IndoorSleepingCapacity),
            SleepingMin = Optional(v.HasSleepingTemperatureMinC, v.SleepingTemperatureMinC),
            SleepingMax = Optional(v.HasSleepingTemperatureMaxC, v.SleepingTemperatureMaxC),
            OutdoorTemperature = Optional(v.HasOutdoorTemperatureC, v.OutdoorTemperatureC),
        };
        f = f with
        {
            MapBounds = Known(r.Bounds),
            ShelterArea = ShelterArea(r.Policies),
            NoDangerArea = AllowedAreaID(r.Policies, NoDangerAreaLabel),
            AnimalWard = f.AnimalWard with { Area = AllowedAreaID(r.Policies, HospitalAreaLabel) },
            IsolationArea = AllowedAreaID(r.Policies, PolicyConstants.IsolationAreaLabel),
            RaidPoints = r.Threat.RaidPoints,
            Monolith = MonolithFacts(r.Anomaly),
        };
        r = r with { Raids = r.Threat.Raids };
        var threat = r.Threat;
        if (threat.WealthItems.TryGetValue(out var items) && threat.WealthBuildings.TryGetValue(out var wealthBuildings)
            && threat.WealthPawns.TryGetValue(out var wealthPawns) && threat.WealthTotal.TryGetValue(out var total))
        {
            f = f with { Wealth = Known(new WealthFacts(items, wealthBuildings, wealthPawns, total)) };
        }
        if (r.FoodChannels.TryGetValue(out var channels))
        {
            f = f with { PenGrazing = Known(channels.Grazing), Pasture = Known(channels.Pasture) };
        }
        if (v.Development?.Observed is { } development && Resolved(buildings, development.Power, row => row.Building))
        {
            var power = new List<PowerBuilding>(development.Power.Count);
            var topology = new PowerTopology();
            var geometryKnown = true;
            var turrets = new List<DefenseTurretFacts>();
            var fuel = new List<FuelConsumer>();
            if (conditionsKnown)
            {
                bool blackout = false, eclipse = false;
                foreach (var condition in conditions)
                {
                    blackout = blackout || condition.DisablesPower;
                    eclipse = eclipse || condition.Definition == "Eclipse";
                }
                topology = topology with { Blackout = Known(blackout), Eclipse = Known(eclipse) };
            }
            var sites = new List<PowerSite>();
            foreach (var row in development.Power)
            {
                buildings.TryGetRow(row.Building, out var b);
                var s = b!.Service;
                var def = b.Building?.HasDefName == true ? b.Building.DefName : null;
                // Whether rain shorts the building is its def's, not the frame's.
                var rainVulnerable = Unknown<bool>();
                if (def is not null && catalog is not null)
                {
                    rainVulnerable = Known(catalog.RainVulnerable(def));
                }
                IReadOnlyList<string> fuels = [];
                if (def is not null)
                {
                    fuels = RequireCatalog(catalog).RefuelFuels(def);
                }
                var baseW = Unknown<double>();
                if (def is not null && catalog is not null)
                {
                    baseW = catalog.PowerBaseW(def, tables.FinishedResearch);
                }
                var capacity = Unknown<double>();
                if (def is not null && catalog is not null)
                {
                    capacity = catalog.BatteryCapacityWd(def);
                }
                var reference = b.Building;
                var site = new PowerSite
                {
                    Id = reference?.Id ?? "",
                    Definition = reference?.DefName ?? "",
                    Cell = CellOf(reference?.Position),
                    Occupied = RectCells(b.Occupied),
                    BaseW = baseW,
                    OutputW = Optional(s.HasPowerOutputW, s.PowerOutputW),
                    Powered = Optional(s.HasPowerOn, s.PowerOn),
                    Connected = Optional(s.HasConnected, s.Connected),
                    Network = Optional(s.HasPowerNetId, s.PowerNetId),
                    Forbidden = Optional(b.Settings.HasForbidden, b.Settings.Forbidden),
                    SwitchedOn = Optional(s.HasSwitchedOn, s.SwitchedOn),
                    Fuel = Optional(s.HasFuel, s.Fuel),
                    TargetFuel = Optional(s.HasTargetFuel, s.TargetFuel),
                    OutOfFuel = Optional(s.HasOutOfFuel, s.OutOfFuel),
                    BrokenDown = Optional(s.HasBrokenDown, s.BrokenDown),
                    FuelDefinitions = fuels,
                    Stored = Optional(row.HasStoredWattDays, row.StoredWattDays),
                    Capacity = capacity,
                    RainVulnerable = rainVulnerable,
                    Roofed = Optional(row.HasRoofed, row.Roofed),
                    TurretDps = Optional(row.HasTurretDps, row.TurretDps),
                };
                power.Add(site);
                var consumer = FuelConsumerOf(catalog, site.Id, site.Definition, site, out var refuelable);
                if (refuelable)
                {
                    fuel.Add(consumer);
                }
                geometryKnown = geometryKnown && reference is { HasDefName: true, Position: not null } && site.Occupied.Count > 0;
                sites.Add(site);
                if (site.TurretDps.IsKnown)
                {
                    turrets.Add(new DefenseTurretFacts { Id = site.Id, Definition = site.Definition, Cell = site.Cell, Powered = site.Powered, Dps = site.TurretDps });
                }
            }
            topology = topology with { Buildings = sites };
            // A conduit is a built plain building of a def whose power comp only transmits: the def rows say
            // which, the building table says where.
            var conduits = new List<Cell>();
            var unsafeConduits = new List<Cell>();
            foreach (var row in buildings.Table.Sorted())
            {
                if (row.Status != O.BuildingStatus.Built || row.Building is not { HasDefName: true } || catalog is null)
                {
                    continue;
                }
                if (!catalog.PlainConduit(row.Building.DefName))
                {
                    continue;
                }
                if (row.Building.Position is null)
                {
                    geometryKnown = false;
                    continue;
                }
                var cell = CellOf(row.Building.Position);
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
                    GenerationW = Optional(row.HasGenerationW, row.GenerationW),
                    ConsumptionW = Optional(row.HasConsumptionW, row.ConsumptionW),
                    StoredWD = Optional(row.HasStoredWattDays, row.StoredWattDays),
                    CapacityWD = Optional(row.HasCapacityWattDays, row.CapacityWattDays),
                });
            }
            var geysers = new List<PowerGeyser>();
            foreach (var row in development.Geysers)
            {
                var reference = tables.Entity(row.Geyser);
                if (reference?.Position is null)
                {
                    geometryKnown = false;
                    continue;
                }
                var geyserCells = new List<Cell>();
                foreach (var cell in row.Cells)
                {
                    geyserCells.Add(CellOf(cell));
                }
                geysers.Add(new PowerGeyser { Id = reference.Id, Cell = CellOf(reference.Position), Occupied = row.Occupied, Cells = geyserCells });
            }
            topology = topology with { Conduits = conduits, UnsafeConduits = unsafeConduits, Networks = networks, Geysers = geysers };
            if (geometryKnown)
            {
                r = r with { PowerPlanning = Known(topology) };
            }
            r = r with { DefenseTurrets = Known(EquatableArray.From(turrets)) };
            f = f with { Fuel = Known(EquatableArray.From(fuel)) };
            var (required, headroom, disabled) = PolicyFunctions.PowerCoverage(Known(EquatableArray.From(power)));
            f = f with { PowerRequired = required, PowerHeadroom = headroom, DisabledConsumers = disabled, PowerWeatherSafe = PolicyFunctions.PowerWeatherSafe(power) };
            if (unsafeConduits.Count > 0)
            {
                f = f with { PowerWeatherSafe = Known(false) };
            }
            if (development.HasShortCircuitTick)
            {
                f = f with { ShortCircuitTick = Known(new Tick((long)development.ShortCircuitTick)) };
            }
        }
        if (v.FoodClimate is { } climate && !HasIssue(v.Issues, "food_climate"))
        {
            outdoorsDark = ColonyOutdoorsDark(v, catalog);
            r = r with
            {
                CropClimate = new CropClimate
                {
                    Sowing = Optional(climate.HasSowingNow, climate.SowingNow),
                    DaysRemaining = Optional(climate.HasGrowingDaysRemaining, climate.GrowingDaysRemaining),
                    OutdoorsDark = outdoorsDark,
                    Biome = Optional<string>(v.HasBiome, v.Biome ?? ""),
                },
            };
            f = f with { Calendar = ColonyCalendar(climate) };
        }
        var curve = v.Planning?.Observed?.Gear?.OutdoorTemperatureByTwelfthC;
        if (curve is not null && curve.Count == 12)
        {
            var temps = new double[curve.Count];
            for (var i = 0; i < curve.Count; i++)
            {
                temps[i] = curve[i];
            }
            r = r with
            {
                ColdMap = Known(PolicyFunctions.ColdMapCurve(temps)),
                HotMap = Known(PolicyFunctions.HotMapCurve(temps)),
                OutdoorCurve = Known(EquatableArray.From(temps)),
                FullRotRateC = RequireCatalog(catalog).Derived.FullRotRateC,
            };
        }
        ApplyAcquisition(v, tables, catalog, ref r);
        f = ColonyProduction(v, f);
        r = r with { ProductionBenches = ColonyProductionBenches(v, buildings, tables.Pawns, catalog) };
        f = f with { TradeMealIngredients = PolicyFunctions.TradeMealIngredients(r.ProductionBenches) };
        if (!HasIssue(v.Issues, "butchering") && Headed(buildings, v.Butchering, b => b.Bench))
        {
            var benches = new List<CookingBench>();
            foreach (var b in v.Butchering)
            {
                benches.Add(new CookingBench
                {
                    Id = b.Bench?.Id ?? "",
                    Definition = buildings.Entity(b.Bench)?.DefName ?? "",
                    Usable = Optional(b.HasUsable, b.Usable),
                    Room = OptionalRef(b.Room),
                });
            }
            var standing = new List<ButcherBench>();
            foreach (var b in benches)
            {
                standing.Add(new ButcherBench { Id = b.Id, Definition = b.Definition, Room = b.Room });
            }
            f = f with { ButcherBenches = Known(EquatableArray.From(standing)) };
            r = r with { ButcheringBenches = Known(EquatableArray.From(benches)) };
        }
        f = ColonyDisaster(v, f, buildings, catalog, conditions, conditionsKnown);
        var comfort = ColonyComfort(v, catalog);
        f = f with { Comfort = comfort, BasicComfort = comfort, HomeCoverage = ColonyHomeCoverage(v) };
        f = f with { StoneStructures = ColonyStoneStructures(v, buildings, catalog) };
        f = f with { Sleeping = ColonySleeping(v, buildings, catalog) };
        f = f with
        {
            AnimalUpkeep = f.AnimalUpkeep with
            {
                AnimalRaces = races,
                Animals = MergeHerdFoodFacts(ColonyAnimals(v, tables.Pawns, races), r.FoodChannels),
                WildAnimals = ColonyWildAnimals(v, tables.Pawns, races),
            },
        };
        if (r.Policies.TryGetValue(out policies))
        {
            policies = policies with { FoodEaters = AnimalFoodEaters(policies.FoodEaters, tables.Pawns, races, catalog) };
            r = r with { Policies = Known(policies) };
        }
        f = f with { Waste = ColonyWaste(v, tables), Pollution = ColonyPollution(r.Biotech) };
        f = f with { Upkeep = ColonyUpkeep(v, tables, catalog) };
        if (f.Upkeep.Lighting.IsKnown && !outdoorsDark.IsKnown)
        {
            outdoorsDark = ColonyOutdoorsDark(v, catalog);
        }
        f = f with { OutdoorsDark = outdoorsDark };
        f = f with { MedicalReserve = ColonyMedicalReserve(v, tables, catalog) };
        // The dialog section is present exactly while a force-pausing choice dialog is open; native omits it otherwise.
        f = f with { ChoiceDialog = Known(v.Dialog is not null) };
        var letters = new List<JoinerLetterOffer>(v.JoinerLetters.Count);
        foreach (var row in v.JoinerLetters)
        {
            letters.Add(new JoinerLetterOffer
            {
                Id = row.LetterId,
                Token = row.SnapshotToken,
                Pawn = new PawnId(row.PawnId),
                Expires = new Tick((long)row.ExpiresTick),
                Label = row.AcceptLabel,
                CanAccept = row.CanAccept,
                CreepJoiner = row.Creepjoiner,
            });
        }
        f = f with { JoinerLetters = Known(EquatableArray.From(letters)) };
        if (!HasIssue(v.Issues, "cooking") && Headed(buildings, v.Cooking, b => b.Bench))
        {
            var benches = new List<CookingBench>();
            foreach (var bench in v.Cooking)
            {
                benches.Add(new CookingBench
                {
                    Id = bench.Bench?.Id ?? "",
                    Definition = buildings.Entity(bench.Bench)?.DefName ?? "",
                    Usable = Optional(bench.HasUsable, bench.Usable),
                    Room = OptionalRef(bench.Room),
                    AutoRefuel = Optional(bench.HasAutoRefuel, bench.AutoRefuel),
                });
            }
            r = r with { CookingBenches = Known(EquatableArray.From(benches)) };
        }
        // A food supply whose stock the things table misses stays unknown until a later frame.
        if (v.FoodSupply?.Observed is { } food)
        {
            if (DecodeFoodSupply(food, tables.Things, catalog) is { } supply)
            {
                r = r with { FoodSupply = Known(supply) };
                f = f with { FoodStorageUpkeep = PolicyFunctions.FoodStorageStocks(supply, RequireCatalog(catalog).Derived.FullRotRateC) };
            }
        }
        if (v.Forecast?.Observed is { } forecast)
        {
            var combined = DecodeFoodSupply(forecast.CombinedFoodSupply, tables.Things, catalog);
            if (combined is not null)
            {
                r = r with { CombinedFoodSupply = Known(combined) };
            }
            if (combined is not null && r.FoodSupply.TryGetValue(out var human) && human.Consumers.Count > 0)
            {
                var selected = new List<PawnId>(human.Consumers.Count);
                foreach (var consumer in human.Consumers)
                {
                    selected.Add(consumer.Id);
                }
                try
                {
                    var forecastFood = PolicyFunctions.ForecastFood(combined, selected);
                    f = f with { FoodDays = forecastFood.RunwayDays };
                    r = r with { FoodAtRiskNutrition = Known(forecastFood.AtRiskNutrition) };
                }
                catch (DomainException)
                {
                    // A forecast the policy refuses leaves the runway unknown.
                }
            }
        }
        f = f with { AnimalUpkeep = f.AnimalUpkeep with { Food = r.CombinedFoodSupply } };
        if (v.HasWorkerCount)
        {
            r = r with { Workers = Known((int)v.WorkerCount) };
        }
        if (!HasIssue(v.Issues, "resources"))
        {
            f = f with { Wood = Known(0L) };
            var rows = new List<Amount>(v.Resources.Count);
            var complete = true;
            var stock = new Dictionary<Resource, long>();
            foreach (var q in v.Resources)
            {
                if (q.DefName == "WoodLog")
                {
                    f = f with { Wood = Optional(q.HasUnits, q.Units) };
                }
                complete = complete && q.HasUnits;
                rows.Add(new Amount(new Resource(q.DefName), q.Units));
                if (q.HasUnits)
                {
                    var key = new Resource(q.DefName);
                    stock[key] = stock.GetValueOrDefault(key) + q.Units;
                }
            }
            if (complete)
            {
                f = f with { Resources = Known(EquatableArray.From(rows)) };
            }
            r = r with { Resources = Known<IReadOnlyDictionary<Resource, long>>(stock) };
        }
        if (v.EventLoot?.Observed is { } loot && Headed(tables, loot.Items, row => row.Item))
        {
            var rows = new List<LootItem>(loot.Items.Count);
            var spawnForbidden = RequireCatalog(catalog).SpawnForbiddenProducts();
            foreach (var row in loot.Items)
            {
                var head = tables.Entity(row.Item);
                var item = new LootItem
                {
                    Supply = new StartingSupply { Thing = row.Item?.Id ?? "", Definition = head?.DefName ?? "", Cell = CellOf(head?.Position) },
                    Forbidden = row.Forbidden,
                    SafeToHaul = row.SafeToHaul,
                    SafetyKnown = row.HasSafeToHaul,
                    Count = row.Count,
                    SpawnForbidden = spawnForbidden.Contains(head?.DefName ?? ""),
                };
                if (row.HasPathLength)
                {
                    item = item with { PathLength = Known(row.PathLength) };
                }
                if (row.HasStorageHeadroom)
                {
                    item = item with { StorageHeadroom = Known(row.StorageHeadroom) };
                }
                rows.Add(item);
            }
            f = f with { EventLoot = Known(EquatableArray.From(rows)) };
        }
        if (v.Planning?.Observed is { } planningObserved && planningObserved.Environment is { } planningEnvironment
            && !HasIssue(planningObserved.Issues, "environment")
            && Headed(buildings, planningEnvironment.Lights, l => l.Building) && Headed(buildings, planningEnvironment.Growers, g => g.Building))
        {
            var environment = ColonyEnvironment(planningEnvironment, v.HasOutdoorTemperatureC ? v.OutdoorTemperatureC : null, buildings, catalog);
            r = r with { Environment = Known(environment) };
        }
        // Facts.Gear needs the catalog: ObserveColony fills it.
        return r with { Facts = f };
    }

    internal static DefinitionCatalog RequireCatalog(DefinitionCatalog? catalog) =>
        catalog ?? throw new BridgeContractException("no definition catalog is held for this load");

    internal static EquatableArray<Cell> CellsOf(IEnumerable<C.Cell> rows)
    {
        var output = new List<Cell>();
        foreach (var row in rows)
        {
            output.Add(CellOf(row));
        }
        return EquatableArray.From(output);
    }

    /// <summary>
    /// Decodes the native controlled-growing census. Rows keep their native order (ids ascending); every
    /// scalar stays unknown when the native side omitted it.
    /// </summary>
    internal static ControlledEnvironment ColonyEnvironment(O.ControlledEnvironment v, double? outdoor, Buildings buildings, DefinitionCatalog? catalog)
    {
        var e = new ControlledEnvironment
        {
            OutdoorTemperatureC = outdoor is { } temperature ? Known(temperature) : Unknown<double>(),
            Daylight = Optional(v.HasDaylight, v.Daylight),
            WeatherAccuracy = Unknown<double>(),
        };
        if (v.Weather is not null)
        {
            e = e with { WeatherAccuracy = Known(RequireCatalog(catalog).WeatherAccuracy(v.Weather)) };
        }
        var lights = new List<GrowLight>();
        foreach (var row in v.Lights)
        {
            var reference = buildings.Entity(row.Building);
            var power = DefPowerW(catalog, reference?.DefName ?? "");
            lights.Add(new GrowLight
            {
                Id = reference?.Id ?? "",
                Definition = reference?.DefName ?? "",
                Cell = CellOf(reference?.Position),
                Room = OptionalRef(row.Room),
                Network = Optional(row.HasPowerNetId, row.PowerNetId),
                Powered = Optional(row.HasPowered, row.Powered),
                PowerW = power,
                LitNow = Optional(row.HasLitNow, row.LitNow),
                GrowthCells = CellsOf(row.GrowthCells),
            });
        }
        var growers = new List<PlantGrower>();
        foreach (var row in v.Growers)
        {
            var reference = buildings.Entity(row.Building);
            var power = DefPowerW(catalog, reference?.DefName ?? "");
            var (fertility, sowTag) = GrowerRows(catalog, reference?.DefName ?? "");
            growers.Add(new PlantGrower
            {
                Id = reference?.Id ?? "",
                Definition = reference?.DefName ?? "",
                Cell = CellOf(reference?.Position),
                Room = OptionalRef(row.Room),
                Network = Optional(row.HasPowerNetId, row.PowerNetId),
                Powered = Optional(row.HasPowered, row.Powered),
                PowerW = power,
                Fertility = fertility,
                SowTag = sowTag,
                Crop = Optional(row.HasCropDefName, row.CropDefName),
                CanSow = Optional(row.HasCanSow, row.CanSow),
                Cells = CellsOf(row.PlantCells),
            });
        }
        var rooms = new List<GrowRoom>();
        foreach (var row in v.Rooms)
        {
            rooms.Add(new GrowRoom
            {
                Id = row.Room?.Id ?? "",
                TemperatureC = Optional(row.HasTemperatureC, row.TemperatureC),
                Cells = Count(row.HasCellCount, row.CellCount),
                OpenRoof = Count(row.HasOpenRoofCount, row.OpenRoofCount),
                Lit = Count(row.HasLitCells, row.LitCells),
                Proper = Optional(row.HasProperRoom, row.ProperRoom),
                Outdoors = Optional(row.HasPsychologicallyOutdoors, row.PsychologicallyOutdoors),
            });
        }
        var networks = new List<PowerHeadroom>();
        foreach (var row in v.Networks)
        {
            networks.Add(new PowerHeadroom
            {
                Id = row.Id,
                GenerationW = Optional(row.HasGenerationW, row.GenerationW),
                SolarW = Optional(row.HasSolarW, row.SolarW),
                WindW = Optional(row.HasWindW, row.WindW),
                ConsumptionW = Optional(row.HasConsumptionW, row.ConsumptionW),
                StoredWattDays = Optional(row.HasStoredWattDays, row.StoredWattDays),
                CapacityWattDays = Optional(row.HasCapacityWattDays, row.CapacityWattDays),
                ActiveSource = Optional(row.HasHasActiveSource, row.HasActiveSource),
            });
        }
        return e with { Lights = [.. lights], Growers = [.. growers], Rooms = [.. rooms], Networks = [.. networks] };
    }

    /// <summary>The base draw of def's power comp row (CompProperties_Power), unknown for a def without one.</summary>
    internal static Fact<double> DefPowerW(DefinitionCatalog? catalog, string def)
    {
        var row = RequireCatalog(catalog).ThingRow(def);
        return DefinitionCatalog.PowerDraw(row) is { } watts ? FiniteFact(watts) : Unknown<double>();
    }

    /// <summary>A plant grower def's own numbers: its fixed fertility (when it sets one) and the sow tag of its building properties, unknown for a def that names none.</summary>
    internal static (Fact<double> Fertility, Fact<string> SowTag) GrowerRows(DefinitionCatalog? catalog, string def)
    {
        var row = RequireCatalog(catalog).FindThing(def);
        var fertility = Unknown<double>();
        var sowTag = Unknown<string>();
        if (row is not null && row.fertility >= 0)
        {
            fertility = FiniteFact(row.fertility);
        }
        var tag = row?.building?.sowTag ?? "";
        if (tag != "")
        {
            sowTag = Known(tag);
        }
        return (fertility, sowTag);
    }

    internal static Fact<int> Count(bool has, uint value) => has ? Known((int)value) : Unknown<int>();

    /// <summary>
    /// Go's string-backed type conversion (<c>policy.WorkType(name)</c>): the wire string as its enum member. A
    /// name the enum does not declare is a producer the brain was not built for, a contract break.
    /// </summary>
    internal static TEnum WireParse<TEnum>(string? name)
        where TEnum : struct, Enum =>
        Wire.TryParse<TEnum>(name ?? "", out var value)
            ? value
            : throw new ObservationContractException($"{typeof(TEnum).Name} {name} is not a defined member");
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
