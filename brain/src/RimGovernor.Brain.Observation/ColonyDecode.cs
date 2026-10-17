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

    internal static Fact<long> CountFact(bool has, uint value) => has ? Known((long)value) : Unknown<long>();

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

    internal static Fact<bool> NativePresence(string? value, IEnumerable<O.ReadIssue> issues, string field) =>
        PlanningWindowRules.CellPresence(value, issues, field, false);

    internal static Cell CellOf(C.Cell? cell) => new(cell?.X ?? 0, cell?.Z ?? 0);

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
        if (reply.Outcome?.Unavailable is not null)
        {
            throw new BridgeUnavailableException();
        }
        if (reply.Outcome?.Failure is not null)
        {
            throw new BridgeRefusedException();
        }
        var v = reply.Outcome?.Observed ?? throw new ObservationContractException("missing colony facts");
        var identity = DecodeReads.ContextIdentity(v.Context);
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
            Bounds = new Bounds((int)v.MapSize.Width, (int)v.MapSize.Height),
            PlayerTechLevel = v.PlayerTechLevel,
            Threat = ColonyThreat.ProjectColonyThreat(v),
        };
        // The race rows are static for a load and come from the catalog; an unbuildable race table fails the reading.
        var races = RequireCatalog(catalog).AnimalRaces();
        r = r with
        {
            FoodChannels = FoodChannelsReads.ColonyFoodChannels(v.FoodChannels, tables.Pawns, races),
            DeliveryLedger = DeliveryLedgerReads.ColonyDeliveryLedger(v.DeliveryLedger),
            DeepResources = DeepResourcesReads.ColonyDeepResources(v.DeepResources, catalog),
            Policies = PoliciesReads.ColonyPolicies(v.Policies),
        };
        if (r.Policies.TryGetValue(out var policies))
        {
            policies = policies with { Books = catalog!.Books(), Foods = catalog.Foods() };
            policies = policies with { BiomeDiseases = catalog.BiomeDiseases(v.Biome) };
            r = r with { Policies = Known(policies) };
        }
        var (conditions, conditionsKnown) = ColonyFramesReads.ColonyConditions(v, catalog);
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
                ComponentSteel = catalog.ProductInputCost(TradePolicyRules.ComponentResource.Value, SiteRules.SteelResource.Value),
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
            Biotech = BiotechColonyReads.ColonyBiotech(v.Biotech),
            Anomaly = AnomalyColonyReads.ColonyAnomaly(v.Anomaly),
            RoyaltyColony = RoyaltyColonyProjectionReads.ColonyRoyalty(v.Royalty),
        };
        var f = new RoundsFacts
        {
            Tick = identity.Tick,
            WorkResources = v.WorkResources is { } workResources ? Known(workResources) : default,
            Colonists = Known((long)v.ColonistCount),
            BedCapacity = Known((long)v.BedCapacity),
            IndoorCapacity = Known((long)v.IndoorSleepingCapacity),
            SleepingMin = Optional(v.SleepingTemperatureMinC is not null, v.SleepingTemperatureMinC.GetValueOrDefault()),
            SleepingMax = Optional(v.SleepingTemperatureMaxC is not null, v.SleepingTemperatureMaxC.GetValueOrDefault()),
            OutdoorTemperature = Known(v.OutdoorTemperatureC),
        };
        f = f with
        {
            MapBounds = Known(r.Bounds),
            ShelterArea = PoliciesReads.ShelterArea(r.Policies),
            NoDangerArea = PoliciesReads.AllowedAreaID(r.Policies, ShelterRules.NoDangerAreaLabel),
            AnimalWard = f.AnimalWard with { Area = PoliciesReads.AllowedAreaID(r.Policies, HospitalAreaLabel) },
            IsolationArea = PoliciesReads.AllowedAreaID(r.Policies, PolicyConstants.IsolationAreaLabel),
            RaidPoints = r.Threat.RaidPoints,
            Monolith = AnomalyColonyReads.MonolithFacts(r.Anomaly),
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
        if (v.Development?.Outcome is { } development && Resolved(buildings, development.Power, row => row.Building))
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
            var defs = RequireCatalog(catalog);
            foreach (var row in development.Power)
            {
                buildings.TryGetRow(row.Building, out var b);
                var s = b!.Service;
                var def = b.Building.DefName;
                var reference = b.Building;
                // Every development power row is a power trader or a battery, so the service row carries its
                // connection, power state and output.
                var site = new PowerSite
                {
                    Id = reference.Id,
                    Definition = reference.DefName,
                    Cell = CellOf(reference.Position),
                    Occupied = CellsRules.RectCells(b.Occupied),
                    BaseW = defs.PowerBaseW(def, tables.FinishedResearch),
                    OutputW = s.PowerOutputW.GetValueOrDefault(),
                    Powered = s.PowerOn.GetValueOrDefault(),
                    Connected = s.Connected.GetValueOrDefault(),
                    Network = s.PowerNetId ?? "",
                    Forbidden = b.Settings.Forbidden,
                    SwitchedOn = s.SwitchedOn,
                    Fuel = Optional(s.Fuel is not null, s.Fuel.GetValueOrDefault()),
                    TargetFuel = Optional(s.TargetFuel is not null, s.TargetFuel.GetValueOrDefault()),
                    OutOfFuel = Optional(s.OutOfFuel is not null, s.OutOfFuel.GetValueOrDefault()),
                    BrokenDown = s.BrokenDown,
                    FuelDefinitions = defs.RefuelFuels(def),
                    Capacity = defs.BatteryCapacityWd(def),
                    // Whether rain shorts the building is its def's, not the frame's.
                    RainVulnerable = defs.RainVulnerable(def),
                    Roofed = row.Roofed,
                    TurretDps = Optional(row.TurretDps is not null, row.TurretDps.GetValueOrDefault()),
                };
                power.Add(site);
                var consumer = FuelReads.FuelConsumerOf(catalog, site.Id, site.Definition, site, out var refuelable);
                if (refuelable)
                {
                    fuel.Add(consumer);
                }
                geometryKnown = geometryKnown && reference.Position is not null && site.Occupied.Count > 0;
                sites.Add(site);
                if (site.TurretDps.IsKnown)
                {
                    turrets.Add(new DefenseTurretFacts { Id = site.Id, Cell = site.Cell, Powered = Known(site.Powered), Dps = site.TurretDps });
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
                    GenerationW = row.GenerationW,
                    ConsumptionW = row.ConsumptionW,
                    StoredWD = row.StoredWattDays,
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
                geysers.Add(new PowerGeyser { Id = reference.Id ?? "", Cell = CellOf(reference.Position), Occupied = row.Occupied, Cells = geyserCells });
            }
            topology = topology with { Conduits = conduits, UnsafeConduits = unsafeConduits, Networks = networks, Geysers = geysers };
            if (geometryKnown)
            {
                r = r with { PowerPlanning = Known(topology) };
            }
            r = r with { DefenseTurrets = Known(EquatableArray.From(turrets)) };
            f = f with { Fuel = Known(EquatableArray.From(fuel)) };
            var (required, headroom, disabled) = PowerRules.PowerCoverage(power);
            f = f with
            {
                PowerRequired = required,
                PowerHeadroom = headroom,
                DisabledConsumers = disabled,
                PowerWeatherSafe = Known(unsafeConduits.Count == 0 && PowerRules.PowerWeatherSafe(power)),
            };
            if (development.ShortCircuitTick is not null)
            {
                f = f with { ShortCircuitTick = Known(new Tick((long)development.ShortCircuitTick)) };
            }
        }
        if (v.FoodClimate is { } climate && !HasIssue(v.Issues, "food_climate"))
        {
            outdoorsDark = ColonyFramesReads.ColonyOutdoorsDark(v, catalog);
            r = r with
            {
                CropClimate = new CropClimate
                {
                    Sowing = Optional(climate.SowingNow is not null, climate.SowingNow.GetValueOrDefault()),
                    DaysRemaining = Optional(climate.GrowingDaysRemaining is not null, climate.GrowingDaysRemaining.GetValueOrDefault()),
                    OutdoorsDark = outdoorsDark,
                    Biome = Known(v.Biome),
                },
            };
            f = f with { Calendar = ColonyProductionReads.ColonyCalendar(climate) };
        }
        var curve = v.Planning?.Outcome?.Observed?.Gear?.OutdoorTemperatureByTwelfthC;
        if (curve is { } curveTemps && curveTemps.Count == 12)
        {
            var temps = new double[curveTemps.Count];
            for (var i = 0; i < curveTemps.Count; i++)
            {
                temps[i] = curveTemps[i];
            }
            r = r with
            {
                ColdMap = Known(PolicyFunctions.ColdMapCurve(temps)),
                HotMap = Known(PolicyFunctions.HotMapCurve(temps)),
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
                    Id = b.Bench?.Id ?? "",
                    Definition = buildings.Entity(b.Bench)?.DefName ?? "",
                    Usable = Optional(b.Usable is not null, b.Usable.GetValueOrDefault()),
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
        f = ColonyFramesReads.ColonyDisaster(v, f, buildings, catalog, conditions, conditionsKnown);
        var comfort = ColonyFramesReads.ColonyComfort(v, catalog);
        f = f with { Comfort = comfort, BasicComfort = comfort, HomeCoverage = FacilityUpkeepReads.ColonyHomeCoverage(v) };
        f = f with { StoneStructures = FacilityUpkeepReads.ColonyStoneStructures(v, buildings, catalog) };
        f = f with { Sleeping = SleepingUpkeepReads.ColonySleeping(v, buildings, catalog) };
        f = f with
        {
            AnimalUpkeep = f.AnimalUpkeep with
            {
                AnimalRaces = races,
                Animals = AnimalUpkeepReads.MergeHerdFoodFacts(AnimalUpkeepReads.ColonyAnimals(v, tables.Pawns, races), r.FoodChannels),
                WildAnimals = AnimalUpkeepReads.ColonyWildAnimals(v, tables.Pawns, races),
            },
        };
        if (r.Policies.TryGetValue(out policies))
        {
            policies = policies with { FoodEaters = PoliciesReads.AnimalFoodEaters(policies.FoodEaters, tables.Pawns, races, catalog) };
            r = r with { Policies = Known(policies) };
        }
        f = f with { Waste = ColonyFramesReads.ColonyWaste(v, tables), Pollution = ColonyFramesReads.ColonyPollution(r.Biotech) };
        f = f with { Upkeep = ColonyUpkeepReads.ColonyUpkeep(v, tables, catalog) };
        if (f.Upkeep.Lighting.IsKnown && !outdoorsDark.IsKnown)
        {
            outdoorsDark = ColonyFramesReads.ColonyOutdoorsDark(v, catalog);
        }
        f = f with { OutdoorsDark = outdoorsDark };
        f = f with { MedicalReserve = ColonyFramesReads.ColonyMedicalReserve(v, tables, catalog) };
        var letters = new List<JoinerLetterOffer>(v.JoinerLetters.Count);
        foreach (var row in v.JoinerLetters)
        {
            letters.Add(new JoinerLetterOffer
            {
                Id = row.LetterId.GetValueOrDefault(),
                Token = row.SnapshotToken ?? "",
                Label = row.AcceptLabel ?? "",
                CanAccept = row.CanAccept.GetValueOrDefault(),
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
                    Usable = Optional(bench.Usable is not null, bench.Usable.GetValueOrDefault()),
                    Room = OptionalRef(bench.Room),
                    AutoRefuel = Optional(bench.AutoRefuel is not null, bench.AutoRefuel.GetValueOrDefault()),
                });
            }
            r = r with { CookingBenches = Known(EquatableArray.From(benches)) };
        }
        // A food supply whose stock the things table misses stays unknown until a later frame.
        if (v.FoodSupply?.Outcome is { } food)
        {
            if (FoodSupplyReads.DecodeFoodSupply(food, tables.Things, catalog) is { } supply)
            {
                r = r with { FoodSupply = Known(supply) };
                f = f with { FoodStorageUpkeep = PolicyFunctions.FoodStorageStocks(supply, RequireCatalog(catalog).Derived.FullRotRateC) };
            }
        }
        if (v.Forecast?.Outcome is { } forecast)
        {
            var combined = FoodSupplyReads.DecodeFoodSupply(forecast.CombinedFoodSupply, tables.Things, catalog);
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
        r = r with { Workers = Known((int)v.WorkerCount) };
        if (!HasIssue(v.Issues, "resources"))
        {
            f = f with { Wood = Known(0L) };
            var rows = new List<Amount>(v.Resources.Count);
            var complete = true;
            var stock = new Dictionary<Resource, long>();
            foreach (var q in v.Resources)
            {
                if ((q.DefName ?? "") == "WoodLog")
                {
                    f = f with { Wood = Optional(q.Units is not null, q.Units.GetValueOrDefault()) };
                }
                complete = complete && q.Units is not null;
                rows.Add(new Amount(new Resource((q.DefName ?? "")), q.Units.GetValueOrDefault()));
                if (q.Units is not null)
                {
                    var key = new Resource((q.DefName ?? ""));
                    stock[key] = stock.GetValueOrDefault(key) + q.Units.GetValueOrDefault();
                }
            }
            if (complete)
            {
                f = f with { Resources = Known(EquatableArray.From(rows)) };
            }
            r = r with { Resources = Known<IReadOnlyDictionary<Resource, long>>(stock) };
        }
        if (v.EventLoot?.Outcome is { } loot && Headed(tables, loot.Items, row => row.Item))
        {
            var rows = new List<LootItem>(loot.Items.Count);
            var spawnForbidden = RequireCatalog(catalog).SpawnForbiddenProducts();
            foreach (var row in loot.Items)
            {
                var head = tables.Entity(row.Item);
                var item = new LootItem
                {
                    Supply = new StartingSupply { Thing = row.Item?.Id ?? "", Definition = head?.DefName ?? "", Cell = CellOf(head?.Position) },
                    Forbidden = row.Forbidden.GetValueOrDefault(),
                    SafeToHaul = row.SafeToHaul.GetValueOrDefault(),
                    SafetyKnown = row.SafeToHaul is not null,
                    Count = row.Count.GetValueOrDefault(),
                    SpawnForbidden = spawnForbidden.Contains(head?.DefName ?? ""),
                    AcceptedBy = EquatableArray.From(row.AcceptedBy.Select(r => r.Id ?? "")),
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
        if (v.Planning?.Outcome?.Observed is { } planningObserved && planningObserved.Environment is { } planningEnvironment
            && !HasIssue(planningObserved.Issues, "environment")
            && Headed(buildings, planningEnvironment.Lights, l => l.Building) && Headed(buildings, planningEnvironment.Growers, g => g.Building))
        {
            var environment = ColonyEnvironment(planningEnvironment, v.OutdoorTemperatureC, buildings, catalog);
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
    internal static ControlledEnvironment ColonyEnvironment(O.ControlledEnvironment v, double outdoor, Buildings buildings, DefinitionCatalog? catalog)
    {
        var e = new ControlledEnvironment
        {
            OutdoorTemperatureC = Known(outdoor),
        };
        var lights = new List<GrowLight>();
        foreach (var row in v.Lights)
        {
            var reference = buildings.Entity(row.Building);
            lights.Add(new GrowLight
            {
                Id = reference?.Id ?? "",
                Room = OptionalRef(row.Room),
                Network = Optional(row.PowerNetId is not null, (row.PowerNetId ?? "")),
                LitNow = Optional(row.LitNow is not null, row.LitNow.GetValueOrDefault()),
                GrowthCells = CellsOf(row.GrowthCells),
            });
        }
        var growers = new List<PlantGrower>();
        foreach (var row in v.Growers)
        {
            var reference = buildings.Entity(row.Building);
            var (fertility, sowTag) = GrowerRows(catalog, reference?.DefName ?? "");
            growers.Add(new PlantGrower
            {
                Id = reference?.Id ?? "",
                Fertility = fertility,
                SowTag = sowTag,
                Crop = Optional(row.CropDefName is not null, (row.CropDefName ?? "")),
                CanSow = Optional(row.CanSow is not null, row.CanSow.GetValueOrDefault()),
            });
        }
        var rooms = new List<GrowRoom>();
        foreach (var row in v.Rooms)
        {
            rooms.Add(new GrowRoom
            {
                Id = row.Room?.Id ?? "",
                GridRoom = Optional(row.GridRoom is not null, row.GridRoom.GetValueOrDefault()),
                TemperatureC = Optional(row.TemperatureC is not null, row.TemperatureC.GetValueOrDefault()),
            });
        }
        var networks = new List<PowerHeadroom>();
        foreach (var row in v.Networks)
        {
            networks.Add(new PowerHeadroom
            {
                Id = row.Id ?? "",
                GenerationW = Optional(row.GenerationW is not null, row.GenerationW.GetValueOrDefault()),
                SolarW = Optional(row.SolarW is not null, row.SolarW.GetValueOrDefault()),
                WindW = Optional(row.WindW is not null, row.WindW.GetValueOrDefault()),
                ConsumptionW = Optional(row.ConsumptionW is not null, row.ConsumptionW.GetValueOrDefault()),
                CapacityWattDays = Optional(row.CapacityWattDays is not null, row.CapacityWattDays.GetValueOrDefault()),
                ActiveSource = Optional(row.HasActiveSource is not null, row.HasActiveSource.GetValueOrDefault()),
            });
        }
        return e with { Lights = [.. lights], Growers = [.. growers], Rooms = [.. rooms], Networks = [.. networks] };
    }

    /// <summary>A plant grower def's own numbers: its fixed fertility (when it sets one) and the sow tag of its building properties, unknown for a def that names none.</summary>
    internal static (Fact<double> Fertility, Fact<string> SowTag) GrowerRows(DefinitionCatalog? catalog, string def)
    {
        var row = RequireCatalog(catalog).FindThing(def);
        var fertility = Unknown<double>();
        var sowTag = Unknown<string>();
        if (row is not null && row.fertility >= 0)
        {
            fertility = PlanningViewReads.FiniteFact(row.fertility);
        }
        var tag = row?.building?.sowTag ?? "";
        if (tag != "")
        {
            sowTag = Known(tag);
        }
        return (fertility, sowTag);
    }

    /// <summary>
    /// Go's string-backed type conversion (<c>policy.WorkType(name)</c>): the wire string as its enum member. A
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
