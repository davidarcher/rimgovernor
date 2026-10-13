using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/sequence.go.
public static partial class ClockJournal
{
    private const int RetainedLimit = 4096;

    private const int SequenceTextLimit = 100000;

    /// <summary>Go <c>ClockRequestID</c>: the canonical request id of sequence <paramref name="sequence"/> in a journal namespace.</summary>
    public static string ClockRequestId(ControllerSessionId @namespace, ulong sequence)
    {
        var valid = false;
        try
        {
            var bytes = Convert.FromHexString(@namespace.Value);
            valid = bytes.Length == 32 && string.Equals(Convert.ToHexStringLower(bytes), @namespace.Value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            // Not hexadecimal: the namespace stays invalid.
        }
        DomainException.Require(valid && sequence != 0, "invalid clock namespace or sequence");
        return "clock-" + @namespace.Value + "-" + sequence.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Go <c>parseClockRequestID</c>: the sequence a request id carries, which must be canonical in <paramref name="namespace"/>.</summary>
    internal static ulong ParseRequestId(ControllerSessionId @namespace, string id)
    {
        var prefix = "clock-" + @namespace.Value + "-";
        DomainException.Require(id.StartsWith(prefix, StringComparison.Ordinal), "clock request namespace mismatch");
        var parsed = ulong.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n);
        DomainException.Require(parsed && n != 0 && string.Equals(ClockRequestId(@namespace, n), id, StringComparison.Ordinal), "noncanonical clock request sequence");
        return n;
    }

    /// <summary>Go <c>validClockSequence</c>.</summary>
    internal static void ValidateSequence(ClockSequenceHead h)
    {
        DomainException.Require(h.RetiredThrough <= h.LastAllocated && h.Retained.Count <= RetainedLimit, "invalid clock sequence head");
        ulong previous = 0;
        foreach (var n in h.Retained)
        {
            DomainException.Require(n != 0 && n > previous && n <= h.LastAllocated, "invalid retained clock sequence");
            previous = n;
        }
        // Every allocated sequence beyond the retirement watermark must remain present.
        var live = h.LastAllocated - h.RetiredThrough;
        DomainException.Require(live <= (ulong)h.Retained.Count, "missing live clock sequence");
        for (ulong n = 0; n < live; n++)
        {
            DomainException.Require(h.Retained[h.Retained.Count - 1 - (int)n] == h.LastAllocated - n, "missing live clock sequence");
        }
    }

    /// <summary>Go <c>saveClockSequence</c>.</summary>
    internal static void SaveSequence(StoreTransaction tx, ClockSequenceHead h)
    {
        ValidateSequence(h);
        var rows = tx.Execute(
            "UPDATE clock_sequence SET last_allocated=$last,retired_through=$retired,retained=$retained WHERE singleton=1",
            ("$last", h.LastAllocated),
            ("$retired", h.RetiredThrough),
            ("$retained", string.Join(',', h.Retained.Select(n => n.ToString(CultureInfo.InvariantCulture))))
        );
        if (rows != 1)
        {
            throw new StoreCorruptException("missing clock sequence metadata");
        }
    }

    private static ClockSequenceHead DecodeSequence(string last, string retired, string retained)
    {
        if (retained.Length > SequenceTextLimit)
        {
            throw new CapacityException();
        }
        var head = new ClockSequenceHead { LastAllocated = ParseCanonical(last), RetiredThrough = ParseCanonical(retired) };
        if (retained.Length != 0)
        {
            foreach (var part in retained.Split(','))
            {
                head.Retained.Add(ParseCanonical(part));
            }
        }
        if (!string.Equals(retained, string.Join(',', head.Retained.Select(n => n.ToString(CultureInfo.InvariantCulture))), StringComparison.Ordinal))
        {
            throw new StoreCorruptException("noncanonical clock sequence metadata");
        }
        ValidateSequence(head);
        return head;
    }

    private static ulong ParseCanonical(string text) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && string.Equals(n.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal)
            ? n
            : throw new StoreCorruptException("noncanonical clock sequence metadata");

    /// <summary>
    /// Go <c>loadClockSequence</c>: the namespace and sequence head, checked against the attempt catalog (the
    /// catalog's rows are exactly the retained sequences).
    /// </summary>
    internal static (ControllerSessionId Namespace, ClockSequenceHead Head) LoadSequence(StoreTransaction tx)
    {
        var session = Store.IdentityIn(tx);
        var (row, found) = tx.QueryFirst("SELECT last_allocated,retired_through,retained FROM clock_sequence WHERE singleton=1", [], r => (r.GetString(0), r.GetString(1), r.GetString(2)));
        if (!found)
        {
            throw new StoreCorruptException("missing clock sequence metadata");
        }
        var head = DecodeSequence(row.Item1, row.Item2, row.Item3);
        var ids = tx.Query("SELECT request_id FROM clock_attempts", [], r => r.GetString(0));
        var actual = new List<ulong>(ids.Count);
        foreach (var id in ids)
        {
            actual.Add(ParseRequestId(session, id));
            if (actual.Count > RetainedLimit)
            {
                throw new CapacityException();
            }
        }
        actual.Sort();
        if (!actual.SequenceEqual(head.Retained))
        {
            throw new StoreCorruptException("clock sequence catalog mismatch");
        }
        return (session, head);
    }

    /// <summary>Go <c>ReadClockSequence</c>.</summary>
    internal static ClockSequenceState ReadSequence(StoreTransaction tx)
    {
        var (session, head) = LoadSequence(tx);
        return new ClockSequenceState(session, head.LastAllocated, head.RetiredThrough);
    }
}
