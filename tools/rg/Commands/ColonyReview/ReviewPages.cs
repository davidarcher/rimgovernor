using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>
/// The static HTML of a run report and of the site index (Go: <c>html.go</c>). The run page is a player (player.js,
/// embedded) over the frames embedded as JSON; text is HTML-encoded as Go's html/template did.
/// </summary>
internal static class ReviewPages
{
    private static readonly Lazy<string> Style = new(() => ReadResource("colonyreview.review.css"));
    private static readonly Lazy<string> PlayerScript = new(() => ReadResource("colonyreview.player.js"));
    private static readonly JsonSerializerOptions FrameOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Renders the run page of <paramref name="rows"/> and their summary.</summary>
    public static string RunPage(IReadOnlyList<Row> rows, RunSummary s)
    {
        var flagged = rows.Where(r => r.Flags.Count > 0).ToList();
        // The serializer escapes <, > and &, so the data cannot close the script.
        var data = JsonSerializer.Serialize(new Dictionary<string, object> { ["frames"] = Frames(rows) }, FrameOptions);
        var date = Enc(s.MetaValue("date"));
        var b = new StringBuilder();
        b.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        b.Append(CultureInfo.InvariantCulture, $"<title>Colony review {date}</title><style>{Style.Value}</style></head><body><main>\n");
        b.Append("<p><a href=\"../index.html\">All runs</a></p>\n");
        b.Append(CultureInfo.InvariantCulture, $"<h1>Colony review {date}</h1>\n");
        b.Append("<p class=\"meta muted\">");
        foreach (var (k, v) in s.Meta)
        {
            b.Append(CultureInfo.InvariantCulture, $"<span>{Enc(k)}: {Enc(v)}</span>");
        }
        b.Append("</p>\n");
        if (!string.IsNullOrEmpty(s.Error))
        {
            b.Append(CultureInfo.InvariantCulture, $"<p class=\"bad\">{(s.Hours > 0 ? "The run ended early" : "The run failed")}: {Enc(s.Error)}</p>\n");
        }
        if (s.Hours > 0)
        {
            AppendTiles(b, s);
        }
        b.Append("<div class=\"layout\"><div class=\"left\">\n");
        b.Append(CultureInfo.InvariantCulture, $"<p id=\"empty\" class=\"card muted\">{(string.IsNullOrEmpty(s.Error) ? "No screenshots were recorded for this run." : "Nothing was recorded.")}</p>\n");
        b.Append(PlayerSection);
        b.Append("<section id=\"now\" class=\"card\"></section>\n");
        if (flagged.Count > 0)
        {
            b.Append(CultureInfo.InvariantCulture, $"<details class=\"card\"><summary>Flagged hours ({s.Flags})</summary><ul class=\"flags\" id=\"flaglist\">");
            foreach (var r in flagged)
            {
                foreach (var f in r.Flags)
                {
                    b.Append(CultureInfo.InvariantCulture, $"<li class=\"{Enc(f.Severity)}\" data-t=\"{r.Tick}\"><a href=\"#{Enc(r.Anchor)}\">{Enc(r.Label)}</a> {Enc(f.Text)}</li>");
                }
            }
            b.Append("</ul></details>\n");
        }
        AppendScore(b, s);
        b.Append("</div><aside class=\"right\" id=\"charts\"></aside></div>\n");
        b.Append(CultureInfo.InvariantCulture, $"<script>var DATA={data};</script>\n<script>{PlayerScript.Value}</script>\n");
        b.Append("</main></body></html>\n");
        return b.ToString();
    }

