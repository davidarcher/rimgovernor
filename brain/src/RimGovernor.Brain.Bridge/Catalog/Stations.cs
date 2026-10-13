using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_stations.go and catalog_station_flows.go: the catalog of buildable stations. For every building that adds to
// a native room role, has a capacity or has environment needs: its shape (the same as PieceShapes), access, role
// contributions, capacity, needs and content. Shapes, membership, capacity and needs come from the defs and the stat parts
// and constants of the game; only the role weights, which are literals in RoomRoleWorker code, come from policy.

public sealed partial class DefinitionCatalog
{
    private const string StatWorkTableSpeed = "WorkTableWorkSpeedFactor";
    private const string StatBabyPlayGain = "BabyPlayGainFactor";
    private const string LifeStageHumanlikeChild = "HumanlikeChild";

    // The fillPercent above which ThingDef.Fillage is Full.
    private const double FullFillPercent = 0.99;

    // The Production designation category the kitchen role worker tests buildings against.
    private const string DesignationProduction = "Production";

    /// <summary>The catalog of buildable stations, built once.</summary>
    public P.StationCatalog StationCatalog() => _stations.Value;

    private P.StationCatalog BuildStationCatalog()
    {
        var shapes = PieceShapes();
        var recipes = RecipesByStation();
        var rec = RecRoomDefs();
        var child = DefRow<LifeStageDef>(LifeStageHumanlikeChild);
        var stations = new Dictionary<string, P.Station>(StringComparer.Ordinal);
        foreach (var (name, row) in _thingDefs)
        {
            if (!shapes.Defs.TryGetValue(name, out var shape) || row.category != ThingCategory.Building)
            {
                continue;
            }

            var recipesHere = recipes.GetValueOrDefault(name) ?? [];
            var station = new P.Station
            {
                Def = name,
                Shape = shape,
                Access = row.building is { canPlaceOverWall: true } or { isPlaceOverableWall: true } || row.placeWorkers.Seq().Contains(typeof(Placeworker_AttachedToWall))
                    ? P.StationAccess.Wall
                    : P.StationAccess.Floor,
                Content = row.modContentPack?.PackageId ?? string.Empty,
                Minifiable = row.minifiedDef is not null,
                FillsCell = row.fillPercent > FullFillPercent,
                Contributions = Contributions(row, recipesHere, rec, child),
                EmptyCountsForBarracks = row.building?.bed_emptyCountsForBarracks ?? false,
                Capacity = CapacityOf(row, recipesHere),
                Needs = Needs(row),
                Cooling = Cooling(row),
                Heat = row.GetCompProperties<CompProperties_HeatPusher>() is { } push
                    ? new P.HeatSource { HeatPerSecond = push.heatPerSecond, MinC = push.heatPushMinTemperature, MaxC = push.heatPushMaxTemperature }
                    : new P.HeatSource(),
            };
            if (PowerDraw(row) is { } watts)
            {
                station = station with { BaseW = 0 - watts };
            }

            if (station.Access == P.StationAccess.Floor && station.Contributions.Count == 0 && station.Capacity.Kind == P.CapacityKind.None && station.Needs.Count == 0 && !station.Cooling.Cools())
            {
                continue;
            }

            stations[name] = station;
        }

        var built = new P.StationCatalog { Stations = stations, Filth = FilthModel() };
        built = built with { Climate = ClimateModel(built), Light = LightModel(), Power = PowerModel(), Comfort = ComfortModel(shapes.Furniture), Mood = MoodModel(), Joy = JoyModel(), Haul = HaulModel() };
        return built;
    }

    // The recipes each building hosts: those it lists and those that list it as a user (ThingDef.AllRecipes).
    private Dictionary<string, List<RecipeDef>> RecipesByStation()
    {
        var hosted = new Dictionary<string, List<RecipeDef>>(StringComparer.Ordinal);
        void Add(string station, RecipeDef recipe)
        {
            if (!hosted.TryGetValue(station, out var list))
            {
                hosted[station] = list = [];
            }

            if (!list.Contains(recipe))
            {
                list.Add(recipe);
            }
        }

        var rows = Rows<RecipeDef>();
        foreach (var (name, row) in _thingDefs)
        {
            foreach (var recipe in row.recipes.Seq())
            {
                if (rows.TryGetValue(recipe.defName, out var found))
                {
                    Add(name, found);
                }
            }
        }

        foreach (var recipe in rows.Values)
        {
            foreach (var user in recipe.recipeUsers.Seq())
            {
                Add(user.defName, recipe);
            }
        }

        foreach (var list in hosted.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
        }

        return hosted;
    }

