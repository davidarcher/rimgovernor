using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One span's distribution over the captures, in ms; Rows is the largest row count a capture reported.</summary>
public sealed class ProfileStat
{
    public string Name { get; set; } = "";

    /// <summary>Left out of the JSON form when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Detail { get; set; }

    public double P50 { get; set; }

    public double P90 { get; set; }

    public double Max { get; set; }

    public long Rows { get; set; }

    public int Seen { get; set; }

    /// <summary>The nearest-rank p50/p90 and the max of <paramref name="values"/>.</summary>
    public static ProfileStat Of(string name, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToArray();
        double Rank(double p) => sorted[Math.Max((int)Math.Ceiling(p * sorted.Length) - 1, 0)];
        return new ProfileStat { Name = name, P50 = Rank(0.5), P90 = Rank(0.9), Max = sorted[^1], Seen = sorted.Length };
    }
}

/// <summary>
/// What profile-capture reports: per-span stats of the op's captures. Its JSON form (count,
/// tick, frameBytes, pawns, things, total, families, details, and equality when present) is profile-capture's -json
/// output.
/// </summary>
public sealed class ProfileSummary
{
    public int Count { get; set; }

    public long Tick { get; set; }

    public long FrameBytes { get; set; }

    public long Pawns { get; set; }

    public long Things { get; set; }

    public ProfileStat Total { get; set; } = new();

    public List<ProfileStat> Families { get; init; } = [];

    public List<ProfileStat> Details { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Fx.ProfileEquality? Equality { get; set; }

    /// <summary>
    /// Reduces the op's raw captures to per-span stats, families and details each ordered by p50, slowest first.
    /// </summary>
    public static ProfileSummary Summarize(JsonObject reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var read = Harness.FixtureReply<Fx.ProfileCaptureReply>(ProfileCaptureCommand.ProfileOp, reply);
        if (read.Captures.Count == 0)
        {
            throw new AcceptanceException($"{ProfileCaptureCommand.ProfileOp} returned no captures: {reply.ToJson()}");
        }

        var summary = new ProfileSummary
        {
            Count = read.Captures.Count,
            Tick = read.Tick,
            FrameBytes = read.FrameBytes,
            Pawns = read.Pawns,
            Things = read.Things,
            Equality = read.Equality,
        };
        var totals = new List<double>();
        var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        var detail = new Dictionary<string, bool>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var capture in read.Captures)
        {
            totals.Add(capture.TotalMs);
            foreach (var section in capture.Sections)
            {
                var name = section.Name;
                if (!samples.TryGetValue(name, out var list))
                {
                    order.Add(name);
                    list = [];
                    samples[name] = list;
                }

                list.Add(section.Ms);
                rows[name] = Math.Max(rows.GetValueOrDefault(name), section.Rows);
                detail[name] = section.Detail;
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

    /// <summary>Writes the table profile-capture prints.</summary>
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
            await writer.WriteLineAsync("equality: " + Dyn.Canonical(OutputJson.Node(Equality, ProfileJsonContext.Default.ProfileEquality)).Trim()).ConfigureAwait(false);
        }
    }
}
