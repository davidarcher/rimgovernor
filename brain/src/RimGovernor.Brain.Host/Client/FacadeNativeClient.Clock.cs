using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.Host;

// Go clock.go (ReadClockStatus) and clock_events.go (ReadClockEvents): the clock reads.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>Client.ReadClockStatus</c>: the clock status of the identity's world; a failure or unavailable status throws.</summary>
    internal async Task<PbClock.StatusReply> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        var reply = await Facade.ReadClockStatusAsync(new PbClock.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        FacadeWriteChecks.ClockStatusReply(reply);
        return reply;
    }

    /// <summary>
    /// Go <c>Client.ReadClockEvents</c>: preserves journal loss, partial evidence and original event contexts. Reading
    /// does not acknowledge an interruption or authorize resuming. Unix timestamps are diagnostic wall time and need
    /// not increase with cursors.
    /// </summary>
    internal async Task<PbClock.EventsReply> ReadClockEventsAsync(PbClock.EventsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AfterCursor is null
            || request.AfterCursor.GetValueOrDefault() < 0
            || request.Limit is null
            || request.Limit.GetValueOrDefault() < 1
            || request.Limit.GetValueOrDefault() > 128)
        {
            throw BridgeFunctions.Contract("clock events cursor/limit");
        }
        var reply = await Facade.ReadClockEventsAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return reply;
    }
}
