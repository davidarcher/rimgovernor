using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Contracts.Wire;
using Pb = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The controller-clock journal's logic (Go <c>internal/store/clock</c>): attempts, epoch ownership, the native
/// event inbox, review and acknowledgement, history compaction and retirement. Every function takes the
/// transaction the caller already opened; the <see cref="Store"/> methods in <c>Store.Clock.cs</c> wrap them.
/// </summary>
/// <remarks>
/// Native evidence (replies, epochs, observation contexts, statuses, event pages) persists as protobuf blobs and is
/// read back only when it is canonical. The small journal bookkeeping records Go kept as canonical JSON blobs
/// (the sequence head, review head and log, history checkpoint, acknowledgement archive and the intent itself) are
/// plain typed columns here, so there is no second serialization format to keep canonical.
/// </remarks>
public static partial class ClockJournal
{
    /// <summary>Go <c>RecordLimit</c>: the maximum encoded size of a clock evidence record.</summary>
    public const int RecordLimit = 1 << 20;

    /// <summary>Go <c>InboxCapacity</c>: the maximum number of retained clock inbox pages and events.</summary>
    public const int InboxCapacity = 4096;

    internal const int InboxBytes = 16 << 20;

    /// <summary>Go <c>clockBinary</c>: the deterministic encoding of a clock evidence message, bounded.</summary>
    internal static byte[] Binary<T>(T message)
        where T : class
    {
        var bytes = WireJson.SerializeToUtf8Bytes(message);
        DomainException.Require(bytes.Length <= RecordLimit, "clock evidence exceeds bound");
        return bytes;
    }

    /// <summary>Go <c>canonicalClockBytes</c>: the encoding without the record bound, for comparing retained pages.</summary>
    internal static byte[] CanonicalBytes<T>(T message)
        where T : class => WireJson.SerializeToUtf8Bytes(message);

    /// <summary>Parses a wire-JSON record; no canonical check. The decoder rejects unknown members.</summary>
    internal static T Parse<T>(byte[] bytes, int sizeLimit = RecordLimit)
        where T : class
    {
        if (bytes.Length > sizeLimit)
        {
            throw new StoreCorruptException("clock evidence exceeds bound");
        }
        try
        {
            return WireJson.Deserialize<T>(bytes);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new StoreCorruptException("undecodable clock record: " + e.Message);
        }
    }

    /// <summary>Go <c>clockUnmarshal</c>: parses a persisted record and rejects anything that is not its own canonical encoding.</summary>
    internal static T Unmarshal<T>(byte[] bytes)
        where T : class
    {
        if (bytes.Length > RecordLimit)
        {
            throw new StoreCorruptException("clock evidence exceeds bound");
        }
        var message = Parse<T>(bytes);
        if (!Binary(message).AsSpan().SequenceEqual(bytes))
        {
            throw new StoreCorruptException("noncanonical clock protobuf");
        }
        return message;
    }

    /// <summary>Go <c>clockExpectation</c>: the original admission facts of an attempt, for receipt recovery.</summary>
    internal static ClockExpectation Expectation(ClockAttempt v)
    {
        var s = v.Intent.Snapshot;
        var identity = new PbCommon.Identity { ColonyId = s.Colony.Value, MapId = s.Map.Value, LoadToken = s.Load.Value };
        return new ClockExpectation(identity, v.NativeAttempt, s.Native.Value, v.Intent.Command);
    }

    /// <summary>Go <c>validateClockIntent</c>.</summary>
    internal static void ValidateIntent(ClockAttempt v)
    {
        var s = v.Intent.Snapshot;
        if (v.Intent.Key.Length != 0)
        {
            Submissions.ValidateId(v.Intent.Key);
        }
        _ = ParseRequestId(new ControllerSessionId(v.NativeAttempt?.ControllerSessionId ?? ""), v.Intent.RequestId);
        DomainException.Require(s.IsValid && s.Native.Value != 0 && s.Revision.Value != 0, "invalid clock admission snapshot");
        DomainException.Require(v.NativeAttempt is { AttemptId: 1 }, "clock command requires its own first attempt");
        ValidateWindow(v.Intent);
        BridgeFunctions.ValidateClockExpectation(Expectation(v));
    }

