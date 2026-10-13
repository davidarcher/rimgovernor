using System.Collections.Concurrent;
using System.Globalization;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using Pb = RimGovernor.Protocol.Clock;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/inbox.go.
public static partial class ClockJournal
{
    private const int LinkDepthLimit = 40;

    /// <summary>
    /// Go <c>canonicalProfiles</c>: remembers each profile path's resolved form. Resolving symlinks stats every
    /// path component, and the poll loop and every review read resolve the same profile inside a store transaction.
    /// A profile directory is not re-linked while the service runs; the directory check still runs on every call.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> _canonicalProfiles = new(StringComparer.Ordinal);

    /// <summary>Go <c>CanonicalProfile</c> (<c>canonicalClockProfile</c>): validates and normalizes a clock profile path.</summary>
    public static string CanonicalProfile(string profile)
    {
        DomainException.Require(Path.IsPathFullyQualified(profile), "clock profile must be absolute");
        var result = _canonicalProfiles.TryGetValue(profile, out var held) ? held : ResolveLinks(profile, 0);
        if (!Directory.Exists(result))
        {
            _ = _canonicalProfiles.TryRemove(profile, out _);
            if (File.Exists(result))
            {
                throw new DomainException("clock profile must be a directory");
            }
            throw new DirectoryNotFoundException("clock profile does not exist: " + result);
        }
        result = CleanPath(Path.GetFullPath(result));
        _canonicalProfiles[profile] = result;
        return result;
    }