    // The defs a joy giver that counts for the rec room lists.
    private HashSet<string> RecRoomDefs() =>
        [.. Rows<JoyGiverDef>().Values.Where(giver => giver.countsForRecRoom).SelectMany(giver => giver.thingDefs.Seq()).Select(def => def.defName)];

    private static bool IsClass(ThingDef row, Type type) => type.IsAssignableFrom(row.thingClass);

    // The game has no isAltar flag on a def (the Go mirror's field was derived): an altar is a building whose ritual
    // focus the worship room is built around. Unverified against the live game; recorded in the commit body.
    private static bool IsAltar(ThingDef row) => row.ritualFocus is not null;

    private static void Score(List<P.RoleScore> into, P.RoomRole role, P.RoleCondition when)
    {
        if (P.PolicyFunctions.TryRoomRoleScore(role, when, out var score))
        {
            into.Add(score);
        }
    }

    // The role scores the building earns, by the membership each RoomRoleWorker tests.
    private List<P.RoleScore> Contributions(ThingDef row, List<RecipeDef> recipes, HashSet<string> rec, LifeStageDef? child)
    {
        var scores = new List<P.RoleScore>();
        if (row.designationCategory?.defName == DesignationProduction && recipes.Any(MakesHumanFood))
        {
            Score(scores, P.RoomRole.Kitchen, P.RoleCondition.Always);
        }

        foreach (var role in new[] { P.RoomRole.Workshop, P.RoomRole.Laboratory })
        {
            if (row.building?.workTableRoomRole?.defName == Wire.Name(role))
            {
                Score(scores, role, P.RoleCondition.Always);
            }
        }

        if (row.surfaceType == SurfaceType.Eat)
        {
            Score(scores, P.RoomRole.DiningRoom, P.RoleCondition.Always);
        }

        if (rec.Contains(row.defName))
        {
            Score(scores, P.RoomRole.RecRoom, P.RoleCondition.Always);
        }

        foreach (var (type, role) in new (Type, P.RoomRole)[] { (typeof(Building_Storage), P.RoomRole.Storeroom), (typeof(Building_Sarcophagus), P.RoomRole.Tomb), (typeof(Building_Throne), P.RoomRole.ThroneRoom) })
        {
            if (IsClass(row, type))
            {
                Score(scores, role, P.RoleCondition.Always);
            }
        }

        BedContributions(scores, row, child);
        if (IsAltar(row))
        {
            Score(scores, P.RoomRole.WorshipRoom, P.RoleCondition.IdeoAltar);
        }

        if (GameRoomRoleDefs.TryGetValue(row.defName, out var furniture) && (furniture == P.FurnitureRole.Desk || furniture == P.FurnitureRole.Board))
        {
            Score(scores, P.RoomRole.Classroom, P.RoleCondition.Always);
        }

        if (StatBaseOf(row, StatBabyPlayGain) > 1)
        {
            Score(scores, P.RoomRole.Playroom, P.RoleCondition.Always);
        }

        if (RoomRoleNamedDefs.TryGetValue(row.defName, out var named))
        {
            Score(scores, named, P.RoleCondition.Always);
        }
        else if (HasComp<CompProperties_DeathrestBindable>(row))
        {
            Score(scores, P.RoomRole.DeathrestChamber, P.RoleCondition.Bindable);
        }

        return [.. scores.OrderBy(s => Wire.Name(s.Role), StringComparer.Ordinal).ThenBy(s => Wire.Name(s.When), StringComparer.Ordinal)];
    }

    // The scores the humanlike and animal bed workers give.
    private static void BedContributions(List<P.RoleScore> scores, ThingDef row, LifeStageDef? child)
    {
        if (!IsBed(row) || row.building is not { } building)
        {
            return;
        }

        if (!building.bed_humanlike)
        {
            Score(scores, P.RoomRole.Barn, P.RoleCondition.Always);
            return;
        }

        if (building.bed_countsForBedroomOrBarracks)
        {
            Score(scores, P.RoomRole.Bedroom, P.RoleCondition.BedFree);
            Score(scores, P.RoomRole.Barracks, P.RoleCondition.BedFree);
        }

        Score(scores, P.RoomRole.Hospital, P.RoleCondition.BedMedical);
        if (child is not null && building.bed_maxBodySize < child.bodySizeFactor)
        {
            Score(scores, P.RoomRole.Nursery, P.RoleCondition.BedFree);
        }

        foreach (var role in new[] { P.RoomRole.PrisonCell, P.RoomRole.PrisonBarracks })
        {
            Score(scores, role, P.RoleCondition.BedPrisoner);
            Score(scores, role, P.RoleCondition.BedPrisonerMedical);
        }
    }

