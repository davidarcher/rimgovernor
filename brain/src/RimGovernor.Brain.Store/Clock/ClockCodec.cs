using System.Globalization;
using System.Text;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using Pb = RimGovernor.Protocol.Clock;
using PbCommon = RimGovernor.Protocol.Common;

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

    private const int RecursionLimit = 64;

    /// <summary>Go <c>clockBinary</c>: the deterministic encoding of a clock evidence message, bounded.</summary>
    internal static byte[] Binary(IMessage message)
    {
        var bytes = message.ToByteArray();
        DomainException.Require(bytes.Length <= RecordLimit, "clock evidence exceeds bound");
        return bytes;
    }

    /// <summary>Go <c>canonicalClockBytes</c>: the encoding without the record bound, for comparing retained pages.</summary>
    internal static byte[] CanonicalBytes(IMessage message) => message.ToByteArray();

    /// <summary>Parses a protobuf record with the recursion limit the journal applies; no canonical check.</summary>
    internal static T Parse<T>(byte[] bytes, int sizeLimit = RecordLimit)
        where T : class, IMessage<T>, new()
    {
        var message = new T();
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var input = CodedInputStream.CreateWithLimits(stream, sizeLimit, RecursionLimit);
            message.MergeFrom(input);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new StoreCorruptException("undecodable clock protobuf: " + e.Message);
        }
        return message;
    }

    /// <summary>Go <c>clockUnmarshal</c>: parses a persisted record and rejects anything that is not its own canonical encoding.</summary>
    internal static T Unmarshal<T>(byte[] bytes)
        where T : class, IMessage<T>, new()
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
        reply.OutcomeCase switch
        {
            Pb.ControlReply.OutcomeOneofCase.Receipt => reply.Receipt.OutcomeCase == Pb.ControlReceipt.OutcomeOneofCase.Applied ? ClockPhase.Applied : ClockPhase.Uncertain,
            Pb.ControlReply.OutcomeOneofCase.Failure => reply.Failure.Code == PbCommon.FailureCode.AttemptConflict ? ClockPhase.Uncertain : ClockPhase.Refused,
            _ => ClockPhase.Refused,
        };

    /// <summary>Go's <c>proto.Equal</c>: value equality, with two absent messages equal.</summary>
    internal static bool SameMessage(IMessage? a, IMessage? b) => Equals(a, b);

    /// <summary>
    /// Whether <paramref name="message"/> itself (not a nested message) carries a field its descriptor lacks: Go's
    /// <c>len(m.ProtoReflect().GetUnknown()) != 0</c>.
    /// </summary>
    internal static bool HasUnknownFields(IMessage message)
    {
        using var input = new CodedInputStream(message.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (message.Descriptor.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag)) is null)
            {
                return true;
            }
            input.SkipLastField();
        }
        return false;
    }

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
        uint LeaseMs,
        uint MaxTicks,
        ByteString Policy,
        ByteString Original,
        bool TestAcceleration,
        uint BlindTickBudget,
        uint MaxTicksPerSecond,
        bool CeilingSet,
        bool PlayerAccelerated,
        string? WindowProfile,
        long? WindowTick,
        ulong? WindowReviewRevision,
        long? WindowCapturedCursor,
        uint? WindowMaxTicks
    );

    /// <summary>The columns <see cref="ReadIntentRow"/> reads, in order.</summary>
    internal const string IntentColumns =
        "intent_key,colony,map_id,load_token,plan_id,revision,native_generation,kind,speed,lease_ms,max_ticks,policy,original,test_acceleration,blind_tick_budget,max_ticks_per_second,ceiling_set,player_accelerated,window_profile,window_tick,window_review_revision,window_captured_cursor,window_max_ticks";

    /// <summary>The placeholders <see cref="IntentParameters"/> binds, in the order of <see cref="IntentColumns"/>.</summary>
    internal const string IntentValues =
        "$key,$colony,$map,$load,$plan,$revision,$native,$kind,$speed,$lease,$maxticks,$policy,$original,$test,$blind,$perSecond,$ceiling,$player,$wprofile,$wtick,$wrevision,$wcursor,$wmaxticks";

    /// <summary>Go <c>encodeClockIntent</c>: validates the attempt and returns the intent's column values.</summary>
    internal static IntentRow EncodeIntent(ClockAttempt v)
    {
        ValidateIntent(v);
        var intent = v.Intent;
        var s = intent.Snapshot;
        string kind;
        var speed = 0;
        uint lease = 0;
        uint maxTicks = 0;
        uint blind = 0;
        uint perSecond = 0;
        bool test = false;
        bool ceilingSet = false;
        bool player = false;
        var policy = ByteString.Empty;
        var original = ByteString.Empty;
        if (intent.Command.Start is { } start)
        {
            kind = "start";
            speed = (int)start.Speed;
            lease = start.LeaseMs;
            maxTicks = start.MaxTicks;
            test = start.TestAcceleration;
            blind = start.BlindTickBudget;
            perSecond = start.MaxTicksPerSecond;
            player = start.PlayerAccelerated;
            policy = ByteString.CopyFrom(Binary(start.Policy!));
        }
        else if (intent.Command.Renew is { } renew)
        {
            kind = "renew";
            lease = renew.LeaseMs;
            original = ByteString.CopyFrom(Binary(renew.Original!));
        }
        else
        {
            var change = intent.Command.Speed!;
            kind = "speed";
            speed = (int)change.Speed;
            if (change.MaxTicksPerSecond is { } ceiling)
            {
                perSecond = ceiling;
                ceilingSet = true;
            }
            original = ByteString.CopyFrom(Binary(change.Original!));
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
            lease,
            maxTicks,
            policy,
            original,
            test,
            blind,
            perSecond,
            ceilingSet,
            player,
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
            ("$lease", (long)row.LeaseMs),
            ("$maxticks", (long)row.MaxTicks),
            ("$policy", row.Policy.ToByteArray()),
            ("$original", row.Original.ToByteArray()),
            ("$test", row.TestAcceleration ? 1 : 0),
            ("$blind", (long)row.BlindTickBudget),
            ("$perSecond", (long)row.MaxTicksPerSecond),
            ("$ceiling", row.CeilingSet ? 1 : 0),
            ("$player", row.PlayerAccelerated ? 1 : 0),
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
            checked((uint)r.GetInt64(at + 10)),
            ByteString.CopyFrom(r.RequiredBlob(at + 11)),
            ByteString.CopyFrom(r.RequiredBlob(at + 12)),
            r.GetInt64(at + 13) != 0,
            checked((uint)r.GetInt64(at + 14)),
            checked((uint)r.GetInt64(at + 15)),
            r.GetInt64(at + 16) != 0,
            r.GetInt64(at + 17) != 0,
            r.Text(at + 18),
            r.Number(at + 19),
            r.IsDBNull(at + 20) ? null : r.UnsignedText(at + 20),
            r.Number(at + 21),
            r.Number(at + 22) is { } maxTicks ? checked((uint)maxTicks) : null
        );

    /// <summary>
    /// Go <c>decodeClockIntent</c>: rebuilds the intent from its columns and refuses a row whose columns do not
    /// round-trip, so a start row carrying renewal fields (or the reverse) is corrupt.
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
                        Unmarshal<Pb.WatchPolicy>(row.Policy.ToByteArray()),
                        row.LeaseMs,
                        row.MaxTicks,
                        row.TestAcceleration,
                        row.BlindTickBudget,
                        row.MaxTicksPerSecond,
                        row.PlayerAccelerated
                    )
                );
                break;
            case "renew":
                command = new ClockCommand(Renew: new ClockRenew(Unmarshal<Pb.Epoch>(row.Original.ToByteArray()), row.LeaseMs));
                break;
            case "speed":
                command = new ClockCommand(
                    Speed: new ClockSpeed(Unmarshal<Pb.Epoch>(row.Original.ToByteArray()), (Pb.Speed)row.Speed, row.CeilingSet ? row.MaxTicksPerSecond : null)
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