    /// <summary>Go <c>clockReplyPhase</c>: the phase a control reply establishes.</summary>
    internal static ClockPhase ReplyPhase(Pb.ControlReply reply) =>
        reply.Outcome switch
        {
            Pb.ControlReplyOutcome.ReceiptCase receipt => receipt.Value.Outcome is Pb.ControlReceiptOutcome.AppliedCase ? ClockPhase.Applied : ClockPhase.Uncertain,
            Pb.ControlReplyOutcome.FailureCase failure => failure.Value.Code.GetValueOrDefault() == PbCommon.FailureCode.AttemptConflict ? ClockPhase.Uncertain : ClockPhase.Refused,
            _ => ClockPhase.Refused,
        };

    /// <summary>Go's <c>proto.Equal</c>: value equality, with two absent messages equal.</summary>
    internal static bool SameMessage(object? a, object? b) => Equals(a, b);

    // ---- intent columns -------------------------------------------------------------------------------------

    /// <summary>The intent as the <c>clock_attempts</c> columns that hold it, comparable by value.</summary>
    internal sealed record IntentRow(
        string Key,
        string Colony,
        int Map,
        string Load,
        string Plan,
        ulong Revision,
        ulong Native,
        string Kind,
        int Speed,
        uint MaxTicks,
        string Policy,
        string Original,
        bool TestAcceleration,
        string? WindowProfile,
        long? WindowTick,
        ulong? WindowReviewRevision,
        long? WindowCapturedCursor,
        uint? WindowMaxTicks
    );

    /// <summary>The columns <see cref="ReadIntentRow"/> reads, in order.</summary>
    internal const string IntentColumns =
        "intent_key,colony,map_id,load_token,plan_id,revision,native_generation,kind,speed,max_ticks,policy,original,test_acceleration,window_profile,window_tick,window_review_revision,window_captured_cursor,window_max_ticks";

    /// <summary>The placeholders <see cref="IntentParameters"/> binds, in the order of <see cref="IntentColumns"/>.</summary>
    internal const string IntentValues =
        "$key,$colony,$map,$load,$plan,$revision,$native,$kind,$speed,$maxticks,$policy,$original,$test,$wprofile,$wtick,$wrevision,$wcursor,$wmaxticks";

    /// <summary>Go <c>encodeClockIntent</c>: validates the attempt and returns the intent's column values.</summary>
    internal static IntentRow EncodeIntent(ClockAttempt v)
    {
        ValidateIntent(v);
        var intent = v.Intent;
        var s = intent.Snapshot;
        string kind;
        var speed = 0;
        uint maxTicks = 0;
        bool test = false;
        var policy = "";
        var original = "";
        if (intent.Command.Start is { } start)
        {
            kind = "start";
            speed = (int)start.Speed;
            maxTicks = start.MaxTicks;
            test = start.TestAcceleration;
            policy = Encoding.UTF8.GetString(Binary(start.Policy!));
        }
        else
        {
            var change = intent.Command.Speed!;
            kind = "speed";
            speed = (int)change.Speed;
            original = Encoding.UTF8.GetString(Binary(change.Original!));
        }
        var w = intent.Window;
        return new IntentRow(
            intent.Key,
            s.Colony.Value,
            s.Map.Value,
            s.Load.Value,
            s.Plan.Value,
            s.Revision.Value,
            s.Native.Value,
            kind,
            speed,
            maxTicks,
            policy,
            original,
            test,
            w?.Profile,
            w?.Tick.Value,
            w?.ReviewRevision,
            w?.CapturedCursor,
            w?.MaxTicks
        );
    }

