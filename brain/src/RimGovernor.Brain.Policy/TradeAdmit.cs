namespace RimGovernor.Brain.Policy;

// Go: trade_admit.go.

/// <summary>
/// Anchors one trade intent's dispatch to a fresh native read (the trade-session read's context). Trade is an
/// intent-mode kind: native judges the intent against live state when it applies and refuses it with a terminal
/// receipt, so admission never gates on a native verdict.
/// </summary>
public sealed record TradeAdmissionFacts
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick PreviewTick { get; init; }
}

public sealed record TradeRequest
{
    public required DomainAction Action { get; init; }

    public required Progress Progress { get; init; }

    public GenerationSnapshot Current { get; init; }

    public Tick MinimumTick { get; init; }

    public TradeAdmissionFacts Facts { get; init; } = new();
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Re-validates one already-selected trade intent immediately before dispatch: the canonical action, its
    /// progress and a fresh anchor in the current world. It never selects a trader, lines or floors.
    /// </summary>
    public static AdmissionDecision EvaluateTrade(TradeRequest r)
    {
        AdmissionDecision Refuse(Reason reason) => new() { Refused = EquatableArray.From([new Refusal(r.Action.Id, reason)]) };

        var trade = r.Action.Trade;
        var canonical = false;
        if (trade is not null)
        {
            try
            {
                canonical = DomainAction.NewTradeAction(r.Action.Id, trade) == r.Action;
            }
            catch (DomainException)
            {
                canonical = false;
            }
        }
        if (!canonical)
        {
            return Refuse(Reason.NotReady);
        }
        var v = r.Progress.View;
        var f = r.Facts;
        if (!r.Current.IsValid || r.Current.Native == default || !f.Snapshot.Matches(r.Current) || r.MinimumTick < default(Tick) || f.PreviewTick < r.MinimumTick)
        {
            return Refuse(Reason.StaleFacts);
        }
        if (r.Progress.Action != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
        {
            return Refuse(Reason.NotReady);
        }
        if (f.PreviewTick < v.Tick || (v.Stage == Stage.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > default(AttemptId) && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map)))
        {
            return Refuse(Reason.StaleFacts);
        }
        return new AdmissionDecision { Admitted = true };
    }
}
