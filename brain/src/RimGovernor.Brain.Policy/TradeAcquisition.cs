namespace RimGovernor.Brain.Policy;


public enum TradeAcquisitionKind
{
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

    public required TradeAcquisitionKind Kind { get; init; }

    public TradeParticipant Participant { get; init; } = new(TradeParticipantKind.Map, "");

    public string Faction { get; init; } = "";

    public string TraderKind { get; init; } = "";

    public string Console { get; init; } = "";

    public string Negotiator { get; init; } = "";

    public EquatableArray<ResourceKey> Compatible { get; init; }

    public bool Eligible { get; init; }

    public double LeadDays { get; init; }

    public double LaborTicks { get; init; }

    public int GoodwillCost { get; init; }

    public string RelationAfterPayment { get; init; } = "";

    public long CooldownTicks { get; init; }

    public bool OrbitalAvailable { get; init; }

    public int PassingShips { get; init; }

    // Trip facts are computed by a fresh native pack for the exact proposed crew and cargo; a settlement option
    // exists only once that pack is safe and both legs are reachable.
    public double OutboundDays { get; init; }

    public double ReturnDays { get; init; }

    public double FoodDays { get; init; }

    public double RotDays { get; init; }
}

/// <summary>Holds an attempt for an unmet demand. It is explanatory demand, not expected stock, purchased goods or delivery credit.</summary>
public sealed record TradeAcquisitionProposal(TradeAcquisitionOption Option);

public sealed record TradeAcquisitionRequest
{
    public IReadOnlyList<SupplyDemandResult> Demands { get; init; } = [];

    public IReadOnlyList<TradeAcquisitionOption> Options { get; init; } = [];

    public long Silver { get; init; }

    public long Reserve { get; init; }

    public string ActiveID { get; init; } = "";
}

public sealed record TradeAcquisitionPlan
{
    public TradeAcquisitionProposal? Proposal { get; init; }
}

public static partial class TradePolicyRules
{
    /// <summary>
    /// Selects one attempt by lead, labor and stable ID. A pending attempt earns no supply credit and does not
    /// modify the caller's local/priced portfolio. ActiveID comes from the owning executor.
    /// </summary>
    public static TradeAcquisitionPlan PlanTradeAcquisition(TradeAcquisitionRequest request)
    {
        TradeAcquisitionProposal? proposal = null;
        var options = request
            .Options.OrderBy(x => x.LeadDays)
            .ThenBy(x => x.LaborTicks)
            .ThenBy(x => x.ID, StringComparer.Ordinal)
            .ToList();
        foreach (var o in options)
        {
            var reason = AcquisitionAttemptGate(o);
            if (request.Reserve < 0 || request.Silver <= request.Reserve)
            {
                reason = "insufficient_silver";
            }
            if (request.ActiveID.Length != 0)
            {
                reason = "active_attempt";
            }
            SupplyDemandResult? matched = null;
            var compatible = false;
            foreach (var d in request.Demands)
            {
                if (d.Gap <= 0 || !o.Compatible.Contains(d.Demand.Good))
                {
                    continue;
                }
                compatible = true;
                if (o.LeadDays > d.Demand.HorizonDays)
                {
                    continue;
                }
                if (matched is null || d.Gap > matched.Gap)
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
                proposal = new TradeAcquisitionProposal(o);
            }
        }
        return new TradeAcquisitionPlan { Proposal = proposal };
    }

    /// <summary>The reason an option cannot be attempted, empty when it can.</summary>
    internal static string AcquisitionAttemptGate(TradeAcquisitionOption o)
    {
        if (!FoodForecastRules.FoodId(o.ID) || !FoodForecastRules.FoodNumber(o.LeadDays) || !FoodForecastRules.FoodNumber(o.LaborTicks))
        {
            return "invalid_option";
        }
        if (!o.Eligible)
        {
            return "ineligible";
        }
        switch (o.Kind)
        {
            case TradeAcquisitionKind.Caravan:
            case TradeAcquisitionKind.Orbital:
                if (o.GoodwillCost < 0 || o.CooldownTicks < 0 || !FoodForecastRules.FoodId(o.Faction) || !FoodForecastRules.FoodId(o.TraderKind))
                {
                    return "invalid_request";
                }
                if (!FoodForecastRules.FoodId(o.Console) || !FoodForecastRules.FoodId(o.Negotiator))
                {
                    return "no_negotiator_console";
                }
                if (o.RelationAfterPayment != "Ally")
                {
                    return "alliance_loss";
                }
                if (o.CooldownTicks != 0)
                {
                    return "cooldown";
                }
                if (o.Kind == TradeAcquisitionKind.Orbital)
                {
                    if (!o.OrbitalAvailable)
                    {
                        return "orbital_unavailable";
                    }
                    if (o.PassingShips != 0)
                    {
                        return "passing_ships";
                    }
                }
                break;
            case TradeAcquisitionKind.Settlement:
                // Scouting precedes native caravan creation. Executable trade keeps the strict participant
                // contract; a predeparture option has only its seller.
                if (o.Participant.Kind != TradeParticipantKind.Settlement || !FoodForecastRules.FoodId(o.Participant.Id) || (o.Participant.Caravan.Length != 0 && !o.Participant.IsValid))
                {
                    return "invalid_target";
                }
                if (!FoodForecastRules.FoodNumber(o.FoodDays) || !FoodForecastRules.FoodNumber(o.RotDays) || !FoodForecastRules.FoodNumber(o.OutboundDays) || !FoodForecastRules.FoodNumber(o.ReturnDays) || o.LeadDays < o.OutboundDays + o.ReturnDays)
                {
                    return "invalid_trip";
                }
                if (o.FoodDays < o.LeadDays)
                {
                    return "insufficient_travel_food";
                }
                if (o.RotDays < o.LeadDays)
                {
                    return "spoiling_cargo";
                }
                break;
        }
        return "";
    }
}
