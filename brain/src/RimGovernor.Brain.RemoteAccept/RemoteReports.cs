using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>One selected case's pass count over a repeat=N soak run (Go <c>SoakRate</c>).</summary>
public sealed record SoakRate(string Case, int Passed, int Runs)
{
    /// <summary>The <c>case: p/n passed</c> line the command prints.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Case}: {Passed}/{Runs} passed");
}

/// <summary>Soak rates (Go soak.go): display only; the verdict is the aggregate's.</summary>
public static class RemoteSoak
{
    /// <summary>
    /// Tallies per-case passes over <paramref name="repeat"/> soak repetitions. Repetition 1 is the verdict evidence under
    /// <paramref name="root"/>; repetition k &gt; 1 lives under <c>&lt;soak&gt;/r&lt;k&gt;/&lt;shard&gt;/attempts.json</c>. A missing or unreadable
    /// repetition counts as a run that did not pass.
    /// </summary>
    public static IReadOnlyList<SoakRate> Rates(string root, string soak, int repeat)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(soak);
        if (repeat < 1)
        {
            throw new RemoteAcceptException("repeat must be at least 1");
        }

        var selection = StrictJson.ParseLoose(File.ReadAllBytes(Path.Combine(root, "selection.json")));
        var caseNames = StrictJson.LooseArr(selection, "cases").Select(c => StrictJson.LooseStr(StrictJson.ElementObj(c), "name")).ToList();
        var shards = StrictJson.LooseArr(selection, "shards").Select(s =>
        {
            var o = StrictJson.ElementObj(s);
            return (Id: StrictJson.LooseStr(o, "id"), Cases: StrictJson.LooseArr(o, "cases").Select(StrictJson.ElementStr).ToList());
        }).ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var passed = new int[caseNames.Count];
        for (var i = 0; i < caseNames.Count; i++)
        {
            index[caseNames[i]] = i;
        }

        for (var rep = 1; rep <= repeat; rep++)
        {
            var dir = rep > 1 ? Path.Combine(soak, string.Create(CultureInfo.InvariantCulture, $"r{rep}")) : root;
            foreach (var sh in shards)
            {
                var file = Path.Combine(dir, sh.Id, "attempts.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                var final = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    var attempts = StrictJson.ParseLoose(File.ReadAllBytes(file));
                    if (StrictJson.LooseStr(attempts, "shard_id") != sh.Id)
                    {
                        continue;
                    }

                    foreach (var item in StrictJson.LooseArr(attempts, "attempts"))
                    {
                        var at = StrictJson.ElementObj(item);
                        final[StrictJson.LooseStr(at, "case")] = StrictJson.LooseStr(at, "status");
                    }
                }
                catch (RemoteAcceptException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var name in sh.Cases)
                {
                    if (index.TryGetValue(name, out var i) && final.GetValueOrDefault(name) == "passed")
                    {
                        passed[i]++;
                    }
                }
            }
        }

        return [.. caseNames.Select((name, i) => new SoakRate(name, passed[i], repeat))];
    }
}

/// <summary>One case's row in index.json: where its evidence lives in the shard artifact the verdict job downloaded. Paths are relative to that artifact's root and empty when the file was not exported.</summary>
public sealed record IndexCase(
    string Name,
    string Status,
    int Attempts,
    string Shard,
    string Artifact,
    IReadOnlyList<string> Diagnosis,
    string Report,
    IReadOnlyList<string> Otlp,
    string Log,
    IReadOnlyList<string> Snapshots);

/// <summary>The per-case evidence index of one remote run (Go <c>Index</c>). Display only: the verdict is aggregate.json's.</summary>
public sealed record EvidenceIndex(int Version, string Status, IReadOnlyList<IndexCase> Cases)
{
    private const int MaxDiagnosis = 8;

    // The OTLP/JSON rings a case's service writes under its otlp directory; the index lists each active file that was
    // exported. Rotated segments (logs.jsonl.N) travel with them and are not listed.
    private static readonly string[] OtlpRings = ["logs.jsonl", "traces.jsonl", "metrics.jsonl"];

