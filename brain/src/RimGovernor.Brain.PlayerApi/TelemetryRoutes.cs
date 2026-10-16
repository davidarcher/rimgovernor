using System.Globalization;
using System.Text.Json;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// <c>POST /api/telemetry/spans</c> (#3287): the acceptance harness and the launcher time their side of a launch
/// (prepare, process start, connect, the start) in their own processes, and the in-process brain owns the run's
/// <c>traces.jsonl</c> (its ring is single-writer), so they post the finished phases here and the brain replays them
/// as one <c>launch</c> tree (<see cref="LaunchSpans.Replay"/>) under its own run. The body is a JSON array of
/// <c>{"name","parent","startUtc","endUtc","attributes"}</c>: <c>parent</c> is "" for a child of the root or the name
/// of an earlier span in the same body (the latest of that name), the times are ISO-8601 UTC (ending in <c>Z</c>) with the end not before the
/// start, and <c>attributes</c> is an object of string values that become span tags.
/// The reply is <c>{"spans": n}</c>, the spans written including the root (0 when nothing records spans).
/// </summary>
public static class TelemetryRoutes
{
    public const string SpansPath = "/api/telemetry/spans";

    /// <summary>The most spans one body may carry.</summary>
    public const int MaxSpans = 32;

    private const int MaxName = 128;

    private const int MaxAttributes = 16;

    private static readonly RouteOptions Bounded = new() { MaxBodyBytes = StrictJson.Limit };

    /// <summary>Registers the route; dispose the result to remove it.</summary>
    public static IDisposable Register(RouteTable routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return routes.Map("POST", SpansPath, (request, _) => Task.FromResult(Post(request)), Bounded);
    }

    internal static ChannelResponse Post(ChannelRequest request)
    {
        if (request.Query.Length != 0)
        {
            return ChannelResponse.Failure(400, "invalid_query", "This route accepts no query");
        }

        List<LaunchInterval> phases;
        try
        {
            phases = Decode(request.Body);
        }
        catch (StrictJsonException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Provide a list of spans: name, parent, startUtc, endUtc (UTC, end not before start) and string attributes");
        }

        var written = phases.Count == 0 ? 0 : LaunchSpans.Replay(phases, phases.Max(p => p.EndUtc));
        return ChannelResponse.Json(200, string.Create(CultureInfo.InvariantCulture, $"{{\"spans\":{written}}}\n"));
    }

    internal static List<LaunchInterval> Decode(byte[] body)
    {
        var root = StrictJson.Parse(body);
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxSpans)
        {
            throw new StrictJsonException("expected a bounded array");
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var phases = new List<LaunchInterval>();
        foreach (var item in root.EnumerateArray())
        {
            var fields = StrictJson.Fields(item, "name", "parent", "startUtc", "endUtc", "attributes");
            var name = StrictJson.String(fields["name"]);
            var parentName = StrictJson.String(fields["parent"]);
            if (name.Length == 0 || name.Length > MaxName)
            {
                throw new StrictJsonException("invalid name");
            }

            var parent = 0;
            if (parentName.Length > 0 && !ids.TryGetValue(parentName, out parent))
            {
                throw new StrictJsonException("parent is not an earlier span");
            }

            var start = Utc(fields["startUtc"]);
            var end = Utc(fields["endUtc"]);
            if (end < start)
            {
                throw new StrictJsonException("end before start");
            }

            var id = phases.Count + 1;
            ids[name] = id;
            phases.Add(new LaunchInterval(id, parent, name, start, end, Attributes(fields["attributes"])));
        }

        return phases;
    }

    private static DateTime Utc(JsonElement element)
    {
        var text = StrictJson.String(element);
        if (!text.EndsWith('Z')
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new StrictJsonException("expected an ISO-8601 UTC time");
        }

        return parsed.UtcDateTime;
    }

    private static Dictionary<string, string> Attributes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new StrictJsonException("expected an attributes object");
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Length == 0 || attributes.Count >= MaxAttributes)
            {
                throw new StrictJsonException("invalid attributes");
            }

            attributes[property.Name] = StrictJson.String(property.Value);
        }

        return attributes;
    }
}
