using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>The food plan inputs are unavailable or invalid.</summary>
public sealed class FoodPlanFactsException() : DomainException("food plan inputs unavailable or invalid");

public sealed record FoodPlanRequest
{
    public FoodForecast Demand { get; init; } = new();

    public double ReserveDays { get; init; }

    public double MinDays { get; init; }

    public double TargetDays { get; init; }

    /// <summary>
    /// The stage's starvation line (FootholdFoodDays). Under it a channel not yet delivering is not credited
    /// toward the target (only delivered food counts) and the lead-0 channels open one per kind first, so hunting,
    /// fishing, foraging and harvest run in parallel up to the labor budget. Zero disables the emergency.
    /// </summary>
    public double EmergencyDays { get; init; }

    public EquatableArray<SupplyCandidate> Channels { get; init; }

    /// <summary>The labor budget, in pawn ticks a day.</summary>
    public double Labor { get; init; }
}

public enum FoodPlanDecision
{
    Open,
    Hold,
    Close,
}

public sealed record FoodPlanEntry
{
    public required SupplyCandidate Channel { get; init; }

    /// <summary>Hold preserves current state: keep an open channel or defer a closed one.</summary>
    public FoodPlanDecision Decision { get; init; }

    public string Reason { get; init; } = "";

    public IReadOnlyList<CandidateTerm> Terms { get; init; } = [];

    /// <summary>Risk-adjusted contribution admitted to this plan.</summary>
    public double DeliveredPerDay { get; init; }

    /// <summary>
    /// Whether the plan relies on the channel: it credits it, or opens it (an
    /// uncredited channel opened under an emergency until it delivers).
    /// </summary>
    public bool Selected => DeliveredPerDay > 0 || Decision == FoodPlanDecision.Open;
}

public sealed record FoodPlan
{
    /// <summary>The shared allocation behind this portfolio, including animals.</summary>
    public FoodForecast Forecast { get; init; } = new();

    public IReadOnlyList<FoodPlanEntry> Portfolio { get; init; } = [];

    public IReadOnlyList<FoodPlanEntry> Unknown { get; init; } = [];

    public double DeliveredPerDay { get; init; }

    public double DemandPerDay { get; init; }

    public double GapPerDay { get; init; }

    /// <summary>The window the plan spreads a one-shot channel's stock over (at least one day): stock = gap x window credits exactly the gap.</summary>
    public double HorizonDays { get; init; }

    public string Explain()
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"delivered={F4(DeliveredPerDay)} demand={F4(DemandPerDay)} gap={F4(GapPerDay)}");
        foreach (var rows in new[] { Portfolio, Unknown })
        {
            foreach (var e in rows)
            {
                b.Append(CultureInfo.InvariantCulture, $"\n {Wire.Name(e.Channel.Kind)}/{e.Channel.Id} {Wire.Name(e.Decision)}: {e.Reason} delivered={F4(e.DeliveredPerDay)}");
                foreach (var t in e.Terms)
                {
                    b.Append(CultureInfo.InvariantCulture, $" {t.Name}={F4(t.Value)}");
                }
            }
        }
        return b.ToString();
    }

    private static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
}

/// <summary>
/// FoodField supplies the observations a FieldPlan does not own. ID identifies the field across reviews;
/// remaining growth and work must not be guessed from the crop name. A newly planned field uses the full
/// GrowDays as its remaining growth. Observed geometry and exposure remain on the native zone.
/// </summary>
public sealed record FoodField
{
    public required RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing Observed { get; init; }
    public string Id => Observed.Id;

    public FieldPlan Plan { get; init; } = new();

    public double RemainingGrowDays { get; init; }

    public double WorkPerDay { get; init; }

    /// <summary>What the field's fallout and blight risks derive from.</summary>
    public FieldExposure Exposure => new FieldExposure.FromZone(Observed);
}

/// <summary>The cooking a harvest can use: the benches and the number of cooks who can work them. Unread benches leave a crop raw.</summary>
public sealed record CropKitchen
{
    public EquatableArray<ProductionBench> Benches { get; init; }

    public double Cooks { get; init; }

