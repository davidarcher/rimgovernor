namespace RimGovernor.Brain.Policy;

/// <summary>Derived once alongside shared demand; observed claims are not native reservations.</summary>
public sealed record ForecastWorkInputs
{
    public Tick Tick { get; init; }
    public Fact<EquatableArray<ForecastCommitment>> ConstructionCommitments { get; init; }
    public Fact<CurrentConstruction> Construction { get; init; }
    public Fact<EquatableArray<WorkPawn>> Pawns { get; init; }
    public Fact<EquatableArray<IngredientReservation>> IngredientReservations { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Use applied journal claims and current native geometry, not a second construction demand ledger.</summary>
    public static ForecastWorkInputs ForecastWorkInputsOf(RoundsFacts facts)
    {
        var input = new ForecastWorkInputs
        {
            Tick = facts.Tick, Construction = facts.CurrentConstruction, Pawns = facts.ForecastObserved.WorkPawns,
            IngredientReservations = facts.BillReservations,
        };
        if (!facts.ConstructionClaims.TryGetValue(out var claims) || !facts.CurrentConstruction.TryGetValue(out var census) || !census.Colony)
            return input;
        var commitments = new List<ForecastCommitment>();
        foreach (var group in claims.Where(c => c.Building is not null).GroupBy(c => c.Building!))
        {
            // The journal may contain historical placements at the same geometry. The current site is one outcome.
            if (!census.Sites.Any(s => s.Building == group.Key)) continue;
            var claim = group.OrderBy(c => c.Plan.Value, StringComparer.Ordinal).ThenBy(c => c.Action.Value, StringComparer.Ordinal).First();
            if (string.IsNullOrWhiteSpace(claim.Plan.Value) || string.IsNullOrWhiteSpace(claim.Action.Value))
                throw new DomainException("construction forecast requires owning Method and action");
            commitments.Add(new("construction/" + claim.Action.Value, default) { Construction = group.Key });
        }
        return input with { ConstructionCommitments = Known(EquatableArray.From(commitments.OrderBy(c => c.Id, StringComparer.Ordinal))) };
    }

    /// <summary>Join estimates to the shared review input; unknown claims cannot be converted to an empty plan.</summary>
    public static Fact<ColonyForecastRequest> ConstructionForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        RoundsFacts facts, RoundsPolicy policy, DerivedDemand demand, ForecastConstructionContext estimates)
    {
        var work = demand.Work;
        if (work.Tick != facts.Tick || origin.ObservedTick != facts.Tick.Value)
            throw new DomainException("forecast demand and facts must come from the same tick");
        if (!work.ConstructionCommitments.TryGetValue(out var commitments) || !work.Pawns.TryGetValue(out var pawns)) return default;
        var observed = pawns.ToDictionary(p => p.Id.Value, StringComparer.Ordinal);
        if (estimates.Workers.Any(w => !observed.ContainsKey(w.Capacity.Pawn)))
            throw new DomainException("forecast estimate names an unobserved pawn");
        return Known(new ColonyForecastRequest
        {
            Origin = origin, HorizonDays = horizonDays, Facts = ColonyForecastFactsOf(facts, policy), Demand = demand,
            Commitments = commitments,
            Construction = estimates with
            {
                Census = work.Construction, IngredientReservations = work.IngredientReservations,
                Workers = estimates.Workers.Select(w => w with { Observed = observed[w.Capacity.Pawn] }).ToArray(),
            },
        });
    }
}
