using System.Security.Cryptography;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using Pb = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/attempts.go and scope.go.
public static partial class ClockJournal
{
    private const int AttemptLimit = 4096;

    private const string AttemptColumns = "request_id,native_action_id,phase,reply,scope_context," + IntentColumns;

    /// <summary>Go <c>scanClock</c>: reads one attempt row and checks it against its own recorded outcome.</summary>
    private static ClockAttempt ScanAttempt(StoreTransaction tx, string id, ControllerSessionId session)
    {
        var (row, found) = tx.QueryFirst(
            "SELECT " + AttemptColumns + " FROM clock_attempts WHERE request_id=$id",
            [("$id", id)],
            r => (Id: r.GetString(0), Action: r.GetString(1), Phase: r.GetString(2), Reply: r.Blob(3), Scope: r.Blob(4), Intent: ReadIntentRow(r, 5))
        );
        if (!found)
        {
            throw new NotFoundException();
        }
        var attempt = new PbCommon.AttemptKey { ControllerSessionId = session.Value, ActionId = row.Action, AttemptId = 1 };
        var intent = DecodeIntent(row.Id, attempt, row.Intent);
        if (!Wire.TryParse<ClockPhase>(row.Phase, out var phase) || phase == ClockPhase.None)
        {
            throw new StoreCorruptException("invalid clock phase");
        }
        var value = new ClockAttempt(intent, attempt, phase);
        if (row.Reply is not null)
        {
            var reply = Unmarshal<Pb.ControlReply>(row.Reply);
            value = value with { Reply = reply };
            BridgeFunctions.ValidateClockControlReply(reply, Expectation(value));
        }
        switch (phase)
        {
            case ClockPhase.Prepared:
            case ClockPhase.Dispatched:
                if (value.Reply is not null)
                {
                    throw new StoreCorruptException("unissued clock phase contains outcome");
                }
                break;
            case ClockPhase.Uncertain:
                if (value.Reply is not null && ReplyPhase(value.Reply) != ClockPhase.Uncertain)
                {
                    throw new StoreCorruptException("uncertain clock phase contradicts outcome");
                }
                break;
            default:
                if (value.Reply is null || ReplyPhase(value.Reply) != phase)
                {
                    throw new StoreCorruptException("terminal clock phase missing matching outcome");
                }
                break;
        }
        if (row.Scope is not null)
        {
            var scope = Unmarshal<PbCommon.ObservationContext>(row.Scope);
            value = value with { SupersededAt = scope };
            ValidateScope(value, scope);
        }
        return value;
    }

    /// <summary>Go <c>loadClock</c>: an attempt by request id, distinguishing retired, missing and never-allocated ids.</summary>
    internal static ClockAttempt LoadAttempt(StoreTransaction tx, string id)
    {
        var (session, head) = LoadSequence(tx);
        var n = ParseRequestId(session, id);
        if (head.Retained.BinarySearch(n) < 0)
        {
            if (n <= head.RetiredThrough)
            {
                throw new ClockRetiredException();
            }
            if (n <= head.LastAllocated)
            {
                throw new StoreCorruptException("missing allocated clock request");
            }
            throw new NotFoundException();
        }
        return LoadAttemptUnchecked(tx, id, session);
    }

    /// <summary>Go <c>loadClockUnchecked</c>: for a caller that already checked the sequence head.</summary>
    internal static ClockAttempt LoadAttemptUnchecked(StoreTransaction tx, string id, ControllerSessionId session)
    {
        var value = ScanAttempt(tx, id, session);
        ActionAvailable(tx, (value.NativeAttempt!.ActionId ?? ""));
        CheckWindowProfile(tx, value.Intent.Window);
        _ = CheckEpoch(tx, value);
        return value;
    }

    /// <summary>Go <c>clockActionAvailable</c>: a native action id the plan catalog also uses is a collision.</summary>
    internal static void ActionAvailable(StoreTransaction tx, string action)
    {
        if (tx.Scalar("SELECT count(*) FROM actions WHERE id=$id", ("$id", action)) != 0)
        {
            throw new ConflictException();
        }
    }

