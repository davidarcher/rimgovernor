using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

// Herd shelter (RG-HABITAT-001, task 2.3). Each pen unit (PenWant's key: the herd's race, "" for the misc unit) is offered
// three resting arrangements, each valued by the consequence it avoids rather than by a categorical rule:
// - open: the animals rest on the pen's ground, the baseline every alternative is measured against (a known zero);
// - barn: a roofed, walled room beside the pen and joined to it, so the animals stay contained with their grazing; it avoids
//   the deaths the seasonal outdoor climate deals the unit's animals outside their comfort range;
// - animal bed: an animal sleeping spot on the pen's ground, which every body size fits (the catalog's spot has no size limit);
//   a spot changes no temperature, so it avoids no exposure (a known zero).
// The barn's exposure is the mirrored hypothermia and heatstroke givers (ForecastValue.ThermalGainPerTick) walked over the
// map's twelve seasonal means from the current twelfth; an animal whose severity reaches the def's lethal value is lost at
// its market value over the days until it does. A comfort range that covers the climate is a known zero, not an exclusion. A
// fact the barn needs unread makes only the barn Unassessed, naming the fact. The barn is a graph node only when it wins
// (BarnWants); the coupled ledger then prices the planned barn with its own shelter model (ForecastHerdShelter).

/// <summary>A pen unit's resting arrangements.</summary>
public enum HerdShelterKind
{
    Open,
    Barn,
    Bed,
}

/// <summary>An arrangement's value: assessed (its gain in capability-days a day, and why), excluded by a constraint, or unassessed for an unread fact.</summary>
public abstract record HerdShelterValue
{
    private HerdShelterValue() { }

    /// <summary>The state the value names: an assessed one's reason, "excluded:" and its reason, or "unassessed:" and the unread fact.</summary>
    public abstract string State { get; }

    public sealed record Assessed(double GainPerDay, string Reason) : HerdShelterValue
    {
        public override string State => Reason;
    }

    public sealed record Excluded(string Reason) : HerdShelterValue
    {
        public override string State => "excluded:" + Reason;
    }

    public sealed record Unassessed(string Fact) : HerdShelterValue
    {
        public override string State => "unassessed:" + Fact;
    }
}

public sealed record HerdShelterAlternative(HerdShelterKind Kind, HerdShelterValue Value);

/// <summary>One pen unit's alternatives, in kind order; Animals is the count of its penned animals.</summary>
public sealed record HerdShelter(string Herd, int Animals, EquatableArray<HerdShelterAlternative> Alternatives)
{
    /// <summary>The assessed alternative with the largest gain, open on a tie: an unassessed or excluded one never wins.</summary>
    public HerdShelterKind Chosen => Alternatives
        .Select(a => (a.Kind, Gain: a.Value is HerdShelterValue.Assessed { GainPerDay: var g } ? g : double.NegativeInfinity))
        .OrderByDescending(a => a.Gain).ThenBy(a => a.Kind).First().Kind;
}

/// <summary>The barn a pen unit's chosen arrangement asks for: Race is the unit's key, Cells the interior its animals sleep on.</summary>
public sealed record BarnWant(string Race, int Cells);

/// <summary>The facts the herd shelter reads: the animals, the herd plan's races with a unit of their own, the pen wants, the map's climate (null unread), the thermal hediff tables and the catalog's animal sleeping spot ("" none).</summary>
public sealed record HerdShelterInput
{
    public IReadOnlyList<UpkeepAnimal> Animals { get; init; } = [];

    public IReadOnlyList<Resource> Herds { get; init; } = [];

    public IReadOnlyList<PenWant> Pens { get; init; } = [];

    public GearClimate? Climate { get; init; }

    public CapacityPenaltyTables Penalties { get; init; } = new();

    public string AnimalSpot { get; init; } = "";
}

public static class HerdShelterRules
{
    public const string ReasonOpenBaseline = "open_baseline";
    public const string ReasonBarnAvoidsExposure = "barn_avoids_exposure";
    public const string ReasonComfortCoversClimate = "comfort_covers_climate";
    public const string ReasonNonlethalExposure = "nonlethal_exposure_unpriced";
    public const string ReasonBarnWithoutPen = "barn_without_pen";
    public const string ReasonBedNoThermalEffect = "animal_bed_no_thermal_effect";
    public const string ReasonNoAnimalSpot = "no_animal_sleeping_spot";

    /// <summary>Why the graph holds a barn: its unit's barn alternative won, or the plan already holds the barn.</summary>
    public const string ReasonHerdShelter = "herd_shelter_owed";

    /// <summary>A barn's floor per animal: its sleeping spot and the free cell it is tended from.</summary>
    internal const int BarnCellsPerAnimal = 2;

