namespace RimGovernor.Brain.Policy;

public static partial class ForecastCoupled
{
    internal sealed partial class CoupledSearchCache
    {
        private readonly Dictionary<string, ForecastTerminalLot> census = new(StringComparer.Ordinal);
        private FoodSupply food = new() { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } };
        private ItemFacts items = new();
        private FoodConsumer[] otherConsumers = [];
        private double consumedUntil;
        private readonly HashSet<string> outsidePhysical = new(StringComparer.Ordinal);

        /// <summary>A scheduled effect's units: its amount, or a named resource's nutrition over that resource's nutrition per
        /// unit, which the item facts carry for every edible def the model schedules.</summary>
        private double ScheduledUnits(ScheduledEffect effect) => effect.Stock != ModelStock.Nutrition || string.IsNullOrEmpty(effect.Resource.Value) ? effect.Amount
            : items.Nutrition.TryGetValue(effect.Resource, out var perUnit) && perUnit > 0 ? effect.Amount / perUnit
            : throw new RequiredFactException("nutrition_per_unit:" + effect.Resource);

        private void ApplyNutrition(string id, double amount, double at)
        {
            if (amount >= 0)
            {
                // A model-only aggregate input has no physical item identity and cannot finance a bound pawn activity.
                census[id] = new(id, default, amount, double.PositiveInfinity, FoodEligibility.Eligible, 1d,
                    EquatableArray.From(food.Consumers.Select(c => c.Id)), false);
                lots[(id, "aggregate", default)] = amount; expiry[id] = double.PositiveInfinity; outsidePhysical.Add(id);
                return;
            }
            var needed = -amount;
            foreach (var key in lots.Keys.Where(k => census.TryGetValue(k.Lot, out var row) && row.FoodEligible is FoodEligibility.Eligible)
                .OrderBy(k => expiry.GetValueOrDefault(k.Lot, double.PositiveInfinity))
                .ThenBy(k => k.Lot, StringComparer.Ordinal).ToArray())
            {
                var row = census[key.Lot];
                if (needed <= 0) break;
                if (row.NutritionPerUnit <= 0) continue;
                var units = Math.Min(lots[key], needed / row.NutritionPerUnit);
                lots[key] -= units; needed -= units * row.NutritionPerUnit;
                if (outsidePhysical.Contains(key.Lot)) continue;
                balance[key.Resource] = balance.GetValueOrDefault(key.Resource) - units;
                effects.Add((key.Resource, at / GameTime.TicksPerDay, -units));
            }
        }

        private void InitializeCensus(ColonyForecastRequest request)
        {
            census.Clear(); outsidePhysical.Clear(); food = request.Facts.Food; items = request.Facts.Items; consumedUntil = 0;
            var physical = request.Facts.Census.ResourceThings.ToHashSet(StringComparer.Ordinal);
            // A consumer whose nutrition rate is not a finite non-negative number cannot be drawn against; the outcome is refused.
            foreach (var consumer in food.Consumers)
                if (!double.IsFinite(consumer.NutritionPerDay) || consumer.NutritionPerDay < 0) Refuse("food_consumer_rate_unread", consumer.Id.Value);
            otherConsumers = food.Consumers.Where(c => double.IsFinite(c.NutritionPerDay) && c.NutritionPerDay >= 0
                && !context.Needs.Pawns.Any(p => p.Pawn == c.Id.Value)).OrderBy(c => c.Id.Value, StringComparer.Ordinal).ToArray();
            foreach (var row in food.Stocks.Concat(food.Barred))
            {
                double count = row.Count;
                var fresh = row.Rot is FoodRot.Rots { Ticks: var rot } ? (double)rot : double.PositiveInfinity;
                var nutrition = row.Count > 0 ? row.Nutrition / row.Count : 0d;
                var barred = food.Barred.Any(s => s.Id == row.Id);
                census[row.Id] = new(row.Id, new Resource(row.DefName), count, fresh,
                    !barred && ForecastStockScope.Eligible(row, food) ? FoodEligibility.Eligible : FoodEligibility.Ineligible, nutrition,
                    EquatableArray.From(row.Eaters.Where(e => (row.Holder.IsEmpty || row.Holder == e)
                        && (!(row.IsHumanMeat || row.IsHumanlike) || food.Consumers.Any(c => c.Id == e && c.HumanMeatAcceptable)))), false);
                Include(row.Id, new Resource(row.DefName), count, fresh);
                if (!physical.Contains(row.Id)) outsidePhysical.Add(row.Id);
            }
            foreach (var row in request.Facts.Medicine.Items)
                {
                    double fresh;
                    if (row.Rot is RimGovernor.Contracts.Wire.Observations.ItemRot.Never) fresh = double.PositiveInfinity;
                    else if (row.Rot is RimGovernor.Contracts.Wire.Observations.ItemRot.Rotting rot) fresh = rot.Ticks;
                    else throw new DomainException("invalid medicine rot state");
                    census[row.Id] = new(row.Id, new Resource(row.Definition), row.Count, fresh, FoodEligibility.Ineligible, 0d, [], !row.Forbidden && physical?.Contains(row.Id) == true);
                    Include(row.Id, new Resource(row.Definition), row.Count, fresh);
                    if (physical is null || !physical.Contains(row.Id)) outsidePhysical.Add(row.Id);
                }
            void Include(string id, Resource resource, double count, double fresh)
            {
                if (!lots.Keys.Any(k => k.Lot == id && k.Resource == resource)) lots.Add((id, "census", resource), count);
                expiry[id] = fresh;
            }
        }

