using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The work a unit of nutrition costs the colony at speed 1 (#3825), from the catalog (DefinitionCatalog.FoodWork) over the
/// research finished: the cheapest crop's sowing and harvest per nutrition harvested (<see cref="Crop"/>, "" when the colony can sow
/// no human-edible crop) and the cheapest meal recipe's work per nutrition made with the ingredient
/// nutrition it takes per nutrition made (<see cref="Meal"/>, "" when the colony can cook none: it eats its crop raw).
/// Its hauling (#3849): the crop's harvested thing (<see cref="Harvest"/>, null without a crop) and whether it rots, the meal's
/// product (<see cref="Dish"/>, null without a meal) and the dishes a run makes, the benches that cook it, and the stats a pawn
/// walks and carries at (<see cref="MoveStat"/>, <see cref="CarryStat"/>), which the needs read reads per pawn.
/// </summary>
public sealed record FoodWorkFacts
{
    public string Crop { get; init; } = "";

    public double GrowTicksPerNutrition { get; init; }

    public string Meal { get; init; } = "";

    public double CookTicksPerNutrition { get; init; }

    public double IngredientPerNutrition { get; init; } = 1;

    public CarriedFood? Harvest { get; init; }

    /// <summary>Whether the harvest rots (its def has CompProperties_Rottable), so it is stored cold.</summary>
    public bool HarvestRots { get; init; }

    public CarriedFood? Dish { get; init; }

    public int DishesPerRun { get; init; }

    /// <summary>The ThingDefs that cook <see cref="Meal"/> (RecipeDef.AllRecipeUsers).</summary>
    public EquatableArray<string> Benches { get; init; }

    public string MoveStat { get; init; } = "";

    public string CarryStat { get; init; } = "";
}

/// <summary>What a pawn's bed costs (#3825, V6): whether a colonist bed stands vacant (SleepingRules.VacantColonistBed) and the
/// capability-days of building the primary bed (its work and cost list at the materials' values, LayoutClusterRules.PiecePrice).</summary>
public sealed record PawnBedFacts(bool Vacant, double BuildDays);

/// <summary>
/// The colony's side of a pawn's value (#3825): the work it is short of (<see cref="WorkShortfall"/>), the work a unit of nutrition
/// costs, the break curve, the bed and the days the bed's cost spreads over (tier 2's horizon, as a served room's credit runs).
/// </summary>
public sealed record PawnValueColony
{
    public WorkShortfall Shortfall { get; init; } = WorkShortfall.None;

    public FoodWorkFacts Food { get; init; } = new();

    public MoodBreakModel Breaks { get; init; } = new();

    public PawnBedFacts? Bed { get; init; }

    /// <summary>The planned layout's food walks (<see cref="FoodHaulRules.Legs"/>). A colony with no layout plan yet values no pawn
    /// (<see cref="PawnValueRules.AwaitingLayoutPlan"/>, #3857), so a valued pawn always has its walks.</summary>
    public required FoodHaulLegs Haul { get; init; }

    public double HorizonDays { get; init; }
}

/// <summary>
/// One pawn's side of its value (#3825), every number plain: its speed per work speed stat, the work types it is incapable of, the
/// ticks a day it is free to work, the nutrition it eats a day and its mood (null when it has none: it never breaks).
/// </summary>
public sealed record PawnValueFacts(string Pawn)
{
    public IReadOnlyDictionary<string, double> Speeds { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    public EquatableArray<string> Incapable { get; init; }

    public double FreeTicksPerDay { get; init; }

    public double FoodPerDay { get; init; }

    public PawnMoodRead? Mood { get; init; }
}

/// <summary>The shortfall a pawn's free time takes on: of one work type at one stat, in ticks a day at speed 1.</summary>
public sealed record PawnWorkShare(string WorkType, string Stat, double TicksPerDay);

/// <summary>
/// A pawn's value in capability-days a day (#3825): the work it adds (<see cref="WorkAdded"/>) less its upkeep, the work its food
/// takes (<see cref="FoodUpkeep"/>), its expected break days (<see cref="BreakUpkeep"/>) and its bed (<see cref="BedUpkeep"/>).
/// <see cref="NotModelled"/> names the mechanics it leaves out.
/// </summary>
public sealed record PawnValue(string Pawn, double WorkAdded, double FoodUpkeep, double BreakUpkeep, double BedUpkeep)
{
    public EquatableArray<PawnWorkShare> Work { get; init; }

    public EquatableArray<string> NotModelled { get; init; }

    /// <summary>The part of <see cref="FoodUpkeep"/> that is hauling the food (#3849), in capability-days a day.</summary>
    public double FoodHaul { get; init; }

    /// <summary>Work added less upkeep; negative for a pawn who costs more than it adds.</summary>
    public double PerDay => WorkAdded - FoodUpkeep - BreakUpkeep - BedUpkeep;
}

public static class PawnValueRules
{
    /// <summary>The mechanics the value leaves out, named on every value (V2, V4, V5 of #3825).</summary>
    public static readonly EquatableArray<string> NotModelled =
    [
        "comparative_advantage", // a faster joiner freeing an existing pawn for other work (V2)
        "effects_on_others", // Abrasive and other traits' mood effects on the colony (V5)
        "skill_growth", // passion's learning over the horizon (#3809)
    ];

    /// <summary>The plan a review's pawn values await before the colony has a layout plan (#3857): its food hauls walk the plan's
    /// rooms, so no value is priced until it stands. The subject a building verdict awaiting the layout plan names.</summary>
    public const string AwaitingLayoutPlan = "layout_plan";

    /// <summary>
    /// The pawn's value (#3825; one work tick is one capability-tick, a day is <see cref="GameTime.TicksPerDay"/>):
    /// <list type="bullet">
    /// <item>work added (V1, V2): the colony's shortfall rows of a work type the pawn is capable of, filled from the pawn's free ticks
    /// at its fastest stat first, each capped at its row, in ticks at speed 1; the rest of its time adds nothing;</item>
    /// <item>food (V4): the nutrition it eats a day times the work a unit costs (cooking it plus growing its ingredients, or growing
    /// it eaten raw when the colony can cook no meal) and hauling it (#3849, <see cref="FoodHaulRules.TicksPerNutrition"/>) over the
    /// planned layout's walks, at the pawn's own MoveSpeed and CarryingCapacity: the pawn is priced as hauling its own food;</item>
    /// <item>breaks (V5): <see cref="MoodBreakModel.DaysLost"/> of a day held at its mood target under its threshold;</item>
    /// <item>bed (V6): nothing while a colonist bed stands vacant, else the bed's build over the horizon.</item>
    /// </list>
    /// A stat the shortfall needs that the pawn's speeds lack, an unpriced break curve, the colony's bed unread, a horizon of no days,
    /// a meal with no planned bench to cook it, a planned walk no route joins, or the pawn's move or carry stat unread
    /// throws <see cref="RequiredFactException"/>. A crop with no planned field names <c>food_field_unplanned</c>: its harvest is not hauled.
    /// </summary>
    public static PawnValue Of(PawnValueFacts pawn, PawnValueColony colony)
    {
        DomainException.Require(double.IsFinite(pawn.FreeTicksPerDay) && pawn.FreeTicksPerDay >= 0, "pawn free ticks");
        DomainException.Require(double.IsFinite(pawn.FoodPerDay) && pawn.FoodPerDay >= 0, "pawn food a day");
        double Speed(string stat) => stat.Length == 0 ? 1
            : pawn.Speeds.TryGetValue(stat, out var speed) ? speed : throw new RequiredFactException("work speed " + stat + " of " + pawn.Pawn);
        var incapable = pawn.Incapable.ToHashSet(StringComparer.Ordinal);
        var free = pawn.FreeTicksPerDay;
        var shares = new List<PawnWorkShare>();
        foreach (var row in colony.Shortfall.Rows.Where(r => !incapable.Contains(r.WorkType))
            .Select(r => (Row: r, Speed: Speed(r.Stat))).Where(r => r.Speed > 0)
            .OrderByDescending(r => r.Speed).ThenBy(r => r.Row.WorkType, StringComparer.Ordinal).ThenBy(r => r.Row.Stat, StringComparer.Ordinal))
        {
            if (free <= 0) break;
            var done = Math.Min(row.Row.TicksPerDay, free * row.Speed);
            free -= done / row.Speed;
            shares.Add(new(row.Row.WorkType, row.Row.Stat, done));
        }

        var food = colony.Food;
        var perNutrition = food.Meal.Length != 0
            ? food.CookTicksPerNutrition + (food.IngredientPerNutrition * food.GrowTicksPerNutrition)
            : food.GrowTicksPerNutrition;
        var legs = colony.Haul;
        DomainException.Require(food.MoveStat.Length > 0 && food.CarryStat.Length > 0, "food haul stats");
        if (food.Dish is not null && !legs.Bench) throw new RequiredFactException("planned bench of " + food.Meal);
        if (!double.IsFinite(legs.FieldToStore) || !double.IsFinite(legs.SourceToBench) || !double.IsFinite(legs.BenchToDish))
            throw new RequiredFactException("planned route of the food's hauls");
        var haul = FoodHaulRules.TicksPerNutrition(food, legs, FoodHaulRules.TicksPerCell(Speed(food.MoveStat)), Speed(food.CarryStat));
        var notModelled = food.Crop.Length == 0 ? [.. NotModelled, "food_source_not_crop"]
            : !legs.Field ? [.. NotModelled, "food_field_unplanned"] : NotModelled;
        var bed = colony.Bed ?? throw new RequiredFactException("colony bed");
        if (!bed.Vacant && !(colony.HorizonDays > 0)) throw new RequiredFactException("bed horizon");
        return new PawnValue(pawn.Pawn, shares.Sum(s => s.TicksPerDay) / GameTime.TicksPerDay, pawn.FoodPerDay * (perNutrition + haul) / GameTime.TicksPerDay,
            BreakDaysPerDay(pawn.Mood, colony.Breaks), bed.Vacant ? 0 : bed.BuildDays / colony.HorizonDays)
        {
            Work = EquatableArray.From(shares), NotModelled = notModelled, FoodHaul = pawn.FoodPerDay * haul / GameTime.TicksPerDay,
        };
    }

    /// <summary>The colonist beds of the sleeping census no one owns, by id (<see cref="SleepingRules.VacantColonistBed"/>).</summary>
    public static IReadOnlyList<string> VacantColonistBeds(SleepingObservation sleeping) =>
        [.. sleeping.Beds.Where(SleepingRules.VacantColonistBed).Select(b => b.Id).Order(StringComparer.Ordinal)];

    /// <summary>The break days a day of a pawn held at its observed mood target under its threshold (V5): none without a mood.</summary>
    public static double BreakDaysPerDay(PawnMoodRead? mood, MoodBreakModel breaks)
    {
        if (mood is null) return 0;
        DomainException.Require(double.IsFinite(mood.Target) && double.IsFinite(mood.Threshold) && mood.Threshold > 0, "pawn mood");
        var ticks = new double[LayoutMoodRules.MoodTierCount];
        var level = Math.Clamp(mood.Target, 0, 1);
        new MoodBreakModel { Threshold = mood.Threshold }.AddTierTicks(level, level, GameTime.TicksPerDay, ticks);
        return breaks.DaysLost(ticks) ?? throw new RequiredFactException("mood break curve");
    }

    /// <summary>
    /// The review's work shortfall and every colonist's value (#3825), the production way: the shortfall of the selection over the
    /// candidates at the colonists' read speeds, each colonist's facts from its needs read (NeedsForecastPawn.Upkeep) and its free hours
    /// from tier 2's input over the investment horizon (<see cref="PawnCycle.FreeHoursByPawn"/>), the colony's from the catalog, the break curve and the bed the
    /// review priced. A colonist tier 2 gives no free hours throws <see cref="RequiredFactException"/>.
    /// A colony with no layout plan yet (the review before its first plan derives, or one whose derive has not recorded a plan;
    /// <see cref="ForecastCoupledContext.FoodHaul"/> null) has walks for no food haul to price: its shortfall stands, it values no
    /// colonist and names the plan the values await, <see cref="AwaitingLayoutPlan"/> (#3857, awaiting_plan as a building verdict
    /// before the plan names it). Otherwise the plan awaited is empty.
    /// </summary>
    public static (WorkShortfall Shortfall, EquatableArray<PawnValue> Values, string AwaitingPlan) Review(ForecastCoupledCandidates candidates, ForecastCoupledSelection selection,
        NeedsForecastRead needs, WorkSpeedFacts work, FoodWorkFacts food, ForecastModelBasis basis)
    {
        var context = candidates.Context;
        var speeds = needs.Pawns.ToDictionary(p => p.Pawn, p => (IReadOnlyDictionary<string, double>)p.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var shortfall = WorkShortfallRules.Of(candidates, selection, work, speeds);
        if (context.FoodHaul is not { } haul) return (shortfall, [], AwaitingLayoutPlan);
        var hours = PawnCycle.FreeHoursByPawn(basis.Input, context.InvestmentHorizonDays);
        var colony = new PawnValueColony { Shortfall = shortfall, Food = food, Breaks = context.MoodBreaks, Bed = context.Bed, Haul = haul, HorizonDays = context.InvestmentHorizonDays };
        var values = needs.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal).Select(p => Of(new PawnValueFacts(p.Pawn)
        {
            Speeds = speeds[p.Pawn], Incapable = p.Upkeep.IncapableWork, FoodPerDay = p.Upkeep.FoodPerDay, Mood = p.Upkeep.Mood,
            FreeTicksPerDay = (hours.TryGetValue(p.Pawn, out var free) ? free : throw new RequiredFactException("free hours of " + p.Pawn)) * GameTime.TicksPerHour,
        }, colony));
        return (shortfall, EquatableArray.From(values), "");
    }
}
