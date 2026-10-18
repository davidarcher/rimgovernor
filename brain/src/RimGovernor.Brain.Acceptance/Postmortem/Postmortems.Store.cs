using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance;

// The raw-store sections of the digest.
public static class PostmortemStore
{
    private static readonly Lock ProviderGate = new();
    private static bool _providerReady;

    /// <summary>Registers the native SQLite provider once (the same SourceGear e_sqlite3 library the store uses).</summary>
    internal static void EnsureProvider()
    {
        lock (ProviderGate)
        {
            if (_providerReady)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            _providerReady = true;
        }
    }

    // --- 3. rounds (raw store) -------------------------------------

    /// <summary>
    /// Opens the store file at <paramref name="path"/> read-only without the store's schema checks; the note explains a
    /// null connection. The caller disposes the connection.
    /// </summary>
    internal static (SqliteConnection? Db, string Note) OpenRaw(string path)
    {
        if (!File.Exists(path))
        {
            return (null, "no service.sqlite (native-only case or the service never started)");
        }

        SqliteConnection? connection = null;
        try
        {
            EnsureProvider();
            connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.GetFullPath(path),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 2,
                }.ToString());
            connection.Open();
            return (connection, "");
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or ArgumentException or IOException)
        {
            connection?.Dispose();
            return (null, "open service.sqlite: " + e.Message);
        }
    }

    private static string? PayloadText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };
    }

    private static JsonObject? ParsePayload(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A member by name, ignoring case.</summary>
    private static JsonNode? Field(JsonNode? node, string name)
    {
        if (node is JsonObject map)
        {
            foreach (var (key, value) in map)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static long Int64Of(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return (long)d;
        }

        return value.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    /// <summary>
    /// Reads rounds raw: the review's revision and tick, and every Need with a live method and its live method count.
    /// </summary>
    internal static DigestSection RoundsSection(SqliteConnection? db, string note)
    {
        var s = new DigestSection("rounds");
        if (db is null)
        {
            s.Note = note;
            return s;
        }

        // development is need -> refused (ranked and not selected).
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM rounds WHERE singleton=1";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                s.Note = "rounds is empty: the service never reviewed, or reset by a world rebuild";
            }
            else if (ParsePayload(PayloadText(reader, 0)) is not { } review)
            {
                s.Note = "rounds payload: not a JSON object";
            }
            else
            {
                var enabled = Field(review, "Enabled").AsBool() ?? false;
                var text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"review revision {Int64Of(Field(review, "Revision"))} at tick {Int64Of(Field(review, "Tick"))} enabled={(enabled ? "true" : "false")}");
                s.Lines.Add(new DigestLine(text, "service.sqlite rounds"));
            }
        }
        catch (SqliteException e)
        {
            s.Note = "rounds: " + e.Message;
        }

        // The Needs with live work: a Need in progress that is missing here offered no Method.
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT m.need_id, count(*) FROM need_methods m JOIN plans p ON p.id=m.plan_id WHERE p.retired=0 GROUP BY m.need_id ORDER BY m.need_id";
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                count++;
                if (s.Lines.Count >= 2 * Postmortems.MaxLines)
                {
                    continue;
                }

                var need = reader.GetString(0);
                s.Lines.Add(new DigestLine(
                    string.Create(CultureInfo.InvariantCulture, $"need {need}: {reader.GetInt64(1)} live methods"),
                    "service.sqlite need_methods#" + need));
            }

            if (count == 0 && s.Note.Length == 0)
            {
                s.Lines.Add(new DigestLine("no need with a live method", "service.sqlite need_methods"));
            }
        }
        catch (SqliteException e)
        {
            s.Lines.Add(new DigestLine("need_methods: " + e.Message, "service.sqlite need_methods"));
        }

        return s;
    }

    // --- 4. unsuccessful stages (raw store) --------------------------------

    /// <summary>Lists every observe transition that ended an attempt unsuccessful, with the action's kind and the native reason.</summary>
    internal static DigestSection UnsuccessfulStages(SqliteConnection? db, string note)
    {
        var s = new DigestSection("unsuccessful plan stages");
        if (db is null)
        {
            s.Note = note;
            return s;
        }

        // The C# store keeps every table the postmortem reads (rounds, plans, transitions,
        // actions), so no table is added for the digest. Its actions carry kind and a JSON body but no definition/pawn
        // columns, so the action line names the kind only; a journal predating the actions table still
        // has the transitions, and the kind is then blank.
        string[] queries =
        [
            "SELECT t.sequence, t.action_id, t.payload, COALESCE(a.kind,''), '', '' FROM transitions t LEFT JOIN actions a ON a.id=t.action_id ORDER BY t.sequence",
            "SELECT sequence, action_id, payload, '', '', '' FROM transitions ORDER BY sequence",
        ];
        SqliteDataReader? reader = null;
        SqliteCommand? command = null;
        SqliteException? failure = null;
        foreach (var query in queries)
        {
            try
            {
                // The command outlives this block: disposing it closes the reader it produced.
                command = db.CreateCommand();
                command.CommandText = query;
                reader = command.ExecuteReader();
                break;
            }
            catch (SqliteException e)
            {
                failure = e;
                command?.Dispose();
                command = null;
            }
        }

        if (reader is null)
        {
            s.Note = "transitions: " + failure?.Message;
            return s;
        }

        using (command)
        using (reader)
        {
            var total = 0;
            try
            {
                while (reader.Read())
                {
                    var sequence = reader.GetInt64(0);
                    var action = reader.GetString(1);
                    var kind = reader.GetString(3);
                    var definition = reader.GetString(4);
                    var pawn = reader.GetString(5);
                    if (ParsePayload(PayloadText(reader, 2)) is not { } evt)
                    {
                        continue;
                    }

                    var observation = Field(evt, "Observation");
                    var unsuccessful = Field(observation, "Effect").AsString() == "unsuccessful";
                    var receipt = Field(evt, "Receipt").AsString();
                    var refused = Field(evt, "Kind").AsString() == "receipt" && (receipt == "refused" || receipt == "unsent");
                    if (!unsuccessful && !refused)
                    {
                        continue;
                    }

                    total++;
                    if (s.Lines.Count >= Postmortems.MaxLines)
                    {
                        continue;
                    }

                    var what = kind;
                    if (definition.Length > 0)
                    {
                        what += " " + definition;
                    }

                    if (pawn.Length > 0)
                    {
                        what += " by " + pawn;
                    }

                    var tick = Int64Of(Field(evt, "Tick"));
                    var text = unsuccessful
                        ? string.Create(CultureInfo.InvariantCulture, $"{action} ({what}) unsuccessful at tick {tick}: {Field(observation, "UnsuccessfulReason").AsString()}")
                        : string.Create(CultureInfo.InvariantCulture, $"{action} ({what}) receipt {receipt} at tick {tick}");
                    s.Lines.Add(new DigestLine(text, string.Create(CultureInfo.InvariantCulture, $"service.sqlite transitions#{sequence}")));
                }
            }
            catch (SqliteException)
            {
                // A row read that fails ends the listing with what was read.
            }

            if (total > s.Lines.Count)
            {
                s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"... {total - s.Lines.Count} more"), "service.sqlite transitions"));
            }

            if (total == 0)
            {
                s.Note = "no unsuccessful observation or refused receipt in transitions";
            }
        }

        return s;
    }

    // --- 6. authority generations ------------------------------------------

    /// <summary>
    /// How an authority row moved the grant: "changed" for native's clock_authority row (an AuthorityChanged event), else
    /// the brain's authority row's change field ("lost", "retaken").
    /// </summary>
    private static string? AuthorityMovement(Postmortems.LogRecordRow row)
    {
        if (row.Rec.Kind == TelemetryKinds.ClockAuthority.Name)
        {
            return "changed";
        }

        if (row.Rec.Kind != "authority")
        {
            return null;
        }

        var change = Postmortems.FirstText(row.Fields(), "change");
        return change.Length > 0 ? change : "changed";
    }

    /// <summary>
    /// Reports how the native authority generation moved: each distinct Snapshot.Native across transitions in sequence
    /// order, the authority rows (changed, lost, retaken) and the Rounder steps that failed on a lost control, and the
    /// report's own reacquisition count. A thrashing generation invalidates every open attempt.
    /// </summary>
    internal static DigestSection AuthorityGenerations(SqliteConnection? db, string note, List<Postmortems.LogRecordRow> rows, JsonObject report)
    {
        var s = new DigestSection("authority generations");
        if (report.TryGetPropertyValue("authority_reacquisitions", out var reacquisitions))
        {
            s.Lines.Add(new DigestLine($"report authority_reacquisitions={Plain(reacquisitions)}", "result.json authority_reacquisitions"));
        }

        if (db is not null)
        {
            try
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT sequence, payload FROM transitions ORDER BY sequence";
                using var reader = command.ExecuteReader();
                long last = -1;
                var flips = 0;
                long first = 0, lastSeq = 0;
                while (reader.Read())
                {
                    var sequence = reader.GetInt64(0);
                    var native = Int64Of(Field(Field(ParsePayload(PayloadText(reader, 1)), "Snapshot"), "Native"));
                    if (native <= 0)
                    {
                        // A transition without a native snapshot (a hold, a cancel) says nothing about the generation.
                        continue;
                    }

                    lastSeq = sequence;
                    if (native == last)
                    {
                        continue;
                    }

                    if (last >= 0)
                    {
                        flips++;
                        if (flips <= Postmortems.MaxLines / 2)
                        {
                            s.Lines.Add(new DigestLine(
                                string.Create(CultureInfo.InvariantCulture, $"generation {last} -> {native}"),
                                string.Create(CultureInfo.InvariantCulture, $"service.sqlite transitions#{sequence}")));
                        }
                    }
                    else
                    {
                        first = native;
                    }

                    last = native;
                }

                if (last >= 0)
                {
                    s.Lines.Add(new DigestLine(
                        string.Create(CultureInfo.InvariantCulture, $"generation {first} first, {last} last, {flips} flips across transitions"),
                        string.Create(CultureInfo.InvariantCulture, $"service.sqlite transitions#{lastSeq}")));
                }
            }
            catch (SqliteException e)
            {
                s.Lines.Add(new DigestLine("transitions: " + e.Message, "service.sqlite transitions"));
            }
        }
        else if (note.Length > 0)
        {
            s.Note = note;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastRow = new Dictionary<string, Postmortems.LogRecordRow>(StringComparer.Ordinal);
        var controlLost = 0;
        Postmortems.LogRecordRow? lastControlLost = null;
        foreach (var row in rows)
        {
            if (AuthorityMovement(row) is { } change)
            {
                counts[change] = counts.GetValueOrDefault(change) + 1;
                lastRow[change] = row;
            }

            if (row.Rec.Kind == TelemetryKinds.PlannerStep.Name && Postmortems.FirstText(row.Fields(), "reason") == "control_lost")
            {
                controlLost++;
                lastControlLost = row;
            }
        }

        foreach (var change in new[] { "changed", "lost", "retaken" })
        {
            if (counts.GetValueOrDefault(change) == 0)
            {
                continue;
            }

            var f = lastRow[change].Fields();
            var text = string.Create(CultureInfo.InvariantCulture, $"{counts[change]} authority rows {change}; last");
            var generation = Postmortems.FirstText(f, "generation");
            if (generation.Length > 0)
            {
                text += " generation " + generation;
            }

            var reason = Postmortems.FirstText(f, "reason");
            if (reason.Length > 0)
            {
                text += " (" + reason + ")";
            }

            s.Lines.Add(new DigestLine(text, lastRow[change].Evidence()));
        }

        if (controlLost > 0 && lastControlLost is not null)
        {
            s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"{controlLost} Rounder steps failed on lost control"), lastControlLost.Evidence()));
        }

        if (s.Lines.Count == 0 && s.Note.Length == 0)
        {
            s.Note = "no authority movement recorded";
        }

        return s;
    }

    /// <summary>A decoded-JSON value as plain text: strings bare, integral numbers without a fraction, none as &lt;nil&gt;.</summary>
    private static string Plain(JsonNode? value)
    {
        if (value is null)
        {
            return "<nil>";
        }

        if (value is JsonValue json)
        {
            if (json.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (json.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }

            if (json.TryGetValue<long>(out var l))
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }

            if (json.TryGetValue<double>(out var d))
            {
                return Postmortems.FormatNumber(d);
            }
        }

        return value.ToJsonString();
    }
}
