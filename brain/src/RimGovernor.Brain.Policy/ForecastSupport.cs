using RimGovernor.Brain.Policy.Methods;

namespace RimGovernor.Brain.Policy;

/// <summary>Physical support consequences retain their units.</summary>
public sealed record ForecastSupportConsequences
{
    public Fact<double> UrgentFirstCareTicks { get; init; } = Known(0d);
    public double PendingCareBatches { get; init; }
    public double FirstCareTicks { get; init; }
    public double UnsuppliedDemand { get; init; }
    public Fact<double> FoodPoisonExposure { get; init; }
    public string FoodProduction { get; init; } = "";
    public Fact<double> CareRoomFactor { get; init; }
    public Fact<double> TendQuality { get; init; }
    public string CareServices { get; init; } = "";
    public Fact<double> SurgeryRoomFactor { get; init; }
    public EquatableArray<string> Findings { get; init; }
}

public static class ForecastSupport
{
    internal static ForecastSupportConsequences ForecastSupportConsequencesOf(ForecastCoupledContext context,
        ForecastActivityPlan plan, IReadOnlyDictionary<string, Fact<bool>> completed, double horizon)
    {
        var read = context.Support;
        var tasks = plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity) == Known(true))
            .Select(a => (Allocation: a, Binding: context.Bindings.GetValueOrDefault((a.Activity, a.Pawn))))
            .Where(p => p.Binding is not null).Select(p => (p.Allocation, Binding: p.Binding!)).ToArray();
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        double urgent = 0, pending = 0, untreated = 0, unsupplied = 0, poison = 0;
        var urgencyKnown = true; var poisonKnown = true; var careKnown = true; var qualityKnown = true;
        var careServices = new List<string>(); var careFactor = 0d; var quality = 0d; var surgery = 0d; var surgeryKnown = true;
        foreach (var demand in read.Demands.Where(d => d.Kind is "care" or "surgery"))
        {
            var service = tasks.Where(t => t.Binding.SupportDemand == demand.Id && t.Binding.Target == demand.Target
                && t.Binding.Job == (demand.Kind == "care" ? "TendPatient" : "DoBill")).ToArray();
            var end = service
                .Select(t => t.Allocation.End).DefaultIfEmpty(horizon).Min();
            var served = service.Length != 0;
            pending += served ? 0 : 1; untreated += end;
            if (demand.Kind == "care")
            {
                if (!demand.BleedRate.TryGetValue(out var bleeding)) { urgencyKnown = false; findings.Add("care_urgency_unknown:" + demand.Id); }
                else if (bleeding > 0) urgent += end;
            }
            findings.Add("first_service_only_remaining_health_unknown:" + demand.Id);
            foreach (var task in service)
            {
                careServices.Add(demand.Id + "/" + (task.Binding.Recipe?.Bill ?? "tend"));
                var worker = read.Workers.FirstOrDefault(w => w.Demand == demand.Id && w.Pawn == task.Binding.Pawn && w.Medicine == task.Binding.Medicine);
                if (demand.Kind == "surgery")
                {
                    if (demand.SurgerySuccessFactor.TryGetValue(out var multiplier)) surgery += multiplier;
                    else { surgeryKnown = false; findings.Add("surgery_room_effect_unknown:" + demand.Id); }
                }
                else if (worker?.InfectionRoomEffectApplies == Known(false)) { }
                else if (worker?.InfectionRoomEffectApplies == Known(true)
                    && worker.InfectionCheckTicks.TryGetValue(out var check) && double.IsFinite(check) && check > task.Allocation.End
                    && demand.InfectionChanceFactor.TryGetValue(out var value)) careFactor += value;
                else { careKnown = false; findings.Add("care_room_effect_unknown:" + demand.Id); }
                if (worker?.TendQuality.TryGetValue(out var tendQuality) == true) quality += tendQuality;
                else qualityKnown = false;
            }
        }
        foreach (var bill in read.Deliveries.GroupBy(d => d.Bill.Length == 0 ? d.Demand : d.Bill))
        {
            // A completed exact recipe already supplied and consumed this iteration's demand.
            // A separate refill cannot earn the same progress a second time.
            var progress = tasks.Any(t => t.Binding.Kind == PawnActivityKind.Recipe && t.Binding.Recipe?.Bill == bill.Key) ? 1d : 0d;
            foreach (var demand in bill.GroupBy(d => d.Demand))
            {
                var requirements = demand.Select(d => d.Required).Distinct().ToArray();
                if (requirements.Length != 1) { findings.Add("inconsistent_support_demand:" + demand.Key); continue; }
                var required = requirements[0];
                if (required <= 0) { progress = 1; continue; }
                var delivered = tasks.Where(t => t.Binding.Kind == PawnActivityKind.Deliver && t.Binding.SupportDemand == demand.Key).Sum(t => (double)t.Binding.Count);
                // The same recipe's material alternatives are alternatives, not independent demand.
                progress = Math.Max(progress, delivered / required);
            }
            unsupplied += Math.Max(0, 1 - progress);
        }
        var production = new List<string>();
        foreach (var task in tasks.Where(t => t.Binding.Kind == PawnActivityKind.Recipe))
        {
            var demand = read.Demands.FirstOrDefault(d => d.Kind == "bench" && d.Target == task.Binding.Target);
            if (demand is null) continue;
            production.Add(task.Binding.Target + "/" + (task.Binding.Recipe?.Recipe ?? task.Binding.Cooking?.Continuation.Recipe ?? ""));
            if (demand.FoodPoisonChance.TryGetValue(out var value)) poison += value;
            else { poisonKnown = false; findings.Add("food_poison_effect_unknown:" + demand.Id); }
        }
        return new()
        {
            UrgentFirstCareTicks = urgencyKnown ? Known(urgent) : default, PendingCareBatches = pending, FirstCareTicks = untreated,
            UnsuppliedDemand = unsupplied, FoodPoisonExposure = poisonKnown ? Known(poison) : default,
            CareRoomFactor = careKnown ? Known(careFactor) : default, TendQuality = qualityKnown ? Known(quality) : default,
            SurgeryRoomFactor = surgeryKnown ? Known(surgery) : default,
            CareServices = string.Join(";", careServices.Order(StringComparer.Ordinal)),
            FoodProduction = string.Join(";", production.Order(StringComparer.Ordinal)), Findings = EquatableArray.From(findings)
        };
    }
}
