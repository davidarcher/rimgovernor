using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Physical support consequences retain their units.</summary>
public sealed record ForecastSupportConsequences
{
    public double UrgentFirstCareTicks { get; init; }
    public double PendingCareBatches { get; init; }
    public double FirstCareTicks { get; init; }
    public double UnsuppliedDemand { get; init; }
    /// <summary>The expected poisoned meals of the schedule's completed recipes: each one's bench room FoodPoisonChance (SupportDemand.FoodPoisonChance).</summary>
    public double FoodPoisonExposure { get; init; }
    public string FoodProduction { get; init; } = "";
    /// <summary>The summed InfectionChanceFactor of the care rooms whose infection check outlasts the tends; Unassessed for a care priced without a room (the colony tend).</summary>
    public CareRoomRead CareRoom { get; init; } = new CareRoomRead.Unassessed();
    public double TendQuality { get; init; }
    public string CareServices { get; init; } = "";
    public double SurgeryRoomFactor { get; init; }
    public EquatableArray<string> Findings { get; init; }
}

/// <summary>The tending rooms' infection factor a support outcome carries: the summed factor, or Unassessed for a care priced
/// without a room.</summary>
public abstract record CareRoomRead
{
    private CareRoomRead() { }

    public sealed record Assessed(double Factor) : CareRoomRead;

    public sealed record Unassessed : CareRoomRead;
}

public static class ForecastSupport
{
    internal static ForecastSupportConsequences ForecastSupportConsequencesOf(ForecastCoupledContext context,
        ForecastActivityPlan plan, IReadOnlyDictionary<string, bool> completed, double horizon)
    {
        var read = context.Support;
        var tasks = plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity))
            .Select(a => (Allocation: a, Binding: context.Bindings.GetValueOrDefault((a.Activity, a.Pawn))))
            .Where(p => p.Binding is not null).Select(p => (p.Allocation, Binding: p.Binding!)).ToArray();
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        double urgent = 0, pending = 0, untreated = 0, unsupplied = 0, poison = 0;
        var careServices = new List<string>(); var careFactor = 0d; var quality = 0d; var surgery = 0d;
        foreach (var demand in read.Demands.Where(d => d.Kind is "care" or "surgery"))
        {
            var service = tasks.Where(t => t.Binding.SupportDemand == demand.Id && t.Binding.Target == demand.Target
                && t.Binding.Job == (demand.Kind == "care" ? "TendPatient" : "DoBill")).ToArray();
            var end = service
                .Select(t => t.Allocation.End).DefaultIfEmpty(horizon).Min();
            var served = service.Length != 0;
            pending += served ? 0 : 1; untreated += end;
            if (demand.Kind == "care" && demand.BleedRate > 0) urgent += end;
            findings.Add("first_service_only_remaining_health_unknown:" + demand.Id);
            foreach (var task in service)
            {
                careServices.Add(demand.Id + "/" + (task.Binding.Recipe?.Bill ?? "tend"));
                var worker = read.Workers.First(w => w.Demand == demand.Id && w.Pawn == task.Binding.Pawn && (w.Medicine?.Id ?? "") == task.Binding.Medicine);
                if (demand.Kind == "surgery") surgery += demand.SurgerySuccessFactor;
                // A tend's room counts when the batch's infection check falls after the tend ends.
                else if (worker.Treatment is SupportTreatment.Tend { Infection: InfectionCheck.Pending(var check) } && check > task.Allocation.End)
                    careFactor += demand.InfectionChanceFactor;
                if (worker.Treatment is SupportTreatment.Tend tend) quality += tend.Quality;
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
            production.Add(task.Binding.Target + "/" + (task.Binding.Recipe?.Recipe ?? ""));
            poison += demand.FoodPoisonChance;
        }
        return new()
        {
            UrgentFirstCareTicks = urgent, PendingCareBatches = pending, FirstCareTicks = untreated,
            UnsuppliedDemand = unsupplied, FoodPoisonExposure = poison,
            CareRoom = new CareRoomRead.Assessed(careFactor), TendQuality = quality,
            SurgeryRoomFactor = surgery,
            CareServices = string.Join(";", careServices.Order(StringComparer.Ordinal)),
            FoodProduction = string.Join(";", production.Order(StringComparer.Ordinal)), Findings = EquatableArray.From(findings)
        };
    }
}
