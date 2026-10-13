namespace RimGovernor.Brain.Policy;

// Go: trade_acquisition.go.

public enum TradeAcquisitionKind
{
    [WireName("")]
    None,

    [WireName("request_caravan")]
    Caravan,

    [WireName("request_orbital")]
    Orbital,

    [WireName("scout_settlement")]
    Settlement,
}

/// <summary>
/// An unpriced attempt, never a SupplyCandidate. Participant identifies an existing seller; requests identify
/// faction and trader kind because the future seller does not exist yet.
/// </summary>
public sealed record TradeAcquisitionOption
{
    public string ID { get; init; } = "";

    public TradeAcquisitionKind Kind { get; init; }

    public TradeParticipant Participant { get; init; } = new(TradeParticipantKind.Map, "");

    public string Faction { get; init; } = "";

    public string TraderKind { get; init; } = "";

    public string Console { get; init; } = "";

    public string Negotiator { get; init; } = "";

    public long LastRequestTick { get; init; }

    public EquatableArray<ResourceKey> Compatible { get; init; }

    public Fact<bool> Eligible { get; init; }

    public Fact<double> LeadDays { get; init; }

    public Fact<double> LaborTicks { get; init; }

    public Fact<int> GoodwillCost { get; init; }

    public Fact<string> RelationAfterPayment { get; init; }

    public Fact<long> CooldownTicks { get; init; }

    public Fact<bool> OrbitalAvailable { get; init; }

    public int PassingShips { get; init; }

    // Trip facts are computed for the exact proposed crew and cargo. SafeCrew includes retaining enough labor at
    // home; Packable includes native gates.
    public Fact<bool> SafeCrew { get; init; }

    public Fact<bool> Packable { get; init; }

    public Fact<bool> RoutesReachable { get; init; }

    public Fact<double> OutboundDays { get; init; }

    public Fact<double> ReturnDays { get; init; }

    public Fact<double> FoodDays { get; init; }

    public Fact<double> RotDays { get; init; }
}

/// <summary>Holds an attempt for an unmet demand. Gap is explanatory demand, not expected stock, purchased goods or delivery credit.</summary>
public sealed record TradeAcquisitionProposal(TradeAcquisitionOption Option, SupplyDemand Demand, double Gap);

public sealed record TradeAcquisitionRequest
{
    public IReadOnlyList<SupplyDemandResult> Demands { get; init; } = [];

    public IReadOnlyList<TradeAcquisitionOption> Options { get; init; } = [];

    public Fact<long> Silver { get; init; }

    public Fact<long> Reserve { get; init; }

    public string ActiveID { get; init; } = "";
}

public sealed record TradeAcquisitionDecision(TradeAcquisitionOption Option, string Reason);

public sealed record TradeAcquisitionPlan
{
    public TradeAcquisitionProposal? Proposal { get; init; }