    /// <summary>Renders the site index: one row per run, in the order given (newest first).</summary>
    public static string SitePage(IReadOnlyList<(string Dir, RunSummary S)> runs)
    {
        var b = new StringBuilder();
        b.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        b.Append(CultureInfo.InvariantCulture, $"<title>Colony reviews</title><style>{Style.Value}</style></head><body><main>\n");
        b.Append("<h1>Colony reviews</h1>\n");
        b.Append("<p class=\"muted\">Each run plays a fresh map for an in-game week with the governor in charge. Open one to see it hour by hour.</p>\n");
        b.Append("<table class=\"runs\"><tr><th></th><th>Run</th><th>Map</th><th>Colonists</th><th>Low food</th><th>Flags</th><th>Score</th></tr>\n");
        foreach (var (dir, s) in runs)
        {
            var d = Enc(dir);
            b.Append("<tr><td>");
            if (s.Thumb.Length > 0)
            {
                b.Append(CultureInfo.InvariantCulture, $"<a href=\"{d}/index.html\"><img loading=\"lazy\" src=\"{d}/{Enc(s.Thumb)}\" alt=\"\"></a>");
            }
            b.Append("</td>\n");
            b.Append(CultureInfo.InvariantCulture, $"<td><a href=\"{d}/index.html\">{Enc(s.MetaValue("date"))}</a><br><span class=\"muted\">{Enc(s.MetaValue("commit"))}</span></td>\n");
            b.Append(CultureInfo.InvariantCulture, $"<td>{Enc(s.MetaValue("biome"))}<br><span class=\"muted\">{Enc(s.MetaValue("seed"))}</span></td>\n");
            if (s.Hours > 0)
            {
                var cls = s.LastColonists < s.FirstColonists ? "bad" : "";
                b.Append(CultureInfo.InvariantCulture, $"<td class=\"{cls}\">{s.FirstColonists} → {s.LastColonists}</td>\n<td>{F(s.MinFoodDays, 1)} d</td>");
            }
            else
            {
                b.Append(CultureInfo.InvariantCulture, $"<td class=\"bad\" colspan=\"2\">failed: {Enc(s.Error ?? "")}</td>");
            }
            b.Append(CultureInfo.InvariantCulture, $"<td>{s.Flags}</td>\n<td>");
            b.Append(s.Score.Scalar is { } scalar ? F(scalar, 1) : "<span class=\"muted\">unknown</span>");
            if (s.Delta is { Status: "ok", Scalar: not null } delta)
            {
                b.Append(CultureInfo.InvariantCulture, $" <span class=\"muted\">({Signed(delta.Scalar, 1)})</span>");
            }
            b.Append("</td></tr>");
        }
        b.Append("\n</table></main></body></html>\n");
        return b.ToString();
    }

    private const string PlayerSection = """
        <section id="player" class="card">
        <div class="viewer"><img id="shot" alt="The colony at the chosen hour"><span class="badge" id="when"></span>
        <div id="vtoggle"><button class="on" data-view="colony">Colony</button><button data-view="map">Whole map</button></div></div>
        <div class="controls">
        <button id="prevday" title="Previous day (shift+left)">⏮</button><button id="prev" title="Previous hour (left)">◀</button>
        <button id="play" title="Play (space)">▶</button>
        <button id="next" title="Next hour (right)">▶|</button><button id="nextday" title="Next day (shift+right)">⏭</button>
        <select id="speed" title="Hours per second"><option value="2">2 h/s</option><option value="8" selected>8 h/s</option><option value="24">24 h/s</option><option value="48">48 h/s</option></select>
        <span class="muted" id="clock"></span>
        <button id="prevflag" title="Previous flagged hour ([)">⚑ ◀</button><button id="nextflag" title="Next flagged hour (])">⚑ ▶</button>
        </div>
        <div class="scrub"><input type="range" id="seek" min="0" max="0" value="0" step="1" aria-label="Hour of the run"><div class="marks" id="marks"></div></div>
        <p class="hint">Space plays and pauses, arrows step an hour, shift+arrows a day, [ and ] jump between flagged hours (red and amber marks). Click or drag a chart to seek.</p>
        </section>

        """;

