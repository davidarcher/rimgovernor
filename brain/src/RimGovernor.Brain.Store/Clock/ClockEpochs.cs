using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using Pb = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/epochs.go.
public static partial class ClockJournal
{
    private static readonly ClockEpochObligation _noEpoch = new("", null, ClockEpochStage.None, 0, null, null);

    /// <summary>Go <c>appliedStartEpoch</c>: the epoch an applied Start receipt describes, null for any other attempt.</summary>
    internal static Pb.Epoch? AppliedStartEpoch(ClockAttempt attempt)
    {
        if (attempt.Intent.Command.Start is null || attempt.Phase != ClockPhase.Applied)
        {
            return null;
        }
        var state = attempt.Reply?.Outcome?.Receipt?.Outcome?.Applied?.Status?.State;
        if (state?.Running is { } running)
        {
            return running.Epoch;
        }
        if (state?.Stopping is { } stopping)
        {
            return stopping.Epoch;
        }
        return state?.Stopped?.Epoch;
    }

    /// <summary>
    /// Go <c>checkClockEpoch</c>; also runs when loading its original attempt, so a missing ownership record cannot
    /// silently turn a completed Start into unowned history.
    /// </summary>
    internal static ClockEpochObligation CheckEpoch(StoreTransaction tx, ClockAttempt attempt)
    {
        var id = attempt.Intent.RequestId;
        var original = AppliedStartEpoch(attempt);
        var (row, found) = tx.QueryFirst(
            "SELECT stage,sequence,context,status FROM clock_epochs WHERE start_request_id=$id",
            [("$id", id)],
            r => (Stage: r.GetString(0), Sequence: r.GetString(1), Context: r.Blob(2), Status: r.Blob(3))
        );
        if (original is null)
        {
            return found ? throw new StoreCorruptException("clock epoch has no applied start") : _noEpoch;
        }
        if (!found)
        {
            throw new StoreCorruptException("clock epoch ownership record is missing");
        }
        if (!Wire.TryParse<ClockEpochStage>(row.Stage, out var stage) || stage == ClockEpochStage.None)
        {
            throw new StoreCorruptException("invalid clock epoch stage");
        }
        var sequence = ParseSequenceText(row.Sequence);
        var value = new ClockEpochObligation(
            id,
            original,
            stage,
            sequence,
            row.Context is null ? null : Unmarshal<PbCommon.ObservationContext>(row.Context),
            row.Status is null ? null : Unmarshal<Pb.Status>(row.Status)
        );
        ValidateEpochRecord(value);
        return value;
    }

    private static ulong ParseSequenceText(string text) =>
        ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && string.Equals(n.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal)
            ? n
            : throw new StoreCorruptException("invalid clock pause sequence");

    private static ClockEpochStage StageOf(ClockEpochState state) =>
        state switch
        {
            ClockEpochState.Required => ClockEpochStage.Required,
            ClockEpochState.Uncertain => ClockEpochStage.Uncertain,
            ClockEpochState.Paused => ClockEpochStage.Paused,
            ClockEpochState.Retired => ClockEpochStage.Retired,
            ClockEpochState.Superseded => ClockEpochStage.Superseded,
            _ => ClockEpochStage.None,
        };

    /// <summary>Go <c>validateClockEpochRecord</c>.</summary>
    private static void ValidateEpochRecord(ClockEpochObligation v)
    {
        BridgeFunctions.ValidateClockEpoch(v.Epoch);
        if (v.Context is not null)
        {
            GenerationFloor(v.Epoch!.Origin, v.Context);
            var state = BridgeFunctions.AssessClockEpoch(v.Epoch, v.Context, v.Status);
            // A dispatch retains its last pre-call observation as a freshness floor.
            var pending = v.Sequence > 0 && v.Stage is ClockEpochStage.Pausing or ClockEpochStage.Uncertain;
            DomainException.Require(StageOf(state) == v.Stage || (pending && state == ClockEpochState.Required), "clock epoch stage contradicts evidence");
            return;
        }
        DomainException.Require(v.Status is null, "clock epoch status has no observation context");
        var matching =
            (v.Stage == ClockEpochStage.Required && v.Sequence == 0)
            || (v.Stage == ClockEpochStage.Pausing && v.Sequence > 0)
            || (v.Stage == ClockEpochStage.Uncertain && v.Sequence > 0);
        DomainException.Require(matching, "clock epoch stage has no matching evidence");
    }

    /// <summary>Go <c>loadClockEpoch</c>.</summary>
    private static ClockEpochObligation LoadEpoch(StoreTransaction tx, string id)
    {
        var attempt = LoadAttempt(tx, id);
        return AppliedStartEpoch(attempt) is null ? throw new NotFoundException() : CheckEpoch(tx, attempt);
    }

    /// <summary>Go <c>LookupEpoch</c>.</summary>
    internal static ClockEpochObligation LookupEpoch(StoreTransaction tx, string id)
    {
        Submissions.ValidateId(id);
        return LoadEpoch(tx, id);
    }

