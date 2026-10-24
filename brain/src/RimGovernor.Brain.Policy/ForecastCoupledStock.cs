namespace RimGovernor.Brain.Policy;

public static partial class ForecastCoupled
{
    internal sealed partial class CoupledSearchCache
    {
        private readonly Dictionary<string, ForecastTerminalLot> census = new(StringComparer.Ordinal);
        private FoodSupply food = new() { Larder = new() };
        private ItemFacts items = new();
        private FoodConsumer[] otherConsumers = [];
        private double consumedUntil;
        private readonly HashSet<string> outsidePhysical = new(StringComparer.Ordinal);
        private bool physicalCensusComplete;

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
                census[id] = new(id, default, Known(amount), Known(double.PositiveInfinity), Known(true), Known(1d),
                    EquatableArray.From(food.Consumers.Select(c => c.Id)), false);
                lots[(id, "aggregate", default)] = Known(amount); expiry[id] = Known(double.PositiveInfinity); outsidePhysical.Add(id);
                return;
            }
            var needed = -amount;
            foreach (var key in lots.Keys.Where(k => census.TryGetValue(k.Lot, out var row) && row.FoodEligible == Known(true))
                .OrderBy(k => expiry.GetValueOrDefault(k.Lot, Known(double.PositiveInfinity)).IsKnown ? expiry[k.Lot].Value : double.PositiveInfinity)
                .ThenBy(k => k.Lot, StringComparer.Ordinal).ToArray())
            {
                var row = census[key.Lot];
                if (needed <= 0) break;
                if (!lots[key].IsKnown || !row.NutritionPerUnit.IsKnown || row.NutritionPerUnit.Value <= 0) { lots[key] = default; continue; }
                var units = Math.Min(lots[key].Value, needed / row.NutritionPerUnit.Value);
                lots[key] = Known(lots[key].Value - units); needed -= units * row.NutritionPerUnit.Value;
                if (outsidePhysical.Contains(key.Lot)) continue;
                var before = balance.GetValueOrDefault(key.Resource, Known(0d));
                balance[key.Resource] = before.IsKnown ? Known(before.Value - units) : default;
                effects.Add((key.Resource, at / GameTime.TicksPerDay, Known(-units)));
            }
        }

        private void InitializeCensus(ColonyForecastRequest request)
        {
            census.Clear(); outsidePhysical.Clear(); food = request.Facts.Food; items = request.Facts.Items; consumedUntil = 0;
            var physical = request.Facts.ResourceThings.TryGetValue(out var identities) ? identities.ToHashSet(StringComparer.Ordinal) : null;
            physicalCensusComplete = physical is not null;
            otherConsumers = food.Consumers.Where(c => !context.Needs.Pawns.Any(p => p.Pawn == c.Id.Value)).OrderBy(c => c.Id.Value, StringComparer.Ordinal).ToArray();
            foreach (var row in food.Stocks.Concat(food.Barred))
            {
                var count = Known((double)row.Count);
                var fresh = Known(row.Rot is FoodRot.Rots { Ticks: var rot } ? (double)rot : double.PositiveInfinity);
                var nutrition = row.Count > 0 ? Known(row.Nutrition / row.Count) : default;
                var barred = food.Barred.Any(s => s.Id == row.Id);
                census[row.Id] = new(row.Id, row.DefName, count, fresh, Known(!barred && ForecastStockScope.Eligible(row, food)), nutrition,
                    EquatableArray.From(row.Eaters.Where(e => (row.Holder.IsEmpty || row.Holder == e)
                        && (!(row.IsHumanMeat || row.IsHumanlike) || food.Consumers.Any(c => c.Id == e && c.HumanMeatAcceptable)))), false);
                Include(row.Id, row.DefName, count, fresh);
                if (physical is null || !physical.Contains(row.Id)) outsidePhysical.Add(row.Id);
            }
            if (request.Facts.Medicine.Items.TryGetValue(out var medicine))
                foreach (var row in medicine)
                {
                    var fresh = row.Perishable == Known(false) ? Known(double.PositiveInfinity)
                        : row.Perishable == Known(true) && row.RotTicks.IsKnown ? Known((double)row.RotTicks.Value) : default;
                    census[row.Id] = new(row.Id, row.Definition, Known((double)row.Count), fresh, Known(false), Known(0d), [], !row.Forbidden && physical?.Contains(row.Id) == true);
                    Include(row.Id, row.Definition, Known((double)row.Count), fresh);
                    if (physical is null || !physical.Contains(row.Id)) outsidePhysical.Add(row.Id);
                }
            void Include(string id, Resource resource, Fact<double> count, Fact<double> fresh)
            {
                if (!lots.Keys.Any(k => k.Lot == id && k.Resource == resource)) lots.Add((id, "census", resource), count);
                expiry[id] = fresh;
            }
        }

        private void RegisterOutput(string id, Resource resource, bool perishable, Fact<double> freshTicks, double at)
        {
            if (census.ContainsKey(id)) return; // A delivery moves the existing lot and its eligibility.
            var uses = context.FoodUses.Where(u => u.Resource == resource.Value).ToArray();
            var edible = items.Nutrition.TryGetValue(resource, out var nutrition) && nutrition > 0;
            var eaters = uses.Where(u => u.Eligible).Select(u => new PawnId(u.Pawn)).Distinct().ToArray();
            var eligible = !edible ? Known(false) : eaters.Length > 0 ? Known(true) : uses.Length > 0 ? Known(false) : default;
            var fresh = !perishable ? Known(double.PositiveInfinity) : freshTicks.IsKnown ? Known(at + freshTicks.Value) : default;
            census[id] = new(id, resource, Known(0d), fresh, eligible, edible ? Known(nutrition) : Known(0d),
                EquatableArray.From(eaters), items.MedicalPotency.ContainsKey(resource));
            expiry[id] = fresh;
        }

        /// <summary>Unbound debits use the same oldest-first physical lots, never an independent aggregate forecast.</summary>
        private void DebitLots(Resource resource, double units)
        {
            foreach (var key in lots.Keys.Where(k => k.Resource == resource && !outsidePhysical.Contains(k.Lot))
                .OrderBy(k => expiry.GetValueOrDefault(k.Lot, Known(double.PositiveInfinity)).TryGetValue(out var t) ? t : double.PositiveInfinity)
                .ThenBy(k => k.Lot, StringComparer.Ordinal).ThenBy(k => k.Location, StringComparer.Ordinal).ToArray())
            {
                if (units <= 0) break;
                if (!lots[key].TryGetValue(out var count)) continue;
                var debit = Math.Min(units, count);
                lots[key] = Known(count - debit); units -= debit;
            }
        }

        private EquatableArray<ForecastTerminalLot> TerminalLots() => EquatableArray.From(census.Values
            .OrderBy(l => l.Id, StringComparer.Ordinal).Select(l =>
            {
                var rows = lots.Where(p => p.Key.Lot == l.Id && p.Key.Resource == l.Resource).Select(p => p.Value).ToArray();
                return l with { Count = rows.All(c => c.IsKnown) ? Known(rows.Sum(c => c.Value)) : default };
            }));

        /// <summary>Consumers without a pawn timeline draw continuously from their native eligible lots between schedule events.</summary>
        private void ConsumeOthers(double until)
        {
            while (consumedUntil < until)
            {
                var end = expiry.Values.Where(v => v.IsKnown && v.Value > consumedUntil && v.Value < until)
                    .Select(v => v.Value).DefaultIfEmpty(until).Min();
                var span = (end - consumedUntil) / GameTime.TicksPerDay;
                foreach (var consumer in otherConsumers)
                {
                    var rate = consumer.NutritionPerDay;
                    if (!double.IsFinite(rate) || rate < 0)
                    {
                        foreach (var key in lots.Keys.Where(k => census.TryGetValue(k.Lot, out var l) && l.Eaters.Contains(consumer.Id)).ToArray())
                        {
                            lots[key] = default;
                            if (outsidePhysical.Contains(key.Lot)) continue;
                            balance[key.Resource] = default;
                            effects.Add((key.Resource, end / GameTime.TicksPerDay, default));
                        }
                        continue;
                    }
                    var needed = rate * span;
                    foreach (var key in lots.Keys.Where(k => census.TryGetValue(k.Lot, out var l) && l.Eaters.Contains(consumer.Id))
                        .OrderBy(k => expiry.GetValueOrDefault(k.Lot).TryGetValue(out var t) ? t : double.PositiveInfinity)
                        .ThenBy(k => k.Lot, StringComparer.Ordinal).ThenBy(k => k.Location, StringComparer.Ordinal).ToArray())
                    {
                        if (needed <= 0) break;
                        var row = census[key.Lot];
                        if (row.FoodEligible != Known(true) || !row.NutritionPerUnit.IsKnown || row.NutritionPerUnit.Value <= 0
                            || !row.FreshUntil.IsKnown || row.FreshUntil.Value <= consumedUntil || !lots[key].IsKnown) continue;
                        var units = Math.Min(lots[key].Value, needed / row.NutritionPerUnit.Value);
                        lots[key] = Known(lots[key].Value - units); needed -= units * row.NutritionPerUnit.Value;
                        if (!outsidePhysical.Contains(key.Lot))
                        {
                            var before = balance.GetValueOrDefault(key.Resource, Known(0d));
                            balance[key.Resource] = before.IsKnown ? Known(before.Value - units) : default;
                            effects.Add((key.Resource, end / GameTime.TicksPerDay, Known(-units)));
                        }
                    }
                    if (needed > 1e-8) Finding("food_consumer_shortfall", consumer.Id.Value);
                }
                consumedUntil = end;
            }
        }
    }
}
