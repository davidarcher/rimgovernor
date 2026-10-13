using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>Go <c>ErrFoodPlanFacts</c>: the food plan inputs are unavailable or invalid.</summary>
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

    public Fact<IReadOnlyList<SupplyCandidate>> Channels { get; init; }

    public Fact<double> Labor { get; init; }
}

public enum FoodPlanDecision
{
    Open,
    Hold,
    Close,
}

public sealed record FoodPlanEntry
{
    public SupplyCandidate Channel { get; init; } = new();

    /// <summary>Hold preserves current state: keep an open channel or defer a closed one.</summary>
    public FoodPlanDecision Decision { get; init; }

    public string Reason { get; init; } = "";

    public IReadOnlyList<CandidateTerm> Terms { get; init; } = [];

    /// <summary>Risk-adjusted contribution admitted to this plan.</summary>
    public double DeliveredPerDay { get; init; }

    /// <summary>
    /// Go <c>FoodPlanEntry.Selected</c>: whether the plan relies on the channel: it credits it, or opens it (an
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
                b.Append(CultureInfo.InvariantCulture, $"\n {Wire.Name(e.Channel.Kind)}/{e.Channel.ID} {Wire.Name(e.Decision)}: {e.Reason} delivered={F4(e.DeliveredPerDay)}");
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
/// GrowDays as its remaining growth. Unknown inputs remain unknown in the resulting row. (Go <c>FoodField</c>.)
/// </summary>
public sealed record FoodField
{
    public string Id { get; init; } = "";

    public FieldPlan Plan { get; init; } = new();

    public Fact<double> RemainingGrowDays { get; init; }

    public Fact<double> WorkPerDay { get; init; }

    /// <summary>What the field's fallout and blight risks derive from.</summary>
    public FieldExposure Exposure { get; init; } = new();
}

/// <summary>The cooking a harvest can use: the benches and the number of cooks who can work them. Unknown benches or cooks leave a crop raw. (Go <c>CropKitchen</c>.)</summary>
public sealed record CropKitchen
{
    public Fact<IReadOnlyList<ProductionBench>> Benches { get; init; }

    public Fact<double> Cooks { get; init; }

    /// <summary>
    /// Go <c>CropKitchen.Cooking</c>: reads the best meal made of vegetables. It is Unknown while the benches,
    /// the cooks or any usable meal recipe's facts are unknown, and known "none" (empty recipe, Known) is never
    /// confused with it: a kitchen with no usable bench or no vegetable meal leaves the crop raw.
    /// </summary>
    public Fact<CropCooking> Cooking()
    {
        var bk = Benches.TryGetValue(out var benches);
        var ck = Cooks.TryGetValue(out var cooks);
        if (!bk || !ck || !PolicyFunctions.FoodNumber(cooks))
        {
            return Unknown<CropCooking>();
        }
        var usable = 0;
        var unknown = false;
        var best = new CropCooking();
        foreach (var b in benches!)
        {
            if (!b.Usable.TryGetValue(out var u))
            {
                unknown = true;
                continue;
            }
            if (!u)
            {
                continue;
            }
            usable++;
            foreach (var r in b.Recipes)
            {
                if (r.Role != RecipeRole.OrdinaryMeal || !PolicyFunctions.Positive(r.Available))
                {
                    continue;
                }
                var ek = r.NutrientEfficiency.TryGetValue(out var eff);
                var wk = r.WorkPerNutrition.TryGetValue(out var work);
                var ik = r.IngredientClasses.TryGetValue(out var classes);
                if (!ek || !wk || !ik || !PolicyFunctions.FieldPositive(eff) || !PolicyFunctions.FoodNumber(work))
                {
                    unknown = true;
                    continue;
                }
                if (!PolicyFunctions.MealSlotsSupported(classes!, new HashSet<FoodIngredientClass> { FoodIngredientClass.Vegetable }))
                {
                    continue;
                }
                if (best.Recipe.Length == 0 || eff > best.NutrientEfficiency
                    || (eff == best.NutrientEfficiency && (work < best.WorkPerNutrition || (work == best.WorkPerNutrition && string.CompareOrdinal(r.Name, best.Recipe) < 0))))
                {
                    best = new CropCooking { Recipe = r.Name, NutrientEfficiency = eff, WorkPerNutrition = work };
                }
            }
        }
        if (best.Recipe.Length == 0 && unknown)
        {
            return Unknown<CropCooking>();
        }
        return Known(best with { CapacityTicks = Math.Min(usable, cooks) * PolicyFunctions.CookTicksPerDay });
    }
}

/// <summary>
/// The best vegetable meal at the kitchen's usable benches. NutrientEfficiency is cooked nutrition per raw
/// nutrition, WorkPerNutrition the bench work per cooked nutrition and CapacityTicks the cook work a day.
/// (Go <c>CropCooking</c>.)
/// </summary>
public sealed record CropCooking
{
    public string Recipe { get; init; } = "";

