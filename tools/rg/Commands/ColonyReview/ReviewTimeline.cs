using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>What the report reads of result.json: the error, the case's review block and the timeline samples.</summary>
internal sealed record CaseResult(string Error, IReadOnlyDictionary<string, string> Review);

/// <summary>
/// Turns a review/colony-week case output into report rows (Go: <c>Load</c> and <c>Derive</c>). The report reads what the
/// run already records: result.json's timeline (the colony census from /api/player/colony and each sampled concern's
/// state, every in-game hour) and the recorder's screenshots under review/ (colony-tick.jpg, map-tick.jpg).
/// </summary>
internal static partial class ReviewTimeline
{
    private const int TicksPerDay = 60000;
    private const int TicksPerHour = 2500;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // The game's markup tags (<color=#fff>, </color>, <b>, <size=12>) that its labels carry for the in-game UI; the page
    // shows plain text.
    [GeneratedRegex("</?[a-zA-Z]+(=[^>]*)?>", RegexOptions.CultureInvariant)]
    private static partial Regex RichText();

    public static string Plain(string s) => RichText().Replace(s, "").Trim();

    /// <summary>
    /// Reads the case output directory: result.json and review/*.jpg. Throws <see cref="FileNotFoundException"/> when there
    /// is no result.json (the case never wrote one).
    /// </summary>
    public static (List<Row> Rows, CaseResult Result) Load(string dir)
    {
        var resultPath = Path.Combine(dir, "result.json");
        if (!File.Exists(resultPath))
        {
            throw new FileNotFoundException("result.json", resultPath);
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllBytes(resultPath));
        }
        catch (JsonException e)
        {
            throw new ToolException($"result.json: {e.Message}", e);
        }
        using (doc)
        {
            var root = doc.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString() ?? ""
                : "";
            var review = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("review", out var rev) && rev.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in rev.EnumerateObject())
                {
                    review[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                }
            }
            var rows = new List<Row>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array)
            {
                rows = SampleRows(timeline);
            }
            return (WithShots(rows, dir), new CaseResult(error, review));
        }
    }

    private static List<Row> SampleRows(JsonElement timeline)
    {
        var rows = new List<Row>();
        var seen = new HashSet<int>();
        foreach (var sample in timeline.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object
                || !sample.TryGetProperty("tick", out var tickElement)
                || !tickElement.TryGetInt32(out var tick)
                || !seen.Add(tick))
            {
                continue; // a sample without a live tick, or a woken duplicate
            }
            var census = new Census();
            if (sample.TryGetProperty("colony", out var colony) && colony.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    census = colony.Deserialize<Census>(ReadOptions) ?? new Census();
                }
                catch (JsonException)
                {
                    // Go ignored the decode error and kept what it had; a malformed block reads as no census.
                }
            }
            census = census with { Pawns = census.Pawns?.Select(p => p with { Label = Plain(p.Label) }).ToList() };
            var concerns = new List<Concern>();
            // An extra owner's sample rides under its key (a Concern id or a NeedId) and is told by its shape, a need
            // and a status, not by its name. The watched owner's own fields sit at the top, named by "concern"; a
            // report written before that field names it "watched".
            foreach (var p in sample.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object
                    && TryString(p.Value, "need", out var need)
                    && TryString(p.Value, "status", out var status)
                    && need.Length > 0)
                {
                    concerns.Add(new Concern(p.Name, need, status));
                }
            }
            if (TryString(sample, "need", out var topNeed) && topNeed.Length > 0)
            {
                TryString(sample, "status", out var topStatus);
                concerns.Add(new Concern(TryString(sample, "concern", out var watched) && watched.Length > 0 ? watched : "watched", topNeed, topStatus));
            }
            concerns.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            rows.Add(Stamp(new Row { Census = census, Concerns = concerns }, tick));
        }
        rows.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return rows;
    }

    private static Row Stamp(Row r, int tick)
    {
        var day = (tick / TicksPerDay) + 1;
        var hour = tick % TicksPerDay / TicksPerHour;
        return r with
        {
            Tick = tick,
            Day = day,
            Hour = hour,
            Anchor = "t" + tick.ToString(CultureInfo.InvariantCulture),
            Label = string.Create(CultureInfo.InvariantCulture, $"Day {day}, {hour:D2}h"),
        };
    }

    // A missing property reads as empty; a present non-string one is not a string at all.
    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (e.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = e.GetString() ?? "";
        return true;
    }

    // The screenshots set the report's hours: the timeline samples when the harness gets an answer, which a fast game
    // outruns, so each hourly shot is a row carrying the last sample at or before it.
    private static List<Row> WithShots(List<Row> rows, string dir)
    {
        var colonyShots = new SortedDictionary<int, string>();
        var mapShots = new SortedDictionary<int, string>();
        var reviewDir = Path.Combine(dir, "review");
        if (Directory.Exists(reviewDir))
        {
            foreach (var path in Directory.EnumerateFiles(reviewDir))
            {
                var file = Path.GetFileName(path);
                var name = file.EndsWith(".jpg", StringComparison.Ordinal) ? file[..^4] : file;
                var dash = name.IndexOf('-', StringComparison.Ordinal);
                if (dash < 0 || !int.TryParse(name[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tick))
                {
                    continue;
                }
                switch (name[..dash])
                {
                    case "colony":
                        colonyShots[tick] = file;
                        break;
                    case "map":
                        mapShots[tick] = file;
                        break;
                }
            }
        }
        if (colonyShots.Count > 0 && rows.Count > 0)
        {
            var hourly = new List<Row>();
            foreach (var (tick, shot) in colonyShots)
            {
                var r = rows[Math.Max(UpperBound(rows, tick) - 1, 0)];
                if (r.Tick != tick)
                {
                    r = r with { Sampled = r.Label };
                }
                hourly.Add(Stamp(r, tick) with { ColonyShot = shot });
            }
            rows = hourly;
        }
        foreach (var (tick, name) in mapShots)
        {
            var i = Math.Max(UpperBound(rows, tick) - 1, 0);
            if (i < rows.Count && rows[i].MapShot.Length == 0)
            {
                rows[i] = rows[i] with { MapShot = name };
            }
        }
        return rows;
    }

    // The first index whose tick is after the given one (Go sort.Search).
    private static int UpperBound(List<Row> rows, int tick)
    {
        var lo = 0;
        var hi = rows.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (rows[mid].Tick > tick)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return lo;
    }

    /// <summary>Fills each row's concern changes and flags and returns the summary.</summary>
    public static RunSummary Derive(List<Row> rows, IReadOnlyDictionary<string, string> meta)
    {
        var flagCount = 0;
        var minMood = 1.0;
        var minFood = -1.0;
        var supplies = false;
        IReadOnlyList<Stockpile>? zones = null;
        var thumb = "";
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Row? prev = i > 0 ? rows[i - 1] : null;
            var changes = new List<string>();
            if (prev is not null)
            {
                var before = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var g in prev.Concerns)
                {
                    before[g.Id] = g.Need; // a duplicate id keeps the last, as a Go map does
                }
                foreach (var g in r.Concerns)
                {
                    if (before.TryGetValue(g.Id, out var b) && b != g.Need)
                    {
                        changes.Add($"{g.Id} {b} → {g.Need}");
                    }
                }
            }
            r = r with { Changes = changes };
            r = r with { Flags = ReviewFlags.Of(rows.Take(i).Append(r).ToList(), prev) };
            rows[i] = r;
            flagCount += r.Flags.Count;
            var c = r.Census;
            if (c.Stockpiles is not null)
            {
                zones = c.Stockpiles;
            }
            if (c.ForbiddenSupplies is { } fs)
            {
                supplies = fs;
            }
            if (c.MoodMean is { } mood && mood < minMood)
            {
                minMood = mood;
            }
            if (c.FoodRunwayDays is { } food && (minFood < 0 || food < minFood))
            {
                minFood = food;
            }
            if (r.ColonyShot.Length > 0)
            {
                thumb = "review/" + r.ColonyShot;
            }
        }
        int first = 0, last = 0;
        double wealth = 0;
        foreach (var r in rows)
        {
            if (r.Census.Colonists is not null)
            {
                if (first == 0)
                {
                    first = r.Colonists;
                }
                last = r.Colonists;
            }
            if (r.Census.WealthTotal is { } w)
            {
                wealth = w;
            }
        }
        return new RunSummary
        {
            Meta = meta,
            Hours = rows.Count,
            FirstColonists = first,
            LastColonists = last,
            MinMood = minMood,
            MinFoodDays = minFood,
            FinalWealth = wealth,
            Flags = flagCount,
            Zones = zones,
            SuppliesForbidden = supplies,
            Score = ColonyScoring.Compute(rows),
            Thumb = thumb,
        };
    }
}
