using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>
/// Gathers the layout section's inputs from a colony-review output (#3751): the journal's plans (<c>service.sqlite</c> beside
/// result.json, or <c>-journal</c>), the service's OTLP log ring (the path result.json's service entry records, or
/// <c>-logs</c>) cut to the run's wall-clock window, the timeline's housing census and the start's biome. A source that is
/// missing or unreadable is left null with a note, so its metrics read unknown.
/// </summary>
internal static class LayoutReviewLoad
{
    private const string ServiceEntry = "service";

    /// <summary>The inputs of the output dir <paramref name="dir"/>; an empty <paramref name="journal"/> or <paramref name="logs"/> takes the run's own.</summary>
    public static LayoutInputs Read(string dir, string journal, string logs)
    {
        var notes = new List<string>();
        var resultPath = Path.Combine(dir, "result.json");
        var biome = "";
        DateTimeOffset? started = null;
        DateTimeOffset? ended = null;
        var recordedLogs = "";
        var housing = new List<HousingRow>();
        if (File.Exists(resultPath))
        {
            ended = File.GetLastWriteTimeUtc(resultPath);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(resultPath));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("review", out var review) && review.ValueKind == JsonValueKind.Object
                        && review.TryGetProperty("biome", out var b) && b.ValueKind == JsonValueKind.String)
                    {
                        biome = b.GetString() ?? "";
                    }
                    if (root.TryGetProperty(Report.StartedAtKey, out var at) && at.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s))
                    {
                        started = s;
                    }
                    recordedLogs = LogPathOf(root);
                    if (root.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array)
                    {
                        housing = HousingOf(timeline);
                    }
                }
            }
            catch (JsonException e)
            {
                notes.Add("result.json: " + e.Message);
            }
        }
        if (housing.Count == 0)
        {
            notes.Add("the timeline has no housing census (a run recorded before #3751)");
        }

        return new LayoutInputs
        {
            Plans = PlansOf(journal.Length > 0 ? journal : Path.Combine(dir, Checkpoints.CheckpointStoreFile), notes),
            Records = RecordsOf(logs.Length > 0 ? logs : recordedLogs, started, ended, notes),
            Housing = housing,
            Biome = biome,
            Notes = notes,
        };
    }

    // The ring path the last service entry (service, service_2, ...) recorded.
    private static string LogPathOf(JsonElement root)
    {
        var path = "";
        foreach (var p in root.EnumerateObject())
        {
            if ((p.Name == ServiceEntry || p.Name.StartsWith(ServiceEntry + "_", StringComparison.Ordinal))
                && p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty(ServiceProcess.LogPathKey, out var log) && log.ValueKind == JsonValueKind.String)
            {
                path = log.GetString() ?? "";
            }
        }
        return path;
    }

    private static List<HousingRow> HousingOf(JsonElement timeline)
    {
        var rows = new List<HousingRow>();
        foreach (var sample in timeline.EnumerateArray())
        {
            if (sample.ValueKind == JsonValueKind.Object
                && sample.TryGetProperty("tick", out var t) && t.TryGetInt32(out var tick)
                && sample.TryGetProperty(ColonyHousing.Key, out var h) && h.ValueKind == JsonValueKind.Object
                && h.TryGetProperty(ColonyHousing.ColonistsKey, out var c) && c.TryGetInt32(out var colonists)
                && h.TryGetProperty(ColonyHousing.HousedKey, out var x) && x.TryGetInt32(out var housed))
            {
                rows.Add(new HousingRow(tick, colonists, housed));
            }
        }
        return rows;
    }

    // The journal's plans of the world its latest plan belongs to. The table is read raw and read-only rather than through
    // Store.Open, which writes (WAL, schema) and refuses a journal from another schema version: CI renders with the trusted
    // rg, which can be older or newer than the tested build. A plan that no longer decodes is skipped.
    private static List<PlanRow>? PlansOf(string path, List<string> notes)
    {
        if (!File.Exists(path))
        {
            notes.Add("no journal at " + path);
            return null;
        }
        try
        {
            var rows = new List<(string Colony, string Map, int Tick, LayoutPlan Plan)>();
            var skipped = 0;
            var (db, note) = PostmortemStore.OpenRaw(path);
            if (db is null)
            {
                notes.Add("journal " + path + ": " + note);
                return null;
            }
            using (var conn = db)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT colony,map_id,tick,plan FROM colony_layout_plans ORDER BY tick, rowid";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var plan = Decode(reader.GetString(3));
                    if (plan is null)
                    {
                        skipped++;
                        continue;
                    }
                    rows.Add((reader.GetValue(0).ToString() ?? "", reader.GetValue(1).ToString() ?? "", reader.GetInt32(2), plan));
                }
            }
            if (skipped > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"journal {path}: {skipped} plan(s) did not decode"));
            }
            if (rows.Count == 0)
            {
                return [];
            }
            var last = rows[^1];
            return [.. rows.Where(r => r.Colony == last.Colony && r.Map == last.Map).Select(r => new PlanRow(r.Tick, r.Plan))];
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or InvalidCastException or IOException)
        {
            notes.Add("journal " + path + ": " + e.Message);
            return null;
        }
    }

    private static LayoutPlan? Decode(string json)
    {
        try
        {
            var plan = StoreJson.Deserialize<LayoutPlan>(json);
            return plan is not null && plan.Valid() ? plan : null;
        }
        catch (Exception e) when (e is StoreCorruptException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // The ring's rows inside the run's wall-clock window (a ring under a kept profile also holds earlier launches').
    private static List<TimelineRecord>? RecordsOf(string path, DateTimeOffset? started, DateTimeOffset? ended, List<string> notes)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            notes.Add(path.Length == 0 ? "result.json names no log ring (pass -logs)" : "no log ring at " + path);
            return null;
        }
        var from = started?.ToUnixTimeMilliseconds() / 1000.0 ?? double.NegativeInfinity;
        var to = ended?.ToUnixTimeMilliseconds() / 1000.0 ?? double.PositiveInfinity;
        try
        {
            return [.. OtlpTimeline.Read(path).Where(r => r.WallTime >= from && r.WallTime <= to)];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            notes.Add("log ring " + path + ": " + e.Message);
            return null;
        }
    }
}