    /// <summary>
    /// The best available vegetable recipe at a usable bench, or no recipe. A kitchen without one leaves the crop raw.
    /// </summary>
    public CropCooking? Cooking()
    {
        if (!FoodForecastRules.FoodNumber(Cooks))
        {
            throw new RequiredFactException("crop kitchen cooks");
        }
        var usable = 0;
        CropCooking? best = null;
        foreach (var b in Benches)
        {
            if (!b.Usable)
            {
                continue;
            }
            usable++;
            foreach (var r in b.Recipes)
            {
                if (r.Role != RecipeRole.OrdinaryMeal || !r.Available)
                {
                    continue;
                }
                if (r.Food is not RecipeFood.Food { WorkPerNutrition: var work, Cooking: RecipeCooking.FromRaw { NutrientEfficiency: var eff, IngredientClasses: var classes } }
                    || !FieldCapacityRules.FieldPositive(eff) || !FoodForecastRules.FoodNumber(work)
                    || !MealRules.MealSlotsSupported(classes, new HashSet<FoodIngredientClass> { FoodIngredientClass.Vegetable }))
                {
                    continue;
                }
                if (best is null || eff > best.NutrientEfficiency
                    || (eff == best.NutrientEfficiency && (work < best.WorkPerNutrition || (work == best.WorkPerNutrition && string.CompareOrdinal(r.Name, best.Recipe) < 0))))
                {
                    best = new CropCooking { Recipe = r.Name, NutrientEfficiency = eff, WorkPerNutrition = work };
                }
            }
        }
        return best is null ? null : best with { CapacityTicks = Math.Min(usable, Cooks) *FoodPlanRules.CookTicksPerDay };
    }
}

/// <summary>
/// The best vegetable meal at the kitchen's usable benches. NutrientEfficiency is cooked nutrition per raw
/// nutrition, WorkPerNutrition the bench work per cooked nutrition and CapacityTicks the cook work a day.
/// </summary>
public sealed record CropCooking
{
    public string Recipe { get; init; } = "";

    public double NutrientEfficiency { get; init; }

    public double WorkPerNutrition { get; init; }

    public double CapacityTicks { get; init; }
}

public static class FoodPlanRules
{

    public static double SeasonalShortfall(FoodForecast forecast, double targetDays) =>
        Math.Max(0, (forecast.Consumers.Sum(c => c.NutritionPerDay) * targetDays) - forecast.UsableNutrition);

    public static double AcquisitionNutrition(FoodPlan plan) => plan.GapPerDay * plan.HorizonDays;

    public static double SupplyLabor(int workers) => workers * CookTicksPerDay;

    /// <summary>
    /// The food view of PlanSupply: the request's forecast becomes the Nutrition
    /// demand, its channels become candidates, and the supply plan reads back as a FoodPlan. PlanSupply owns the
    /// ranking, labor budget and credit; GapPerDay is the covered demand minus admitted, risk-adjusted delivery.
    /// Throws <see cref="FoodPlanFactsException"/>.
    /// </summary>
    public static FoodPlan SupplyFoodPlan(FoodPlanRequest r)
    {
        SupplyDemand demand;
        SupplyPlan plan;
        try
        {
            demand = SupplyPlanRules.NutritionDemand(new NutritionDemandInput
            {
                Forecast = r.Demand,
                ReserveDays = r.ReserveDays,
                MinDays = r.MinDays,
                TargetDays = r.TargetDays,
                EmergencyDays = r.EmergencyDays,
            });
            var rows = r.Channels;
            foreach (var c in rows)
            {
                if (!ValidFoodChannelKind(c.Kind) || c.Yields.Count == 0 || c.Yields[0].Good != SupplyPlanRules.NutritionKey)
                {
                    throw new FoodPlanFactsException();
                }
            }
            plan = SupplyPlanRules.PlanSupply(new SupplyPlanRequest { Demands = [demand], Candidates = rows, Labor = r.Labor });
        }
        catch (DomainException e) when (e is not FoodPlanFactsException)
        {
            throw new FoodPlanFactsException();
        }
        var demandPerDay = 0.0;
        foreach (var c in r.Demand.Consumers)
        {
            demandPerDay += c.NutritionPerDay;
        }
        List<FoodPlanEntry> View(IEnumerable<SupplyEntry> rows)
        {
            var entries = new List<FoodPlanEntry>();
            foreach (var e in rows)
            {
                var delivered = 0.0;
                foreach (var c in e.Credit)
                {
                    delivered += c.Amount;
                }
                entries.Add(
                    new FoodPlanEntry
                    {
                        Channel = e.Candidate,
                        Decision = (FoodPlanDecision)e.Decision,
                        Reason = e.Reason,
                        Terms = [.. e.Terms.Select(t => new CandidateTerm(t.Name, t.Value))],
                        DeliveredPerDay = delivered,
                    }
                );
            }
            return entries;
        }
        return new FoodPlan
        {
            Forecast = r.Demand,
            GapPerDay = plan.Gap(SupplyPlanRules.NutritionKey),
            DeliveredPerDay = plan.Delivered(SupplyPlanRules.NutritionKey),
            HorizonDays = demand.Window,
            DemandPerDay = demandPerDay,
            Portfolio = View(plan.Portfolio),
            Unknown = View(plan.Unknown),
        };
    }