    /// <summary>The years of the seasonal cycle the walk follows: the second catches a severity the first year carries over.</summary>
    private const int WalkYears = 2;

    private const double TicksPerTwelfth = GameTime.TicksPerYear / 12.0;

    /// <summary>The unit an animal's pen belongs to: its race when the herd plan gives the race a unit, else the misc unit "".</summary>
    internal static string UnitOf(UpkeepAnimal animal, IReadOnlyList<Resource> herds) => herds.Contains(animal.Definition) ? animal.Definition.Value : "";

    /// <summary>The penned animals (RequiresPen) of unit <paramref name="herd"/>, by id.</summary>
    internal static IReadOnlyList<UpkeepAnimal> UnitAnimals(HerdShelterInput input, string herd) =>
        [.. input.Animals.Where(a => a.RequiresPen && UnitOf(a, input.Herds) == herd).OrderBy(a => a.Id.Value, StringComparer.Ordinal)];

    /// <summary>Each pen unit's alternatives, in unit key order.</summary>
    public static IReadOnlyList<HerdShelter> Assess(HerdShelterInput input)
    {
        var units = input.Animals.Where(a => a.RequiresPen).Select(a => UnitOf(a, input.Herds)).Distinct().Order(StringComparer.Ordinal);
        var output = new List<HerdShelter>();
        foreach (var herd in units)
        {
            var animals = UnitAnimals(input, herd);
            HerdShelterAlternative[] alternatives =
            [
                new(HerdShelterKind.Open, new HerdShelterValue.Assessed(0, ReasonOpenBaseline)),
                new(HerdShelterKind.Barn, Value(() => BarnValue(input, herd, animals))),
                new(HerdShelterKind.Bed, input.AnimalSpot.Length == 0 ? new HerdShelterValue.Excluded(ReasonNoAnimalSpot) : new HerdShelterValue.Assessed(0, ReasonBedNoThermalEffect)),
            ];
            output.Add(new HerdShelter(herd, animals.Count, EquatableArray.From(alternatives)));
        }
        return output;
    }

    /// <summary>The finding naming an arrangement's state: "herd_shelter:&lt;unit&gt;:&lt;kind&gt;:" then <see cref="HerdShelterValue.State"/>.</summary>
    public static string StateOf(string herd, HerdShelterAlternative alternative) =>
        $"herd_shelter:{herd}:{Wire.Name(alternative.Kind)}:{alternative.Value.State}";

    /// <summary>The barns the chosen arrangements ask for: one per unit whose barn wins, sized BarnCellsPerAnimal per penned animal.</summary>
    public static IReadOnlyList<BarnWant> BarnWants(IReadOnlyList<HerdShelter> shelters) =>
        [.. shelters.Where(s => s.Chosen == HerdShelterKind.Barn).Select(s => new BarnWant(s.Herd, s.Animals * BarnCellsPerAnimal))];

    private static HerdShelterValue Value(Func<HerdShelterValue> value)
    {
        try
        {
            return value();
        }
        catch (RequiredFactException e)
        {
            return new HerdShelterValue.Unassessed(e.Fact);
        }
    }

    private static HerdShelterValue BarnValue(HerdShelterInput input, string herd, IReadOnlyList<UpkeepAnimal> animals)
    {
        // A barn joins its unit's pen: without one it would hold the animals away from their grazing and outside an enclosure.
        if (!input.Pens.Any(p => p.Race == herd))
        {
            return new HerdShelterValue.Excluded(ReasonBarnWithoutPen);
        }
        var climate = Climate(input);
        RequireThermalTables(input.Penalties);
        var gain = 0.0;
        var exposed = false;
        foreach (var animal in animals)
        {
            var (min, max) = ComfortOf(animal);
            exposed |= climate.Curve.Any(t => ForecastValue.ThermalGainPerTick(true, t, min, max) > 0 || ForecastValue.ThermalGainPerTick(false, t, min, max) > 0);
            if (DaysToDeath(climate, t => climate.Curve[t], min, max, input.Penalties) is { } days)
            {
                gain += LossPerDay(MarketValueOf(animal), days);
            }
        }
        var reason = gain > 0 ? ReasonBarnAvoidsExposure : exposed ? ReasonNonlethalExposure : ReasonComfortCoversClimate;
        return new HerdShelterValue.Assessed(gain, reason);
    }

    /// <summary>The map's seasonal means and where the year stands: the current twelfth and the ticks left in it.</summary>
    internal sealed record SeasonalClimate(EquatableArray<double> Curve, int Twelfth, double TicksToNext);

