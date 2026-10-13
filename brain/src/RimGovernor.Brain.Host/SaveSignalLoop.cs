using System.Diagnostics;
using RimGovernor.Brain.NativeFacade;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Holds the one pre_save wait: on a signal it flushes within the budget, then acks whether or not the flush failed
/// (native must not wait out its timeout). It re-arms after every return, a quiet timeout and a failed wait alike
/// (Go <c>runSaveFlusher</c>). Native's SaveGame prefix never cancels a save; it parks the vanilla save for about 3 s
/// while the signal is held.
/// </summary>
internal sealed class SaveSignalLoop(INativeFacade facade, StateFlusher flusher, HostLog log)
{
    /// <summary>How long a pre_save flush may run: native parks the vanilla save for about 3 s, so a flush past it only lands in the next save.</summary>
    internal static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(3);

    /// <summary>Bounds the flush_done call.</summary>
    internal static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The pause after a failed wait.</summary>
    internal static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? token;
            try
            {
                token = await WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.Warn("save_signal_wait_failed", "save signal wait failed", ("error", e.Message));
                try
                {
                    await Task.Delay(Retry, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            if (token is null)
            {
                continue;
            }
            await FlushAndAckAsync(token, ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> WaitAsync(CancellationToken ct)
    {
        var reply = await facade.WaitSaveSignalAsync(new PbLifecycle.WaitSaveSignalRequest(), ct).ConfigureAwait(false);
        return reply.Outcome switch
        {
            PbLifecycle.WaitSaveSignalReplyOutcome.SignalCase signal => signal.Value.Token ?? string.Empty,
            PbLifecycle.WaitSaveSignalReplyOutcome.TimeoutCase => null,
            PbLifecycle.WaitSaveSignalReplyOutcome.FailureCase failure => throw FacadeReplies.Refused("WaitSaveSignal", failure.Value),
            _ => throw new RimGovernor.Brain.Bridge.Contracts.BridgeContractException("WaitSaveSignal reply carried no outcome"),
        };
    }

    private async Task FlushAndAckAsync(string token, CancellationToken ct)
    {
        var began = Stopwatch.GetTimestamp();
        Exception? flushError = null;
        using (var flush = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            flush.CancelAfter(FlushBudget);
            try
            {
                await flusher.FlushAsync(flush.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                flushError = e;
            }
        }
        var flushed = Stopwatch.GetElapsedTime(began);
        string? ackError = null;
        // The ack is not tied to the host's lifetime: a shutdown that cancels the flush still releases native.
        using (var ack = new CancellationTokenSource(AckTimeout))
        {
            try
            {
                var reply = await facade.FlushDoneAsync(new PbLifecycle.FlushDoneRequest { Token = token }, ack.Token).ConfigureAwait(false);
                if (reply.Outcome is PbLifecycle.FlushDoneReplyOutcome.FailureCase failure)
                {
                    ackError = FacadeReplies.Refused("FlushDone", failure.Value).Message;
                }
            }
            catch (Exception e)
            {
                ackError = e.Message;
            }
        }
        if (flushError is not null)
        {
            log.Warn("pre_save_flush_failed", "pre_save flush failed; acked anyway", ("error", flushError.Message), ("flush_ms", (long)flushed.TotalMilliseconds));
        }
        else
        {
            log.Info("pre_save_flushed", "pre_save flushed", ("flush_ms", (long)flushed.TotalMilliseconds), ("total_ms", (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds));
        }
        if (ackError is not null)
        {
            log.Warn("pre_save_ack_failed", "pre_save ack failed", ("error", ackError));
        }
    }
}
