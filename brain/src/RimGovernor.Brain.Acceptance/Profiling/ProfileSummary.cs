using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One span's distribution over the captures, in ms; Rows is the largest row count a capture reported (Go <c>profileStat</c>).</summary>
public sealed class ProfileStat
{
    public string Name { get; set; } = "";

    public bool Detail { get; set; }

    public double P50 { get; set; }

    public double P90 { get; set; }

    public double Max { get; set; }

    public long Rows { get; set; }

    public int Seen { get; set; }

    /// <summary>The stat as its JSON object (tags name, detail (omitted when false), p50, p90, max, rows, seen).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["name"] = Name };
        if (Detail)
        {
            row["detail"] = true;
        }

        row["p50"] = P50;
        row["p90"] = P90;
        row["max"] = Max;
        row["rows"] = Rows;
        row["seen"] = Seen;
        return row;
    }

    /// <summary>Reads a stat back; absent fields read as zero.</summary>
    public static ProfileStat FromJson(JsonNode? node) => new()
    {
        Name = node.Member("name").AsString(),
        Detail = node.Member("detail").AsBool() == true,
        P50 = Zero(node.Member("p50")),
        P90 = Zero(node.Member("p90")),
        Max = Zero(node.Member("max")),
        Rows = (long)Zero(node.Member("rows")),
        Seen = (int)Zero(node.Member("seen")),
    };

    private static double Zero(JsonNode? value) => value is null ? 0 : Math.Max(value.AsNumber(), 0);

    /// <summary>The nearest-rank p50/p90 and the max of <paramref name="values"/> (Go <c>stat</c>).</summary>
    public static ProfileStat Of(string name, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToArray();
        double Rank(double p) => sorted[Math.Max((int)Math.Ceiling(p * sorted.Length) - 1, 0)];
        return new ProfileStat { Name = name, P50 = Rank(0.5), P90 = Rank(0.9), Max = sorted[^1], Seen = sorted.Length };
    }
}

/// <summary>What profile-capture reports: per-span stats of the op's captures (Go <c>profileSummary</c>).</summary>
public sealed class ProfileSummary
{
    public int Count { get; set; }

    public long Tick { get; set; }

    public long FrameBytes { get; set; }

    public long Pawns { get; set; }

    public long Things { get; set; }

    public ProfileStat Total { get; set; } = new();

    public List<ProfileStat> Families { get; } = [];

    public List<ProfileStat> Details { get; } = [];

    public JsonObject? Equality { get; set; }

    /// <summary>The summary as its JSON object (tags count, tick, frameBytes, pawns, things, total, families, details, equality (omitted when null)).</summary>
    public JsonObject ToJson()
    {
        var families = new JsonArray();
        foreach (var family in Families)
        {
            families.Add(family.ToJson());
        }

        var details = new JsonArray();
        foreach (var detail in Details)
        {
            details.Add(detail.ToJson());
        }

        var row = new JsonObject
        {
            ["count"] = Count,
            ["tick"] = Tick,
            ["frameBytes"] = FrameBytes,
            ["pawns"] = Pawns,
            ["things"] = Things,
            ["total"] = Total.ToJson(),
            ["families"] = families,
            ["details"] = details,
        };
        if (Equality is not null)
        {
            row["equality"] = Equality.DeepClone();
        }

        return row;
    }

    /// <summary>Reads a summary back from a profile-capture -json output or a record's profile; absent fields read as zero.</summary>
    public static ProfileSummary FromJson(JsonNode? node)
    {
        var summary = new ProfileSummary
        {
            Count = (int)Math.Max(node.Member("count").AsNumber(), 0),
            Tick = (long)Math.Max(node.Member("tick").AsNumber(), 0),
            FrameBytes = (long)Math.Max(node.Member("frameBytes").AsNumber(), 0),
            Pawns = (long)Math.Max(node.Member("pawns").AsNumber(), 0),
            Things = (long)Math.Max(node.Member("things").AsNumber(), 0),
            Total = ProfileStat.FromJson(node.Member("total")),
            Equality = node.Member("equality").AsMap()?.DeepClone() as JsonObject,
        };
        summary.Families.AddRange(node.Member("families").AsSlice().Select(ProfileStat.FromJson));
        summary.Details.AddRange(node.Member("details").AsSlice().Select(ProfileStat.FromJson));
        return summary;
    }