    internal static SeasonalClimate Climate(HerdShelterInput input)
    {
        var climate = input.Climate ?? throw new RequiredFactException("gear census");
        var curve = ClimateRules.RequireOutdoorCurve(climate.Temperatures);
        if (climate.CurrentTwelfth is < 0 or > 11 || climate.TicksToNextTwelfth <= 0)
        {
            throw new RequiredFactException("climate twelfth");
        }
        return new SeasonalClimate(curve, climate.CurrentTwelfth, climate.TicksToNextTwelfth);
    }

    internal static void RequireThermalTables(CapacityPenaltyTables tables)
    {
        if (!tables.Hypothermia.Valid() || !tables.Heatstroke.Valid())
        {
            throw new RequiredFactException("thermal_penalty_tables");
        }
    }

    /// <summary>The animal's comfortable temperature range (ComfyTemperatureMin and Max); unread when not finite and ordered.</summary>
    internal static (double Min, double Max) ComfortOf(UpkeepAnimal animal)
    {
        var body = animal.Pawn.Settings.Body;
        if (!double.IsFinite(body.ComfortableMinC) || !double.IsFinite(body.ComfortableMaxC) || body.ComfortableMinC > body.ComfortableMaxC)
        {
            throw new RequiredFactException("animal comfort range:" + animal.Id.Value);
        }
        return (body.ComfortableMinC, body.ComfortableMaxC);
    }

    /// <summary>The penned animal's market value, read off its roam row as the pen credit reads it.</summary>
    internal static double MarketValueOf(UpkeepAnimal animal) =>
        animal.Colony?.Roping is AnimalRoping.Roped { Roam.MarketValue: var value } && double.IsFinite(value) && value >= 0
            ? value : throw new RequiredFactException("animal market value:" + animal.Id.Value);

    /// <summary>The capability-days a day an animal's death after <paramref name="days"/> costs: its value over those days, as the pen credit prices a roamer over its mean time to leave.</summary>
    internal static double LossPerDay(double marketValue, double days) =>
        marketValue / SilverGapRules.ValuePerWorkTick / GameTime.TicksPerDay / Math.Max(days, 1);

    /// <summary>
    /// The days until hypothermia or heatstroke kills an animal of comfort range [min, max] living at <paramref name="temperature"/>
    /// of each twelfth, from severity 0 at the current point of the year over <see cref="WalkYears"/> cycles; null when neither
    /// severity reaches its def's lethal value. Each twelfth holds its seasonal mean (the day's swing is not read).
    /// </summary>
    internal static double? DaysToDeath(SeasonalClimate climate, Func<int, double> temperature, double min, double max, CapacityPenaltyTables tables)
    {
        double? death = null;
        foreach (var cold in new[] { true, false })
        {
            if ((cold ? tables.Hypothermia : tables.Heatstroke).Lethality is not Lethality.Lethal { Severity: var lethal })
            {
                continue;
            }
            var severity = 0.0;
            var elapsed = 0.0;
            var twelfth = climate.Twelfth;
            var span = climate.TicksToNext;
            for (var i = 0; i <= WalkYears * 12; i++)
            {
                var t = temperature(twelfth);
                var gain = ForecastValue.ThermalGainPerTick(cold, t, min, max);
                if (gain > 0)
                {
                    if (severity + (gain * span) >= lethal)
                    {
                        var at = (elapsed + ((lethal - severity) / gain)) / GameTime.TicksPerDay;
                        death = death is { } d ? Math.Min(d, at) : at;
                        break;
                    }
                    severity += gain * span;
                }
                else if (ForecastValue.ThermalRecovers(cold, t, min, max))
                {
                    severity = ForecastValue.RecoveredSeverity(severity, span);
                }
                elapsed += span;
                twelfth = (twelfth + 1) % 12;
                span = TicksPerTwelfth;
            }
        }
        return death;
    }
}

public static class FactsHerdShelterExtensions
{
    /// <summary>The herd shelter's facts from the routine reading.</summary>
    public static HerdShelterInput HerdShelterInput(this ReviewFacts f) => new()
    {
        Animals = f.AnimalUpkeep.Animals,
        Herds = HerdPlanRules.PlanHerd(f.HerdPlanInput()).Herds,
        Pens = f.PenWants(),
        Climate = f.ClimateRead,
        Penalties = f.CapacityPenalties,
        AnimalSpot = f.Shapes.Furniture.AnimalSpot,
    };

    /// <summary>The barns the layout gives the herd, from the routine reading.</summary>
    public static IReadOnlyList<BarnWant> BarnWants(this ReviewFacts f) => HerdShelterRules.BarnWants(HerdShelterRules.Assess(f.HerdShelterInput()));
}