    public double NutrientEfficiency { get; init; }

    public double WorkPerNutrition { get; init; }

    public double CapacityTicks { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>SupplyFoodPlan</c>: the food view of PlanSupply: the request's forecast becomes the Nutrition
    /// demand, its channels become candidates, and the supply plan reads back as a FoodPlan. PlanSupply owns the
    /// ranking, labor budget and credit; GapPerDay is the covered demand minus admitted, risk-adjusted delivery.
    /// Throws <see cref="FoodPlanFactsException"/> where Go returned ErrFoodPlanFacts.
    /// </summary>
    public static FoodPlan SupplyFoodPlan(FoodPlanRequest r)
    {
        SupplyDemand demand;
        SupplyPlan plan;
        try
        {
            demand = NutritionDemand(new NutritionDemandInput
            {
                Forecast = r.Demand,
                ReserveDays = r.ReserveDays,
                MinDays = r.MinDays,
                TargetDays = r.TargetDays,
                EmergencyDays = r.EmergencyDays,
            });
            var req = new SupplyPlanRequest { Demands = Known<IReadOnlyList<SupplyDemand>>([demand]), Labor = r.Labor, Candidates = Unknown<IReadOnlyList<SupplyCandidate>>() };
            if (r.Channels.TryGetValue(out var rows))
            {
                foreach (var c in rows)
                {
                    if (!ValidFoodChannelKind(c.Kind) || c.Yields.Count == 0 || c.Yields[0].Good != NutritionKey)
                    {
                        throw new FoodPlanFactsException();
                    }
                }
                req = req with { Candidates = Known(rows) };
            }
            plan = PlanSupply(req);
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
            GapPerDay = plan.Gap(NutritionKey),
            DeliveredPerDay = plan.Delivered(NutritionKey),
            HorizonDays = demand.Window(),
            DemandPerDay = demandPerDay,
            Portfolio = View(plan.Portfolio),
            Unknown = View(plan.Unknown),
        };
    }

    private static bool ValidFoodChannelKind(CandidateKind k) =>
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
    /// Go <c>FoodSupportCandidate</c>: a zero-contribution Hold row: a supporting method (cooking capacity,
    /// stock protection) that creates no nutrition by itself and leaves the method to its own observed
    /// preconditions.
    /// </summary>
    public static SupplyCandidate FoodSupportCandidate(CandidateKind kind, string id) =>
        FoodCandidate(kind, id, Known(0.0)) with
        {
            LaborPerDay = Known(0.0),
            LeadDays = Known(0.0),
            State = FoodState(Known(false), Unknown<bool>()),
            Terms = EquatableArray.From([new CandidateTerm("supporting_method", 1)]),
        };

    public static IReadOnlyList<SupplyCandidate> ForageChannels(IReadOnlyList<AcquisitionSource> sources)
    {
        var @out = new List<SupplyCandidate>();
        foreach (var s in sources)
        {
            if (s.Food && !s.Tree && !s.Hunt)
            {
                var c = FoodCandidate(CandidateKind.Forage, s.ID, Known(s.NutritionYield / FoodForageCycleDays)) with
                {
                    LaborPerDay = Known(FoodForageWorkTicks / FoodForageCycleDays),
                    LeadDays = Known(0.0),
                    State = FoodState(Known(false), Known(s.Designated)),
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
    /// Go <c>CropChannels</c>: prices each field as the better of raw and cooked nutrition. Cooked nutrition is
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
            if (f.Plan.Crop.Edible.TryGetValue(out var edible) && !edible)
            {
                continue;
            }
            var c = PriceCrop(f.Plan.Crop, f.Plan.Sites.Cells, f.WorkPerDay, cooking);
            var lead = WithCropPause(f.RemainingGrowDays, season.Conditions);
            c = c with { ID = f.Id, LeadDays = lead, Source = "crop:" + f.Id };
            c = c with { Risk = EquatableArray.From(CropRisks(season, lead, f.Exposure)), State = FoodState(Known(false), Known(true)) };
            @out.Add(c);
        }
        return @out;
    }

    /// <summary>The nutrition, work and rot cap of cells of crop, shared by a planted field and a candidate one. baseWork is the daily harvest work.</summary>
    private static SupplyCandidate PriceCrop(CropChoice crop, int cells, Fact<double> baseWork, Fact<CropCooking> cooking)
    {
        var ek = crop.Edible.TryGetValue(out _);
        var yk = crop.HarvestNutrition.TryGetValue(out var yield);
        var dk = crop.GrowDays.TryGetValue(out var days);
        var nutrition = Unknown<double>();
        var work = baseWork;
        var terms = new List<CandidateTerm>();
        // Preserve malformed known numbers for PlanSupply's error boundary.
        if ((yk && !FoodNumber(yield)) || (dk && !FieldPositive(days)) || cells < 0)
        {
            nutrition = Known(double.NaN);
        }
        else if (ek && yk && dk)
        {
            var raw = yield * cells / days;
            nutrition = Known(raw);
            terms.Add(new CandidateTerm("raw_nutrition_per_day", raw));
            if (cooking.TryGetValue(out var cook) && cook.Recipe.Length != 0)
            {
                var cooked = raw * cook.NutrientEfficiency;
                var cookWork = cooked * cook.WorkPerNutrition;
                var pk = crop.RawPreferred.TryGetValue(out var preferred);
                if (cookWork <= cook.CapacityTicks && (cooked > raw || (cooked == raw && pk && !preferred)))
                {
                    nutrition = Known(cooked);
                    terms.Add(new CandidateTerm("cooked_nutrition_per_day", cooked));
                    terms.Add(new CandidateTerm("cook_work_per_day", cookWork));
                    if (baseWork.TryGetValue(out var w))
                    {
                        work = Known(w + cookWork);
                    }
                }
            }
        }
        var c = FoodCandidate(CandidateKind.Crop, "", nutrition) with { LaborPerDay = work };
        if (dk && FieldPositive(days))
        {
            terms.Add(new CandidateTerm("grow_days", days));
        }
        if (nutrition.TryGetValue(out var n) && FoodNumber(n))
        {
            var rk = crop.RotDays.TryGetValue(out var rot);
            var pk = crop.Perishable.TryGetValue(out var perishable);
            if (rk && pk && perishable && FieldPositive(rot))
            {
                var yields = c.Yields.Items.ToBuilder();
                yields[0] = yields[0] with { StockCap = Known((long)Math.Ceiling(n * rot)) };
                c = c with { Yields = yields.ToImmutable() };
                terms.Add(new CandidateTerm("rot_days", rot));
            }
        }
        return c with { Terms = EquatableArray.From(terms) };
    }

    /// <summary>The sowing work of one cell (a typical plant's sowWork); the zone itself costs no work.</summary>
    public const double FieldSowTicksPerCell = 170.0;

    /// <summary>
    /// Go <c>NewFieldLeadDays</c>: the days from sowing now to a new field's first harvest: the full grow days
    /// across the calendar, so growth that outlasts the growing days left waits out the non-growing stretch. A
    /// crop is sown only while it grows, so the walk starts at the current temperature factor 1.
    /// </summary>
    public static Fact<double> NewFieldLeadDays(Fact<double> growDays, Fact<Calendar> calendar)
    {
        var dk = growDays.TryGetValue(out var days);
        var ck = calendar.TryGetValue(out var c);
        if (!dk || !ck || !c!.Valid() || !FieldPositive(days))
        {
            return Unknown<double>();
        }
        if (days > c.GrowingDaysRemaining)
        {
            days += c.NonGrowingDays;
        }
        return Known(Math.Min(YearDays, days));
    }

    /// <summary>
    /// Go <c>NewFieldChannels</c>: offers one closed candidate per viable crop of the field request: the cells
    /// the crop still needs, priced like a planted field, with the sowing as upfront labor and the full grow days
    /// across the calendar as lead. Nothing is offered while crops cannot be sown outdoors. Site choice stays with
    /// the field executor, which acts on a candidate the plan opened.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> NewFieldChannels(FieldRequest r, CropKitchen kitchen)
    {
        if (!r.Climate.SowingOutdoors().TryGetValue(out var sowing) || !sowing)
        {
            return [];
        }
        var (viables, _, ok) = ViableCrops(r, false);
        if (!ok)
        {
            return [];
        }
        var cooking = kitchen.Cooking();
        var @out = new List<SupplyCandidate>();
        foreach (var v in viables)
        {
            var work = Unknown<double>();
            if (v.Crop.HarvestWork.TryGetValue(out var harvest))
            {
                work = Known(harvest * v.Needed / v.Days);
            }
            var c = PriceCrop(v.Crop, v.Needed, work, cooking);
            var lead = WithCropPause(NewFieldLeadDays(v.Crop.GrowDays, r.Calendar), r.Conditions);
            // A field not yet sown lies under open sky and carries no blight.
            var open = Known((long)v.Needed);
            c = c with
            {
                ID = NewFieldPrefix + v.Crop.Name,
                LeadDays = lead,
                Risk = EquatableArray.From(CropRisks(new CropSeason { Calendar = r.Calendar, Conditions = r.Conditions }, lead, new FieldExposure { ZoneCells = open, UnroofedCells = open })),
                UpfrontCost = c.UpfrontCost with { LaborTicks = Known(v.Needed * FieldSowTicksPerCell) },
                State = FoodState(Known(false), Known(false)),
                Terms = EquatableArray.From(c.Terms.Append(new CandidateTerm("new_field_cells", v.Needed))),
            };
            @out.Add(c);
        }
        return @out;
    }

    /// <summary>Starts the ID of a candidate field the plan may open.</summary>
    public const string NewFieldPrefix = "new:";
}
