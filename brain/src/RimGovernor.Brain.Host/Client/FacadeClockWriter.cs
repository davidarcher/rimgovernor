using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.NativeFacade;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>bridge.ClockControl</c> (clock.go): the explicit clock mutation capability over the facade, separate from the
/// clock reads. The runtime owner must authorize its use. No operation retries or reacquires. A possibly applied
/// operation throws <see cref="ClockUncertainException"/>; a typed refusal throws <see cref="NativeFailureException"/>.
/// </summary>
internal sealed class FacadeClockWriter(INativeFacade facade) : IClockWriter
{
    private readonly INativeFacade _facade = facade ?? throw new ArgumentNullException(nameof(facade));

    private static void Precondition(PbAuthority.WritePrecondition? pre)
    {
        if (pre is null)
        {
            throw BridgeFunctions.Contract("clock authority required");
        }
        BridgeFunctions.ClockWire(pre);
        BridgeFunctions.ValidateIdentity(pre.Identity);
        BridgeFunctions.BuildingAttempt(pre.Attempt);
        if (pre.ExpectedGeneration is null || pre.ExpectedGeneration.GetValueOrDefault() == 0)
        {
            throw BridgeFunctions.Contract("clock authority presence");
        }
    }

    private static void Owned(PbClock.OwnedRequest? request)
    {
        if (request is null)
        {
            throw BridgeFunctions.Contract("owned clock request required");
        }
        BridgeFunctions.ClockWire(request);
        BridgeFunctions.ValidateIdentity(request.Identity);
        BridgeFunctions.ClockOwner(request.Owner);
    }

    private static void Original(PbClock.OwnedRequest? request, PbAuthority.WritePrecondition pre, PbClock.Epoch? original)
    {
        Owned(request);
        BridgeFunctions.ClockEpoch(original);
        if (pre.ExpectedGeneration.GetValueOrDefault() != original!.Origin?.NativeGeneration.GetValueOrDefault()
            || !BridgeFunctions.SameIdentity(request!.Identity, pre.Identity)
            || !BridgeFunctions.SameIdentity(original.Origin?.Identity, request.Identity)
            || !Equals(original.Owner, request.Owner)
            || (request.Owner?.ControllerSessionId ?? "") != (pre.Attempt?.ControllerSessionId ?? ""))
        {
            throw BridgeFunctions.Contract("clock original epoch mismatch");
        }
    }

    /// <summary>Go <c>ClockControl.Start</c>.</summary>
    public async Task<PbClock.ControlReply> StartAsync(PbClock.StartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BridgeFunctions.ClockWire(request);
        Precondition(request.Authority);
        BridgeFunctions.AuthorityDuration(request.LeaseMs);
        if (request.Speed is null
            || (int)request.Speed.GetValueOrDefault() < 1
            || (int)request.Speed.GetValueOrDefault() > 4
            || request.MaxTicks is null
            || request.MaxTicks.GetValueOrDefault() < 1
            || request.MaxTicks.GetValueOrDefault() > 1800000)
        {
            throw BridgeFunctions.Contract("clock start speed or tick budget");
        }
        if (request.TestAcceleration.GetValueOrDefault() && request.Speed.GetValueOrDefault() != PbClock.Speed.Ultrafast)
        {
            throw BridgeFunctions.Contract("clock test acceleration requires ultrafast");
        }
        if ((request.BlindTickBudget is not null && (request.BlindTickBudget.GetValueOrDefault() < 1 || request.BlindTickBudget.GetValueOrDefault() > 1800000))
            || (request.MaxTicksPerSecond is not null && (request.MaxTicksPerSecond.GetValueOrDefault() < 1 || request.MaxTicksPerSecond.GetValueOrDefault() > 60000)))
        {
            throw BridgeFunctions.Contract("clock blind tick budget or tick rate ceiling");
        }
        BridgeFunctions.ClockPacing(request.Pacing.GetValueOrDefault(), request.Speed.GetValueOrDefault(), request.TestAcceleration.GetValueOrDefault());
        BridgeFunctions.ClockPolicy(request.Policy, request.MaxTicks.GetValueOrDefault());
        var pre = request.Authority!;
        var expectation = new ClockExpectation(
            pre.Identity,
            pre.Attempt,
            pre.ExpectedGeneration.GetValueOrDefault(),
            new ClockCommand(Start: new ClockStart(
                request.Speed.GetValueOrDefault(),
                request.Policy,
                request.LeaseMs.GetValueOrDefault(),
                request.MaxTicks.GetValueOrDefault(),
                request.TestAcceleration.GetValueOrDefault(),
                request.BlindTickBudget.GetValueOrDefault(),
                request.MaxTicksPerSecond.GetValueOrDefault(),
                request.Pacing.GetValueOrDefault() == PbClock.Pacing.PlayerAccelerated)));
        return await CallAsync(() => _facade.StartClockAsync(request, ct), pre, expectation, ct).ConfigureAwait(false);
    }