    private static void AppendTiles(StringBuilder b, RunSummary s)
    {
        var score = s.Score.Scalar is { } v ? F(v, 1) : "unknown";
        b.Append("<div class=\"tiles\">\n");
        b.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\"><span class=\"muted\">Score</span><b>{score}</b></div>\n");
        b.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\"><span class=\"muted\">Colonists</span><b>{s.FirstColonists} → {s.LastColonists}</b></div>\n");
        b.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\"><span class=\"muted\">Lowest mean mood</span><b>{F(s.MinMood * 100, 0)}%</b></div>\n");
        b.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\"><span class=\"muted\">Lowest food runway</span><b>{F(s.MinFoodDays, 1)} days</b></div>\n");
        b.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\"><span class=\"muted\">Final wealth</span><b>{F(s.FinalWealth, 0)}</b></div>\n");
        b.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\"><span class=\"muted\">Hours · flags</span><b>{s.Hours} · <span class=\"{(s.Flags > 0 ? "warn" : "")}\">{s.Flags}</span></b></div>\n");
        b.Append("</div>\n");
    }

    private static void AppendScore(StringBuilder b, RunSummary s)
    {
        b.Append("<details class=\"card\"><summary>Score and storage</summary>\n");
        var scalar = s.Score.Scalar is { } v ? F(v, 1) + " / 100" : "unknown";
        b.Append(CultureInfo.InvariantCulture, $"<p>Score <b>{scalar}</b> <span class=\"muted\">weighted scalar of the components below; a signal, not a verdict.</span></p>\n<table>");
        foreach (var c in s.Score.Components)
        {
            var value = c.Value is { } cv ? F(cv, 2) + " " + Enc(c.Unit) : "<span class=\"muted\">unknown</span>";
            var score = c.Score is { } cs ? F(cs, 2) : "";
            var note = string.IsNullOrEmpty(c.Note) ? "" : " · " + Enc(c.Note);
            b.Append(CultureInfo.InvariantCulture, $"<tr><td>{Enc(c.Name)}</td><td>{value}</td><td>{score}</td><td class=\"muted\">weight {F(c.Weight, 1)}{note}</td></tr>");
        }
        b.Append("</table>\n");
        if (s.Delta is { } delta)
        {
            b.Append("<h2>Against the previous night</h2>");
            if (delta.Status == "no_baseline")
            {
                b.Append("<p class=\"muted\">No earlier run with this seed to compare with (unknown, not zero).</p>");
            }
            else
            {
                var commit = string.IsNullOrEmpty(delta.BaselineCommit) ? "" : $" ({Enc(delta.BaselineCommit)})";
                b.Append(CultureInfo.InvariantCulture, $"<p>Score {Signed(delta.Scalar, 1)} points against {Enc(delta.Baseline ?? "")}{commit}. <span class=\"muted\">One run per seed: a signal, not a verdict.</span></p>\n<table>");
                foreach (var c in delta.Components ?? [])
                {
                    var unit = c.ValueDelta is not null ? Signed(c.ValueDelta, 2) + " in its unit" : "";
                    b.Append(CultureInfo.InvariantCulture, $"<tr><td>{Enc(c.Name)}</td><td>{Signed(c.Delta, 2)}</td><td class=\"muted\">{unit}</td></tr>");
                }
                b.Append("</table>");
            }
        }
        b.Append("\n<h2>Storage at the end</h2>");
        if (s.Zones is { Count: > 0 } zones)
        {
            b.Append("<table>");
            foreach (var z in zones)
            {
                b.Append(CultureInfo.InvariantCulture, $"<tr><td>{Enc(z.Role)}</td><td>{z.Zones} zone{(z.Zones != 1 ? "s" : "")}</td><td>{z.Used} of {z.Cells} cells in use</td></tr>");
            }
            b.Append("</table>\n");
            var supplies = s.SuppliesForbidden ? "<span class=\"warn\">still forbidden</span>" : "allowed";
            b.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">Starting supplies {supplies}</p>");
        }
        else
        {
            b.Append("<p class=\"muted\">No stockpile review filed.</p>");
        }
        b.Append("\n</details>\n");
    }

    private static List<ReviewFrame> Frames(IReadOnlyList<Row> rows)
    {
        var output = new List<ReviewFrame>(rows.Count);
        foreach (var r in rows)
        {
            var c = r.Census;
            output.Add(new ReviewFrame
            {
                Tick = r.Tick,
                Day = r.Day,
                Label = r.Label,
                Shot = NullIfEmpty(r.ColonyShot),
                Map = NullIfEmpty(r.MapShot),
                Sample = NullIfEmpty(r.Sampled),
                Err = NullIfEmpty(c.Error),
                N = c.Colonists,
                Mood = c.MoodMean,
                Food = c.FoodRunwayDays,
                Wealth = c.WealthTotal,
                Tier = NullIfEmpty(c.TechTier ?? ""),
                Pawns = c.Pawns is { Count: > 0 } ps ? [.. ps.Select(p => new FramePawn { Label = p.Label, Mood = p.Mood, Food = p.Food, Downed = p.Downed == true })] : null,
                Zones = c.Stockpiles is { Count: > 0 } zs ? [.. zs.Select(z => new FrameZone { Role = z.Role, Zones = z.Zones, Cells = z.Cells, Used = z.Used })] : null,
                Forbid = c.ForbiddenSupplies == true,
                Ledger = c.MoodLedger,
                Flags = r.Flags.Count > 0 ? [.. r.Flags.Select(f => new FrameFlag { Severity = f.Severity, Text = f.Text })] : null,
                Change = r.Changes.Count > 0 ? r.Changes : null,
                Unmet = [.. r.Concerns.Where(g => g.Need == "unmet").Select(g => new[] { g.Id, g.Status })],
            });
        }
        return output;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string F(double v, int digits) => v.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // Go "%+.1f": always signed; "unknown" for a missing value.
    private static string Signed(double? v, int digits) =>
        v is { } x ? (x < 0 || double.IsNegative(x) ? "-" : "+") + F(Math.Abs(x), digits) : "unknown";

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>One hour as the player reads it (player.js); keys are short because a run is hundreds of frames.</summary>
internal sealed record ReviewFrame
{
    [JsonPropertyName("t")]
    public int Tick { get; init; }

    [JsonPropertyName("d")]
    public int Day { get; init; }

    [JsonPropertyName("l")]
    public string Label { get; init; } = "";

    [JsonPropertyName("s")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Shot { get; init; }

    [JsonPropertyName("m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Map { get; init; }

    [JsonPropertyName("sam")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sample { get; init; }

    [JsonPropertyName("err")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Err { get; init; }

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? N { get; init; }

    [JsonPropertyName("mood")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Mood { get; init; }

    [JsonPropertyName("food")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Food { get; init; }

    [JsonPropertyName("wealth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Wealth { get; init; }

    [JsonPropertyName("tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tier { get; init; }

    [JsonPropertyName("p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FramePawn>? Pawns { get; init; }

    [JsonPropertyName("z")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FrameZone>? Zones { get; init; }

    [JsonPropertyName("fb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Forbid { get; init; }

    [JsonPropertyName("ml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoodLedger? Ledger { get; init; }

    [JsonPropertyName("f")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FrameFlag>? Flags { get; init; }

    [JsonPropertyName("c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Change { get; init; }

    /// <summary>Concern id and status for each concern in deficit.</summary>
    [JsonPropertyName("u")]
    public List<string[]> Unmet { get; init; } = [];
}

/// <summary>A census pawn in a frame.</summary>
internal sealed record FramePawn
{
    [JsonPropertyName("l")]
    public string Label { get; init; } = "";

    [JsonPropertyName("m")]
    public double? Mood { get; init; }

    [JsonPropertyName("f")]
    public double? Food { get; init; }

    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Downed { get; init; }
}

/// <summary>A stockpile role in a frame.</summary>
internal sealed record FrameZone
{
    [JsonPropertyName("r")]
    public string Role { get; init; } = "";

    [JsonPropertyName("z")]
    public int Zones { get; init; }

    [JsonPropertyName("c")]
    public int Cells { get; init; }

    [JsonPropertyName("u")]
    public int Used { get; init; }
}

/// <summary>A reviewer flag in a frame.</summary>
internal sealed record FrameFlag
{
    [JsonPropertyName("s")]
    public string Severity { get; init; } = "";

    [JsonPropertyName("t")]
    public string Text { get; init; } = "";
}
