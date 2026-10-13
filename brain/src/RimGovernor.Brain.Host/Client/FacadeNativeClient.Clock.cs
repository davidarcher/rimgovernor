using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.Host;

// Go clock.go (ReadClockStatus, ReadClockAttempt) and clock_events.go (ReadClockEvents): the clock reads.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>Client.ReadClockStatus</c>: the clock status of the identity's world, validated.</summary>
    internal async Task<(PbClock.StatusReply Value, BridgeResult Receipt)> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        var reply = await Facade.ReadClockStatusAsync(new PbClock.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        FacadeWriteChecks.ClockStatusReply(reply, identity);
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadClockAttempt</c>: never interprets Unknown as proof of no effect or permission to retry. The
    /// caller retains the original request for reconciliation.
    /// </summary>
    internal async Task<(PbClock.AttemptReply Value, BridgeResult Receipt)> ReadClockAttemptAsync(PbClock.AttemptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BridgeFunctions.ClockWire(request);
        BridgeFunctions.ValidateIdentity(request.Identity);
        BridgeFunctions.BuildingAttempt(request.Attempt);
        var reply = await Facade.ReadClockAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case PbClock.AttemptReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbClock.AttemptReplyOutcome.UnknownCase:
                break;
            case PbClock.AttemptReplyOutcome.ReceiptCase { Value: var receipt }:
                BridgeFunctions.ClockReceipt(receipt, request.Identity, request.Attempt, 0);
                break;
            default:
                throw BridgeFunctions.Contract("missing clock attempt outcome");
        }
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadClockEvents</c>: preserves journal loss, partial evidence and original event contexts. Reading
    /// does not acknowledge an interruption or authorize resuming. Unix timestamps are diagnostic wall time and need
    /// not increase with cursors.
    /// </summary>
    internal async Task<(PbClock.EventsReply Value, BridgeResult Receipt)> ReadClockEventsAsync(PbClock.EventsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BridgeFunctions.ClockWire(request);
        BridgeFunctions.ValidateIdentity(request.Identity);
        if (request.AfterCursor is null
            || request.AfterCursor.GetValueOrDefault() < 0
            || request.Limit is null
            || request.Limit.GetValueOrDefault() < 1
            || request.Limit.GetValueOrDefault() > 128)
        {
            throw BridgeFunctions.Contract("clock events cursor/limit");
        }
        var reply = await Facade.ReadClockEventsAsync(request, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case PbClock.EventsReplyOutcome.FailureCase { Value: var failure }:
                throw Failure(failure);
            case PbClock.EventsReplyOutcome.PageCase { Value: var page }:
                BridgeFunctions.ClockEventsPage(page, request);
                break;
            default:
                throw BridgeFunctions.Contract("clock events outcome missing");
        }
        return (reply, NoReceipt);
    }
}