    internal static bool ValidFoodChannelKind(CandidateKind k) =>
        k is CandidateKind.Forage or CandidateKind.Hunt or CandidateKind.Slaughter or CandidateKind.Crop or CandidateKind.AnimalProduct or CandidateKind.Fishing
            or CandidateKind.Trade or CandidateKind.Tame or CandidateKind.AnimalBuy or CandidateKind.Corpse or CandidateKind.Reserve or CandidateKind.Cook;

    // Seed acquisition estimates budget one collection/hunt cycle per day. They describe finite observed sources,
    // not guaranteed renewable production; refresh the census each review. Work constants are policy estimates in
    // pawn ticks, not native measurements. Designation alone does not prove delivery (Open).
    public const double FoodForageCycleDays = 1.0;
    public const double FoodHuntCycleDays = 1.0;
    public const double FoodForageWorkTicks = 2500.0;
    public const double FoodHuntWorkTicks = 7500.0;

    /// <summary>
    /// A zero-contribution Hold row: a supporting method (cooking capacity,
    /// stock protection) that creates no nutrition by itself and leaves the method to its own observed
    /// preconditions.
    /// </summary>
    public static SupplyCandidate FoodSupportCandidate(CandidateKind kind, string id) =>
        CandidateRules.FoodCandidate(kind, id, new YieldSupply.Steady(0), CandidateState.Closed, new CandidateLead.Days(0), new CandidateWork.PerDay(0)) with
        {
            Terms = EquatableArray.From([new CandidateTerm("supporting_method", 1)]),
        };

    public static IReadOnlyList<SupplyCandidate> ForageChannels(IReadOnlyList<AcquisitionSource> sources)
    {
        var @out = new List<SupplyCandidate>();
        foreach (var s in sources)
        {
            if (s.Food && !s.Tree && !s.Hunt)
            {
                var c = CandidateRules.FoodCandidate(
                    CandidateKind.Forage,
                    s.Id,
                    new YieldSupply.Steady(s.NutritionYield / FoodForageCycleDays),
                    CandidateRules.FoodState(false, s.Designated),
                    new CandidateLead.Days(0),
                    new CandidateWork.PerDay(FoodForageWorkTicks / FoodForageCycleDays)) with
                {
                    Terms = EquatableArray.From([new CandidateTerm("estimated_cycle_days", FoodForageCycleDays), new CandidateTerm("estimated_work_ticks", FoodForageWorkTicks)]),
                };
                if (s.Definition.Length != 0)
                {
                    c = c with { Source = "forage:" + s.Definition };
                }
                @out.Add(c);
            }
        }
        return @out;
    }

    /// <summary>The work one cook gives a bench per day, the same budget convention as the plan's labor (eight hours a worker).</summary>
    public const double CookTicksPerDay = 20000.0;

    /// <summary>
    /// Prices each field as the better of raw and cooked nutrition. Cooked nutrition is
    /// the raw harvest times the best meal's efficiency, adds the cook labor to WorkPerDay and needs the
    /// kitchen's capacity; it is chosen only when it beats raw (raw wins ties unless raw is known not preferred).
    /// A perishable harvest bounds the stock it holds to what survives its rot days (StockCap); an unknown recipe
    /// or rot fact leaves that facet Unknown, never zero. A planted field is Designated; the delivery ledger
    /// moves it to Delivering (DeliveryCredit.Apply). An observed growth pause (an eclipse, a volcanic winter, a
    /// cold snap) extends the lead, and the season's fallout, the field's blight and the frost inside the lead
    /// window are its risks.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> CropChannels(IReadOnlyList<FoodField> fields, CropKitchen kitchen, CropSeason season)
    {
        var cooking = kitchen.Cooking();
        var @out = new List<SupplyCandidate>();
        foreach (var f in fields)
        {
            var edible = f.Plan.Crop.Edible;
            if (!edible)
            {
                continue;
            }
            var lead = CropRiskRules.WithCropPause(f.RemainingGrowDays, season.Conditions);
            var c = PriceCrop(f.Id, f.Plan.Crop, f.Plan.Sites.Cells, f.WorkPerDay, cooking, lead, CandidateState.Designated);
            @out.Add(c with { Source = "crop:" + f.Id, Risk = EquatableArray.From(CropRiskRules.CropRisks(season, lead, f.Exposure)) });
        }
        return @out;
    }

