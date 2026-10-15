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
/// The controller-clock journal's logic (Go <c>internal/store/clock</c>): the native
/// event inbox, review and acknowledgement, and history compaction. Every function takes the
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

    /// <summary>Go <c>filepath.Clean</c> for a path <see cref="Path.GetFullPath(string)"/> already normalized: no trailing separator unless a root.</summary>
    internal static string CleanPath(string full) =>
        string.Equals(Path.GetPathRoot(full), full, StringComparison.Ordinal) ? full : Path.TrimEndingDirectorySeparator(full);
}

/// <summary>Column readers the clock rows need beyond <see cref="Rows"/>.</summary>
internal static class ClockRows
{
    /// <summary>A BLOB column; null when the value is NULL.</summary>
    public static byte[]? Blob(this SqliteDataReader r, int column) => r.IsDBNull(column) ? null : (byte[])r.GetValue(column);

    /// <summary>A BLOB column that must not be NULL.</summary>
    public static byte[] RequiredBlob(this SqliteDataReader r, int column) => r.Blob(column) ?? throw new StoreCorruptException("clock record has a missing blob");
}