    /// <summary>The query parameters that store <paramref name="row"/>, named after <see cref="IntentColumns"/>.</summary>
    internal static (string Name, object? Value)[] IntentParameters(IntentRow row) =>
        [
            ("$key", row.Key),
            ("$colony", row.Colony),
            ("$map", row.Map),
            ("$load", row.Load),
            ("$plan", row.Plan),
            ("$revision", row.Revision),
            ("$native", row.Native),
            ("$kind", row.Kind),
            ("$speed", row.Speed),
            ("$maxticks", (long)row.MaxTicks),
            ("$policy", Encoding.UTF8.GetBytes(row.Policy)),
            ("$original", Encoding.UTF8.GetBytes(row.Original)),
            ("$test", row.TestAcceleration ? 1 : 0),
            ("$wprofile", row.WindowProfile),
            ("$wtick", row.WindowTick),
            ("$wrevision", row.WindowReviewRevision),
            ("$wcursor", row.WindowCapturedCursor),
            ("$wmaxticks", row.WindowMaxTicks is { } m ? (object)(long)m : null),
        ];

    /// <summary>Reads the intent columns starting at <paramref name="at"/> in the order of <see cref="IntentColumns"/>.</summary>
    internal static IntentRow ReadIntentRow(SqliteDataReader r, int at) =>
        new(
            r.GetString(at),
            r.GetString(at + 1),
            checked((int)r.GetInt64(at + 2)),
            r.GetString(at + 3),
            r.GetString(at + 4),
            r.UnsignedText(at + 5),
            r.UnsignedText(at + 6),
            r.GetString(at + 7),
            checked((int)r.GetInt64(at + 8)),
            checked((uint)r.GetInt64(at + 9)),
            Encoding.UTF8.GetString(r.RequiredBlob(at + 10)),
            Encoding.UTF8.GetString(r.RequiredBlob(at + 11)),
            r.GetInt64(at + 12) != 0,
            r.Text(at + 13),
            r.Number(at + 14),
            r.IsDBNull(at + 15) ? null : r.UnsignedText(at + 15),
            r.Number(at + 16),
            r.Number(at + 17) is { } maxTicks ? checked((uint)maxTicks) : null
        );

    /// <summary>
    /// Go <c>decodeClockIntent</c>: rebuilds the intent from its columns and refuses a row whose columns do not
    /// round-trip, so a start row carrying speed-change fields (or the reverse) is corrupt.
    /// </summary>
    internal static ClockIntent DecodeIntent(string id, PbCommon.AttemptKey attempt, IntentRow row)
    {
        var snapshot = new GenerationSnapshot(new ColonyId(row.Colony), new MapId(row.Map), new LoadId(row.Load), new PlanId(row.Plan), new PlanRevision(row.Revision), new NativeGeneration(row.Native));
        ClockWindowAdmission? window = row.WindowProfile is null
            ? null
            : new ClockWindowAdmission(
                row.WindowProfile,
                snapshot,
                new Tick(row.WindowTick ?? 0),
                row.WindowReviewRevision ?? 0,
                row.WindowCapturedCursor ?? 0,
                row.WindowMaxTicks ?? 0
            );
        ClockCommand command;
        switch (row.Kind)
        {
            case "start":
                command = new ClockCommand(
                    Start: new ClockStart(
                        (Pb.Speed)row.Speed,
                        Unmarshal<Pb.WatchPolicy>(Encoding.UTF8.GetBytes(row.Policy)),
                        row.MaxTicks,
                        row.TestAcceleration
                    )
                );
                break;
            case "speed":
                command = new ClockCommand(
                    Speed: new ClockSpeed(Unmarshal<Pb.Epoch>(Encoding.UTF8.GetBytes(row.Original)), (Pb.Speed)row.Speed)
                );
                break;
            default:
                throw new StoreCorruptException("unknown persisted clock command");
        }
        var intent = new ClockIntent(id, row.Key, snapshot, command, window);
        if (!EncodeIntent(new ClockAttempt(intent, attempt, ClockPhase.Prepared)).Equals(row))
        {
            throw new StoreCorruptException("extraneous clock command fields");
        }
        return intent;
    }

