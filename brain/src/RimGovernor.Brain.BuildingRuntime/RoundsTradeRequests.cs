using System.Text.Json;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The saved record of a comms trade request attempt.</summary>
internal sealed record CommsTradeRequestRecord
{
    public CommsTradeRequest Request { get; init; } = null!;
}

internal static partial class BuildingRuntimePackage
{
    internal static CommsTradeRequestRecord RequestRecord(string record)
    {
        using var doc = JsonDocument.Parse(record);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("comms_trade_request", out var inner))
        {
            throw new JsonException("request record is null");
        }
        return new CommsTradeRequestRecord { Request = global::RimGovernor.Brain.Store.Codec.StoreJson.Deserialize<CommsTradeRequest>(inner.GetRawText()) };
    }

    internal static bool RequestRecordPending(CommsTradeRequestRecord record, Executor.CommsTradeInspection read)
    {
        if (!read.RequestKnown)
        {
            return false;
        }
        if (read.MatchingWork || read.MatchingArrival)
        {
            return true;
        }
        if (read.LastRequestTick == record.Request.ExpectedLastRequestTick || read.MatchingSeller)
        {
            return false;
        }
        long expiry = 360000;
        if (record.Request.Kind == TradeRequestKind.Orbital)
        {
            expiry = 5001;
        }
        return (long)read.Tick.Value <= read.LastRequestTick + expiry;
    }
}

public sealed partial class RoundsTradePlanner
{
    /// <summary>
    /// Requests are methods of the supply concern that selected the attempt. A native arrival later appears in
    /// the ordinary trader census and is browsed by the existing shared trade phases; request intent never
    /// supplies stock.
    /// </summary>
    internal async Task<(RoundsTradeResult Result, bool Handled)> RequestAsync(ControlState state, Sto.Rounds review, CancellationToken call, CancellationToken epoch)
    {
        var journal = Reviewer.Player.Journal;
        (string Name, ConcernId Concern)[] owners = [("food", EnsureFoodSupply), ("resources", MaintainResource)];
        // Recover outstanding attempts from the owning saved Concern's linked shared method, before considering
        // proposals from either supply owner. This also fences a restart before native's queued job becomes visible.
        var settled = new List<(string Owner, string Id)>();
        foreach (var owner in owners)
        {
            var (goal, _) = journal.Workable(review, owner.Concern, call);
            if (goal is null)
            {
                continue;
            }
            foreach (var method in goal.History)
            {
                var plan = journal.LoadPlan(method.Plan, call);
                foreach (var progress in plan.Progress)
                {
                    var request = progress.Action.CommsTradeRequest;
                    if (request is null)
                    {
                        continue;
                    }
                    if (StorePackage.PlanOpen(plan))
                    {
                        return (new RoundsTradeResult(), false);
                    }
                    var kind = "TRADE_REQUEST_KIND_CARAVAN";
                    if (request.Kind == TradeRequestKind.Orbital)
                    {
                        kind = "TRADE_REQUEST_KIND_ORBITAL";
                    }
                    settled.Add((owner.Name, request.Faction + "/" + kind + "/" + request.TraderKind));
                }
            }
            if (!string.IsNullOrEmpty(goal.Standard.Record))
            {
                var record = RequestRecord(goal.Standard.Record);
                if (Native is not ICommsTradeRequestNative source)
                {
                    throw new ControlException("request: native lacks the comms trade request read");
                }
                var recovery = DomainAction.NewCommsTradeRequestAction(new ActionId("request-recovery"), record.Request);
                var reader = new TradeBoundary(new ExecutorClockOf(Reviewer.Clock), new TradeCapabilities { Requests = source });
                var read = await reader.InspectCommsTradeRequestAsync(new Target(recovery, state.Snapshot), call).ConfigureAwait(false);
                if (RequestRecordPending(record, read))
                {
                    return (new RoundsTradeResult(), false);
                }
                journal.RecordStandard(goal.Standard.Id, goal.Revision, "", call);
            }
        }
        foreach (var request in settled)
        {
            Reviewer.ReleaseTradeAcquisition(request.Owner, request.Id);
        }
        foreach (var owner in owners)
        {
            var proposal = Reviewer.TradeAcquisitionPlan(owner.Name).Proposal;
            if (proposal is null || proposal.Option.Kind == Pol.TradeAcquisitionKind.Settlement)
            {
                continue;
            }
            var (goal, workable) = journal.Workable(review, owner.Concern, call);
            if (!workable || goal is null)
            {
                continue;
            }
            var open = false;
            foreach (var method in goal.Methods)
            {
                if (StorePackage.PlanOpen(journal.LoadPlan(method.Plan, call)))
                {
                    open = true;
                }
            }
            if (open)
            {
                continue;
            }
            var option = proposal.Option;
            if (!Reviewer.ClaimTradeAcquisition(owner.Name, option.ID))
            {
                continue;
            }
            void Release() => Reviewer.ReleaseTradeAcquisition(owner.Name, option.ID);
            try
            {
                var kind = option.Kind == Pol.TradeAcquisitionKind.Orbital ? TradeRequestKind.Orbital : TradeRequestKind.Caravan;
                var request = new CommsTradeRequest(kind, option.Faction, option.TraderKind, option.Console, option.Negotiator, option.LastRequestTick);
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewCommsTradeRequestAction(new ActionId(id + "-request"), request);
                var plan = new PlanSpec(id, new PlanRevision(1), [action]);
                Reviewer.Player.Current(call, epoch);
                if (Reviewer.Player.Session.State() != state)
                {
                    throw new ControlException("request: session moved");
                }
                var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"trade-request-{goal.Admitted}"));
                var record = "{\"comms_trade_request\":" + global::RimGovernor.Brain.Store.Codec.StoreJson.Serialize(request) + "}";
                journal.CommitMethodRecord(goal.Standard.Id, goal.Revision, method, "", plan, record, call);
                // Keep the derived admission claim until this shared method reconciles. The saved method and native
                // facts own recovery after restart.
                return (new RoundsTradeResult { Verdict = BuildingReasonAdmitted, Plan = id, NativeWorkTicks = TradeWalkTicks }, true);
            }
            catch
            {
                Release();
                throw;
            }
        }
        return (new RoundsTradeResult(), false);
    }
}