    /// <summary>
    /// Reads aggregate.json, run.json and each case's exported files under <paramref name="root"/>. Failing cases sort
    /// first, then by name. Missing optional evidence leaves a field empty, never an error.
    /// </summary>
    public static EvidenceIndex Build(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var agg = StrictJson.ParseLoose(File.ReadAllBytes(Path.Combine(root, "aggregate.json")));
        long runId = 0;
        long attempt = 0;
        try
        {
            var run = StrictJson.ParseLoose(File.ReadAllBytes(Path.Combine(root, "run.json")));
            if (StrictJson.LooseObj(run, "trigger") is { } trigger)
            {
                runId = StrictJson.LooseInt64(trigger, "actions_run_id");
                attempt = StrictJson.LooseInt64(trigger, "actions_run_attempt");
            }
        }
        catch (Exception e) when (e is RemoteAcceptException or IOException)
        {
            // run.json is optional.
            runId = 0;
            attempt = 0;
        }

        var rows = new List<IndexCase>();
        foreach (var item in StrictJson.LooseArr(agg, "cases"))
        {
            var c = StrictJson.ElementObj(item);
            var name = StrictJson.LooseStr(c, "name");
            var shard = StrictJson.LooseStr(c, "shard_id");
            var artifact = "";
            var report = "";
            var log = "";
            List<string> otlp = [];
            List<string> snapshots = [];
            IReadOnlyList<string> diagnosis = [];
            if (shard.Length > 0)
            {
                artifact = string.Create(CultureInfo.InvariantCulture, $"acceptance-{runId}-{attempt}-shard-{shard}");
                var baseDir = shard + "/fixture";
                report = Existing(root, baseDir + "/" + name + "/result.json");
                foreach (var ring in OtlpRings)
                {
                    if (Existing(root, baseDir + "/" + name + "/otlp/" + ring) is { Length: > 0 } p)
                    {
                        otlp.Add(p);
                    }
                }

                log = Existing(root, baseDir + "/" + name + ".log");
                var snapshotDir = Path.Combine(root, (baseDir + "/" + RemoteExport.SnapshotDir + "/" + name).Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(snapshotDir))
                {
                    var matches = Directory.GetFiles(snapshotDir, "routine-stream-*.jsonl").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal);
                    foreach (var m in matches)
                    {
                        snapshots.Add(baseDir + "/" + RemoteExport.SnapshotDir + "/" + name + "/" + m);
                    }
                }

                if (report.Length > 0)
                {
                    diagnosis = DiagnosisLines(Path.Combine(root, report.Replace('/', Path.DirectorySeparatorChar)));
                }
            }

            rows.Add(new IndexCase(name, StrictJson.LooseStr(c, "status"), (int)StrictJson.LooseInt64(c, "attempt_count"), shard, artifact, diagnosis, report, otlp, log, snapshots));
        }

