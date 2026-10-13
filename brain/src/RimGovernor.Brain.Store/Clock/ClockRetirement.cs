using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/retirement.go.
public static partial class ClockJournal
{
    private const uint KeepRecentLimit = 256;

    /// <summary>
    /// Go <c>RetireHistory</c>: drops only settled or never-dispatched history. The exact retained index and
    /// retirement watermark commit with deletions, so retirement cannot turn an old request into permission for
    /// another native dispatch.
    /// </summary>
    internal static ClockRetirement RetireHistory(StoreTransaction tx, ClockSequenceState expected, uint keepRecent)
    {
        if (keepRecent > KeepRecentLimit)
        {
            throw new CapacityException();
        }
        var (namespaceId, head) = LoadSequence(tx);
        var actual = new ClockSequenceState(namespaceId, head.LastAllocated, head.RetiredThrough);
        if (actual != expected)
        {
            throw new ConflictException();
        }
        var ids = new Dictionary<ulong, string>(head.Retained.Count);
        foreach (var id in tx.Query("SELECT request_id FROM clock_attempts", [], r => r.GetString(0)))
        {
            ids[ParseRequestId(namespaceId, id)] = id;
        }
        var attempts = new ClockAttempt[head.Retained.Count];
        var epochs = new ClockEpochObligation[head.Retained.Count];
        var keep = new bool[head.Retained.Count];
        var epochCount = 0;
        var latestWindow = -1;
        for (var i = 0; i < head.Retained.Count; i++)
        {
            if (!ids.TryGetValue(head.Retained[i], out var id))
            {
                throw new StoreCorruptException("retained clock attempt missing");
            }
            var v = LoadAttemptUnchecked(tx, id, namespaceId);
            attempts[i] = v;
            var owned = CheckEpoch(tx, v);
            epochs[i] = owned;
            if (AppliedStartEpoch(v) is not null)
            {
                epochCount++;
                if (!TerminalEpoch(owned.Stage))
                {
                    keep[i] = true;
                }
            }
            if (v.Phase is ClockPhase.Dispatched or ClockPhase.Uncertain)
            {
                keep[i] = true;
            }
            if (v.Intent.Window is not null)
            {
                latestWindow = i;
            }
        }
        if (tx.Scalar("SELECT count(*) FROM clock_epochs") != epochCount)
        {
            throw new StoreCorruptException("orphaned clock epoch obligation");
        }
        if (latestWindow >= 0)
        {
            keep[latestWindow] = true;
        }
        for (var i = Math.Max(0, keep.Length - (int)keepRecent); i < keep.Length; i++)
        {
            keep[i] = true;
        }
        // Retained commands keep their original Start provenance, including a terminal epoch. No dependency is
        // inferred merely from a matching session or world.
        for (var i = 0; i < attempts.Length; i++)
        {
            if (!keep[i])
            {
                continue;
            }
            var command = attempts[i].Intent.Command;
            var original = command.Speed?.Original ?? command.Renew?.Original;
            if (original is null)
            {
                continue;
            }
            for (var j = 0; j < epochs.Length; j++)
            {
                if (epochs[j].Epoch is { } owner && SameMessage(owner.Owner, original.Owner) && SameMessage(owner.Origin, original.Origin))
                {
                    keep[j] = true;
                }
            }
        }
        var retained = new List<ulong>(head.Retained.Count);
        var removedAttempts = 0;
        var removedEpochs = 0;
        for (var i = 0; i < head.Retained.Count; i++)
        {
            if (keep[i])
            {
                retained.Add(head.Retained[i]);
                continue;
            }
            var requestId = attempts[i].Intent.RequestId;
            if (epochs[i].Epoch is not null)
            {
                _ = tx.Execute("DELETE FROM clock_epochs WHERE start_request_id=$id", ("$id", requestId));
                removedEpochs++;
            }
            _ = tx.Execute("DELETE FROM clock_attempts WHERE request_id=$id", ("$id", requestId));
            removedAttempts++;
        }
        head.RetiredThrough = head.LastAllocated;
        head.Retained = retained;
        SaveSequence(tx, head);
        _ = LoadSequence(tx);
        return new ClockRetirement(new ClockSequenceState(namespaceId, head.LastAllocated, head.RetiredThrough), removedAttempts, removedEpochs, retained.Count);
    }
}
