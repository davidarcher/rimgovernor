namespace RimGovernor.Brain.Policy;

/// <summary>Capability is observed; rates and availability must carry their estimation assumptions.</summary>
public sealed record ForecastConstructionWorker(WorkPawn Observed, ForecastPawnCapacity Capacity, Fact<double> WorkPerTick);

/// <summary>One explicit route/service estimate. Delivery includes all pickup/load/trips to the site.</summary>
public sealed record ForecastConstructionEstimate(string Site, string Pawn, string PickupLocation,
    Fact<bool> DeliveryAllowed, Fact<bool> FinishAllowed, Fact<double> DeliveryTicks, Fact<double> RecoveryLaborTicks);

public sealed record ForecastConstructionContext
{
    public Fact<CurrentConstruction> Census { get; init; }
    public ForecastConstructionWorker[] Workers { get; init; } = [];
    public ForecastConstructionEstimate[] Estimates { get; init; } = [];
    public ForecastActivityRoute[] Routes { get; init; } = [];
    public Fact<EquatableArray<IngredientReservation>> IngredientReservations { get; init; }
    public int ExpansionBudget { get; init; } = 512;
}

public sealed record ForecastConstructionResult(string Commitment, string Site, string Reason,
    Fact<double> CompletedDay, ForecastActivityComparison? Comparison)
{
    public EquatableArray<ForecastStockChange> MaterialChanges { get; init; }
}