    /// <summary>
    /// Go <c>Prepare</c>: <c>Created</c> is false only for exact durable intent replay. It records no live lease and
    /// grants no permission to call native control.
    /// </summary>
    internal static (ClockAttempt Attempt, bool Created) Prepare(StoreTransaction tx, ClockIntent intent)
    {
        var (session, head) = LoadSequence(tx);
        var sequence = ParseRequestId(session, intent.RequestId);
        var entropy = RandomNumberGenerator.GetBytes(32);
        var native = new PbCommon.AttemptKey { ControllerSessionId = session.Value, ActionId = "clock-" + Convert.ToHexStringLower(entropy), AttemptId = 1 };
        var candidate = new ClockAttempt(intent, native, ClockPhase.Prepared);
        var payload = EncodeIntent(candidate);
        CheckWindowProfile(tx, intent.Window);
        ClockAttempt? old = null;
        try
        {
            old = LoadAttempt(tx, intent.RequestId);
        }
        catch (NotFoundException)
        {
            // Not yet allocated: fall through to allocate it.
        }
        if (old is not null)
        {
            if (!EncodeIntent(old).Equals(payload))
            {
                throw new ConflictException();
            }
            return (old, false);
        }
        if (head.LastAllocated == ulong.MaxValue || sequence != head.LastAllocated + 1)
        {
            throw new ConflictException();
        }
        ActionAvailable(tx, (native.ActionId ?? ""));
        if (tx.Scalar("SELECT count(*) FROM clock_attempts") >= AttemptLimit)
        {
            throw new CapacityException();
        }
        (string Name, object? Value)[] parameters =
        [
            ("$id", intent.RequestId),
            ("$action", (native.ActionId ?? "")),
            ("$phase", Wire.Name(ClockPhase.Prepared)),
            .. IntentParameters(payload),
        ];
        _ = tx.ExecuteOrConflict(
            "INSERT INTO clock_attempts(request_id,native_action_id,phase," + IntentColumns + ") VALUES($id,$action,$phase," + IntentValues + ")",
            parameters
        );
        head.LastAllocated = sequence;
        head.Retained.Add(sequence);
        SaveSequence(tx, head);
        return (LoadAttempt(tx, intent.RequestId), true);
    }

    /// <summary>Go <c>LookupAttempt</c>.</summary>
    internal static ClockAttempt LookupAttempt(StoreTransaction tx, string id)
    {
        Submissions.ValidateId(id);
        return LoadAttempt(tx, id);
    }

    /// <summary>Go <c>LoadAttempts</c>: every retained attempt in sequence order, or <see cref="CapacityException"/> past <paramref name="limit"/>.</summary>
    internal static List<ClockAttempt> LoadAttempts(StoreTransaction tx, int limit)
    {
        DomainException.Require(limit >= 1 && limit <= AttemptLimit, "clock attempt limit must be 1..4096");
        var (session, head) = LoadSequence(tx);
        if (head.Retained.Count > limit)
        {
            throw new CapacityException();
        }
        var attempts = new List<ClockAttempt>(head.Retained.Count);
        foreach (var sequence in head.Retained)
        {
            attempts.Add(LoadAttemptUnchecked(tx, ClockRequestId(session, sequence), session));
        }
        return attempts;
    }

    /// <summary>Go <c>Dispatch</c>: prepared to dispatched; a finite window is rechecked against the review in the same transaction.</summary>
    internal static ClockAttempt Dispatch(StoreTransaction tx, string id) =>
        Update(
            tx,
            id,
            v => v.Phase == ClockPhase.Prepared ? (ClockPhase.Dispatched, null) : throw new ConflictException()
        );

    /// <summary>Go <c>MarkUncertain</c>.</summary>
    internal static ClockAttempt MarkUncertain(StoreTransaction tx, string id) =>
        Update(
            tx,
            id,
            v => v.Phase is ClockPhase.Dispatched or ClockPhase.Uncertain ? (ClockPhase.Uncertain, v.Reply) : throw new ConflictException()
        );