    public IReadOnlyList<TradeAcquisitionDecision> Decisions { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Selects one attempt by known lead, known labor and stable ID. A pending attempt earns no supply credit and
    /// does not modify the caller's local/priced portfolio. ActiveID comes from the owning executor.
    /// </summary>
    public static TradeAcquisitionPlan PlanTradeAcquisition(TradeAcquisitionRequest request)
    {
        TradeAcquisitionProposal? proposal = null;
        var decisions = new List<TradeAcquisitionDecision>();
        var sk = request.Silver.TryGetValue(out var silver);
        var rk = request.Reserve.TryGetValue(out var reserve);
        var options = request
            .Options.OrderBy(
                x => x,
                Comparer<TradeAcquisitionOption>.Create(
                    (x, y) =>
                    {
                        var xk = x.LeadDays.TryGetValue(out var a);
                        var yk = y.LeadDays.TryGetValue(out var b);
                        if (xk != yk)
                        {
                            return xk ? -1 : 1;
                        }
                        if (a != b)
                        {
                            return a < b ? -1 : 1;
                        }
                        xk = x.LaborTicks.TryGetValue(out a);
                        yk = y.LaborTicks.TryGetValue(out b);
                        if (xk != yk)
                        {
                            return xk ? -1 : 1;
                        }
                        if (a != b)
                        {
                            return a < b ? -1 : 1;
                        }
                        return string.CompareOrdinal(x.ID, y.ID);
                    }
                )
            )
            .ToList();
        foreach (var o in options)
        {
            var reason = AcquisitionAttemptGate(o);
            if (!sk || !rk || reserve < 0 || silver <= reserve)
            {
                reason = "insufficient_silver";
            }
            if (request.ActiveID.Length != 0)
            {
                reason = "active_attempt";
            }
            SupplyDemandResult? matched = null;
            o.LeadDays.TryGetValue(out var lead);
            var compatible = false;
            foreach (var d in request.Demands)
            {
                if (d.Gap <= 0 || !o.Compatible.Contains(d.Demand.Good))
                {
                    continue;
                }
                compatible = true;
                if (lead > d.Demand.HorizonDays)
                {
                    continue;
                }
                if (matched is null || d.Demand.Priority > matched.Demand.Priority)
                {
                    matched = d;
                }
            }
            if (reason.Length == 0 && matched is null)
            {
                reason = compatible ? "late_arrival" : "no_compatible_demand";
            }
            if (reason.Length == 0 && proposal is null)
            {
                proposal = new TradeAcquisitionProposal(o, matched!.Demand, matched.Gap);
                reason = "selected";
            }
            else if (reason.Length == 0)
            {
                reason = "other_attempt_selected";
            }
            decisions.Add(new TradeAcquisitionDecision(o, reason));
        }
        return new TradeAcquisitionPlan { Proposal = proposal, Decisions = decisions };
    }

    private static string AcquisitionAttemptGate(TradeAcquisitionOption o)
    {
        var ek = o.Eligible.TryGetValue(out var eligible);
        var lk = o.LeadDays.TryGetValue(out var lead);
        var wk = o.LaborTicks.TryGetValue(out var labor);
        if (!FoodId(o.ID) || !ek || !lk || !wk || !FoodNumber(lead) || !FoodNumber(labor))
        {
            return "unknown_eligibility_or_cost";
        }
        if (!eligible)
        {
            return "ineligible";
        }
        switch (o.Kind)
        {
            case TradeAcquisitionKind.Caravan:
            case TradeAcquisitionKind.Orbital:
            {
                var ck = o.GoodwillCost.TryGetValue(out var cost);
                var rk = o.RelationAfterPayment.TryGetValue(out var relation);
                var dk = o.CooldownTicks.TryGetValue(out var cooldown);
                if (!ck || !rk || !dk || cost < 0 || cooldown < 0 || !FoodId(o.Faction) || !FoodId(o.TraderKind) || !FoodId(o.Console) || !FoodId(o.Negotiator))
                {
                    return "unknown_request_facts";
                }
                if (relation != "Ally")
                {
                    return "alliance_loss";
                }
                if (cooldown != 0)
                {
                    return "cooldown";
                }
                if (o.Kind == TradeAcquisitionKind.Orbital)
                {
                    var known = o.OrbitalAvailable.TryGetValue(out var available);
                    if (!known || !available)
                    {
                        return "orbital_unavailable";
                    }
                    if (o.PassingShips != 0)
                    {
                        return "passing_ships";
                    }
                }
                break;
            }
            case TradeAcquisitionKind.Settlement:
            {
                // Scouting precedes native caravan creation. Executable trade keeps the strict participant
                // contract; a predeparture option has only its seller.
                if (o.Participant.Kind != TradeParticipantKind.Settlement || !FoodId(o.Participant.Id) || (o.Participant.Caravan.Length != 0 && !o.Participant.IsValid))
                {
                    return "unknown_target";
                }
                var sk = o.SafeCrew.TryGetValue(out var safe);
                var pk = o.Packable.TryGetValue(out var pack);
                var fk = o.FoodDays.TryGetValue(out var food);
                var rk = o.RotDays.TryGetValue(out var rot);
                var ok = o.OutboundDays.TryGetValue(out var outbound);
                var hk = o.ReturnDays.TryGetValue(out var home);
                var reachableKnown = o.RoutesReachable.TryGetValue(out var reachable);
                if (!sk || !pk || !fk || !rk || !ok || !hk || !reachableKnown || !FoodNumber(food) || !FoodNumber(rot) || !FoodNumber(outbound) || !FoodNumber(home) || lead < outbound + home)
                {
                    return "unknown_trip_facts";
                }
                if (!reachable)
                {
                    return "unreachable_route";
                }
                if (!safe)
                {
                    return "unsafe_crew";
                }
                if (!pack)
                {
                    return "cannot_pack";
                }
                if (food < lead)
                {
                    return "insufficient_travel_food";
                }
                if (rot < lead)
                {
                    return "spoiling_cargo";
                }
                break;
            }
            default:
                return "unknown_kind";
        }
        return "";
    }
}
