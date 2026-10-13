using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Builds the digest <c>acceptance why</c> prints for a failed case (Go package <c>postmortem</c>): the manual
/// sequence every diagnosis started with, read from the case's own evidence in a fixed order. Each line names the file
/// and row it came from so the digest is checkable, not a verdict.
/// The store is read raw (a run's service.sqlite may predate the current schema, which the store's own open refuses); a
/// missing table or file is reported as a line, never an error, so a native-only case still gets a digest.
/// </summary>
public static partial class Postmortems
{
    /// <summary>Bounds every section: the digest is the first thing read, not the whole log.</summary>
    private const int MaxLines = 12;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reads <paramref name="dir"/>'s evidence and returns the digest. <paramref name="report"/> is the case's result
    /// (result.json decoded, or the live <see cref="Report.Data"/>); null reads result.json from <paramref name="dir"/>.
    /// Never fails on missing or malformed evidence: it records what it could not read as section notes.
    /// </summary>
    public static Digest Collect(string dir, JsonObject? report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dir);
        if (report is null)
        {
            report = [];
            try
            {
                if (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "result.json"))) is JsonObject read)
                {
                    report = read;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // A report that cannot be read leaves every report-derived section noting its absence.
            }
        }

        var sections = new List<DigestSection>();
        var rows = LogRows(dir);
        sections.Add(Revision(report, cancellationToken));
        sections.Add(StageGraph(report));
        sections.Add(Refusals(dir, rows));
        var (db, storeNote) = OpenRaw(dir);
        using (db)
        {
            sections.Add(RoundsSection(db, storeNote));
            sections.Add(ExtentEligibility(dir));
            sections.Add(UnsuccessfulStages(db, storeNote));
            sections.Add(JobFailures(rows));
            sections.Add(AuthorityGenerations(db, storeNote, rows, report));
        }

        return new Digest(report["case"].AsString(), report["error"].AsString(), sections);
    }

    // --- 1. revision -----------------------------------------------------

    /// <summary>
    /// Compares the run binary's source revision (the installed package manifest on the report) with main: a failure
    /// already fixed on main is the cheapest diagnosis there is.
    /// </summary>
    private static DigestSection Revision(JsonObject report, CancellationToken cancellationToken)
    {
        var s = new DigestSection("revision");
        var pkg = report["installed_package"];
        var rev = pkg.Member("source_revision").AsString();
        if (rev.Length == 0)
        {
            s.Note = "result.json has no installed_package.source_revision (native-only case or no manifest)";
            return s;
        }

        var text = "run binary built from " + Short(rev);
        if (pkg.Member("source_dirty").AsBool() ?? false)
        {
            text += " (dirty worktree)";
        }

        s.Lines.Add(new DigestLine(text, "result.json installed_package.source_revision"));
        var repo = FindRepo(Directory.GetCurrentDirectory());
        if (repo is null)
        {
            s.Note = "no enclosing checkout: compare against main by hand";
            return s;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GitTimeout);
        var tip = Git(repo, ["rev-parse", "--short", "main"], deadline.Token);
        if (!tip.Ok)
        {
            s.Note = "git could not resolve main: " + tip.Error;
            return s;
        }

        if (!Git(repo, ["cat-file", "-e", rev + "^{commit}"], deadline.Token).Ok)
        {
            s.Lines.Add(new DigestLine("revision is not in this checkout (a peer's worktree?); main is " + tip.Output, "git rev-parse main"));
            return s;
        }

        if (!Git(repo, ["merge-base", "--is-ancestor", rev, "main"], deadline.Token).Ok)
        {
            s.Lines.Add(new DigestLine("revision is not an ancestor of main " + tip.Output + " (unlanded branch build)", "git merge-base --is-ancestor"));
            return s;
        }

        var log = Git(repo, ["log", "--oneline", rev + "..main"], deadline.Token);
        if (!log.Ok)
        {
            s.Note = "git log failed: " + log.Error;
            return s;
        }

        var range = "git log " + Short(rev) + "..main";
        if (log.Output.Length == 0)
        {
            s.Lines.Add(new DigestLine("run binary is at main " + tip.Output, range));
            return s;
        }

        var landings = log.Output.Split('\n');
        s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"{landings.Length} landings on main since the run binary; check them before diagnosing"), range));
        for (var i = 0; i < landings.Length; i++)
        {
            if (i >= MaxLines - 1)
            {
                s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"... {landings.Length - i} more"), "git log"));
                break;
            }

            s.Lines.Add(new DigestLine(landings[i], range));
        }

        return s;
    }

    private static string Short(string rev) => rev.Length > 12 ? rev[..12] : rev;

    /// <summary>The checkout enclosing <paramref name="dir"/>: the nearest ancestor holding .git (a directory in the main checkout and a file in a worktree); null outside one.</summary>
    private static string? FindRepo(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d is not null; d = d.Parent)
        {
            var git = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return d.FullName;
            }
        }

        return null;
    }

    private readonly record struct GitResult(bool Ok, string Output, string Error);

    private static GitResult Git(string repo, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return new GitResult(false, "", "git did not start");
            }

            using var kill = cancellationToken.Register(() => TryKill(process));
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (cancellationToken.IsCancellationRequested)
            {
                return new GitResult(false, output, "git timed out");
            }

            return process.ExitCode == 0
                ? new GitResult(true, output, "")
                : new GitResult(false, output, error.Length > 0 ? error : string.Create(CultureInfo.InvariantCulture, $"exit status {process.ExitCode}"));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return new GitResult(false, "", e.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }

    // --- 2. refusals -------------------------------------------------------

    /// <summary>
    /// One decoded log record with the segment it came from. A row's evidence is "&lt;file&gt;#&lt;line&gt;": the segment
    /// path relative to the case directory and the 1-based line there, the number <c>grep -n</c> prints.
    /// </summary>
    private sealed record LogRecordRow(string File, int Line, TimelineRecord Rec)
    {
        public string Evidence() => string.Create(CultureInfo.InvariantCulture, $"{File}#{Line}");

        /// <summary>The row's data as one flat map (a decision row's attrs with its verdict, reason and target).</summary>
        public IReadOnlyDictionary<string, object?> Fields() => RowFields(Rec);
    }

    /// <summary>
    /// The row's data under one flat map: a decision row's attrs plus its verdict, reason, target and dur_ms (attrs win
    /// on a clash), any other row's payload.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> RowFields(TimelineRecord row)
    {
        if (!row.Payload.ContainsKey("verdict"))
        {
            return row.Payload;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in new[] { "verdict", "reason", "target", "dur_ms" })
        {
            if (row.Payload.TryGetValue(key, out var v))
            {
                fields[key] = v;
            }
        }

        if (row.Payload.GetValueOrDefault("attrs") is IReadOnlyDictionary<string, object?> attrs)
        {
            foreach (var (k, v) in attrs)
            {
                fields[k] = v;
            }
        }

        return fields;
    }

    /// <summary>Decodes every log ring under <paramref name="dir"/>, oldest launch first (see <see cref="LogRings"/>). A line that is not a row is skipped: the digest is read from whatever survived.</summary>
    private static List<LogRecordRow> LogRows(string dir)
    {
        var rows = new List<LogRecordRow>();
        foreach (var ring in LogRings(dir))
        {
            OtlpRing.Scan(
                Path.Combine(dir, ring.Replace('/', Path.DirectorySeparatorChar)),
                (pos, line) =>
                {
                    if (OtlpReader.DecodeLog(line.Span) is not { } log || OtlpTimeline.FromLog(log) is not { } rec)
                    {
                        return;
                    }

                    rows.Add(new LogRecordRow(Path.GetRelativePath(dir, pos.File).Replace('\\', '/'), pos.Line, rec));
                });
        }

        // Rows of successive rings merge by wall time; each keeps its own line, which is why evidence names the file.
        return [.. rows.OrderBy(r => r.Rec.WallTime)];
    }

    private static int LaunchIndex(string name)
    {
        if (name == "service")
        {
            return 1;
        }

        return int.TryParse(name.StartsWith("service-", StringComparison.Ordinal) ? name["service-".Length..] : name, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    /// <summary>
    /// Lists every OTLP log ring under <paramref name="dir"/> (the case's own otlp/logs.jsonl and each relaunch's
    /// service-N/otlp/logs.jsonl), oldest launch first, relative to <paramref name="dir"/>; the ring scan reads a ring's
    /// rotated segments (logs.jsonl.N) itself.
    /// </summary>
    private static List<string> LogRings(string dir)
    {
        var rings = new List<string>();
        void Add(string sub)
        {
            var ring = Path.Combine(sub, "otlp", OtlpFiles.Logs).Replace('\\', '/');
            if (OtlpRing.Segments(Path.Combine(dir, ring.Replace('/', Path.DirectorySeparatorChar))).Count > 0)
            {
                rings.Add(ring);
            }
        }

        Add(".");
        var relaunches = new List<string>();
        if (Directory.Exists(dir))
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateDirectories())
            {
                if (entry.Name.StartsWith("service-", StringComparison.Ordinal) && entry.Name != "service-profile")
                {
                    relaunches.Add(entry.Name);
                }
            }
        }

        relaunches.Sort((a, b) => LaunchIndex(a).CompareTo(LaunchIndex(b)));
        foreach (var r in relaunches)
        {
            Add(r);
        }

        return rings;
    }

    /// <summary>
    /// Lists the last native refusals: refused step results, the Worker's refused dispatch rows, and native_call rows
    /// that carry an error or a failure code. Repeats collapse (distinct texts are counted) and the most recent come
    /// first, so a refusal repeated every window shows once with its count.
    /// </summary>
    private static DigestSection Refusals(string dir, List<LogRecordRow> rows)
    {
        var s = new DigestSection("native refusals (last first)");
        s.Lines.AddRange(RefusedSteps(dir));
        foreach (Func<LogRecordRow, string?> read in new Func<LogRecordRow, string?>[] { DispatchRefusal, NativeCallRefusal })
        {
            foreach (var l in GroupRows(rows, read))
            {
                if (s.Lines.Count >= 2 * MaxLines)
                {
                    break;
                }

                s.Lines.Add(l);
            }
        }

        if (s.Lines.Count == 0)
        {
            s.Note = "no refused step result, refused dispatch row, or native_call error or failure in otlp/logs.jsonl*";
        }

        return s;
    }

    /// <summary>Reads every row with <paramref name="read"/> and returns the distinct texts, most recent first, each with its count and the evidence of its last row.</summary>
    private static List<DigestLine> GroupRows(List<LogRecordRow> rows, Func<LogRecordRow, string?> read)
    {
        var groups = new List<Group>();
        var index = new Dictionary<string, Group>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (read(rows[i]) is not { } text)
            {
                continue;
            }

            var key = Normalize(text);
            if (!index.TryGetValue(key, out var g))
            {
                g = new Group(text);
                index[key] = g;
                groups.Add(g);
            }

            g.Count++;
            g.Last = i;
            g.Evidence = rows[i].Evidence();
        }

        var lines = new List<DigestLine>(groups.Count);
        foreach (var g in groups.OrderByDescending(g => g.Last))
        {
            var text = Clip(g.Text);
            if (g.Count > 1)
            {
                text = string.Create(CultureInfo.InvariantCulture, $"{text} (x{g.Count})");
            }

            lines.Add(new DigestLine(text, g.Evidence));
        }

        return lines;
    }

    private sealed class Group(string text)
    {
        public string Text { get; } = text;

        public string Evidence { get; set; } = "";

        public int Last { get; set; }

        public int Count { get; set; }
    }

    /// <summary>Whether <paramref name="kind"/> is the Worker's per-action outcome row. (The per-run worker_dispatch tally is not an outcome.)</summary>
    private static bool IsOutcomeKind(string kind) => kind == "dispatch";

    /// <summary>One outcome row read under the v2 decision shape (target, verdict, reason, attrs.error, attrs.refused) or the legacy payload (action, outcome, refused, err).</summary>
    private readonly record struct Outcome(string Action, string Verdict, string Reason, string Refused, string Err, string Stage);

    private static Outcome? ReadOutcome(LogRecordRow row)
    {
        if (!IsOutcomeKind(row.Rec.Kind))
        {
            return null;
        }

        var f = row.Fields();
        return new Outcome(
            FirstText(f, "action", "target"),
            FirstText(f, "outcome", "verdict"),
            FirstText(f, "reason"),
            FirstText(f, "refused"),
            FirstText(f, "err", "error"),
            FirstText(f, "stage"));
    }

    /// <summary>The first of <paramref name="keys"/> the row carries as text; a list renders comma-joined, and an empty list is no text.</summary>
    private static string FirstText(IReadOnlyDictionary<string, object?> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            switch (fields.GetValueOrDefault(key))
            {
                case string text:
                    if (text.Length > 0)
                    {
                        return text;
                    }

                    break;
                case IEnumerable<object?> list:
                    var parts = list.OfType<string>().Where(item => item.Length > 0).ToList();
                    if (parts.Count > 0)
                    {
                        return string.Join(',', parts);
                    }

                    break;
                case double number:
                    return FormatNumber(number);
                case bool flag:
                    return flag ? "true" : "false";
            }
        }

        return "";
    }

    /// <summary>A number as Go's <c>%v</c> prints it: integral values without a fraction.</summary>
    private static string FormatNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Reads a Worker outcome row the native side or the planner turned down: verdict refused, or a non-empty refused list.</summary>
    private static string? DispatchRefusal(LogRecordRow row)
    {
        if (ReadOutcome(row) is not { } o || (o.Verdict != "refused" && o.Refused.Length == 0))
        {
            return null;
        }

        var text = "dispatch " + o.Action + " refused";
        if (o.Refused.Length > 0)
        {
            text += " [" + o.Refused + "]";
        }

        if (o.Reason.Length > 0)
        {
            text += ": " + o.Reason;
        }

        if (o.Err.Length > 0)
        {
            text += ": " + o.Err;
        }

        return text;
    }

    private static string Text(object? value) => value as string ?? "";

    /// <summary>
    /// Reads a native_call row that failed: a transport or refusal error (the text the native side gave, when it gave
    /// one), or a reply carrying a {"failure":{"code":...}} payload, a refusal the native side answered rather than
    /// raised.
    /// </summary>
    private static string? NativeCallRefusal(LogRecordRow row)
    {
        if (row.Rec.Kind != "native_call")
        {
            return null;
        }

        var payload = row.Rec.Payload;
        var tool = Text(payload.GetValueOrDefault("native_tool"));
        if (tool.Length == 0)
        {
            tool = Text(payload.GetValueOrDefault("tool"));
        }

        var errText = Text(payload.GetValueOrDefault("error"));
        if (errText.Length > 0)
        {
            var refused = Text(payload.GetValueOrDefault("refused_text"));
            if (refused.Length > 0)
            {
                errText = refused;
            }

            return $"native_call error {tool}: {Clip(errText)}";
        }

        if (RecordedReply(payload) is not { } reply)
        {
            return null;
        }

        var failure = reply["failure"];
        var code = failure.Member("code").AsString();
        if (code.Length == 0)
        {
            return null;
        }

        var text = $"native_call refused {tool}: {code}";
        var detail = failure.Member("detail").AsString();
        if (detail.Length > 0)
        {
            text += ": " + Clip(detail);
        }

        return text;
    }

    /// <summary>
    /// A recorded native_call row's reply as a JSON object: the JSON text the row's <c>result.payload</c> holds, or the
    /// <c>result</c> object itself when it already is the reply. (The Go rows also carried a packed protobuf reply; the
    /// C# rows are plain JSON.)
    /// </summary>
    private static JsonObject? RecordedReply(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault("result") is not IReadOnlyDictionary<string, object?> result)
        {
            return null;
        }

        if (result.GetValueOrDefault("payload") is string text)
        {
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return result.ContainsKey("failure") ? ToNode(result) as JsonObject : null;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        double d => JsonValue.Create(d),
        IReadOnlyDictionary<string, object?> map => ToObject(map),
        IEnumerable<object?> list => ToArray(list),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static JsonObject ToObject(IReadOnlyDictionary<string, object?> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map)
        {
            o[k] = ToNode(v);
        }

        return o;
    }

    private static JsonArray ToArray(IEnumerable<object?> list)
    {
        var a = new JsonArray();
        foreach (var v in list)
        {
            a.Add(ToNode(v));
        }

        return a;
    }

    private static List<DigestLine> RefusedSteps(string dir)
    {
        var lines = new List<DigestLine>();
        if (!Directory.Exists(dir))
        {
            return lines;
        }

        // Sequence numbers can grow beyond the four-digit minimum width.
        static int Sequence(string path)
        {
            var name = Path.GetFileName(path);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            return int.TryParse(dash > 0 ? name[..dash] : name, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        var files = Directory.EnumerateFiles(dir, "*-*.json")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Length > 5 && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && char.IsAsciiDigit(name[2]) && char.IsAsciiDigit(name[3]) && name.IndexOf('-', 4) > 0;
            })
            .OrderByDescending(Sequence);
        foreach (var file in files)
        {
            JsonObject? step;
            try
            {
                step = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (step is null || StepResult.Parse(step["result"]) is not { } failure)
            {
                continue;
            }

            var tool = step["request"].Member("tool").AsString();
            var kind = failure.Kind;
            if (kind == "native exception" && tool.StartsWith("test/", StringComparison.Ordinal))
            {
                kind = "fixture exception";
            }

            var text = $"{kind}: {tool}: {failure.Summary}";
            if (kind == "blocking attention")
            {
                text += "\n" + failure.Detail;
            }

            lines.Add(new DigestLine(text, Path.GetFileName(file) + " result"));
            if (lines.Count == MaxLines)
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>Strips ids and numbers so repeats of one refusal collapse.</summary>
    private static string Normalize(string text)
    {
        var b = new StringBuilder();
        foreach (var r in text.Trim())
        {
            if (r is >= '0' and <= '9')
            {
                continue;
            }

            b.Append(r);
        }

        return b.ToString();
    }

    private static string Clip(string text)
    {
        text = text.Trim();
        return text.Length > 300 ? text[..300] + "..." : text;
    }

    // --- 5. job failures ---------------------------------------------------

    /// <summary>
    /// Lists the Worker outcome rows that failed (verdict failed, or an error text): the native refusal or contract
    /// failure a pawn order carried, and the pooled-job mismatch a completed order raised when the native record read a
    /// pooled Job, grouped by text with a count.
    /// </summary>
    private static DigestSection JobFailures(List<LogRecordRow> rows)
    {
        var s = new DigestSection("native job failures");
        var lines = GroupRows(rows, row =>
        {
            if (ReadOutcome(row) is not { } o || (o.Verdict != "failed" && o.Err.Length == 0))
            {
                return null;
            }

            var text = "dispatch " + o.Action + " " + o.Verdict;
            if (o.Stage.Length > 0)
            {
                text += " at " + o.Stage;
            }

            if (o.Err.Length > 0)
            {
                text += ": " + o.Err;
            }

            return text;
        });
        for (var i = 0; i < lines.Count; i++)
        {
            if (i >= MaxLines)
            {
                s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"... {lines.Count - i} more distinct failures"), "otlp/logs.jsonl*"));
                break;
            }

            s.Lines.Add(lines[i]);
        }

        if (s.Lines.Count == 0)
        {
            s.Note = "no failed dispatch row in otlp/logs.jsonl*";
        }

        return s;
    }
}