    /// <summary>
    /// Go <c>RecordReply</c>: accepts an original control-call reply or a recovered receipt. A lookup failure must
    /// never be repackaged as a pre-admission control refusal.
    /// </summary>
    internal static ClockAttempt RecordReply(StoreTransaction tx, string id, Pb.ControlReply? reply)
    {
        if (reply is null)
        {
            throw new DomainException("clock reply required");
        }
        var recorded = reply;
        return Update(
            tx,
            id,
            v =>
            {
                BridgeFunctions.ValidateClockControlReply(recorded, Expectation(v));
                if (v.Phase is ClockPhase.Applied or ClockPhase.Refused)
                {
                    return SameMessage(v.Reply, recorded) ? (v.Phase, v.Reply) : throw new ConflictException();
                }
                if (v.Phase is not (ClockPhase.Dispatched or ClockPhase.Uncertain))
                {
                    throw new ConflictException();
                }
                // Admission evidence and attempt conflicts require receipt recovery;
                // a different later refusal cannot establish that nothing happened.
                if (v.Reply is not null && recorded.Outcome?.Receipt is null && !SameMessage(v.Reply, recorded))
                {
                    throw new ConflictException();
                }
                return (ReplyPhase(recorded), recorded);
            }
        );
    }

    private static ClockAttempt Update(StoreTransaction tx, string id, Func<ClockAttempt, (ClockPhase Phase, Pb.ControlReply? Reply)> change)
    {
        Submissions.ValidateId(id);
        var old = LoadAttempt(tx, id);
        if (old.SupersededAt is not null)
        {
            throw new ConflictException();
        }
        var (phase, reply) = change(old);
        if (phase == ClockPhase.Dispatched)
        {
            CheckWindowDispatch(tx, old.Intent.Window);
        }
        if (phase == old.Phase && SameMessage(reply, old.Reply))
        {
            return old;
        }
        _ = tx.Execute(
            "UPDATE clock_attempts SET phase=$phase,reply=$reply WHERE request_id=$id",
            ("$phase", Wire.Name(phase)),
            ("$reply", reply is null ? null : Binary(reply)),
            ("$id", id)
        );
        if (phase == ClockPhase.Applied && old.Intent.Command.Start is not null)
        {
            _ = tx.Execute("INSERT INTO clock_epochs(start_request_id,stage,sequence) VALUES($id,'required','0')", ("$id", id));
        }
        return LoadAttempt(tx, id);
    }

    // ---- world scope supersession ---------------------------------------------------------------------------

    /// <summary>Go <c>validateClockScope</c>: only a dispatched or uncertain start can be superseded, by a different world.</summary>
    private static void ValidateScope(ClockAttempt attempt, PbCommon.ObservationContext observed)
    {
        if (attempt.Intent.Command.Start is null || attempt.Phase is not (ClockPhase.Dispatched or ClockPhase.Uncertain))
        {
            throw new ConflictException();
        }
        BridgeFunctions.ValidateContext(observed);
        var original = attempt.Intent.Snapshot;
        var actual = observed.Identity ?? throw new ConflictException();
        if (string.Equals(actual.ColonyId, original.Colony.Value, StringComparison.Ordinal) && actual.MapId.GetValueOrDefault() == original.Map.Value && string.Equals(actual.LoadToken, original.Load.Value, StringComparison.Ordinal))
        {
            throw new ConflictException();
        }
    }

    /// <summary>
    /// Go <c>MarkScopeSuperseded</c>: retires only a positively replaced world scope. The original command outcome
    /// stays uncertain; no epoch or absence is inferred.
    /// </summary>
    internal static ClockAttempt MarkScopeSuperseded(StoreTransaction tx, string id, PbCommon.ObservationContext? observed)
    {
        Submissions.ValidateId(id);
        if (observed is null)
        {
            throw new ConflictException();
        }
        var data = Binary(observed);
        var old = LoadAttempt(tx, id);
        ValidateScope(old, observed);
        if (old.SupersededAt is not null)
        {
            return SameMessage(old.SupersededAt, observed) ? old : throw new ConflictException();
        }
        _ = tx.Execute("UPDATE clock_attempts SET scope_context=$scope WHERE request_id=$id", ("$scope", data), ("$id", id));
        return LoadAttempt(tx, id);
    }
}