    /// <summary>Go <c>ClockControl.Renew</c>.</summary>
    public async Task<PbClock.ControlReply> RenewAsync(PbClock.RenewRequest request, PbClock.Epoch original, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BridgeFunctions.ClockWire(request);
        Precondition(request.Authority);
        BridgeFunctions.AuthorityDuration(request.LeaseMs);
        var pre = request.Authority!;
        Original(request.Epoch, pre, original);
        var expectation = new ClockExpectation(
            pre.Identity,
            pre.Attempt,
            pre.ExpectedGeneration.GetValueOrDefault(),
            new ClockCommand(Renew: new ClockRenew(original, request.LeaseMs.GetValueOrDefault())));
        return await CallAsync(() => _facade.RenewClockAsync(request, ct), pre, expectation, ct).ConfigureAwait(false);
    }

    /// <summary>Go <c>ClockControl.ChangeSpeed</c>.</summary>
    public async Task<PbClock.ControlReply> ChangeSpeedAsync(PbClock.SpeedRequest request, PbClock.Epoch original, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BridgeFunctions.ClockWire(request);
        Precondition(request.Authority);
        if (request.Speed is null || (int)request.Speed.GetValueOrDefault() < 1 || (int)request.Speed.GetValueOrDefault() > 4)
        {
            throw BridgeFunctions.Contract("clock ordinary speed required");
        }
        if ((original?.TestAcceleration).GetValueOrDefault() && request.Speed.GetValueOrDefault() != PbClock.Speed.Ultrafast)
        {
            throw BridgeFunctions.Contract("clock accelerated epoch cannot change speed");
        }
        if (request.MaxTicksPerSecond is not null && (request.MaxTicksPerSecond.GetValueOrDefault() < 1 || request.MaxTicksPerSecond.GetValueOrDefault() > 60000))
        {
            throw BridgeFunctions.Contract("clock tick rate ceiling");
        }
        var pre = request.Authority!;
        Original(request.Epoch, pre, original);
        var expectation = new ClockExpectation(
            pre.Identity,
            pre.Attempt,
            pre.ExpectedGeneration.GetValueOrDefault(),
            new ClockCommand(Speed: new ClockSpeed(original, request.Speed.GetValueOrDefault(), request.MaxTicksPerSecond)));
        return await CallAsync(() => _facade.ChangeClockSpeedAsync(request, ct), pre, expectation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Go <c>ClockControl.OwnedPause</c>: remains usable after authority revocation. Its exact original epoch prevents
    /// it from stopping a replacement owner. Inspect the returned pause facts; Stopping is an armed failed pause, not
    /// proof that the game stopped.
    /// </summary>
    public async Task<PbClock.StatusReply> OwnedPauseAsync(PbClock.OwnedRequest request, CancellationToken ct)
    {
        Owned(request);
        ct.ThrowIfCancellationRequested();
        PbClock.StatusReply reply;
        try
        {
            reply = await _facade.PauseClockAsync(request, ct).ConfigureAwait(false);
            var status = FacadeWriteChecks.ClockStatusReply(reply, request.Identity);
            var epoch = BridgeFunctions.ClockStatusEpoch(status);
            if (epoch is null
                || !Equals(epoch.Owner, request.Owner)
                || !BridgeFunctions.SameIdentity(epoch.Origin?.Identity, request.Identity)
                || status.State is PbClock.StatusState.RunningCase)
            {
                throw BridgeFunctions.Contract("owned pause epoch/state mismatch");
            }
        }
        catch (BridgeRefusedException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ClockUncertainException(e, FacadeNativeClient.NoReceipt);
        }
        return reply;
    }

    /// <summary>Go <c>ClockControl.clockCall</c>: the reply of a control call, after its outcome is checked against the expectation.</summary>
    private static async Task<PbClock.ControlReply> CallAsync(
        Func<Task<PbClock.ControlReply>> call,
        PbAuthority.WritePrecondition pre,
        ClockExpectation expectation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PbClock.ControlReply reply;
        try
        {
            reply = await call().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ClockUncertainException(e, FacadeNativeClient.NoReceipt);
        }
        BridgeRefusedException? refused = null;
        try
        {
            switch (reply.Outcome)
            {
                case PbClock.ControlReplyOutcome.FailureCase { Value: var failure }:
                    refused = FacadeNativeClient.Failure(failure) as BridgeRefusedException;
                    break;
                case PbClock.ControlReplyOutcome.LongEventPendingCase { Value: var pending }:
                    if (!BridgeFunctions.Diagnostic(pending.Detail is not null, pending.Detail ?? ""))
                    {
                        throw BridgeFunctions.Contract("invalid clock pending result");
                    }
                    return reply;
                case PbClock.ControlReplyOutcome.ReceiptCase { Value: var receipt }:
                    BridgeFunctions.ClockReceipt(receipt, pre.Identity, pre.Attempt, pre.ExpectedGeneration.GetValueOrDefault());
                    if (receipt.Outcome is PbClock.ControlReceiptOutcome.UncertainCase)
                    {
                        throw new InvalidOperationException("native reported uncertain clock control");
                    }
                    BridgeFunctions.ValidateClockReceipt(receipt, expectation);
                    return reply;
                default:
                    throw BridgeFunctions.Contract("clock control outcome missing");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ClockUncertainException(e, FacadeNativeClient.NoReceipt);
        }
        throw refused!;
    }
}