    /// <summary>
    /// Reduces the op's raw captures to per-span stats, families and details each ordered by p50, slowest first (Go
    /// <c>summarizeProfile</c>).
    /// </summary>
    public static ProfileSummary Summarize(JsonObject reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var raw = reply["captures"].AsSlice();
        if (raw.Count == 0)
        {
            throw new AcceptanceException($"{ProfileCaptureCommand.ProfileOp} returned no captures: {reply.ToJson()}");
        }

        var summary = new ProfileSummary
        {
            Count = raw.Count,
            Tick = (long)reply["tick"].AsNumber(),
            FrameBytes = (long)reply["frameBytes"].AsNumber(),
            Pawns = (long)reply["pawns"].AsNumber(),
            Things = (long)reply["things"].AsNumber(),
            Equality = reply["equality"].AsMap()?.DeepClone() as JsonObject,
        };
        var totals = new List<double>();
        var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        var detail = new Dictionary<string, bool>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var c in raw)
        {
            var capture = c.AsMap() ?? [];
            totals.Add(capture["totalMs"].AsNumber());
            foreach (var x in capture["sections"].AsSlice())
            {
                var section = x.AsMap() ?? [];
                var name = section["name"].AsString();
                if (!samples.TryGetValue(name, out var list))
                {
                    order.Add(name);
                    list = [];
                    samples[name] = list;
                }

                list.Add(section["ms"].AsNumber());
                rows[name] = Math.Max(rows.GetValueOrDefault(name), (long)section["rows"].AsNumber());
                detail[name] = section["detail"].AsBool() == true;
            }
        }

        summary.Total = ProfileStat.Of("total", totals);
        foreach (var name in order)
        {
            var st = ProfileStat.Of(name, samples[name]);
            st.Rows = rows[name];
            st.Detail = detail[name];
            (st.Detail ? summary.Details : summary.Families).Add(st);
        }

        SortSlowestFirst(summary.Families);
        SortSlowestFirst(summary.Details);
        return summary;
    }

    private static void SortSlowestFirst(List<ProfileStat> list)
    {
        var sorted = list.OrderByDescending(stat => stat.P50).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    /// <summary>Writes the table profile-capture prints (Go <c>printProfile</c>).</summary>
    public async Task PrintAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var culture = CultureInfo.InvariantCulture;
        await writer.WriteLineAsync(string.Create(culture, $"{Count} captures at tick {Tick}: {Pawns} pawns, {Things} things, frame {FrameBytes} bytes")).ConfigureAwait(false);
        await writer.WriteLineAsync($"{"span",-32} {"p50 ms",9} {"p90 ms",9} {"max ms",9} {"rows",8}").ConfigureAwait(false);

        async Task Line(ProfileStat st, string indent) =>
            await writer.WriteLineAsync(string.Create(culture, $"{indent + st.Name,-32} {st.P50,9:F2} {st.P90,9:F2} {st.Max,9:F2} {st.Rows,8}")).ConfigureAwait(false);

        await Line(Total, "").ConfigureAwait(false);
        foreach (var st in Families)
        {
            await Line(st, "  ").ConfigureAwait(false);
        }

        if (Details.Count > 0)
        {
            await writer.WriteLineAsync("detail spans:").ConfigureAwait(false);
            foreach (var st in Details)
            {
                await Line(st, "  ").ConfigureAwait(false);
            }
        }

        if (Equality is not null)
        {
            await writer.WriteLineAsync("equality: " + Dyn.Canonical(Equality).Trim()).ConfigureAwait(false);
        }
    }
}