    // ---- finite window admission ----------------------------------------------------------------------------

    /// <summary>Go <c>validateClockWindow</c>.</summary>
    internal static void ValidateWindow(ClockIntent intent)
    {
        var w = intent.Window;
        if (w is null)
        {
            return;
        }
        DomainException.Require(
            intent.Command.Start is not null
                && w.Snapshot == intent.Snapshot
                && w.Tick.Value >= 0
                && w.MaxTicks != 0
                && w.MaxTicks <= 1800000
                && w.MaxTicks == intent.Command.Start.MaxTicks
                && w.Tick.Value <= long.MaxValue - w.MaxTicks
                && w.CapturedCursor >= 0,
            "invalid finite clock window admission"
        );
        DomainException.Require(CanonicalProfilePath(w.Profile), "clock window requires a canonical profile path");
    }

    /// <summary>Go's <c>utf8.ValidString && len &lt;= 4096 && no NUL && filepath.IsAbs && filepath.Clean(p) == p</c>.</summary>
    private static bool CanonicalProfilePath(string profile)
    {
        if (profile.Contains('\0'))
        {
            return false;
        }
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(profile) > 4096)
            {
                return false;
            }
            if (!Path.IsPathFullyQualified(profile))
            {
                return false;
            }
            var full = Path.GetFullPath(profile);
            return string.Equals(full, profile, StringComparison.Ordinal) && string.Equals(CleanPath(full), full, StringComparison.Ordinal);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Go <c>filepath.Clean</c> for a path <see cref="Path.GetFullPath(string)"/> already normalized: no trailing separator unless a root.</summary>
    internal static string CleanPath(string full) =>
        string.Equals(Path.GetPathRoot(full), full, StringComparison.Ordinal) ? full : Path.TrimEndingDirectorySeparator(full);

    /// <summary>Go <c>checkClockWindowProfile</c>: the admission names the profile this journal is bound to.</summary>
    internal static void CheckWindowProfile(StoreTransaction tx, ClockWindowAdmission? w)
    {
        if (w is null)
        {
            return;
        }
        var (profile, found) = tx.QueryFirst("SELECT profile FROM clock_inbox WHERE singleton=1", [], r => r.GetString(0));
        if (!found)
        {
            throw new DomainException("clock inbox profile is not bound");
        }
        if (!SameProfile(profile!, w.Profile))
        {
            throw new ConflictException();
        }
    }

    /// <summary>
    /// Go <c>checkClockWindowDispatch</c>: the ingestion/review watermark and dispatch share one transaction.
    /// Callers cannot bypass this check by using the ordinary clock dispatch method.
    /// </summary>
    internal static void CheckWindowDispatch(StoreTransaction tx, ClockWindowAdmission? w)
    {
        if (w is null)
        {
            return;
        }
        var replay = LoadReview(tx, w.Profile);
        var state = ReviewStateOf(replay.Head, replay.Inbox, replay.Inbox.State.Cursor);
        if (state.Revision != w.ReviewRevision || state.InboxCursor != w.CapturedCursor || state.ReviewedCursor != w.CapturedCursor || state.Holds.Count != 0)
        {
            throw new ConflictException();
        }
    }
}

/// <summary>Column readers the clock rows need beyond <see cref="Rows"/>.</summary>
internal static class ClockRows
{
    /// <summary>A BLOB column; null when the value is NULL.</summary>
    public static byte[]? Blob(this SqliteDataReader r, int column) => r.IsDBNull(column) ? null : (byte[])r.GetValue(column);

    /// <summary>A BLOB column that must not be NULL.</summary>
    public static byte[] RequiredBlob(this SqliteDataReader r, int column) => r.Blob(column) ?? throw new StoreCorruptException("clock record has a missing blob");
}
