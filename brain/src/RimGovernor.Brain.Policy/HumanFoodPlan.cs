namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Routes finite stocks. It deliberately contributes no new nutrition/day: the forecast already owns stock,
    /// and butchery is conversion.
    /// </summary>
    public static (SupplyCandidate Channel, bool Ok) HumanFoodChannel(
        IReadOnlyList<ProductionBench> benches,
        FoodSupply supply,
        IReadOnlyList<PawnId> humans,
        double targetDays,
        IdeologyRead ideology
    )
    {
        var none = (new SupplyCandidate(), false);
        if (!FoodNumber(targetDays) || targetDays <= 0)
        {
            return none;
        }
        var qualified = false;
        foreach (var b in benches)
        {
            var (_, ok) = QualifyingHumanButcher(b.HumanButchers, ideology);
            if (ok)
            {
                qualified = true;
            }
        }
        if (!qualified)
        {
            return none;
        }
        var baselineStocks = new List<FoodStock>();
        double nutrition = 0.0;
        double vegetables = 0.0;
        double tradeSurplus = 0.0;
        foreach (var stock in supply.Stocks)
        {
            var k = stock.Nutrition.TryGetValue(out var n);
            var hk = stock.Holder.TryGetValue(out var holder);
            if (!k || !hk || !holder.IsEmpty)
            {
                continue;
            }
            if (stock.Corpse)
            {
                if (!stock.Forbidden.TryGetValue(out var forbidden) || forbidden)
                {
                    continue;
                }
            }
            if (stock.IsHumanlike || (stock.IsHumanMeat && (stock.RawMeat || stock.Reserve)))
            {
                if (!stock.Reserve)
                {
                    nutrition += n;
                }
                else
                {
                    tradeSurplus += n;
                }
                continue;
            }
            baselineStocks.Add(stock);
            if (stock.Vegetable && !stock.Reserve)
            {
                vegetables += n;
            }
        }
        if (nutrition <= 0 && tradeSurplus <= 0)
        {
            return none;
        }
        FoodForecast forecast;
        try
        {
            forecast = ForecastFood(supply with { Stocks = baselineStocks }, null);
        }
        catch (FoodFactsException)
        {
            return none;
        }
        var human = new HashSet<PawnId>(humans);
        var accepts = new Dictionary<PawnId, bool>();
        foreach (var c in supply.Consumers)
        {
            accepts[c.Id] = c.HumanMeatAcceptable.ValueOrDefault;
        }
        double feed = 0.0;
        double meals = 0.0;
        foreach (var c in forecast.Consumers)
        {
            var deficit = Math.Max(0, targetDays - c.RunwayDays) * c.NutritionPerDay;
            if (!human.Contains(c.Id))
            {
                feed += deficit;
            }
            else if (accepts.GetValueOrDefault(c.Id))
            {
                meals += deficit;
            }
        }
        var survival = false;
        foreach (var b in benches)
        {
            if (!b.Usable.ValueOrDefault)
            {
                continue;
            }
            foreach (var r in b.Recipes)
            {
                if (!r.Available.ValueOrDefault)
                {
                    continue;
                }
                foreach (var p in r.Products)
                {
                    survival = survival || p.SurvivalMeal();
                }
            }
        }
        IReadOnlyList<HumanMeatAllocation> routes;
        try
        {
            routes = RouteHumanMeat(
                new HumanMeatRouting
                {
                    Nutrition = nutrition,
                    FeedShortfall = feed,
                    EligibleMealDemand = meals,
                    VegetableNutrition = Math.Max(0, vegetables - Math.Min(feed, nutrition)),
                    CanMakeSurvivalMeals = survival,
                }
            );
        }
        catch (FoodFactsException)
        {
            return none;
        }
        var terms = new List<CandidateTerm> { new("finite_human_nutrition", nutrition) };
        foreach (var r in routes)
        {
            terms.Add(new CandidateTerm(Wire.Name(r.Route), r.Nutrition));
        }
        terms.Add(new CandidateTerm("trade_surplus_nutrition", tradeSurplus));
        var channel = FoodCandidate(CandidateKind.Corpse, "human-butchery", Known(0.0)) with
        {
            LaborPerDay = Known(0.0),
            LeadDays = Known(0.0),
            State = FoodState(Known(false), Known(false)),
            Terms = EquatableArray.From(terms),
        };
        return (channel, true);
    }

    /// <summary>
    /// An explicit route filter. Shared colonist meals are admitted only when every diner accepts them; mixed
    /// colonies keep human ingredients in feed and protected trade output.
    /// </summary>
    public static IReadOnlyList<string> HumanCookingIngredients(FoodSupply supply, IReadOnlyList<FoodConsumer> humans, Fact<FoodPlan> plan, HumanMeatRoute route)
    {
        if (HumanRouteNutrition(plan, route) <= 0)
        {
            return [];
        }
        if (route == HumanMeatRoute.Meals)
        {
            if (humans.Count == 0)
            {
                return [];
            }
            foreach (var c in humans)
            {
                if (!c.HumanMeatAcceptable.TryGetValue(out var accept) || !accept)
                {
                    return [];
                }
            }
        }
        var defs = new HashSet<string>(StringComparer.Ordinal);
        var meat = false;
        var veg = false;
        foreach (var s in supply.Stocks)
        {
            var hk = s.Holder.TryGetValue(out var holder);
            var nk = s.Nutrition.TryGetValue(out var n);
            if (!hk || !holder.IsEmpty || !nk || n <= 0 || s.Corpse || s.Reserve)
            {
                continue;
            }
            if ((s.IsHumanMeat && s.RawMeat) || s.Vegetable)
            {
                defs.Add(s.DefName.Value);
                meat = meat || s.IsHumanMeat;
                veg = veg || s.Vegetable;
            }
        }
        if (!meat || !veg)
        {
            return [];
        }
        var @out = new List<string>(defs.Count);
        foreach (var d in defs)
        {
            if (!FoodId(d))
            {
                return [];
            }
            @out.Add(d);
        }
        @out.Sort(StringComparer.Ordinal);
        return @out;
    }

    public static double HumanRouteNutrition(Fact<FoodPlan> plan, HumanMeatRoute route)
    {
        if (!plan.TryGetValue(out var p))
        {
            return 0;
        }
        var name = Wire.Name(route);
        foreach (var e in p.Portfolio)
        {
            if (e.Channel.Kind == CandidateKind.Corpse && e.Channel.Id == "human-butchery" && e.Decision != FoodPlanDecision.Close)
            {
                foreach (var term in e.Terms)
                {
                    if (term.Name == name)
                    {
                        return term.Value;
                    }
                }
            }
        }
        return 0;
    }

    public static bool HumanFoodPending(Fact<FoodPlan> plan) =>
        HumanRouteNutrition(plan, HumanMeatRoute.Feed)
            + HumanRouteNutrition(plan, HumanMeatRoute.SurvivalTrade)
            + HumanRouteNutrition(plan, HumanMeatRoute.RawTrade)
            + HumanRouteNutrition(plan, HumanMeatRoute.Meals)
        > 0;

    /// <summary>Explicit human-meat/vegetable membership isolates trade output from ordinary meal bills. Native admission verifies every slot and protects sale products.</summary>
    public static (BillSelection Selection, bool Ok) SelectHumanSurvivalBill(Fact<EquatableArray<ProductionBench>> benches, FoodSupply supply, Fact<FoodPlan> plan)
    {
        var none = (new BillSelection(), false);
        var budget = HumanRouteNutrition(plan, HumanMeatRoute.SurvivalTrade);
        if (budget <= 0)
        {
            return none;
        }
        if (!benches.TryGetValue(out var rows))
        {
            return none;
        }
        var defs = new HashSet<string>(StringComparer.Ordinal);
        double raw = 0.0;
        double veg = 0.0;
        foreach (var s in supply.Stocks)
        {
            var hk = s.Holder.TryGetValue(out var holder);
            var nk = s.Nutrition.TryGetValue(out var n);
            if (!hk || !holder.IsEmpty || !nk || s.Corpse || s.Reserve)
            {
                continue;
            }
            if (s.IsHumanMeat && s.RawMeat)
            {
                defs.Add(s.DefName.Value);
                raw += n;
            }
            else if (s.Vegetable)
            {
                defs.Add(s.DefName.Value);
                veg += n;
            }
        }
        budget = Math.Min(budget, Math.Min(raw, veg));
        if (budget <= 0)
        {
            return none;
        }
        var ingredients = new List<string>(defs.Count);
        foreach (var d in defs)
        {
            if (!FoodId(d))
            {
                return none;
            }
            ingredients.Add(d);
        }
        ingredients.Sort(StringComparer.Ordinal);
        var choices = new List<BillSelection>();
        foreach (var b in rows)
        {
            var uk = b.Usable.TryGetValue(out var usable);
            var tk = b.Token.TryGetValue(out var token);
            if (!uk || !usable || !tk || b.Bills.Count >= 15)
            {
                continue;
            }
            foreach (var r in b.Recipes)
            {
                if (!r.Available.ValueOrDefault || r.Products.Count != 1 || !r.Products[0].SurvivalMeal())
                {
                    continue;
                }
                var exists = false;
                foreach (var bill in b.Bills)
                {
                    exists = exists || bill.Recipe == r.Name;
                }
                if (exists)
                {
                    continue;
                }
                var ek = r.NutrientEfficiency.TryGetValue(out var eff);
                var pk = r.Products[0].Nutrition.TryGetValue(out var pn);
                if (!ek || !pk || !FieldPositive(eff) || !FieldPositive(pn))
                {
                    continue;
                }
                var target = Math.Floor(budget * eff / pn);
                if (target < 1 || target > int.MaxValue)
                {
                    continue;
                }
                choices.Add(
                    new BillSelection
                    {
                        Bench = b.Id,
                        Recipe = r.Name,
                        Token = token!,
                        Mode = BillMode.FoodTarget,
                        Target = (int)target,
                        Ingredients = EquatableArray.From(ingredients),
                    }
                );
            }
        }
        if (choices.Count == 0)
        {
            return none;
        }
        return (choices.OrderBy(c => c.Bench, StringComparer.Ordinal).First(), true);
    }
}
