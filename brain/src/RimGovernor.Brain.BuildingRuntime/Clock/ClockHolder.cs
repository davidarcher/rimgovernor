namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Holds the native clock on the session's behalf when the bot stops acting (Manual, a checkpoint save, shutdown):
/// one Hold with the Review reason, always allowed because native needs only the loaded identity. The identity is
/// the last one authority was enabled under, so cleanup can stop play after authority is gone. A clock that was
/// never run, or a world never seen, has nothing to hold.
/// </summary>
public sealed class ClockHolder : IDisposable
{
    private readonly Lock _mu = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IClockWriter _writer;
    private readonly TimeSpan _callTimeout;
    private PbCommon.Identity? _identity;
    private bool _stopped;

    private ClockHolder(IClockWriter writer, TimeSpan callTimeout)
    {
        _writer = writer;
        _callTimeout = callTimeout;
    }

    /// <summary>Throws for a missing writer or a call bound outside (0, 1 minute].</summary>
    public static ClockHolder Create(IClockWriter writer, TimeSpan callTimeout)
    {
        if (writer is null || callTimeout <= TimeSpan.Zero || callTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentException("invalid clock holder dependencies");
        }
        return new ClockHolder(writer, callTimeout);
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Remembers the loaded world authority was enabled under; a disabled authority keeps the last one.</summary>
    public void UpdateAuthority(Authority value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Enabled && (!value.Snapshot.IsValid || value.Snapshot.Native.Value == 0 || value.Snapshot.Revision.Value == 0))
        {
            throw new AuthorityException();
        }
        lock (_mu)
        {
            if (_stopped)
            {
                if (value.Enabled)
                {
                    throw new StoppedException();
                }
                return;
            }
            if (value.Enabled)
            {
                _identity = Boundary.Identity(value.Snapshot);
            }
        }
    }

    /// <summary>Permanently refuses new holds and joins the one in flight; a cancelled token means retry.</summary>
    public async Task Stop(CancellationToken ct)
    {
        lock (_mu)
        {
            _stopped = true;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
    }

    /// <summary>
    /// Holds the clock for <paramref name="reason"/> unless no world was ever seen. A native refusal is an
    /// exception; a clock already held keeps its first reason and answers as success.
    /// </summary>
    public async Task HoldAsync(PbClock.HoldReason reason, string detail, CancellationToken ct)
    {
        PbCommon.Identity? identity;
        lock (_mu)
        {
            identity = _identity;
        }
        if (identity is null)
        {
            return;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(_callTimeout);
            _ = await _writer.HoldAsync(new PbClock.HoldRequest { Identity = identity, Reason = reason, Detail = detail }, bound.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
