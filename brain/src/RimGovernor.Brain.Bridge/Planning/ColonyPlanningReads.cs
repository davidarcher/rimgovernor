using Identity = RimGovernor.Brain.GameAccess.ObservationIdentity;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using Cell = RimGovernor.Brain.Domain.Cell;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using ControlledEnvironment = RimGovernor.Brain.GameFacts.Observations.ControlledEnvironment;
using PowerHeadroom = RimGovernor.Brain.GameFacts.Observations.PowerHeadroom;
using PenGrazing = RimGovernor.Brain.GameFacts.Observations.PenGrazing;
using System.Diagnostics.CodeAnalysis;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Bridge;

public static partial class ColonyPlanningReads
{
    /// <summary>The total same-frame room census; absence is a broken native observation.</summary>
    internal static RoomObservation RoomsOf(Tables tables, DefinitionCatalog catalog)
    {
        var rooms = tables.Rooms ?? throw new BridgeContractException("missing total rooms census");
        return new RoomObservation { Rooms = rooms.Rooms, Cells = tables.RoomCells, Dining = catalog.DiningFurniture(), Shapes = catalog.PieceShapes(), EligibleBeds = [] };
    }

    /// <summary>The total seasonal climate shared by direct and routine colony reads.</summary>
    internal static EquatableArray<double> OutdoorCurveOf(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v)
    {
        var gear = v.Gear ?? throw new BridgeContractException("missing total gear census");
        var climate = gear.Climate ?? throw new BridgeContractException("missing total gear climate");
        var temperatures = climate.Temperatures;
        if (temperatures.Count != 12 || temperatures.Any(t => !double.IsFinite(t)))
        {
            throw new BridgeContractException("gear climate must contain twelve finite seasonal temperatures");
        }
        return EquatableArray.From(temperatures);
    }