    /// <summary>Go <c>filepath.EvalSymlinks</c>: the path with every link component replaced by its target.</summary>
    private static string ResolveLinks(string path, int depth)
    {
        DomainException.Require(depth < LinkDepthLimit, "too many links resolving clock profile");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                throw new DirectoryNotFoundException("clock profile does not exist: " + current);
            }
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new DirectoryNotFoundException("clock profile link is broken: " + current);
                current = ResolveLinks(target.FullName, depth + 1);
            }
        }
        return current;
    }

    /// <summary>Go <c>sameClockProfile</c>: paths compare case-insensitively on Windows.</summary>
    internal static bool SameProfile(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Go <c>BindInbox</c>: binds one native profile journal, independent of loaded worlds.</summary>
    internal static ClockInboxState BindInbox(StoreTransaction tx, string path)
    {
        var (saved, found) = tx.QueryFirst("SELECT profile FROM clock_inbox WHERE singleton=1", [], r => r.GetString(0));
        if (!found)
        {
            var retained = tx.Scalar(
                "SELECT (SELECT count(*) FROM clock_event_pages)+(SELECT count(*) FROM clock_inbox_events)+(SELECT count(*) FROM clock_review_log)+(SELECT count(*) FROM clock_acknowledgements)"
            );
            if (retained != 0)
            {
                throw new StoreCorruptException("clock history has no profile binding");
            }
            if (LoadCheckpoint(tx) != default)
            {
                throw new StoreCorruptException("clock checkpoint has no profile binding");
            }
            _ = tx.Execute("INSERT INTO clock_inbox(singleton,profile) VALUES(1,$profile)", ("$profile", path));
        }
        else if (!SameProfile(saved!, path))
        {
            throw new ConflictException();
        }
        return ReadInbox(tx, path, InboxCapacity).Inbox.State;
    }

    /// <summary>
    /// Go <c>LoadInbox</c>: returns every retained page or fails; <paramref name="limit"/> never truncates.
    /// Ingestion is not processing or acknowledgement of an interruption.
    /// </summary>
    internal static ClockInbox LoadInbox(StoreTransaction tx, string path, int limit)
    {
        DomainException.Require(limit >= 1 && limit <= InboxCapacity, "invalid clock inbox limit");
        return ReadInbox(tx, path, limit).Inbox;
    }

    /// <summary>
    /// Go <c>AppendEvents</c>: commits a scanned page, surviving events and cursor together. Only exact retained
    /// request/page replay is a no-op. A changed stale page must be refetched at the current cursor; its new
    /// context does not rewrite history.
    /// </summary>
    internal static (ClockInboxState State, bool Created) AppendEvents(StoreTransaction tx, string path, Pb.EventsRequest request, Pb.EventsPage page)
    {
        BridgeFunctions.ValidateClockEventsPage(page, request);
        request = request.Clone();
        page = page.Clone();
        var rb = CanonicalBytes(request);
        var pb = CanonicalBytes(page);
        var (inbox, size) = ReadInbox(tx, path, InboxCapacity);
        foreach (var previous in inbox.Pages)
        {
            if (CanonicalBytes(previous.Request).AsSpan().SequenceEqual(rb) && CanonicalBytes(previous.Page).AsSpan().SequenceEqual(pb))
            {
                return (inbox.State, false);
            }
        }
        if (request.AfterCursor != inbox.State.Cursor)
        {
            throw new ConflictException();
        }
        if (page.NextCursor == inbox.State.Cursor)
        {
            return (inbox.State, false);
        }
        if (inbox.State.PageCount == InboxCapacity || inbox.State.EventCount + page.Events.Count > InboxCapacity || (long)rb.Length + pb.Length > InboxBytes - size)
        {
            throw new CapacityException();
        }
        var sequence = inbox.Checkpoint.Pages + inbox.State.PageCount + 1;
        _ = tx.Execute(
            "INSERT INTO clock_event_pages(sequence,after_cursor,next_cursor,request,page) VALUES($sequence,$after,$next,$request,$page)",
            ("$sequence", sequence),
            ("$after", request.AfterCursor),
            ("$next", page.NextCursor),
            ("$request", rb),
            ("$page", pb)
        );
        foreach (var @event in page.Events)
        {
            _ = tx.Execute(
                "INSERT INTO clock_inbox_events(cursor,page_sequence,payload) VALUES($cursor,$sequence,$payload)",
                ("$cursor", @event.Cursor),
                ("$sequence", sequence),
                ("$payload", CanonicalBytes(@event))
            );
        }
        var next = inbox.State with
        {
            Cursor = page.NextCursor,
            Gap = inbox.State.Gap || page.Gap,
            LostCount = inbox.State.LostCount + page.LostCount,
            PageCount = inbox.State.PageCount + 1,
            EventCount = inbox.State.EventCount + page.Events.Count,
        };
        return (next, true);
    }

    /// <summary>
    /// Go <c>loadClockInbox</c>: replays every retained page, checking each against its request, its neighbours
    /// and the event rows. Returns the inbox and the retained request/page byte size.
    /// </summary>
    internal static (ClockInbox Inbox, long Size) ReadInbox(StoreTransaction tx, string profile, int limit)
    {
        var (saved, found) = tx.QueryFirst("SELECT profile FROM clock_inbox WHERE singleton=1", [], r => r.GetString(0));
        if (!found)
        {
            throw new NotFoundException();
        }
        if (!SameProfile(saved!, profile))
        {
            throw new ConflictException();
        }
        var checkpoint = LoadCheckpoint(tx);
        var state = new ClockInboxState(saved!, checkpoint.Cursor, checkpoint.LostCount != 0, checkpoint.LostCount, 0, 0);
        var (pages, size) = tx.QueryFirst("SELECT count(*),coalesce(sum(length(request)+length(page)),0) FROM clock_event_pages", [], r => (r.GetInt64(0), r.GetInt64(1))).Row;
        var (events, eventBytes) = tx.QueryFirst("SELECT count(*),coalesce(sum(length(payload)),0) FROM clock_inbox_events", [], r => (r.GetInt64(0), r.GetInt64(1))).Row;
        if (pages > limit || pages > InboxCapacity || events > InboxCapacity || size > InboxBytes || eventBytes > InboxBytes)
        {
            throw new CapacityException();
        }
        var rows = tx.Query(
            "SELECT sequence,after_cursor,next_cursor,request,page FROM clock_event_pages ORDER BY sequence",
            [],
            r => (Sequence: r.GetInt64(0), After: r.GetInt64(1), Next: r.GetInt64(2), Request: r.RequiredBlob(3), Page: r.RequiredBlob(4))
        );
        var retained = new List<ClockEventPage>(rows.Count);
        foreach (var row in rows)
        {
            var request = Parse<Pb.EventsRequest>(row.Request, int.MaxValue);
            var page = Parse<Pb.EventsPage>(row.Page, int.MaxValue);
            BridgeFunctions.ValidateClockEventsPage(page, request);
            if (!CanonicalBytes(request).AsSpan().SequenceEqual(row.Request)
                || !CanonicalBytes(page).AsSpan().SequenceEqual(row.Page)
                || row.Sequence != checkpoint.Pages + retained.Count + 1
                || row.After != state.Cursor
                || row.After != request.AfterCursor
                || row.Next != page.NextCursor
                || row.Next <= row.After)
            {
                throw new StoreCorruptException("invalid clock inbox replay");
            }
            retained.Add(new ClockEventPage(request, page));
            state = state with { Cursor = row.Next, Gap = state.Gap || page.Gap, LostCount = state.LostCount + page.LostCount, EventCount = state.EventCount + page.Events.Count };
        }
        state = state with { PageCount = retained.Count };
        if (state.EventCount != events || state.PageCount != pages)
        {
            throw new StoreCorruptException("clock inbox count mismatch");
        }
        var eventRows = tx.Query("SELECT cursor,page_sequence,payload FROM clock_inbox_events ORDER BY cursor", [], r => (Cursor: r.GetInt64(0), Sequence: r.GetInt64(1), Payload: r.RequiredBlob(2)));
        var at = 0;
        for (var i = 0; i < retained.Count; i++)
        {
            foreach (var @event in retained[i].Page.Events)
            {
                if (at >= eventRows.Count)
                {
                    throw new StoreCorruptException("missing clock inbox event");
                }
                var row = eventRows[at++];
                if (row.Cursor != @event.Cursor || row.Sequence != checkpoint.Pages + i + 1 || !CanonicalBytes(@event).AsSpan().SequenceEqual(row.Payload))
                {
                    throw new StoreCorruptException(string.Create(CultureInfo.InvariantCulture, $"invalid clock inbox event {row.Cursor}"));
                }
            }
        }
        if (at != eventRows.Count)
        {
            throw new StoreCorruptException("extra clock inbox event");
        }
        return (new ClockInbox(state, retained) { Checkpoint = checkpoint }, size);
    }
}