        var ordered = rows
            .OrderBy(r => r.Status == "passed" ? 1 : 0)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        return new EvidenceIndex(2, StrictJson.LooseStr(agg, "status"), ordered);
    }

    /// <summary>Writes the index as index.json (two-space indent) under <paramref name="root"/>.</summary>
    public void WriteTo(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        RemoteEvidence.WriteJson(root, "index.json", Write);
    }

    /// <summary>Renders the index as a step-summary table.</summary>
    public string Markdown()
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"### Case index ({Status})\n\n");
        b.Append("| Case | Status | Attempts | Shard | `gh run download -n` | Evidence | Postmortem |\n|---|---|---|---|---|---|---|\n");
        foreach (var c in Cases)
        {
            var ev = new List<string>();
            foreach (var p in new[] { c.Report }.Concat(c.Otlp).Append(c.Log))
            {
                if (p.Length > 0)
                {
                    ev.Add("`" + p + "`");
                }
            }

            if (c.Snapshots.Count > 0)
            {
                var first = c.Snapshots[0];
                ev.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Snapshots.Count} snapshot streams `{first[..first.LastIndexOf('/')]}/`"));
            }

            b.Append(CultureInfo.InvariantCulture, $"| {Cell(c.Name)} | {Cell(c.Status)} | {c.Attempts} | {Cell(c.Shard)} | {Code(c.Artifact)} | {string.Join("<br>", ev)} | {Cell(string.Join("<br>", c.Diagnosis))} |\n");
        }

        return b.ToString();
    }

    private static string Code(string s) => s.Length == 0 ? "" : "`" + s + "`";

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

    private static string Existing(string root, string rel) =>
        File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))) ? rel : "";

    /// <summary>Reads the postmortem digest a failed case's report carries ("diagnosis") as <c>section: text</c> lines.</summary>
    private static List<string> DiagnosisLines(string report)
    {
        var lines = new List<string>();
        try
        {
            var r = StrictJson.ParseLoose(File.ReadAllBytes(report));
            if (StrictJson.LooseObj(r, "diagnosis") is not { } diagnosis)
            {
                return lines;
            }

            foreach (var sectionNode in StrictJson.LooseArr(diagnosis, "sections"))
            {
                var section = StrictJson.ElementObj(sectionNode);
                var sectionName = StrictJson.LooseStr(section, "name");
                foreach (var lineNode in StrictJson.LooseArr(section, "lines"))
                {
                    if (lines.Count == MaxDiagnosis)
                    {
                        return lines;
                    }

                    lines.Add(sectionName + ": " + StrictJson.LooseStr(StrictJson.ElementObj(lineNode), "text"));
                }
            }
        }
        catch (Exception e) when (e is RemoteAcceptException or IOException)
        {
            return [];
        }

        return lines;
    }

    private void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("schema_version", Version);
        w.WriteString("status", Status);
        w.WritePropertyName("cases");
        w.WriteStartArray();
        foreach (var c in Cases)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("status", c.Status);
            w.WriteNumber("attempt_count", c.Attempts);
            w.WriteString("shard_id", c.Shard);
            w.WriteString("artifact", c.Artifact);
            WriteList(w, "diagnosis", c.Diagnosis);
            if (c.Report.Length > 0)
            {
                w.WriteString("report", c.Report);
            }

            WriteList(w, "otlp", c.Otlp);
            if (c.Log.Length > 0)
            {
                w.WriteString("log", c.Log);
            }

            WriteList(w, "snapshots", c.Snapshots);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}

/// <summary>The measured wall time of each case on the CI runners (Go casetimes.go, the regeneration half).</summary>
public static class CaseTimes
{
    /// <summary>Reads every suite <c>result.json</c> under <paramref name="root"/> (a downloaded run's shard artifacts) and returns the table merged over the embedded one.</summary>
    public static SortedDictionary<string, long> Collect(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var table = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (JsonNode.Parse(SuiteFiles.ReadText("casetimes.json")) is JsonObject current)
        {
            foreach (var (name, value) in current)
            {
                if (value is not null)
                {
                    table[name] = (long)value.AsValue().GetValue<double>();
                }
            }
        }

        Walk(root, table);
        return table;
    }

    /// <summary>Renders the table one sorted case per line, exactly as the embedded file is kept.</summary>
    public static string Encode(IReadOnlyDictionary<string, long> times)
    {
        ArgumentNullException.ThrowIfNull(times);
        var names = times.Keys.Order(StringComparer.Ordinal).ToList();
        var b = new StringBuilder("{\n");
        for (var i = 0; i < names.Count; i++)
        {
            b.Append("  \"").Append(JsonEncodedText.Encode(names[i], JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString()).Append("\": ")
                .Append(times[names[i]].ToString(CultureInfo.InvariantCulture));
            if (i < names.Count - 1)
            {
                b.Append(',');
            }

            b.Append('\n');
        }

        return b.Append("}\n").ToString();
    }

    private static void Walk(string dir, SortedDictionary<string, long> table)
    {
        foreach (var entry in Directory.GetFileSystemEntries(dir).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Walk(entry, table);
                continue;
            }

            if (Path.GetFileName(entry) != "result.json")
            {
                continue;
            }

            try
            {
                var suite = StrictJson.ParseLoose(File.ReadAllBytes(entry));
                foreach (var item in StrictJson.LooseArr(suite, "cases"))
                {
                    var c = StrictJson.ElementObj(item);
                    var name = StrictJson.LooseStr(c, "name");
                    var wall = StrictJson.LooseInt64(c, "wall_ms");
                    if (name.Length > 0 && wall > 0)
                    {
                        table[name] = wall;
                    }
                }
            }
            catch (RemoteAcceptException)
            {
                // Not a suite report; skip it, as Go does.
                continue;
            }
        }
    }
}