        private void RegisterOutput(string id, Resource resource, bool perishable, double freshTicks, double at)
        {
            if (census.ContainsKey(id)) return; // A delivery moves the existing lot and its eligibility.
            var uses = context.FoodUses.Where(u => u.Resource == resource.Value).ToArray();
            var edible = items.Nutrition.TryGetValue(resource, out var nutrition) && nutrition > 0;
            var eaters = uses.Where(u => u.Eligible).Select(u => new PawnId(u.Pawn)).Distinct().ToArray();
            var eligible = !edible ? FoodEligibility.Ineligible : eaters.Length > 0 ? FoodEligibility.Eligible
                : uses.Length > 0 ? FoodEligibility.Ineligible : FoodEligibility.NoObservedUse;
            var fresh = perishable ? at + freshTicks : double.PositiveInfinity;
            census[id] = new(id, resource, 0d, fresh, eligible, edible ? nutrition : 0d,
                EquatableArray.From(eaters), items.MedicalPotency.ContainsKey(resource));
            expiry[id] = fresh;
        }

        /// <summary>Unbound debits use the same oldest-first physical lots, never an independent aggregate forecast.</summary>
        private void DebitLots(Resource resource, double units)
        {
            foreach (var key in lots.Keys.Where(k => k.Resource == resource && !outsidePhysical.Contains(k.Lot))
                .OrderBy(k => expiry.GetValueOrDefault(k.Lot, double.PositiveInfinity))
                .ThenBy(k => k.Lot, StringComparer.Ordinal).ThenBy(k => k.Location, StringComparer.Ordinal).ToArray())
            {
                if (units <= 0) break;
                var debit = Math.Min(units, lots[key]);
                lots[key] -= debit; units -= debit;
            }
        }

        private EquatableArray<ForecastTerminalLot> TerminalLots() => EquatableArray.From(census.Values
            .OrderBy(l => l.Id, StringComparer.Ordinal).Select(l =>
                l with { Count = lots.Where(p => p.Key.Lot == l.Id && p.Key.Resource == l.Resource).Sum(p => p.Value) }));

        /// <summary>Consumers without a pawn timeline draw continuously from their native eligible lots between schedule events.</summary>
        private void ConsumeOthers(double until)
        {
            while (consumedUntil < until)
            {
                var end = expiry.Values.Where(v => v > consumedUntil && v < until).DefaultIfEmpty(until).Min();
                var span = (end - consumedUntil) / GameTime.TicksPerDay;
                foreach (var consumer in otherConsumers)
                {
                    var needed = consumer.NutritionPerDay * span;
                    foreach (var key in lots.Keys.Where(k => census.TryGetValue(k.Lot, out var l) && l.Eaters.Contains(consumer.Id))
                        .OrderBy(k => expiry.GetValueOrDefault(k.Lot, double.PositiveInfinity))
                        .ThenBy(k => k.Lot, StringComparer.Ordinal).ThenBy(k => k.Location, StringComparer.Ordinal).ToArray())
                    {
                        if (needed <= 0) break;
                        var row = census[key.Lot];
                        if (row.FoodEligible is not FoodEligibility.Eligible || row.NutritionPerUnit <= 0 || row.FreshUntil <= consumedUntil) continue;
                        var units = Math.Min(lots[key], needed / row.NutritionPerUnit);
                        lots[key] -= units; needed -= units * row.NutritionPerUnit;
                        if (!outsidePhysical.Contains(key.Lot))
                        {
                            balance[key.Resource] = balance.GetValueOrDefault(key.Resource) - units;
                            effects.Add((key.Resource, end / GameTime.TicksPerDay, -units));
                        }
                    }
                    if (needed > 1e-8) Finding("food_consumer_shortfall", consumer.Id.Value);
                }
                consumedUntil = end;
            }
        }
    }
}