    /// <summary>
    /// Go <c>LoadEpochs</c>: checks the complete attempt catalog as well as ownership rows. It never omits a missing
    /// or corrupt obligation from recovery.
    /// </summary>
    internal static List<ClockEpochObligation> LoadEpochs(StoreTransaction tx, int limit)
    {
        if (limit < 1 || limit > AttemptLimit)
        {
            throw new CapacityException();
        }
        // The sequence head is loaded once: it has already checked that the catalog's rows are exactly the
        // retained sequences, so each attempt is read unchecked rather than re-verifying the head per row, which
        // made this read quadratic in the retained tail.
        var (session, head) = LoadSequence(tx);
        var values = new List<ClockEpochObligation>();
        foreach (var sequence in head.Retained)
        {
            var attempt = LoadAttemptUnchecked(tx, ClockRequestId(session, sequence), session);
            if (AppliedStartEpoch(attempt) is null)
            {
                continue;
            }
            values.Add(CheckEpoch(tx, attempt));
            if (values.Count > limit)
            {
                throw new CapacityException();
            }
        }
        if (tx.Scalar("SELECT count(*) FROM clock_epochs") != values.Count)
        {
            throw new StoreCorruptException("orphaned clock epoch obligation");
        }
        return values;
    }

    /// <summary>Go <c>terminalClockEpoch</c>.</summary>
    internal static bool TerminalEpoch(ClockEpochStage stage) => stage is ClockEpochStage.Paused or ClockEpochStage.Retired or ClockEpochStage.Superseded;

    private static ClockEpochObligation UpdateEpoch(StoreTransaction tx, string id, ulong sequence, Func<ClockEpochObligation, ClockEpochObligation> change)
    {
        Submissions.ValidateId(id);
        var old = LoadEpoch(tx, id);
        if (sequence != old.Sequence)
        {
            throw new ConflictException();
        }
        var next = change(old);
        ValidateEpochRecord(next);
        if (old.Stage == next.Stage && old.Sequence == next.Sequence && SameMessage(old.Context, next.Context) && SameMessage(old.Status, next.Status))
        {
            return old;
        }
        _ = tx.Execute(
            "UPDATE clock_epochs SET stage=$stage,sequence=$sequence,context=$context,status=$status WHERE start_request_id=$id",
            ("$stage", Wire.Name(next.Stage)),
            ("$sequence", next.Sequence),
            ("$context", next.Context is null ? null : Binary(next.Context)),
            ("$status", next.Status is null ? null : Binary(next.Status)),
            ("$id", id)
        );
        return next;
    }

    /// <summary>Go <c>BeginPause</c>: required to pausing, advancing the local pause-dispatch fence.</summary>
    internal static ClockEpochObligation BeginPause(StoreTransaction tx, string id, ulong sequence) =>
        UpdateEpoch(
            tx,
            id,
            sequence,
            v =>
            {
                if (v.Stage != ClockEpochStage.Required)
                {
                    throw new ConflictException();
                }
                if (v.Sequence == ulong.MaxValue)
                {
                    throw new CapacityException();
                }
                return v with { Sequence = v.Sequence + 1, Stage = ClockEpochStage.Pausing };
            }
        );

    /// <summary>Go <c>MarkPauseUncertain</c>.</summary>
    internal static ClockEpochObligation MarkPauseUncertain(StoreTransaction tx, string id, ulong sequence) =>
        UpdateEpoch(
            tx,
            id,
            sequence,
            v => v.Stage is ClockEpochStage.Pausing or ClockEpochStage.Uncertain ? v with { Stage = ClockEpochStage.Uncertain } : throw new ConflictException()
        );

    /// <summary>
    /// Go <c>ObserveEpoch</c>: stores fresh evidence under the current pause sequence. An uncertain pause must be
    /// observed as running before another pause dispatch.
    /// </summary>
    internal static ClockEpochObligation ObserveEpoch(StoreTransaction tx, string id, ulong sequence, PbCommon.ObservationContext? current, Pb.Status? status)
    {
        if (current is null)
        {
            throw new DomainException("clock observation context required");
        }
        var observed = current;
        var evidence = status;
        return UpdateEpoch(
            tx,
            id,
            sequence,
            v =>
            {
                if (TerminalEpoch(v.Stage))
                {
                    return SameMessage(v.Context, observed) && SameMessage(v.Status, evidence) ? v : throw new ConflictException();
                }
                GenerationFloor(v.Epoch!.Origin, observed);
                if (v.Context is not null && SameMessage(v.Context.Identity, observed.Identity))
                {
                    var regressed = observed.Tick.GetValueOrDefault() < v.Context.Tick.GetValueOrDefault() || (v.Context.NativeGeneration is not null && (observed.NativeGeneration is null || observed.NativeGeneration.GetValueOrDefault() < v.Context.NativeGeneration.GetValueOrDefault()));
                    if (regressed)
                    {
                        throw new ConflictException();
                    }
                }
                var state = BridgeFunctions.AssessClockEpoch(v.Epoch, observed, evidence);
                return v with { Stage = StageOf(state), Context = observed, Status = evidence };
            }
        );
    }

    /// <summary>Go <c>clockGenerationFloor</c>: a later observation of the same world cannot carry an older native generation.</summary>
    private static void GenerationFloor(PbCommon.ObservationContext? prior, PbCommon.ObservationContext? current)
    {
        if (prior is not null
            && current is not null
            && SameMessage(prior.Identity, current.Identity)
            && prior.NativeGeneration is not null
            && (current.NativeGeneration is null || current.NativeGeneration.GetValueOrDefault() < prior.NativeGeneration.GetValueOrDefault()))
        {
            throw new ConflictException();
        }
    }
}
