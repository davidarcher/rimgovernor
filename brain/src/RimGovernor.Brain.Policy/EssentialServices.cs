using System.Collections.Immutable;
namespace RimGovernor.Brain.Policy;

public enum EssentialService { Food, Cooking, Medical, Shelter, Temperature, Power, Materials, Fuel, Logistics }
public enum ServiceCoverage { Unknown, Adequate, Shortfall, NotApplicable }
public enum DevelopmentAlternative { Continue, Defer, AlternateChain, DevelopCapability, RestoreFacility, AcquireResources }
public enum DevelopmentReviewState { Active, Satisfied, Obsolete, Unknown }

/// <summary>A reference to an existing owner predicate, not an independent stock target or skill floor.</summary>
public sealed record ServiceTarget(ConcernId Owner, string Subject, string Predicate, Fact<double> HorizonDays);
public sealed record ServiceAssessment(EssentialService Service, ServiceTarget Target, ServiceCoverage Coverage,
    EquatableArray<string> Beneficiaries, EquatableArray<string> MissingEvidence);
public sealed record ServiceStressAssessment(ServiceTarget Target, string Unavailable, ServiceCoverage Coverage,
    EquatableArray<string> MissingEvidence);
public sealed record DevelopmentProposal(string Id, ServiceTarget Target, Tick Observed,
    EquatableArray<EssentialService> Services, EquatableArray<string> Beneficiaries,
    EquatableArray<string> Evidence, EquatableArray<DevelopmentAlternative> Alternatives)
{
    public EquatableArray<ServiceTarget> Requirements { get; init; }
    public EquatableArray<ServiceStressAssessment> StressEvidence { get; init; }
}
public sealed record EssentialServiceReview(Tick Observed, EquatableArray<ServiceAssessment> Coverage,
    EquatableArray<ServiceStressAssessment> Stress, EquatableArray<DevelopmentProposal> Proposals, bool Truncated);

