using System.Collections.Immutable;
namespace RimGovernor.Brain.Policy;

public enum EssentialService { Food, Cooking, Medical, Shelter, Temperature, Power, Materials, Fuel, Logistics }
public enum ServiceCoverage { EvidenceGap, Adequate, Shortfall, NotApplicable }
public enum DevelopmentAlternative { Continue, Defer, AlternateChain, DevelopCapability, RestoreFacility, AcquireResources }
public enum DevelopmentReviewState { Active, Satisfied, Obsolete, RetirementUnproven }

/// <summary>
/// A reference to an existing owner predicate, not an independent stock target or skill floor. The owner is a
/// NeedId under its <see cref="NeedId.FilingKey"/> (#3250).
/// </summary>
public sealed record ServiceTarget(ConcernId Owner, string Subject, string Predicate)
{
    /// <summary>The target of a Need's own predicate.</summary>
    public static ServiceTarget ForNeed(NeedId need, string subject = "", string predicate = "need_satisfied")
    {
        need.Validate();
        return new(need.FilingKey, subject, predicate);
    }

    /// <summary>The NeedId that owns the target; default for a Concern owner.</summary>
    public NeedId OwnerNeed => Owner.Value.Contains('/', StringComparison.Ordinal) ? new NeedId(Owner.Value) : default;
}
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
    public EquatableArray<ProductionBench> Benches { get; init; }
    public PowerTopology Power { get; init; } = new();
}

public static class EssentialServicesRules
{
    /// <summary>
    /// A Need owner's service coverage (#3250): met is adequate, unmet a shortfall. A family slice that moves a
    /// service row onto its Need reads coverage from here, so the move preserves the measured row.
    /// </summary>
    public static ServiceCoverage NeedCoverage(Need need) => need.Satisfaction == Satisfaction.Met ? ServiceCoverage.Adequate : ServiceCoverage.Shortfall;

    /// <summary>Disappearance or a truncated search is not observed satisfaction. No state is retained here.</summary>
    public static DevelopmentReviewState ReassessDevelopment(DevelopmentProposal previous, EssentialServiceReview current)
    {
        if (current.Observed < previous.Observed) return DevelopmentReviewState.RetirementUnproven;
        if (current.Proposals.Any(p => p.Id == previous.Id)) return DevelopmentReviewState.Active;
        var requirements = previous.Requirements.Select(t => current.Coverage.FirstOrDefault(c => c.Target.Owner == t.Owner
            && c.Target.Subject == t.Subject && c.Target.Predicate == t.Predicate)).ToArray();
        if (requirements.Any(r => r is null || r.Coverage == ServiceCoverage.EvidenceGap)) return DevelopmentReviewState.RetirementUnproven;
        if (requirements.All(r => r!.Coverage == ServiceCoverage.NotApplicable)) return DevelopmentReviewState.Obsolete;
        if (requirements.Any(r => r!.Coverage != ServiceCoverage.Adequate)) return DevelopmentReviewState.RetirementUnproven;
        // Portfolio delivery is a projection. Only the owning observed service recovery can retire its goal.
        if (previous.Requirements.Any(t => t.Predicate == "food_plan_gap") && !current.Coverage.Any(c =>
            c.Target.Owner == FoodRules.FoodSupplyNeed.FilingKey && c.Target.Predicate == "need_satisfied" && c.Coverage == ServiceCoverage.Adequate))
            return DevelopmentReviewState.RetirementUnproven;
        foreach (var old in previous.StressEvidence)
        {
            var check = current.Stress.FirstOrDefault(s => s.Target.Owner == old.Target.Owner && s.Target.Subject == old.Target.Subject
                && s.Target.Predicate == old.Target.Predicate && s.Unavailable == old.Unavailable);
            if (check?.Coverage != ServiceCoverage.Adequate) return DevelopmentReviewState.RetirementUnproven;
        }
        return DevelopmentReviewState.Satisfied;
    }