    // The kitchen worker's product test: the recipe makes a nutrition-giving ingestible a human can eat (foodType & OmnivoreHuman).
    private bool MakesHumanFood(RecipeDef recipe)
    {
        foreach (var product in recipe.products.Seq())
        {
            if (product.thingDef is not { } thing || FindThing(thing.defName) is not { ingestible: { } ingestible } row || (ingestible.foodType & FoodTypeFlags.OmnivoreHuman) == 0)
            {
                continue;
            }

            var (nutrition, shown) = ShownStatValue(row.defName, string.Empty, StatNames.Nutrition);
            if (shown && nutrition > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static float StatBaseOf(ThingDef row, string stat) =>
        row.statBases.Seq().LastOrDefault(modifier => modifier.stat?.defName == stat)?.value ?? 0;

    // What one building serves.
    private P.StationCapacity CapacityOf(ThingDef row, List<RecipeDef> recipes)
    {
        if (IsBed(row))
        {
            return new P.StationCapacity { Kind = P.CapacityKind.Sleepers, Slots = row.size.x };
        }

        if (IsClass(row, typeof(Building_Storage)) && row.building?.fixedStorageSettings is not null)
        {
            return new P.StationCapacity { Kind = P.CapacityKind.Items, Slots = row.size.x * row.size.z * row.building.maxItemsInCell };
        }

        if (!IsClass(row, typeof(Building_WorkTable)) || recipes.Count == 0)
        {
            return new P.StationCapacity();
        }

        var work = new List<P.RecipeWorkAmount>();
        foreach (var recipe in recipes)
        {
            if (RecipeWorkAmount(recipe.defName).TryGetValue(out var amount) && amount > 0)
            {
                work.Add(new P.RecipeWorkAmount(recipe.defName, amount));
            }
        }

        return new P.StationCapacity { Kind = P.CapacityKind.Workers, Slots = 1, Recipes = [.. work] };
    }

    // The environment needs the defs state: the room stats and work-table stat parts of the stats the building is subject
    // to, its place workers, and cold for a store that takes perishable things.
    private List<P.EnvNeed> Needs(ThingDef row)
    {
        var needs = new List<P.EnvNeed>();
        var subject = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var modifier in row.statBases.Seq())
        {
            if (modifier.stat is not null)
            {
                subject[modifier.stat.defName] = modifier.value;
            }
        }

        var table = IsClass(row, typeof(Building_WorkTable));
        var order = subject.Keys.ToList();
        if (table && !subject.ContainsKey(StatWorkTableSpeed))
        {
            order.Add(StatWorkTableSpeed);
        }

        order.Sort(StringComparer.Ordinal);
        foreach (var stat in order)
        {
            foreach (var part in DefRow<StatDef>(stat)?.parts.Seq() ?? [])
            {
                switch (part)
                {
                    case StatPart_RoomStat roomStat:
                        var name = GameMembers.RoomStat(roomStat).defName;
                        needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.RoomStat, Stat = name, Input = RootRoomStat(name), Via = stat, Base = subject.GetValueOrDefault(stat) });
                        break;
                    case StatPart_WorkTableTemperature when table:
                        needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.Temperature, Via = stat, Min = StatPart_WorkTableTemperature.MinTemp, Max = StatPart_WorkTableTemperature.MaxTemp, Factor = StatPart_WorkTableTemperature.WorkRateFactor });
                        break;
                    case StatPart_WorkTableOutdoors when table:
                        needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.Indoors, Via = stat, Factor = StatPart_WorkTableOutdoors.WorkRateFactor });
                        break;
                    case StatPart_WorkTableRoomRole when table:
                        if (row.building?.workTableRoomRole?.defName is { Length: > 0 } role && Wire.TryParse<P.RoomRole>(role, out var roomRole))
                        {
                            needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.Role, Via = stat, Role = roomRole, Factor = row.building.workTableNotInRoomRoleFactor });
                        }

                        break;
                }
            }
        }

        if (row.placeWorkers.Seq().Contains(typeof(PlaceWorker_MustBeIndoors)))
        {
            needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.Indoors, Via = "RimWorld.PlaceWorker_MustBeIndoors" });
        }

        if (row.placeWorkers.Seq().Contains(typeof(PlaceWorker_NotUnderRoof)))
        {
            needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.OpenSky, Via = "RimWorld.PlaceWorker_NotUnderRoof" });
        }

        if (StoresPerishables(row))
        {
            needs.Add(new P.EnvNeed { Kind = P.EnvNeedKind.Cold, Via = "fixedStorageSettings" });
        }

        return needs;
    }

    // Follows a room stat's inputStat chain to its end.
    private string RootRoomStat(string name)
    {
        for (var seen = 0; seen < 8; seen++)
        {
            if (DefRow<RoomStatDef>(name)?.inputStat is not { } input)
            {
                return name;
            }

            name = input.defName;
        }

        return name;
    }

    // Whether the storage building's fixed filter takes a def with a rottable comp.
    private bool StoresPerishables(ThingDef row) =>
        IsClass(row, typeof(Building_Storage))
        && row.building?.fixedStorageSettings?.filter is { } filter
        && _thingDefs.Keys.Order(StringComparer.Ordinal).Any(name => RotDays(name) is not null && FilterAccepts(filter, name));

    // The building's cooling: a temperature control whose energyPerSecond is negative (a cooler or air conditioner draws
    // heat out; a heater's is positive), with the power comp's draw.
    private static P.StationCooling Cooling(ThingDef row)
    {
        if (row.GetCompProperties<CompProperties_TempControl>() is not { energyPerSecond: < 0 } control)
        {
            return new P.StationCooling();
        }

        return new P.StationCooling
        {
            HeatPerSecond = -control.energyPerSecond,
            PowerDraw = row.GetCompProperties<CompProperties_Power>() is { } power ? GameMembers.BasePowerConsumption(power) : 0,
        };
    }

    /// <summary>
    /// The item flows of every recipe a station works: the ingredient slots as alternatives and the products with their
    /// counts, as the station graph reads them. A recipe whose ingredient slots the catalog cannot resolve has none (its
    /// flow is unknown, not empty).
    /// </summary>
    public IReadOnlyDictionary<string, P.RecipeFlow> StationRecipeFlows(P.StationCatalog stations)
    {
        var flows = new Dictionary<string, P.RecipeFlow>(StringComparer.Ordinal);
        foreach (var def in stations.Stations.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var work in stations.Stations[def].Capacity.Recipes)
            {
                if (flows.ContainsKey(work.Recipe))
                {
                    continue;
                }

                var row = Recipe(work.Recipe);
                var products = row.products.Seq()
                    .Where(p => p.thingDef is not null && p.count > 0)
                    .Select(p => new P.Amount(new P.Resource(p.thingDef.defName), p.count))
                    .OrderBy(a => a.Resource.Value, StringComparer.Ordinal)
                    .ToList();
                flows[work.Recipe] = new P.RecipeFlow
                {
                    Ingredients = RecipeIngredients(work.Recipe).TryGetValue(out var slots) ? [.. slots.Select(slot => (IReadOnlyList<P.Amount>)slot)] : [],
                    Products = products,
                    Butchery = row.specialProducts.Seq().Contains(SpecialProductType.Butchery),
                    Food = MakesHumanFood(row),
                };
            }
        }

        return flows;
    }

    /// <summary>
    /// The item rows the station graph reads of a stock: each stocked item's largest stack and whether it rots, and
    /// whether each item the recipes take in or put out rots, and each item the pens move (herd products and feed).
    /// An item the catalog has no row for is an error, never a zero.
    /// </summary>
    public (IReadOnlyDictionary<P.Resource, long> StackLimit, IReadOnlyDictionary<P.Resource, bool> Perishable) StationItemFacts(
        IReadOnlyDictionary<P.Resource, long> stock,
        IReadOnlyDictionary<string, P.RecipeFlow> recipes,
        IEnumerable<P.Resource> moved)
    {
        var stackLimit = new Dictionary<P.Resource, long>();
        var perishable = new Dictionary<P.Resource, bool>();
        void Rots(P.Resource resource) => perishable.TryAdd(resource, RotDays(resource.Value) is not null);

        foreach (var (resource, count) in stock)
        {
            if (count > 0)
            {
                stackLimit[resource] = StackLimit(resource.Value);
                Rots(resource);
            }
        }

        foreach (var flow in recipes.Values)
        {
            foreach (var amount in flow.Ingredients.SelectMany(slot => slot).Concat(flow.Products))
            {
                Rots(amount.Resource);
            }
        }

        foreach (var resource in moved)
        {
            Rots(resource);
        }

        return (stackLimit, perishable);
    }
}