    /// <summary>The load id of the allowed area labelled <paramref name="label"/>, "" when the map has none.</summary>
    internal static string AllowedAreaID(RimGovernor.Brain.GameFacts.Observations.Policies policies, string label)
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
    internal static IReadOnlyList<FoodEaterDiet> FoodEaters(IEnumerable<RimGovernor.Brain.GameFacts.Observations.FoodEater> eaters, Pawns pawns, AnimalRaceCatalog races, DefinitionCatalog? catalog)
    {
        var output = new List<FoodEaterDiet>();
        foreach (var e in eaters)
        {
            var traits = new List<PawnTrait>(e.Traits.Count);
            foreach (var t in e.Traits)
            {
                traits.Add(WorkReads.ResolveTrait(catalog, t));
            }
            var eater = new FoodEaterDiet { Observed = e, Traits = traits };
            if (e.Kind == RimGovernor.Brain.GameFacts.Observations.FoodEaterKind.Animal)
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
    public static ColonyReading Compose(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Identity expected, Tables tables, DefinitionCatalog? catalog)
    {
        if (v.Upkeep is not { Lighting: not null, Flooring: not null, Routes: not null })
            throw new BridgeContractException("missing total upkeep census");
        var climate = v.FoodClimate ?? throw new BridgeContractException("missing total food climate census");
        var environment = v.ControlledEnvironment ?? throw new BridgeContractException("missing total controlled environment census");
        var development = v.Development?.Outcome ?? throw new BridgeContractException("missing total development census");
        if (!climate.Valid()) throw new BridgeContractException("invalid total food climate census");
        if (tables is null || tables.PawnCensus is null) throw new BridgeContractException("missing total pawn census");
        _ = OutdoorCurveOf(v);
        expected.Validate();
        var identity = new ObservationIdentity { Context = v.Context };
        if (expected.NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase generation && !identity.NativeGeneration.IsAt(generation.Value))
            throw new ObservationChangedException();
        var buildings = tables.Buildings;
        // The race rows are static for a load and come from the catalog; an unbuildable race table fails the reading.
        var races = RequireCatalog(catalog).AnimalRaces();
        var productionBenches = ColonyProductionReads.ColonyProductionBenches(v, tables.Pawns, catalog);
        var foodSupply = FoodForecastRules.FromCensus(v.FoodSupply);
        var combinedFoodSupply = FoodForecastRules.FromCensus(v.Forecast.CombinedFoodSupply);
        var defs = RequireCatalog(catalog);
        var definitions = new DefinitionFacts { Catalog = defs, Finished = DefinitionsReads.FinishedSet(tables.FinishedResearch), Crops = DefinitionsReads.CropRows(v.Crops) };
        var observedRooms = RoomsOf(tables, defs);
        var f = new ReviewFacts
        {
            EssentialInputs = new() { Benches = productionBenches },
            Definitions = definitions.AppendDefinitions([], DefinitionsReads.StarterDefinitions.Concat(defs.SowableCrops()).Concat(defs.BuildableFloors()).Concat(defs.GeneratorDefinitions())),
            FoodSupply = foodSupply,
            CombinedFoodSupply = combinedFoodSupply,
            Books = defs.Books(),
            Foods = defs.Foods(),
            FoodEaters = FoodEaters(v.Policies.FoodEaters, tables.Pawns, races, defs),
            Tick = identity.Tick,
            MapBounds = new Bounds(checked((int)v.MapSize.Width), checked((int)v.MapSize.Height)),
            CurrentConstruction = ConstructionReads.ConstructionFromTables(tables),
        };
        f = f with { FoodChannels = FoodChannelsReads.ColonyFoodChannels(v.FoodChannels, tables.Pawns, races) };
        var conditions = ColonyFramesReads.ColonyConditions(v, catalog);
        if (catalog is not null)
        {
            f = f with
            {
                RoofSupport = Verse.RoofCollapseUtility.RoofMaxSupportDistance,
                Packable = catalog.Packable(),
                LightTerrains = new LightTerrains(catalog.LightTerrains().ToDictionary(terrain => terrain, _ => true)),
                Impressiveness = catalog.ImpressivenessLevels(),
                PowerSources = catalog.PowerSources(),
            };
            var generatorRows = new Dictionary<string, GeneratorRow>(StringComparer.Ordinal);
            foreach (var name in catalog.GeneratorDefinitions())
            {
                generatorRows[name] = catalog.GeneratorRow(name, development.MaintenanceCostFactor);
            }
            f = f with
            {
                GeneratorRows = generatorRows,
                ComponentSteel = catalog.ProductInputCost(TradePolicyRules.ComponentResource.Value, SiteRules.SteelResource.Value),
            };
            if (catalog.FindThing(PowerRules.BatteryDefinition) is not null)
            {
                f = f with { PowerBattery = catalog.PowerBattery(PowerRules.BatteryDefinition) };
            }
        }
        // The biome's permanent darkness is its map conditions': a frame without a biome row or a catalog fails, never counts as lit.
        var outdoorsDark = ColonyFramesReads.ColonyOutdoorsDark(v, catalog);
        f = f with
        {
                        ShelterArea = AllowedAreaID(v.Policies, ShelterRules.SafeAreaLabel),
            NoDangerArea = AllowedAreaID(v.Policies, ShelterRules.NoDangerAreaLabel),
            AnimalWard = f.AnimalWard with { Area = AllowedAreaID(v.Policies, HerdWardRules.HospitalAreaLabel) },
            IsolationArea = AllowedAreaID(v.Policies, IsolationRoomRules.IsolationAreaLabel),
            Census = v,
        };
        f = f with { PenGrazing = f.FoodChannels.Grazing, Pasture = f.FoodChannels.Pasture };
        {
            var power = buildings.Table.Sorted().Where(row => row.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built && row.Service.Power is not RimGovernor.Brain.GameFacts.Observations.BuildingPower.NoPower).ToList();
            var topology = new PowerTopology
            {
                Blackout = conditions.Any(condition => condition.DisablesPower),
                Eclipse = conditions.Any(condition => condition.Definition == DisasterRules.ConditionEclipse),
                Buildings = power,
            };
            var fuel = power.Where(site => site.Service.Fuel is RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable);
            var conduits = buildings.Table.Sorted().Where(row => row.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built && row.Conduit != RimGovernor.Brain.GameFacts.Observations.ConduitKind.None).Select(row => row.Cell).ToList();
            var unsafeConduits = buildings.Table.Sorted().Where(row => row.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built && row.Conduit == RimGovernor.Brain.GameFacts.Observations.ConduitKind.Exposed).Select(row => row.Cell).ToList();
            topology = topology with { Conduits = conduits, UnsafeConduits = unsafeConduits, Networks = development.Networks, Geysers = development.Geysers };
            f = f with { EssentialInputs = f.EssentialInputs with { Power = topology } };
            var coverage = PowerRules.PowerCoverageOf(power);
            f = f with
            {
                Fuel = EquatableArray.From(fuel),
                PowerRequired = coverage.Required,
                PowerHeadroom = coverage.HeadroomW,
                DisabledConsumers = coverage.Disabled,
                PowerWeatherSafe = unsafeConduits.Count == 0 && PowerRules.PowerWeatherSafe(power),
            };
            f = f with { ShortCircuit = development.ShortCircuit is RimGovernor.Brain.GameFacts.Observations.ObservedShortCircuit.Seen shorted ? new DisasterShortCircuit.Seen(new Tick(shorted.Tick)) : new DisasterShortCircuit.NoneSeen() };
        }
        f = f with
        {
            FullRotRateC = RequireCatalog(catalog).Derived.FullRotRateC,
            RotRates = RequireCatalog(catalog).Derived.RotRates,
        };
        AcquisitionReads.ApplyAcquisition(v, tables, catalog, ref f);
        f = ColonyProductionReads.ColonyProduction(v, f);
        f = f with
        {
            TradeMealIngredients = TradePolicyRules.TradeMealIngredients(productionBenches),
        };
        f = ColonyFramesReads.ColonyDisaster(v, f, buildings, conditions);
        var comfort = ColonyFramesReads.ColonyComfort(v, catalog);
        f = f with { Comfort = comfort, BasicComfort = comfort, HomeCoverage = v.Upkeep.HomeCoverage };
        f = f with { Sleeping = SleepingUpkeepReads.ColonySleeping(v, buildings, tables.Pawns, catalog) };
        f = f with
        {
            AnimalUpkeep = f.AnimalUpkeep with
            {
                AnimalRaces = races,
                Animals = AnimalUpkeepReads.MergeHerdFoodFacts(AnimalUpkeepReads.ColonyAnimals(v, tables.Pawns, races), f.FoodChannels),
                WildAnimals = AnimalUpkeepReads.ColonyWildAnimals(v, tables.Pawns, races),
                Roamers = AnimalUpkeepReads.ColonyRoamers(v),
            },
        };
        f = f with { Waste = ColonyFramesReads.ColonyWaste(v, tables), Blight = ColonyFramesReads.BlightCensus(v, tables), Biotech = v.Biotech };
        f = f with { Upkeep = ColonyUpkeepReads.ColonyUpkeep(v, tables, catalog) };
        f = f with { OutdoorsDark = outdoorsDark };
        f = f with { MedicalReserve = ColonyFramesReads.ColonyMedicalReserve(v, tables, catalog) };
        f = f with { JoinerLetters = v.JoinerLetters };
        f = f with { FoodStorageUpkeep = FoodStorageUpkeepRules.FoodStorageStocks(foodSupply, RequireCatalog(catalog).Derived.FullRotRateC) };
        if (foodSupply.Consumers.Count == 0)
        {
            f = f with { FoodDays = new FoodRunway.NoEaters() };
        }
        else
        {
            var selected = new List<PawnId>(foodSupply.Consumers.Count);
            foreach (var consumer in foodSupply.Consumers)
            {
                selected.Add(consumer.Id);
            }
            // The forecast over the colonists who eat; one the policy refuses fails the reading.
            try
            {
                var forecastFood = FoodForecastRules.ForecastFood(combinedFoodSupply, selected);
                f = f with { FoodDays = forecastFood.RunwayDays };
            }
            catch (DomainException e)
            {
                throw new ObservationContractException("the food forecast refused the colony's food supply: " + e.Message);
            }
        }
        f = f with { AnimalUpkeep = f.AnimalUpkeep with { Food = combinedFoodSupply } };
        var lootRows = new List<LootItem>(v.EventLoot.Count);
        var spawnForbidden = RequireCatalog(catalog).SpawnForbiddenProducts();
        foreach (var row in v.EventLoot)
        {
            var head = tables.Entity(row.Item) ?? throw new BridgeContractException($"loot stack {row.Item.Id} has no entity head in the tables");
            lootRows.Add(new LootItem
            {
                Observed = row,
                Entity = head,
                SpawnForbidden = spawnForbidden.Contains(head.DefName),
            });
        }
        f = f with { EventLoot = EquatableArray.From(lootRows) };
        observedRooms = observedRooms with { EligibleBeds = [.. f.Sleeping.Beds.Where(b => b.Humanlike && !b.Medical && !b.Prisoners).Select(b => b.Id)] };
        f = f with { Upkeep = f.Upkeep with { Rooms = observedRooms }, Sleeping = f.Sleeping with { Rooms = SleepingUpkeepReads.UpkeepRooms((tables.Rooms ?? throw new ObservationContractException("required room census")), f.Sleeping.Beds) } };
        var reading = new ColonyReading { Tables = tables, Identity = identity, Facts = f };
        reading = ColonyPawnReads.WithPawnCensus(reading, v, tables, defs);
        reading = ColonyZonesReads.ApplyZones(reading, tables.Zones);
        // Apparel bill options price the actual growing fields, on routine and direct colony reads alike.
        var gearDefinitions = new GearDefinitions { Catalog = RequireCatalog(catalog), Finished = tables.FinishedResearch.ToHashSet(StringComparer.Ordinal) };
        var gear = ColonyGearReads.StampGearCreepjoiners(
            ColonyGearReads.GearFacts(v, tables, gearDefinitions, reading.Facts.FoodFields),
            tables.PawnCensus, gearDefinitions.Catalog.CreepJoinerDownsides());
        return reading with { Facts = reading.Facts with { Gear = gear, Garments = EquatableArray.From(ColonyGearReads.ClothingGarments(gearDefinitions, gear)) } };
    }

    internal static DefinitionCatalog RequireCatalog(DefinitionCatalog? catalog) =>
        catalog ?? throw new BridgeContractException("no definition catalog is held for this load");


}

/// <summary>Null-tolerant row lookups over the bridge tables, which take a non-null reference.</summary>
internal static class TableLookups
{
    public static bool TryGetRow(this Buildings buildings, RimGovernor.Brain.GameFacts.Common.Ref? reference, [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Observations.BuildingState? row)
    {
        row = null;
        return reference is not null && buildings.TryRow(reference, out row);
    }

    public static bool TryGetRow(this Things things, RimGovernor.Brain.GameFacts.Common.Ref? reference, [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Observations.Thing? row)
    {
        row = null;
        return reference is not null && things.TryRow(reference, out row);
    }

    public static bool TryGetRow(this Pawns pawns, RimGovernor.Brain.GameFacts.Common.Ref? reference, [NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Observations.PawnState? row)
    {
        row = null;
        return reference is not null && pawns.TryRow(reference, out row);
    }
}
