namespace RimGovernor.Brain.Policy;

/// <summary>Derived once alongside shared demand; observed claims are not native reservations.</summary>
public sealed record ForecastWorkInputs
{
    public Tick Tick { get; init; }
    public Fact<EquatableArray<ForecastCommitment>> ConstructionCommitments { get; init; }
    public Fact<CurrentConstruction> Construction { get; init; }
    public Fact<EquatableArray<WorkPawn>> Pawns { get; init; }
    public Fact<EquatableArray<IngredientReservation>> IngredientReservations { get; init; }
    public Fact<EquatableArray<ForecastCommitment>> ProductionPromises { get; init; }
    public WorkResourceClaims? Claims { get; init; }
    public WorkResourceView? Resources { get; init; }

    /// <summary>One material-claim commitment per planned room (PlannedRoomCommitments); unknown while the plan is.</summary>
    public Fact<EquatableArray<ForecastCommitment>> PlannedRooms { get; init; }
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
            ProductionPromises = facts.ProductionPromises,
            Claims = facts.WorkClaims,
            Resources = facts.WorkView,
            PlannedRooms = PlannedRoomCommitments(facts.PlannedConstruction),
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

    private static readonly EquatableArray<string> PlannedLaborUnsupported = EquatableArray.From(new[] { "planned_construction_labor" });

    /// <summary>
    /// What the layout plan still owes each room, as forecast commitments: exact material claims by room id, no
    /// stock effect and no site. The plan places nothing yet, so there is no work, route or finish time to
    /// schedule; labor is declared unsupported rather than guessed. The claims hold free material back from other
    /// alternatives the way a production promise does, so a proposal that spends the plan's wood is seen to.
    /// </summary>
    public static Fact<EquatableArray<ForecastCommitment>> PlannedRoomCommitments(Fact<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>> planned)
    {
        if (!planned.TryGetValue(out var rooms))
        {
            return default;
        }
        var commitments = new List<ForecastCommitment>();
        foreach (var (room, needs) in rooms.OrderBy(r => r.Key))
        {
            var claims = needs.Where(n => n.Value > 0).OrderBy(n => n.Key.Value, StringComparer.Ordinal)
                .Select(n => new ForecastMaterialClaim(n.Key, Known((double)n.Value), Known(string.Empty))).ToArray();
            if (claims.Length == 0)
            {
                continue;
            }
            commitments.Add(new("planned/room" + room, default)
            {
                MaterialClaims = EquatableArray.From(claims),
                UnsupportedEffects = PlannedLaborUnsupported,
            });
        }
        return Known(EquatableArray.From(commitments));
    }

    /// <summary>Join estimates to the shared review input; unknown claims cannot be converted to an empty plan.</summary>
    public static Fact<ColonyForecastRequest> ConstructionForecastRequestOf(ColonyForecastOrigin origin, double horizonDays,
        RoundsFacts facts, RoundsPolicy policy, DerivedDemand demand, ForecastConstructionContext estimates)
    {
        var work = demand.Work;
        if (work.Tick != facts.Tick || origin.ObservedTick != facts.Tick.Value)
            throw new DomainException("forecast demand and facts must come from the same tick");
        if (!work.ConstructionCommitments.TryGetValue(out var commitments) || !work.Pawns.TryGetValue(out var pawns)
            || !work.ProductionPromises.TryGetValue(out var production)) return default;
        // An unknown plan leaves the planned rooms out rather than the whole request unknown: it is one input among several.
        var plannedRooms = work.PlannedRooms.TryGetValue(out var rooms) ? rooms : default;
        var observed = pawns.ToDictionary(p => p.Id.Value, StringComparer.Ordinal);
        if (estimates.Workers.Any(w => !observed.ContainsKey(w.Capacity.Pawn)))
            throw new DomainException("forecast estimate names an unobserved pawn");
        return Known(new ColonyForecastRequest
        {
            Origin = origin, HorizonDays = horizonDays, Facts = ColonyForecastFactsOf(facts, policy), Demand = demand,
            Commitments = EquatableArray.From(commitments.Concat(production).Concat(plannedRooms)),
            Construction = estimates with
            {
                Census = work.Construction, IngredientReservations = work.IngredientReservations,
                Workers = estimates.Workers.Select(w => w with { Observed = observed[w.Capacity.Pawn],
                    Capacity = work.Resources is null ? w.Capacity : w.Capacity with
                    { Windows = work.Resources.Capacity.GetValueOrDefault(w.Capacity.Pawn)?.Capacity.Windows ?? default } }).ToArray(),
            },
        });
    }
}