/// <summary>Existing decoded owners; no duplicate observations or persisted goals.</summary>
public sealed record EssentialServiceInputs
{
    public Fact<EquatableArray<ProductionBench>> Benches { get; init; }
    public Fact<PowerTopology> Power { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Disappearance or a truncated search is not observed satisfaction. No state is retained here.</summary>
    public static DevelopmentReviewState ReassessDevelopment(DevelopmentProposal previous, EssentialServiceReview current)
    {
        if (current.Observed < previous.Observed) return DevelopmentReviewState.Unknown;
        if (current.Proposals.Any(p => p.Id == previous.Id)) return DevelopmentReviewState.Active;
        var requirements = previous.Requirements.Select(t => current.Coverage.FirstOrDefault(c => c.Target.Owner == t.Owner
            && c.Target.Subject == t.Subject && c.Target.Predicate == t.Predicate)).ToArray();
        if (requirements.Any(r => r is null || r.Coverage == ServiceCoverage.Unknown)) return DevelopmentReviewState.Unknown;
        if (requirements.All(r => r!.Coverage == ServiceCoverage.NotApplicable)) return DevelopmentReviewState.Obsolete;
        if (requirements.Any(r => r!.Coverage != ServiceCoverage.Adequate)) return DevelopmentReviewState.Unknown;
        // Portfolio delivery is a projection. Only the owning observed service recovery can retire its goal.
        if (previous.Requirements.Any(t => t.Predicate == "food_plan_gap") && !current.Coverage.Any(c =>
            c.Target.Owner == EnsureFoodSupply && c.Target.Predicate == "owner_observed_recovery" && c.Coverage == ServiceCoverage.Adequate))
            return DevelopmentReviewState.Unknown;
        foreach (var old in previous.StressEvidence)
        {
            var check = current.Stress.FirstOrDefault(s => s.Target.Owner == old.Target.Owner && s.Target.Subject == old.Target.Subject
                && s.Target.Predicate == old.Target.Predicate && s.Unavailable == old.Unavailable);
            if (check?.Coverage != ServiceCoverage.Adequate) return DevelopmentReviewState.Unknown;
        }
        return DevelopmentReviewState.Satisfied;
    }

    /// <summary>Full-review assessment only. Unknown stress evidence never erases baseline evidence.
    /// Proposals reference owner satisfaction predicates; they grant neither practice nor spending.</summary>
    public static EssentialServiceReview ReviewEssentialServices(RoundsFacts facts, RoundsPolicy policy,
        DerivedDemand demand, IReadOnlyList<RoundsAssessment> owners, int scenarioBudget = 128)
    {
        if (scenarioBudget < 0) throw new DomainException("negative service scenario budget");
        if (demand.Work.Tick != facts.Tick) throw new DomainException("service demand and facts must share a tick");
        var rows = new List<ServiceAssessment>();
        var stress = new List<ServiceStressAssessment>();
        var truncated = false;
        var spent = 0;
        var beneficiaries = facts.ForecastObserved.WorkPawns.TryGetValue(out var pawns)
            ? EquatableArray.From(pawns.Select(p => p.Id.Value).Order(StringComparer.Ordinal)) : default;
        var inactive = facts.Colonists.TryGetValue(out var count) && count == 0;
        ServiceTarget Target(ConcernId owner, string subject = "", string predicate = "owner_observed_recovery", Fact<double> horizon = default) =>
            new(owner, subject, predicate, horizon);
        void Owner(EssentialService service, ConcernId owner)
        {
            var assessment = owners.SingleOrDefault(a => a.Id == owner);
            var coverage = inactive ? ServiceCoverage.NotApplicable : assessment?.Finding switch
            { Finding.Met => ServiceCoverage.Adequate, Finding.Unmet => ServiceCoverage.Shortfall, _ => ServiceCoverage.Unknown };
            rows.Add(new(service, Target(owner), coverage, beneficiaries,
                coverage == ServiceCoverage.Unknown ? ["owner_evidence:" + owner.Value] : []));
        }
        void Scenario(ServiceTarget target, string unavailable, Func<(ServiceCoverage Coverage, EquatableArray<string> Missing)> assess)
        {
            if (spent == scenarioBudget) { truncated = true; return; }
            spent++;
            var result = assess();
            stress.Add(new(target, unavailable, result.Coverage, result.Missing));
        }
        Owner(EssentialService.Food, EnsureFoodSupply);
        Owner(EssentialService.Cooking, EnsureCooking);
        Owner(EssentialService.Medical, CriticalMedicine);
        Owner(EssentialService.Medical, MaintainSurgery);
        Owner(EssentialService.Medical, MaintainMedicalReserves);
        Owner(EssentialService.Shelter, MaintainHousing);
        Owner(EssentialService.Shelter, MaintainShelter);
        Owner(EssentialService.Temperature, EnsureTemperatureSafety);
        Owner(EssentialService.Food, MaintainFoodStorage);
        Owner(EssentialService.Food, MaintainRefrigeration);
        Owner(EssentialService.Power, EnsureBasicPower);
        Owner(EssentialService.Logistics, MaintainRoutes);
        Owner(EssentialService.Materials, MaintainResource);

        // The owner already merged overlapping costs. Never add a fuel or service copy to demand.
        var fuel = facts.FuelRunway().Projection;
        rows.Add(new(EssentialService.Fuel, Target(MaintainResource, "", "shared_resource_targets", Known(MaintenanceHorizonDays)),
            facts.Fuel.TryGetValue(out var consumers) && consumers.Count == 0 ? ServiceCoverage.NotApplicable
                : fuel.TryGetValue(out var runway) ? runway.Resources.Any(r => r.MissingItems > 0) ? ServiceCoverage.Shortfall
                    : runway.Gaps.Count > 0 ? ServiceCoverage.Unknown : ServiceCoverage.Adequate : ServiceCoverage.Unknown,
            consumers.Select(c => c.Id).ToImmutableArray(), fuel.IsKnown ? fuel.Value.Gaps.ToImmutableArray() : ["fuel_burn_stock_or_gates"]));

        if (!inactive)
        {
            // Inventory-level food adequacy and alternative channels belong to FoodPlan, not an invented source ranking.
            if (facts.FoodPlan.TryGetValue(out var food))
                rows.Add(new(EssentialService.Food, Target(EnsureFoodSupply, "", "food_plan_gap", Known(food.HorizonDays)),
                    food.DemandPerDay == 0 ? ServiceCoverage.NotApplicable : food.GapPerDay > 0 ? ServiceCoverage.Shortfall : ServiceCoverage.Adequate,
                    food.Forecast.Consumers.Select(c => c.Id.Value).ToImmutableArray(), []));
            else rows.Add(new(EssentialService.Food, Target(EnsureFoodSupply, "", "food_plan_gap"), ServiceCoverage.Unknown, beneficiaries, ["food_plan"]));

            AssessServiceMedical(facts, rows, Scenario);
            AssessServiceCooking(facts, demand, rows, Scenario);
            AssessServiceRoutes(facts, rows, Scenario);
            // Current aggregate owners cannot establish a counterfactual after an unidentified physical dependency disappears.
            foreach (var service in new[] { EssentialService.Food, EssentialService.Shelter, EssentialService.Temperature })
            {
                var target = rows.First(r => r.Service == service).Target;
                Scenario(target, "unmapped_dependency", () => (ServiceCoverage.Unknown,
                    new EquatableArray<string>(["owner_counterfactual_dependencies:" + service])));
            }
            AssessServicePower(facts, rows, Scenario);
        }

        // A physical owner/subject gets one proposal, regardless of how many services use it.
        var deficits = rows.Where(r => r.Coverage == ServiceCoverage.Shortfall).ToList();
        var stressed = new List<ServiceStressAssessment>();
        foreach (var check in stress.Where(s => s.Coverage == ServiceCoverage.Shortfall))
        {
            var source = rows.FirstOrDefault(r => r.Target == check.Target);
            if (source is not { Coverage: ServiceCoverage.Adequate }) continue;
            deficits.Add(source);
            stressed.Add(check);
        }
        var proposals = deficits.GroupBy(r => (r.Target.Owner, r.Target.Subject))
            .OrderBy(g => g.Key.Owner.Value, StringComparer.Ordinal).ThenBy(g => g.Key.Subject, StringComparer.Ordinal)
            .Select(g => new DevelopmentProposal(g.Key.Owner.Value + "/" + g.Key.Subject, g.First().Target, facts.Tick,
                g.Select(r => r.Service).Distinct().Order().ToImmutableArray(),
                g.SelectMany(r => r.Beneficiaries).Distinct().Order(StringComparer.Ordinal).ToImmutableArray(),
                g.Select(r => (r.Coverage == ServiceCoverage.Shortfall ? "observed_shortfall:" : "stress_shortfall:") + r.Target.Predicate)
                    .Distinct().Order(StringComparer.Ordinal).ToImmutableArray(),
                [DevelopmentAlternative.Continue, DevelopmentAlternative.Defer, DevelopmentAlternative.AlternateChain,
                    DevelopmentAlternative.DevelopCapability, DevelopmentAlternative.RestoreFacility, DevelopmentAlternative.AcquireResources])
                { Requirements = g.Select(r => r.Target).Distinct().ToImmutableArray(),
                  StressEvidence = stressed.Where(s => s.Target.Owner == g.Key.Owner && s.Target.Subject == g.Key.Subject).ToImmutableArray() })
            .ToImmutableArray();
        return new(facts.Tick, rows.ToImmutableArray(), stress.ToImmutableArray(), proposals, truncated);
    }

    private static void AssessServiceCooking(RoundsFacts facts, DerivedDemand demand, List<ServiceAssessment> rows,
        Action<ServiceTarget, string, Func<(ServiceCoverage, EquatableArray<string>)>> scenario)
    {
        var target = new ServiceTarget(EnsureCooking, "recipe-capability", "recipe_eligibility_and_inputs", default);
        if (!facts.FoodPlan.IsKnown || !FoodPlanSupport(facts.FoodPlan, CandidateKind.Cook, "cooking-capacity"))
        {
            rows.Add(new(EssentialService.Cooking, target, facts.FoodPlan.IsKnown ? ServiceCoverage.NotApplicable : ServiceCoverage.Unknown,
                [], facts.FoodPlan.IsKnown ? [] : ["food_plan_cooking_dependency"]));
            return;
        }
        if (!facts.EssentialInputs.Benches.TryGetValue(out var benches) || !facts.ForecastObserved.WorkPawns.TryGetValue(out var workers))
        {
            rows.Add(new(EssentialService.Cooking, target, ServiceCoverage.Unknown, [], ["production_benches_or_work_pawns"]));
            return;
        }
        // This is feasibility of one useful meal iteration, not a throughput or future-food claim.
        (ServiceCoverage, EquatableArray<string>) Assess(string excluded)
        {
            var unknown = false;
            foreach (var bench in benches.Where(b => "facility/" + b.Id != excluded).OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                if (!bench.Usable.IsKnown) { unknown = true; continue; }
                if (!bench.Usable.Value) continue;
                foreach (var recipe in bench.Recipes.Where(r => r.Role == RecipeRole.OrdinaryMeal).OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    if (!recipe.Available.IsKnown || !recipe.CookSkillFloor.IsKnown) { unknown = true; continue; }
                    if (!recipe.Available.Value) continue;
                    var capable = false;
                    foreach (var worker in workers.Where(w => "pawn/" + w.Id.Value != excluded))
                    {
                        if (!worker.Available.IsKnown || !worker.Applies.IsKnown || !worker.Work.IsKnown || !worker.Skills.IsKnown)
                        { unknown = true; continue; }
                        if (!worker.Available.Value || !worker.Applies.Value || facts.WorkClaims?.Pawns.Contains(worker.Id.Value) == true) continue;
                        var work = worker.Work.Value.FirstOrDefault(w => w.Work == WorkType.Cooking);
                        if (work is null || work.Disabled || work.Priority <= 0) continue;
                        var skill = worker.Skills.Value.FirstOrDefault(s => s.Name == work.Skill);
                        if (skill is null) { unknown = true; continue; }
                        if (!skill.Disabled && skill.Level >= recipe.CookSkillFloor.Value) capable = true;
                    }
                    if (!capable) continue;
                    if (facts.WorkClaims is null || facts.WorkClaims.Truncated) { unknown = true; continue; }
                    // Reuse the existing fixed-batch material feasibility owner, including alternatives within slots.
                    var material = SelectLedgerMaterials([new(bench.Id, new OrderSpec { Recipe = recipe.Name, Mode = BillMode.GearBatch, Target = 1 })],
                        new(facts.WorkAdmission.Recipes, demand.MaterialAvailability));
                    if (material.Selected.Count > 0) return excluded.Length == 0
                        ? (ServiceCoverage.Adequate, [])
                        : (ServiceCoverage.Unknown, ["replacement_throughput_not_established_by_recipe_eligibility"]);
                    if (material.GenerationTruncated || material.Comparison.SearchTruncated || material.Comparison.Findings.Count > 0
                        || !demand.MaterialAvailability.IsKnown) unknown = true;
                }
            }
            return unknown ? (ServiceCoverage.Unknown, ["recipe_skill_material_or_worker_evidence"]) : (ServiceCoverage.Shortfall, []);
        }
        var baseline = Assess("");
        rows.Add(new(EssentialService.Cooking, target, baseline.Item1, workers.Select(w => w.Id.Value).ToImmutableArray(), baseline.Item2));
        if (baseline.Item1 != ServiceCoverage.Adequate) return;
        foreach (var dependency in workers.Select(w => "pawn/" + w.Id.Value)
            .Concat(benches.Where(b => b.Recipes.Any(r => r.Role == RecipeRole.OrdinaryMeal)).Select(b => "facility/" + b.Id))
            .Order(StringComparer.Ordinal)) scenario(target, dependency, () => Assess(dependency));
    }

    private static void AssessServiceMedical(RoundsFacts facts, List<ServiceAssessment> rows,
        Action<ServiceTarget, string, Func<(ServiceCoverage, EquatableArray<string>)>> scenario)
    {
        if (!facts.MedicalPawns.TryGetValue(out var patients)) return;
        foreach (var patient in patients.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            if (patient.QuestProtected || !patient.Dead.TryGetValue(out var dead) || dead || !patient.Operations.TryGetValue(out var operations)) continue;
            foreach (var group in operations.Where(o => ServedSurgery(patient, o) is { Known: true, Weight: > 0 })
                .GroupBy(o => o.PartIndex.IsKnown ? o.PartIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "whole")
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var target = new ServiceTarget(MaintainSurgery, patient.Id.Value + "/" + group.Key, "served_procedure_feasible", default);
                var options = group.ToArray();
                (ServiceCoverage, EquatableArray<string>) Assess(string excluded)
                {
                    var unknown = false;
                    foreach (var operation in options)
                    {
                        if (operation.IngredientsOnMap.IsKnown && !operation.IngredientsOnMap.Value) continue;
                        if (!operation.IngredientsOnMap.IsKnown || !operation.Violation.IsKnown || !operation.Lethal.IsKnown)
                        { unknown = true; continue; }
                        var current = operation;
                        if (excluded.Length != 0)
                        {
                            if (operation.DoctorChances is null) { unknown = true; continue; }
                            var chances = operation.DoctorChances.Where(d => d.Key.Value != excluded).Select(d => d.Value).ToArray();
                            current = operation with { EligibleDoctors = Known(chances.Length), SuccessChance = chances.Length > 0 ? Known(chances.Max()) : default };
                        }
                        if (SurgeryAcceptable(current, RestoreFailureCap)) return (ServiceCoverage.Adequate, []);
                        if (!current.EligibleDoctors.IsKnown || current.EligibleDoctors.Value > 0 && !current.SuccessChance.IsKnown) unknown = true;
                    }
                    return unknown ? (ServiceCoverage.Unknown, ["procedure_eligibility_ingredients_or_doctor_chances"]) : (ServiceCoverage.Shortfall, []);
                }
                var baseline = Assess("");
                rows.Add(new(EssentialService.Medical, target, baseline.Item1, [patient.Id.Value], baseline.Item2));
                foreach (var doctor in options.Where(o => o.DoctorChances is not null).SelectMany(o => o.DoctorChances!.Keys)
                    .Distinct().OrderBy(p => p.Value, StringComparer.Ordinal))
                    scenario(target, "pawn/" + doctor.Value, () => Assess(doctor.Value));
                if (options.Any(o => o.DoctorChances is null))
                    scenario(target, "unmapped_doctor", () => (ServiceCoverage.Unknown, ["procedure_doctor_identities"]));
            }
        }
    }

    private static void AssessServiceRoutes(RoundsFacts facts, List<ServiceAssessment> rows,
        Action<ServiceTarget, string, Func<(ServiceCoverage, EquatableArray<string>)>> scenario)
    {
        if (!facts.Upkeep.Routes.TryGetValue(out var routes)) return;
        foreach (var facility in routes.Facilities.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            var target = new ServiceTarget(MaintainRoutes, facility.Id, "observed_facility_reachable", default);
            (ServiceCoverage, EquatableArray<string>) Assess(string excluded)
            {
                var remaining = routes.Pawns.Where(p => p != excluded).ToArray();
                if (remaining.Any(p => facility.Travel.Any(t => t.Pawn == p && t.Reachable))) return (ServiceCoverage.Adequate, []);
                if (routes.Pawns.Count == 0 || remaining.Any(p => !facility.Travel.Any(t => t.Pawn == p)))
                    return (ServiceCoverage.Unknown, ["facility_reachability"]);
                return (ServiceCoverage.Shortfall, []);
            }
            var baseline = Assess("");
            rows.Add(new(EssentialService.Logistics, target, baseline.Item1, [facility.Id], baseline.Item2));
            foreach (var pawn in facility.Travel.Where(t => t.Reachable).Select(t => t.Pawn).Distinct().Order(StringComparer.Ordinal))
                scenario(target, "pawn/" + pawn, () => Assess(pawn));
        }
    }

    private static void AssessServicePower(RoundsFacts facts, List<ServiceAssessment> rows,
        Action<ServiceTarget, string, Func<(ServiceCoverage, EquatableArray<string>)>> scenario)
    {
        if (facts.PowerRequired.IsKnown && !facts.PowerRequired.Value) return;
        if (!facts.EssentialInputs.Power.TryGetValue(out var power))
        {
            scenario(new(EnsureBasicPower, "", "owner_power_coverage", default), "unmapped_facility",
                () => (ServiceCoverage.Unknown, ["power_topology"]));
            return;
        }
        foreach (var generator in power.Buildings.Where(b => b.BaseW.IsKnown && b.BaseW.Value > 0).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            scenario(new(EnsureBasicPower, "", "owner_power_coverage", default), "facility/" + generator.Id, () =>
            {
                var coverage = PowerCoverage(Known<EquatableArray<PowerBuilding>>(power.Buildings.Where(b => b.Id != generator.Id).Cast<PowerBuilding>().ToImmutableArray()));
                if (!coverage.Headroom.IsKnown) return (ServiceCoverage.Unknown, ["power_network_output"]);
                return (coverage.Headroom.Value >= 0 ? ServiceCoverage.Adequate : ServiceCoverage.Shortfall, []);
            });
        }
        // Network service membership is not inferred from the sign of watts: decorative consumers are not essential services.
        scenario(new(EnsureBasicPower, "", "essential_consumer_membership", default), "unmapped_service",
            () => (ServiceCoverage.Unknown, ["power_consumer_service_membership"]));
    }
}