public static partial class PolicyFunctions
{
    // This is a bounded construction slice of the existing forecast, not an admission or dispatch gate.
    private static ColonyForecastScenario ForecastConstructionScenario(ColonyForecastRequest request,
        Dictionary<string, ForecastCommitment> commitments)
    {
        var context = request.Construction;
        var rows = commitments.Values.Where(c => c.Construction is not null).ToArray();
        if (rows.Length == 0) return ForecastInventory(request.Facts.Stock, commitments, request.HorizonDays);
        if (context.ExpansionBudget is < 1 or > 4096
            || context.Workers.Select(w => w.Capacity.Pawn).Distinct().Count() != context.Workers.Length
            || context.Workers.Any(w => w.Capacity.Pawn != w.Observed.Id.Value)
            || context.Estimates.Select(e => (e.Site, e.Pawn)).Distinct().Count() != context.Estimates.Length
            || rows.Select(c => c.Construction).Distinct().Count() != rows.Length)
            throw new DomainException("invalid construction forecast identities or budget");
        var resolved = new Dictionary<string, ForecastCommitment>(commitments, StringComparer.Ordinal);
        var results = new List<ForecastConstructionResult>();
        var capacity = context.Workers.ToDictionary(w => w.Capacity.Pawn, w => w.Capacity, StringComparer.Ordinal);
        var expansions = 0;
        var complete = context.Census.TryGetValue(out var census) && census.Colony;
        census ??= new CurrentConstruction();
        var sites = census.Sites.Where(s => s.Building is not null).ToDictionary(s => s.Building!);
        // Reuse the material owner, but guard unknown reservations rather than its legacy unknown-as-empty behavior.
        var material = context.IngredientReservations.IsKnown
            ? MaterialBudget(request.Facts.Stock, Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()), context.IngredientReservations, default)
            : Unknown<IReadOnlyDictionary<Resource, long>>();
        var materialKnown = material.TryGetValue(out var free);
        var stock = materialKnown ? new Dictionary<Resource, double>(free!.Select(p => new KeyValuePair<Resource, double>(p.Key, p.Value))) : [];
        // Supplied non-construction consumption is protected for the whole horizon; future deliveries are not spendable yet.
        foreach (var change in commitments.Values.Where(c => c.Construction is null).SelectMany(c => c.StockChanges))
        {
            if (!change.Units.TryGetValue(out var units)) { materialKnown = false; continue; }
            if (units >= 0) continue;
            if (!change.Day.TryGetValue(out var day)) { materialKnown = false; continue; }
            if (day <= request.HorizonDays) stock[change.Resource] = stock.GetValueOrDefault(change.Resource) + units;
        }
        foreach (var commitment in rows.OrderBy(c => sites.TryGetValue(c.Construction!, out var s) && s.Tier.TryGetValue(out var tier) ? (int)tier : -1)
            .ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var site = sites.GetValueOrDefault(commitment.Construction!);
            void Result(string reason, ForecastActivityComparison? comparison = null, Fact<double> day = default) =>
                results.Add(new(commitment.Id, site?.Id ?? "", reason, day, comparison));
            if (!complete) { Result("unknown_construction_census"); continue; }
            if (census.Built(commitment.Construction!) is not null) { Result("already_built", day: Known(0.0)); continue; }
            if (site is null) { Result("construction_gone"); continue; }
            if (!site.WorkLeft.TryGetValue(out var work) || !site.MaterialsOwed.TryGetValue(out var owed)
                || !site.ResourcesComplete.TryGetValue(out var supplied)) { Result("unknown_construction_work_or_materials"); continue; }
            if (!double.IsFinite(work) || work < 0 || owed.Any(a => !ValidResource(a.Resource) || a.Count < 0)
                || owed.Select(a => a.Resource).Distinct().Count() != owed.Count || supplied != owed.All(a => a.Count == 0))
                throw new DomainException("invalid construction work or material facts");
            var needs = owed.Where(a => a.Count > 0).ToArray();
            if (needs.Length != 0)
            {
                if (!materialKnown) { Result("unknown_available_materials"); continue; }
                // Conservatively preserve the native tier rule. Per-pawn reachability exceptions await a native estimate.
                if (site.Tier.TryGetValue(out var tier))
                {
                    var lowerSites = census.Sites.Where(other => other.Id != site.Id
                        && other.Tier.TryGetValue(out var lower) && lower < tier).ToArray();
                    if (lowerSites.Any(other => !other.MaterialsOwed.IsKnown)) { Result("unknown_lower_tier_material_need"); continue; }
                    if (lowerSites.Any(other => other.MaterialsOwed.Value.Any(n => n.Count > 0 && needs.Any(a => a.Resource == n.Resource))))
                    { Result("lower_tier_material_need"); continue; }
                }
                if (needs.Any(n => stock.GetValueOrDefault(n.Resource) < n.Count)) { Result("insufficient_uncommitted_materials"); continue; }
            }
            if (expansions == context.ExpansionBudget) { Result("search_budget_exhausted"); continue; }
            var estimates = context.Estimates.Where(e => e.Site == site.Id).ToArray();
            var pickups = estimates.Where(e => e.DeliveryAllowed.TryGetValue(out var allowed) && allowed).Select(e => e.PickupLocation).Distinct().ToArray();
            if (needs.Length != 0 && pickups.Length != 1) { Result("unknown_or_inconsistent_pickup"); continue; }
            var options = new List<ForecastActivityOption>();
            var eligibilityUnknown = false;
            foreach (var worker in context.Workers)
            {
                var pawn = worker.Observed;
                var estimate = estimates.SingleOrDefault(e => e.Pawn == pawn.Id.Value);
                if (estimate is null) { eligibilityUnknown = true; continue; }
                if (!pawn.Available.TryGetValue(out var available) || !pawn.Applies.TryGetValue(out var applies))
                { eligibilityUnknown = true; continue; }
                if (!available || !applies) continue;
                if (needs.Length != 0)
                {
                    if (!estimate.DeliveryAllowed.TryGetValue(out var delivery)) eligibilityUnknown = true;
                    else if (delivery) options.Add(new("deliver", pawn.Id.Value, estimate.DeliveryTicks, estimate.RecoveryLaborTicks));
                }
                if (!estimate.FinishAllowed.TryGetValue(out var finish) || !pawn.ConstructionAble.TryGetValue(out var able)
                    || !pawn.Skills.TryGetValue(out var skills) || !site.NativeFinishingSkill.TryGetValue(out var native)
                    || !site.QualitySensitive.TryGetValue(out var quality) || (quality && !site.MinimumFinishingSkill.IsKnown))
                { eligibilityUnknown = true; continue; }
                var minimum = Math.Max(native, site.MinimumFinishingSkill.TryGetValue(out var floor) ? floor : 0);
                if (!finish || !able || !skills.Any(s => s.Name == "Construction" && !s.Disabled && s.Level >= minimum)) continue;
                var duration = Unknown<double>();
                if (worker.WorkPerTick.TryGetValue(out var rate))
                {
                    if (!double.IsFinite(rate) || rate < 0) throw new DomainException("invalid construction rate");
                    if (rate == 0) continue;
                    duration = Known(work / rate);
                }
                options.Add(new("finish", pawn.Id.Value, duration, estimate.RecoveryLaborTicks));
            }
            var comparison = CompareActivityPlans(new ForecastActivityProblem
            {
                Activities = needs.Length == 0 ? [new("finish", site.Id, site.Id, [])]
                    : [new("deliver", pickups[0], site.Id, []), new("finish", site.Id, site.Id, ["deliver"])],
                Pawns = capacity.Values.ToArray(), Options = options.ToArray(), Routes = context.Routes,
                Deadline = Known(request.HorizonDays * GameTime.TicksPerDay), AlternativeLaborCost = commitment.AlternativeLaborCost,
                ExpansionBudget = context.ExpansionBudget - expansions,
            });
            expansions += comparison.Expansions;
            if (eligibilityUnknown) comparison = comparison with { Findings = [.. comparison.Findings, "unknown_construction_eligibility"] };
            if (comparison.Selected is not { } plan) { Result(comparison.Decision, comparison); continue; }
            foreach (var group in plan.Allocations.GroupBy(a => a.Pawn))
            {
                var last = group.MaxBy(a => a.End)!;
                var available = capacity[group.Key];
                capacity[group.Key] = available with
                {
                    Windows = Known(EquatableArray.From(available.Windows.Value.Where(w => w.End >= last.End)
                        .Select(w => last.End >= w.Start ? w with { Start = last.End, StartLocation = Known(site.Id) } : w))), Location = site.Id,
                };
            }
            var deliveryRow = plan.Allocations.SingleOrDefault(a => a.Activity == "deliver");
            foreach (var need in needs) stock[need.Resource] -= need.Count;
            var effects = needs.Select(n => new ForecastStockChange(n.Resource,
                Known(deliveryRow!.Arrive / GameTime.TicksPerDay), Known(-(double)n.Count))).ToArray();
            resolved[commitment.Id] = commitment with { StockChanges = EquatableArray.From(effects) };
            Result("scheduled", comparison, Known(plan.CompletedAt / GameTime.TicksPerDay));
            results[^1] = results[^1] with { MaterialChanges = EquatableArray.From(effects) };
        }
        var forecast = ForecastInventory(request.Facts.Stock, resolved, request.HorizonDays);
        return forecast with
        {
            Commitments = EquatableArray.From(commitments.Values.OrderBy(c => c.Id, StringComparer.Ordinal)),
            Construction = EquatableArray.From(results),
        };
    }
}