    /// <summary>Full-review assessment only. A counterfactual evidence gap never erases baseline evidence.
    /// Proposals reference owner satisfaction predicates; they grant neither practice nor spending.</summary>
    public static EssentialServiceReview ReviewEssentialServices(RoundsFacts facts, RoundsPolicy policy,
        DerivedDemand demand, int scenarioBudget = 128, bool medicineActive = false)
    {
        if (scenarioBudget < 0) throw new DomainException("negative service scenario budget");
        if (demand.Work.Tick != facts.Tick) throw new DomainException("service demand and facts must share a tick");
        var rows = new List<ServiceAssessment>();
        var stress = new List<ServiceStressAssessment>();
        var truncated = false;
        var spent = 0;
        var beneficiaries = EquatableArray.From(facts.ForecastObserved.WorkPawns.Pawns.Select(p => p.Id.Value).Order(StringComparer.Ordinal));
        var inactive = facts.Colonists == 0;
        // An owner whose Need raises on an unread fact is an explicit evidence gap, never adequate,
        // with the owner named as the missing evidence. The review itself still commits.
        void NeedOwner(EssentialService service, NeedId id, Func<Need> need)
        {
            var target = ServiceTarget.ForNeed(id);
            ServiceCoverage coverage;
            try
            {
                coverage = inactive ? ServiceCoverage.NotApplicable : NeedCoverage(need());
            }
            catch (RequiredFactException)
            {
                coverage = ServiceCoverage.EvidenceGap;
            }
            rows.Add(new(service, target, coverage, beneficiaries,
                coverage == ServiceCoverage.EvidenceGap ? ["owner_evidence:" + target.Owner.Value] : []));
        }
        void Scenario(ServiceTarget target, string unavailable, Func<(ServiceCoverage Coverage, EquatableArray<string> Missing)> assess)
        {
            if (spent == scenarioBudget) { truncated = true; return; }
            spent++;
            var result = assess();
            stress.Add(new(target, unavailable, result.Coverage, result.Missing));
        }
        var food = FoodFacts.From(facts, policy);
        NeedOwner(EssentialService.Food, FoodRules.FoodSupplyNeed, () => FoodRules.FoodSupplyNeedOf(food));
        NeedOwner(EssentialService.Cooking, FoodRules.CookingNeed, () => FoodRules.CookingNeedOf(food));
        // The medical family's Needs (#3258): the critical census as one row (the review counts the patients, it
        // does not name them), then surgery and reserves; medicineActive is the review's medicine latch.
        var medical = MedicalFacts.From(facts, policy, medicineActive);
        NeedOwner(EssentialService.Medical, MedicalRules.CriticalCensusNeed, () => MedicalRules.CriticalCensusNeedOf(facts.CriticalPatients));
        NeedOwner(EssentialService.Medical, MedicalRules.SurgeryNeed, () => MedicalRules.SurgeryNeedOf(medical));
        NeedOwner(EssentialService.Medical, MedicalRules.MedicalReservesNeed, () => MedicalRules.MedicalReservesNeedOf(medical));
        var shelter = ShelterFacts.From(facts, policy);
        NeedOwner(EssentialService.Shelter, ShelterFamily.Sleeping, () => ShelterRules.SleepingNeedOf(shelter));
        NeedOwner(EssentialService.Shelter, ShelterFamily.SafeArea, () => ShelterRules.SafeAreaNeedOf(shelter));
        NeedOwner(EssentialService.Temperature, ShelterFamily.Temperature, () => ShelterRules.TemperatureNeedOf(shelter));
        NeedOwner(EssentialService.Food, FoodStoresRules.FoodStorageNeed, () => FoodStoresRules.FoodStorageNeedOf(FoodStoresFacts.From(facts), policy.FoodStorage));
        NeedOwner(EssentialService.Food, FoodStoresRules.RefrigerationNeed, () => FoodStoresRules.RefrigerationNeedOf(FoodStoresFacts.From(facts), policy.FoodStorage));
        NeedOwner(EssentialService.Power, PowerFamily.Basic, () => PowerRules.PowerNeedOf(PowerFacts.From(facts)));
        NeedOwner(EssentialService.Logistics, LayoutFamily.Routes, () => LayoutFamily.RoutesNeed(LayoutFamily.RoutesStandingOf(facts.Upkeep.Routes ?? throw new RequiredFactException("routes census"), facts.Upkeep.Rooms, policy)));
        NeedOwner(EssentialService.Materials, EconomyFamily.Resource, () => EconomyRules.ResourceNeedOf(ResourceFacts.From(facts, demand)));

        // The owner already merged overlapping costs. Never add a fuel or service copy to demand.
        var fuel = facts.FuelRunway();
        static (ServiceCoverage Coverage, ImmutableArray<string> Gaps) FuelCoverage(FuelRunway fuel)
        {
            switch (fuel.Projection)
            {
                case FuelProjection.Projected runway:
                    return (fuel.Needs.Count > 0 ? ServiceCoverage.Shortfall
                        : runway.Gaps.Count > 0 ? ServiceCoverage.EvidenceGap : ServiceCoverage.Adequate, runway.Gaps.ToImmutableArray());
                case FuelProjection.RainDependent weather:
                    return (ServiceCoverage.EvidenceGap, weather.Consumers.ToImmutableArray());
            }
            throw new DomainException("undefined fuel projection");
        }
        var (fuelCoverage, fuelGaps) = FuelCoverage(fuel);
        rows.Add(new(EssentialService.Fuel, ServiceTarget.ForNeed(EconomyFamily.Resource, "", "shared_resource_targets"),
            facts.Fuel.Count == 0 ? ServiceCoverage.NotApplicable : fuelCoverage,
            facts.Fuel.Select(c => c.Id).ToImmutableArray(), fuelGaps));

        if (!inactive)
        {
            // Inventory-level food adequacy and alternative channels belong to FoodPlan, not an invented source ranking.
            if (facts.FoodPlan is { } plan)
                rows.Add(new(EssentialService.Food, ServiceTarget.ForNeed(FoodRules.FoodSupplyNeed, "", "food_plan_gap"),
                    plan.DemandPerDay == 0 ? ServiceCoverage.NotApplicable : plan.GapPerDay > 0 ? ServiceCoverage.Shortfall : ServiceCoverage.Adequate,
                    plan.Forecast.Consumers.Select(c => c.Id.Value).ToImmutableArray(), []));
            else rows.Add(new(EssentialService.Food, ServiceTarget.ForNeed(FoodRules.FoodSupplyNeed, "", "food_plan_gap"), ServiceCoverage.EvidenceGap, beneficiaries, ["food_plan"]));

            AssessServiceMedical(facts, rows, Scenario);
            AssessServiceCooking(facts, demand, rows, Scenario);
            AssessServiceRoutes(facts, rows, Scenario);
            // Current aggregate owners cannot establish a counterfactual after an unidentified physical dependency disappears.
            foreach (var service in new[] { EssentialService.Food, EssentialService.Shelter, EssentialService.Temperature })
            {
                var target = rows.First(r => r.Service == service).Target;
                Scenario(target, "unmapped_dependency", () => (ServiceCoverage.EvidenceGap,
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
        var target = ServiceTarget.ForNeed(FoodRules.CookingNeed, "recipe-capability", "recipe_eligibility_and_inputs");
        if (facts.FoodPlan is not { } plan || !FoodOrdersRules.FoodPlanSupport(plan, CandidateKind.Cook, "cooking-capacity"))
        {
            rows.Add(new(EssentialService.Cooking, target, facts.FoodPlan is not null ? ServiceCoverage.NotApplicable : ServiceCoverage.EvidenceGap,
                [], facts.FoodPlan is not null ? [] : ["food_plan_cooking_dependency"]));
            return;
        }
        var benches = facts.EssentialInputs.Benches;
        var workers = facts.ForecastObserved.WorkPawns.Pawns;
        // This is feasibility of one useful meal iteration, not a throughput or future-food claim.
        (ServiceCoverage, EquatableArray<string>) Assess(string excluded)
        {
            var unknown = false;
            foreach (var bench in benches.Where(b => "facility/" + b.Id != excluded).OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                if (!bench.Usable) continue;
                foreach (var recipe in bench.Recipes.Where(r => r.Role == RecipeRole.OrdinaryMeal).OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    if (!recipe.Available || recipe.Food is not RecipeFood.Food { Cooking: RecipeCooking.FromRaw { CookSkillFloor: var floor } }) continue;
                    var capable = false;
                    foreach (var worker in workers.Where(w => "pawn/" + w.Id.Value != excluded))
                    {
                        if (!worker.Available || worker.Work is not PawnWorkTable.Works { Rows: var rows } || facts.WorkClaims?.Pawns.Contains(worker.Id.Value) == true) continue;
                        var work = rows.FirstOrDefault(w => w.Work == WorkType.Cooking);
                        if (work is null || work.Disabled || work.Priority <= 0) continue;
                        var skill = worker.Skills.FirstOrDefault(s => s.Name == work.Skill);
                        if (skill is null) { unknown = true; continue; }
                        if (!skill.Disabled && skill.Level >= floor) capable = true;
                    }
                    if (!capable) continue;
                    if (facts.WorkClaims is null || facts.WorkClaims.Truncated) { unknown = true; continue; }
                    // Reuse the existing fixed-batch material feasibility owner, including alternatives within slots.
                    var material = LedgerMaterialChoicesRules.SelectLedgerMaterials([new(bench.Id, new OrderSpec { Recipe = recipe.Name, Mode = BillMode.GearBatch, Target = 1 })],
                        new(facts.WorkAdmission.Recipes, demand.MaterialAvailability));
                    if (material.Selected.Count > 0) return excluded.Length == 0
                        ? (ServiceCoverage.Adequate, [])
                        : (ServiceCoverage.EvidenceGap, ["replacement_throughput_not_established_by_recipe_eligibility"]);
                    if (material.GenerationTruncated || material.Comparison.SearchTruncated || material.Comparison.Findings.Count > 0
                        || !demand.MaterialAvailability.IsKnown) unknown = true;
                }
            }
            return unknown ? (ServiceCoverage.EvidenceGap, ["recipe_skill_material_or_worker_evidence"]) : (ServiceCoverage.Shortfall, []);
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
        var patients = facts.MedicalPawns;
        foreach (var patient in patients.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            if (patient.QuestProtected || patient.Dead) continue;
            foreach (var group in patient.Operations.Where(o => SurgeryRules.ServedSurgery(patient, o) > 0)
                .Select(o => o.Operation)
                .GroupBy(o => o.Part is { } part ? part.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "whole")
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var target = new ServiceTarget(MedicalRules.SurgeryNeed.FilingKey, patient.Id.Value + "/" + group.Key, "served_procedure_feasible");
                var options = group.ToArray();
                (ServiceCoverage, EquatableArray<string>) Assess(string excluded)
                {
                    var unknown = false;
                    foreach (var operation in options)
                    {
                        if (!operation.IngredientsOnMap) continue;
                        // A recipe without an outcome effect rates no doctor: its eligible doctors' odds are unread.
                        if (operation.Chance is not SurgeryChance.Rated rated) { unknown = unknown || operation.EligibleDoctors > 0; continue; }
                        var current = operation;
                        if (excluded.Length != 0)
                        {
                            var chances = rated.Doctors.Where(d => d.Key != excluded).Select(d => d.Value).ToArray();
                            current = chances.Length > 0
                                ? operation with { EligibleDoctors = chances.Length, Chance = rated with { Success = chances.Max() } }
                                : operation with { EligibleDoctors = 0, Chance = new SurgeryChance.NoEligibleDoctor() };
                        }
                        if (SurgeryRules.SurgeryAcceptable(current, SurgeryRules.RestoreFailureCap)) return (ServiceCoverage.Adequate, []);
                    }
                    return unknown ? (ServiceCoverage.EvidenceGap, ["procedure_eligibility_ingredients_or_doctor_chances"]) : (ServiceCoverage.Shortfall, []);
                }
                var baseline = Assess("");
                rows.Add(new(EssentialService.Medical, target, baseline.Item1, [patient.Id.Value], baseline.Item2));
                foreach (var doctor in options.Select(o => o.Chance).OfType<SurgeryChance.Rated>().SelectMany(r => r.Doctors.Keys)
                    .Distinct().Order(StringComparer.Ordinal))
                    scenario(target, "pawn/" + doctor, () => Assess(doctor));
                if (options.Any(o => o.Chance is SurgeryChance.NoOutcomeEffect && o.EligibleDoctors > 0))
                    scenario(target, "unmapped_doctor", () => (ServiceCoverage.EvidenceGap, ["procedure_doctor_identities"]));
            }
        }
    }

    private static void AssessServiceRoutes(RoundsFacts facts, List<ServiceAssessment> rows,
        Action<ServiceTarget, string, Func<(ServiceCoverage, EquatableArray<string>)>> scenario)
    {
        if (facts.Upkeep.Routes is not { } routes) return;
        foreach (var facility in routes.Facilities.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            var target = ServiceTarget.ForNeed(LayoutFamily.Routes, facility.Id, "observed_facility_reachable");
            (ServiceCoverage, EquatableArray<string>) Assess(string excluded)
            {
                var remaining = routes.Pawns.Where(p => p != excluded).ToArray();
                if (remaining.Any(p => facility.Travel.Any(t => t.Pawn == p && t.Reachable))) return (ServiceCoverage.Adequate, []);
                if (routes.Pawns.Count == 0 || remaining.Any(p => !facility.Travel.Any(t => t.Pawn == p)))
                    return (ServiceCoverage.EvidenceGap, ["facility_reachability"]);
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
        if (!facts.PowerRequired) return;
        var power = facts.EssentialInputs.Power;
        foreach (var generator in power.Buildings.Where(b => b.BaseW > 0).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            scenario(ServiceTarget.ForNeed(PowerFamily.Basic, "", "owner_power_coverage"), "facility/" + generator.Id, () =>
            {
                var coverage = PowerRules.PowerCoverageOf([.. power.Buildings.Where(b => b.Id != generator.Id)]);
                return (coverage.HeadroomW >= 0 ? ServiceCoverage.Adequate : ServiceCoverage.Shortfall, []);
            });
        }
        // Network service membership is not inferred from the sign of watts: decorative consumers are not essential services.
        scenario(ServiceTarget.ForNeed(PowerFamily.Basic, "", "essential_consumer_membership"), "unmapped_service",
            () => (ServiceCoverage.EvidenceGap, ["power_consumer_service_membership"]));
    }
}