    /// <summary>
    /// The nutrition, work and rot cap of cells of crop, shared by a planted field and a candidate one. baseWork is the
    /// daily harvest work; an unread work, nutrition or lead is the candidate's unpriced or unscheduled state.
    /// </summary>
    private static SupplyCandidate PriceCrop(string id, CropChoice crop, int cells, double baseWork, CropCooking? cook, double lead, CandidateState state)
    {
        if (!FieldCapacityRules.FieldPositive(crop.GrowDays) || !FoodForecastRules.FoodNumber(crop.HarvestNutrition) || cells < 0 || !FoodForecastRules.FoodNumber(baseWork))
            throw new RequiredFactException("crop production price");
        var raw = crop.HarvestNutrition * cells / crop.GrowDays;
        var nutrition = raw;
        var work = baseWork;
        var terms = new List<CandidateTerm> { new("raw_nutrition_per_day", raw), new("grow_days", crop.GrowDays) };
        if (cook is not null)
        {
            var cooked = raw * cook.NutrientEfficiency;
            var cookWork = cooked * cook.WorkPerNutrition;
            if (cookWork <= cook.CapacityTicks && (cooked > raw || (cooked == raw && !crop.RawPreferred)))
            {
                nutrition = cooked;
                work += cookWork;
                terms.Add(new CandidateTerm("cooked_nutrition_per_day", cooked));
                terms.Add(new CandidateTerm("cook_work_per_day", cookWork));
            }
        }
        YieldSupply supply = new YieldSupply.Steady(nutrition);
        if (crop.Decay is { } decay)
        {
            if (!FieldCapacityRules.FieldPositive(decay.Days)) throw new RequiredFactException("harvest rot duration");
            supply = new YieldSupply.Bounded(nutrition, (long)Math.Ceiling(nutrition * decay.Days));
            terms.Add(new CandidateTerm("rot_days", decay.Days));
        }
        return CandidateRules.FoodCandidate(CandidateKind.Crop, id, supply, state, new CandidateLead.Days(lead), new CandidateWork.PerDay(work)) with { Terms = EquatableArray.From(terms) };
    }
    /// <summary>The sowing work of one cell (a typical plant's sowWork); the zone itself costs no work.</summary>
    public const double FieldSowTicksPerCell = 170.0;

    /// <summary>
    /// The days from sowing now to a new field's first harvest: the full grow days
    /// across the calendar, so growth that outlasts the growing days left waits out the non-growing stretch. A
    /// crop is sown only while it grows, so the walk starts at the current temperature factor 1.
    /// </summary>
    public static double NewFieldLeadDays(double growDays,RimGovernor.Brain.GameFacts.Observations.FoodClimate calendar)
    {
        var days = growDays;
        var c = calendar;
        if (!c.Valid() || !FieldCapacityRules.FieldPositive(days))
        {
            throw new RequiredFactException("new field growth calendar");
        }
        if (days > c.GrowingDaysRemaining)
        {
            days += c.NonGrowingDays;
        }
        return Math.Min(CalendarRules.YearDays, days);
    }

    /// <summary>
    /// Offers one closed candidate per viable crop of the field request: the cells
    /// the crop still needs, priced like a planted field, with the sowing as upfront labor and the full grow days
    /// across the calendar as lead. Nothing is offered while crops cannot be sown outdoors. Site choice stays with
    /// the field executor, which acts on a candidate the plan opened.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> NewFieldChannels(FieldRequest r, CropKitchen kitchen)
    {
        var sowing = r.Climate.SowingOutdoors();
        if (!sowing)
        {
            return [];
        }
        var (viables, _, ok) = CropChoiceRules.ViableCrops(r, false);
        if (!ok)
        {
            return [];
        }
        var cooking = kitchen.Cooking();
        var @out = new List<SupplyCandidate>();
        foreach (var v in viables)
        {
            var work = v.Crop.HarvestWork * v.Needed / v.Days;
            var lead = CropRiskRules.WithCropPause(NewFieldLeadDays(v.Crop.GrowDays, r.Calendar), r.Conditions);
            var c = PriceCrop(NewFieldPrefix + v.Crop.Name, v.Crop, v.Needed, work, cooking, lead, CandidateState.Closed);
            // A field not yet sown lies under open sky and carries no blight.
            var open = (long)v.Needed;
            c = c with
            {
                Risk = EquatableArray.From(CropRiskRules.CropRisks(new CropSeason { Calendar = r.Calendar, Conditions = r.Conditions }, lead, new FieldExposure.PlannedOpen(open))),
                UpfrontCost = c.UpfrontCost with { LaborTicks = v.Needed * FieldSowTicksPerCell },
                Terms = EquatableArray.From(c.Terms.Append(new CandidateTerm("new_field_cells", v.Needed))),
            };
            @out.Add(c);
        }
        return @out;
    }

    /// <summary>Starts the ID of a candidate field the plan may open.</summary>
    public const string NewFieldPrefix = "new:";
}
