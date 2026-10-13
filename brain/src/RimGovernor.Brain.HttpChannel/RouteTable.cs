namespace RimGovernor.Brain.HttpChannel;

/// <summary>The limits of one route.</summary>
public sealed record RouteOptions
{
    /// <summary>The largest request body accepted; a bigger declared length is answered 413 without reading it.</summary>
    public int MaxBodyBytes { get; init; } = 8192;

    /// <summary>How long the handler may run before the request is answered 504.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The routes of the channel: exact paths and prefixes, each for one method. A GET route also answers HEAD. The
/// table is shared by the channel's own routes and by the route modules the brain host publishes, and a module can
/// remove its routes again (the brain host reloads its assembly context, #3084), so registrations are disposable.
/// </summary>
public sealed class RouteTable
{
    private readonly object _gate = new();
    private List<Entry> _entries = [];

    public IDisposable Map(string method, string path, RouteHandler handler, RouteOptions? options = null) =>
        Add(new Entry(method, path, false, handler, options ?? new RouteOptions()));

    /// <summary>A route for every path that starts with <paramref name="prefix"/> (which ends with a slash).</summary>
    public IDisposable MapPrefix(string method, string prefix, RouteHandler handler, RouteOptions? options = null) =>
        Add(new Entry(method, prefix, true, handler, options ?? new RouteOptions()));

    internal Resolution Resolve(string method, string path)
    {
        var snapshot = Volatile.Read(ref _entries);
        var lookup = method == "HEAD" ? "GET" : method;
        Entry? match = null;
        var allow = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot)
        {
            var pathMatches = entry.Prefix
                ? path.StartsWith(entry.Path, StringComparison.Ordinal) && path.Length > entry.Path.Length
                : string.Equals(path, entry.Path, StringComparison.Ordinal);
            if (!pathMatches)
            {
                continue;
            }

            allow.Add(entry.Method);
            if (entry.Method == "GET")
            {
                allow.Add("HEAD");
            }

            if (entry.Method == lookup && (match is null || (match.Prefix && !entry.Prefix)))
            {
                match = entry;
            }
        }

        if (match is not null)
        {
            return new Resolution(match, null);
        }

        return new Resolution(null, allow.Count == 0 ? null : string.Join(", ", allow));
    }

    private Registration Add(Entry entry)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Method == entry.Method && e.Path == entry.Path && e.Prefix == entry.Prefix))
            {
                throw new InvalidOperationException($"Route {entry.Method} {entry.Path} is already registered");
            }

            Volatile.Write(ref _entries, [.. _entries, entry]);
        }

        return new Registration(this, entry);
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            Volatile.Write(ref _entries, _entries.Where(e => !ReferenceEquals(e, entry)).ToList());
        }
    }

    internal sealed record Entry(string Method, string Path, bool Prefix, RouteHandler Handler, RouteOptions Options);

    /// <summary>Either the matching entry, or (when only the method is wrong) the Allow header, or neither for 404.</summary>
    internal readonly record struct Resolution(Entry? Entry, string? Allow);

    private sealed class Registration(RouteTable table, Entry entry) : IDisposable
    {
        public void Dispose() => table.Remove(entry);
    }
}
