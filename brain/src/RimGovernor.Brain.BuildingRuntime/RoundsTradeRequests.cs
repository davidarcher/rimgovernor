using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The saved record of a comms trade request attempt.</summary>
internal sealed record CommsTradeRequestRecord
{
    [JsonPropertyName("comms_trade_request")]
    public CommsTradeRequest Request { get; init; } = null!;
}

internal static partial class BuildingRuntimePackage
{
    internal static CommsTradeRequestRecord RequestRecord(string record)
    {
        var output = JsonSerializer.Deserialize<CommsTradeRequestRecord>(record) ?? throw new JsonException("request record is null");
        output.Request.Validate();
        return output;
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
        if (record.Request.Kind == TradeRequestKind.TradeRequestOrbital)
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
    internal (RoundsTradeResult Result, bool Handled) Request(CancellationToken call, CancellationToken epoch, ControlState state, Sto.Rounds review)
    {
        var journal = Reviewer.Player.Journal;
        (string Name, ConcernId Concern)[] owners = [("food", EnsureFoodSupply), ("resources", MaintainResource)];
        // Recover outstanding attempts from the owning saved Concern's linked shared method, before considering
        // proposals from either supply owner. This also fences a restart before native's queued job becomes visible.
        var settled = new List<(string Owner, string Id)>();
        foreach (var owner in owners)
        {
            var (goal, _) = journal.Workable(call, review, owner.Concern);
            foreach (var method in goal.History)
            {
                var plan = journal.LoadPlan(call, method.Plan);
                foreach (var progress in plan.Progress)
                {
                    if (!progress.Action.TryCommsTradeRequest(out var request))
                    {
                        continue;
                    }
                    if (StorePackage.PlanOpen(plan))
                    {
                        return (new RoundsTradeResult(), false);
                    }
                    var kind = "TRADE_REQUEST_KIND_CARAVAN";
                    if (request.Kind == TradeRequestKind.TradeRequestOrbital)
                    {
                        kind = "TRADE_REQUEST_KIND_ORBITAL";
                    }
                    settled.Add((owner.Name, request.Faction + "/" + kind + "/" + request.TraderKind));
                }
            }
            if (goal.Standard.Record != "")
            {
                var record = RequestRecord(goal.Standard.Record);
                if (Native is not ICommsTradeRequestNative source)
                {
                    throw new ControlException("request: native lacks the comms trade request read");
                }
                var recovery = DomainAction.NewCommsTradeRequestAction(new ActionId("request-recovery"), record.Request);
                var reader = new TradeBoundary(new Boundary.Boundary { Clock = Reviewer.Clock }, new TradeCapabilities { Requests = source });
                var read = reader.InspectCommsTradeRequest(call, new Executor.Target { Action = recovery, Snapshot = state.Snapshot });
                if (RequestRecordPending(record, read))
                {
                    return (new RoundsTradeResult(), false);
                }
                journal.RecordStandard(call, goal.Standard.Id, goal.Revision, "");
            }
        }
        foreach (var request in settled)
        {
            Reviewer.ReleaseTradeAcquisition(request.Owner, request.Id);
        }
        foreach (var owner in owners)
        {
            var proposal = Reviewer.TradeAcquisitionPlan(owner.Name).Proposal;
            if (proposal is null || proposal.Option.Kind == Pol.TradeAcquireKind.Settlement)
            {
                continue;
            }
            var (goal, workable) = journal.Workable(call, review, owner.Concern);
            if (!workable)
            {
                continue;
            }
            var open = false;
            foreach (var method in goal.Methods)
            {
                if (StorePackage.PlanOpen(journal.LoadPlan(call, method.Plan)))
                {
                    open = true;
                }
            }
            if (open)
            {
                continue;
            }
            var option = proposal.Option;
            if (!Reviewer.ClaimTradeAcquisition(owner.Name, option.Id))
            {
                continue;
            }
            void Release() => Reviewer.ReleaseTradeAcquisition(owner.Name, option.Id);
            try
            {
                var kind = option.Kind == Pol.TradeAcquireKind.Orbital ? TradeRequestKind.TradeRequestOrbital : TradeRequestKind.TradeRequestCaravan;
                var request = new CommsTradeRequest { Kind = kind, Faction = option.Faction, TraderKind = option.TraderKind, Console = option.Console, Negotiator = option.Negotiator, ExpectedLastRequestTick = option.LastRequestTick };
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewCommsTradeRequestAction(new ActionId(id + "-request"), request);
                var plan = new PlanSpec(id, new PlanRevision(1), [action]);
                Reviewer.Player.Current(call, epoch);
                if (Reviewer.Player.Session.State() != state)
                {
                    throw new ControlException("request: session moved");
                }
                var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"trade-request-{goal.Admitted}"));
                var record = JsonSerializer.Serialize(new CommsTradeRequestRecord { Request = request });
                journal.CommitMethodRecord(call, goal.Standard.Id, goal.Revision, method, "", plan, record);
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
