using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>A recomputed verdict (Go <c>Evaluation</c>); a supplied aggregate's pass bit is never evidence.</summary>
public sealed class Evaluation
{
    internal Evaluation(Ref run, Ref selection, IReadOnlyList<Shard> shards)
    {
        Aggregate = new Aggregate(1, run, selection, shards, "passed", true, [], null);
        Report = new Report("", false, null, [], new Remote(0, null, "", "", null));
    }

    /// <summary>The recomputed aggregate.</summary>
    public Aggregate Aggregate { get; internal set; }

    /// <summary>The native-shaped result report.</summary>
    public Report Report { get; internal set; }

    /// <summary>The run identity read from run.json, once read.</summary>
    public Run? Run { get; internal set; }

    /// <summary>The selection read from selection.json, once read.</summary>
    public Selection? Selection { get; internal set; }

    /// <summary>The digest-verified file closure, sorted, useful to upload tooling.</summary>
    public IReadOnlyList<string> Files { get; internal set; } = [];

    internal void Problem(string status, string message)
    {
        Aggregate = Aggregate with
        {
            Passed = false,
            Status = Aggregate.Status != "incomplete" ? status : Aggregate.Status,
            Error = Aggregate.Error is null ? message : Aggregate.Error + "; " + message,
        };
    }
}

/// <summary>The evidence graph operations (Go <c>internal/remoteaccept</c>): digest-pinned references, the recomputed verdict and verification.</summary>
public static class RemoteEvidence
{
    /// <summary>The digest-pinned reference to <paramref name="relativePath"/> under <paramref name="root"/> (Go <c>FileRef</c>).</summary>
    public static Ref FileRef(string root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!EvidenceRules.ValidPath(relativePath))
        {
            throw new RemoteAcceptException($"unsafe path \"{relativePath}\"");
        }

        return new Ref(relativePath, EvidenceRules.HashFile(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>
    /// Recomputes the aggregate verdict from the digest-pinned evidence under <paramref name="root"/>. Throws
    /// <see cref="EvaluationFailedException"/> (carrying the partial evaluation) when a check fails closed.
    /// </summary>
    public static Evaluation Evaluate(string root, Ref runRef, Ref selectionRef, IReadOnlyList<Shard> shards)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(runRef);
        ArgumentNullException.ThrowIfNull(selectionRef);
        ArgumentNullException.ThrowIfNull(shards);
        var e = new Evaluation(runRef, selectionRef, shards);
        try
        {
            EvaluateCore(e, root, runRef, selectionRef, shards);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EvaluationFailedException(ex.Message, e, ex);
        }

        return e;
    }

    /// <summary>Recomputes the aggregate and compares every verdict and coverage field with the one on disk (Go <c>Verify</c>).</summary>
    public static Evaluation Verify(string root, Ref aggregateRef)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(aggregateRef);
        var tree = EvidenceTree.Open(root);
        var supplied = tree.Read(aggregateRef, Aggregate.Parse);
        var e = Evaluate(root, supplied.Run, supplied.Selection, supplied.Shards);
        if (!supplied.SameAs(e.Aggregate))
        {
            throw new RemoteAcceptException("aggregate disagrees with verified shard evidence");
        }

        e.Report = e.Report with { Remote = e.Report.Remote with { Aggregate = aggregateRef } };
        return e;
    }

    /// <summary>Writes <paramref name="write"/>'s JSON (two-space indent, trailing newline) to <paramref name="relativePath"/> under <paramref name="root"/> and returns its reference (Go <c>WriteJSON</c>).</summary>
    internal static Ref WriteJson(string root, string relativePath, Action<Utf8JsonWriter> write)
    {
        if (!EvidenceRules.ValidPath(relativePath))
        {
            throw new RemoteAcceptException($"unsafe output path \"{relativePath}\"");
        }

        var abs = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var parent = Path.GetDirectoryName(abs)!;
        if (!Directory.Exists(parent))
        {
            throw new RemoteAcceptException($"output directory not found: {parent}");
        }

        EvidenceRules.RejectLinkedPath(parent, "linked output directory");
        var info = new FileInfo(abs);
        if (info.Attributes != (FileAttributes)(-1) && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw new RemoteAcceptException($"nonregular output {relativePath}");
        }

        var bytes = StrictJson.Indented(write);
        File.WriteAllBytes(abs, bytes);
        return new Ref(relativePath, EvidenceRules.Hash(bytes));
    }

    internal static Ref WriteAggregate(string root, string relativePath, Aggregate aggregate) => WriteJson(root, relativePath, aggregate.Write);

    internal static Ref WriteReport(string root, string relativePath, Report report) => WriteJson(root, relativePath, report.Write);

    private static void EvaluateCore(Evaluation e, string root, Ref runRef, Ref selectionRef, IReadOnlyList<Shard> shards)
    {
        var t = EvidenceTree.Open(root);
        var r = t.Read(runRef, Run.Parse);
        e.Run = r;
        if (r.Version != 1 || !EvidenceRules.IsRepository(r.Repository) || !EvidenceRules.IsOid(r.TestedCommit) || !EvidenceRules.IsOid(r.WorkflowCommit)
            || (r.Tier != "smoke" && r.Tier != "nightly" && r.Tier != "cases"))
        {
            throw new RemoteAcceptException("invalid run identity/version/tier");
        }

        if (r.Trigger.RunId <= 0 || r.Trigger.Attempt < 1 || r.Trigger.Actor.Length == 0 || !r.Trigger.PublishedRef.StartsWith("refs/", StringComparison.Ordinal)
            || (r.Trigger.Event != "workflow_dispatch" && r.Trigger.Event != "schedule")
            || r.Id != string.Create(CultureInfo.InvariantCulture, $"gh:{r.Repository}:{r.Trigger.RunId}:{r.Trigger.Attempt}"))
        {
            throw new RemoteAcceptException("invalid trigger/run ID");
        }

        var l = r.Limits;
        if (l.Shards < 1 || l.Shards > 32 || l.Parallel < 1 || l.Parallel > 20 || l.Workers != 1 || l.Attempts < 1 || l.Attempts > 2 || l.Bytes < 0 || l.Paid
            || l.Runner != "windows-2022" || l.JobMinutes < 16 || l.JobMinutes > 360 || l.SuiteMinutes < 1 || l.SuiteMinutes > 345
            || l.JobMinutes - l.SuiteMinutes < 15 || l.Retention < 1 || l.Retention > 7)
        {
            throw new RemoteAcceptException("invalid run limits");
        }

        var bundleVersion = t.Read(r.Bundle, n => StrictJson.Int(n as JsonObject ?? throw new RemoteAcceptException("expected a JSON object"), "schema_version"));
        if (bundleVersion != 1)
        {
            throw new RemoteAcceptException("unknown bundle version");
        }

        var s = t.Read(selectionRef, Selection.Parse);
        e.Selection = s;
        if (s.Version != 1 || s.Run != runRef || s.Planner != r.TestedCommit
            || (s.Algorithm != "sorted-round-robin-v1" && s.Algorithm != RemotePlanning.DependencyAlgorithm && s.Algorithm != RemotePlanning.BudgetAlgorithm))
        {
            throw new RemoteAcceptException("selection identity/version/algorithm mismatch");
        }

        if (s.Cases.Count == 0 || s.Shards.Count == 0 || s.Shards.Count > l.Shards)
        {
            throw new RemoteAcceptException("empty or oversized selection");
        }

        var planned = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        var budgets = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var c in s.Cases)
        {
            if (!EvidenceRules.ValidPath(c.Name) || c.Name.Count(static ch => ch == '/') != 1 || c.Reasons.Count == 0)
            {
                throw new RemoteAcceptException($"invalid selected case {c.Name}");
            }

            if (names.Count > 0 && string.CompareOrdinal(names[^1], c.Name) >= 0)
            {
                throw new RemoteAcceptException($"duplicate/unsorted selected case {c.Name}");
            }

            foreach (var reason in c.Reasons)
            {
                if (reason != "smoke" && !(reason == "requested" && r.Tier == "cases") && !(reason == r.Tier && r.Tier == "nightly"))
                {
                    throw new RemoteAcceptException($"invalid reason {reason}");
                }
            }

            names.Add(c.Name);
            budgets[c.Name] = c.BudgetNs;
        }

        var wantShards = RemotePlanning.PlanShards(names, l.Shards, s.Algorithm, budgets);
        if (!SameShards(s.Shards, wantShards))
        {
            throw new RemoteAcceptException("shards do not match selection algorithm");
        }

        foreach (var sh in s.Shards)
        {
            foreach (var name in sh.Cases)
            {
                planned[name] = sh.Id;
            }
        }

        var actual = new Dictionary<string, Shard>(StringComparer.Ordinal);
        foreach (var sh in shards)
        {
            if (!actual.TryAdd(sh.Id, sh))
            {
                throw new RemoteAcceptException($"duplicate shard {sh.Id}");
            }

            if (!s.Shards.Any(p => p.Id == sh.Id))
            {
                throw new RemoteAcceptException($"unknown shard {sh.Id}");
            }

            if (sh.Status != "complete" && sh.Status != "missing" && sh.Status != "cancelled" && sh.Status != "timed_out")
            {
                throw new RemoteAcceptException($"unknown shard status {sh.Status}");
            }
        }

        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var summary = new Dictionary<string, CaseRow>(StringComparer.Ordinal);
        foreach (var sh in s.Shards)
        {
            if (!actual.TryGetValue(sh.Id, out var got))
            {
                got = new Shard(sh.Id, "missing", null);
                e.Aggregate = e.Aggregate with { Shards = [.. e.Aggregate.Shards, got] };
            }

            if (got.Status != "complete" || got.Attempts is null)
            {
                e.Problem("incomplete", "shard " + sh.Id + " is " + got.Status + " or lacks attempts");
            }

            foreach (var name in sh.Cases)
            {
                summary[name] = new CaseRow(name, sh.Id, 0, null, "incomplete");
            }

            if (got.Attempts is null)
            {
                continue;
            }

            var a = t.Read(got.Attempts, Attempts.Parse);
            if (a.Version != 1 || a.Run != runRef || a.Selection != selectionRef || a.ShardId != sh.Id)
            {
                throw new RemoteAcceptException($"shard {sh.Id} identity mismatch");
            }

            if (a.Runner.OS != "windows" || a.Runner.Arch != "x64" || a.Runner.Image.Length == 0 || a.Runner.Cpus <= 0 || a.Runner.Memory <= 0 || a.Runner.Disk <= 0)
            {
                throw new RemoteAcceptException($"invalid runner {sh.Id}");
            }

            var bootstrapPassed = t.Read(a.Runner.Bootstrap, n => StrictJson.Bool(n as JsonObject ?? throw new RemoteAcceptException("expected a JSON object"), "passed"));
            if (!bootstrapPassed)
            {
                e.Problem("incomplete", "bootstrap failed: " + sh.Id);
            }

            var previous = new Dictionary<string, Attempt>(StringComparer.Ordinal);
            foreach (var attempt in a.Items)
            {
                if (!planned.TryGetValue(attempt.Case, out var plannedShard) || plannedShard != sh.Id)
                {
                    throw new RemoteAcceptException($"extra or wrong-shard case {attempt.Case}");
                }

                var hasPrev = previous.TryGetValue(attempt.Case, out var prev);
                ValidateAttempt(attempt, prev, hasPrev, l.Attempts);
                if (attempt.Log is { Length: > 0 } logText)
                {
                    Ref log;
                    try
                    {
                        log = Ref.Parse(StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(logText)));
                    }
                    catch (RemoteAcceptException ex)
                    {
                        throw new RemoteAcceptException("missing/malformed attempt log: " + ex.Message, ex);
                    }

                    t.ReadBytes(log);
                }

                if (hasPrev && prev!.Evidence.Path == attempt.Evidence.Path)
                {
                    throw new RemoteAcceptException($"retry overwrites prior evidence {attempt.Evidence.Path}");
                }

                var native = t.ReadObject(attempt.Evidence);
                if (native.ContainsKey("cases"))
                {
                    native = SuiteRow(native, attempt.Case, sh.Cases);
                }

                var passedField = StrictJson.Bool(native, "passed");
                var name = StrictJson.OptStr(native, "name");
                var caseField = StrictJson.OptStr(native, "case");
                var exitField = StrictJson.OptNullableInt(native, "exit");
                _ = StrictJson.OptBool(native, "fixture_only");
                List<Ref> files = [];
                if (native.TryGetPropertyValue("evidence_files", out var filesNode))
                {
                    files = StrictJson.Elements(filesNode as JsonArray ?? throw new RemoteAcceptException("evidence_files: expected an array"), Ref.Parse);
                }

                if ((string.IsNullOrEmpty(name) && string.IsNullOrEmpty(caseField)) || (!string.IsNullOrEmpty(name) && name != attempt.Case) || (!string.IsNullOrEmpty(caseField) && caseField != attempt.Case))
                {
                    throw new RemoteAcceptException($"native case identity mismatch: {attempt.Case}");
                }

                foreach (var file in files)
                {
                    t.ReadBytes(file);
                }

                foreach (var key in new[] { "log", "output" })
                {
                    if (native.TryGetPropertyValue(key, out var raw))
                    {
                        string p;
                        if (raw is null)
                        {
                            p = "";
                        }
                        else if (raw is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                        {
                            p = v.GetValue<string>();
                        }
                        else
                        {
                            throw new RemoteAcceptException($"{key}: expected a string");
                        }

                        if (!EvidenceRules.ValidPath(p) || !t.Has(p))
                        {
                            throw new RemoteAcceptException($"missing/unsafe {key} evidence: {p}");
                        }
                    }
                }

                var passed = attempt.Status == "passed";
                if (passed != passedField || (exitField is { } nativeExit && attempt.Exit is { } attemptExit && nativeExit != attemptExit))
                {
                    throw new RemoteAcceptException($"native verdict/exit disagrees for {attempt.Case}");
                }

                var status = passed ? "passed" : "failed";
                // Retain each native report untouched. The projected row adds only links to the original report and its complete attempt history.
                var row = new JsonObject();
                foreach (var (key, value) in native)
                {
                    row[key] = value?.DeepClone();
                }

                row["name"] = attempt.Case;
                row["exit"] = attempt.Exit is { } x ? JsonValue.Create(x) : null;
                row["remote_evidence"] = RefNode(attempt.Evidence);
                row["remote_attempts"] = RefNode(got.Attempts);
                rows[attempt.Case] = StrictJson.SortedCopy(row);
                summary[attempt.Case] = new CaseRow(attempt.Case, sh.Id, attempt.Number, attempt.Number, status);
                previous[attempt.Case] = attempt;
            }
        }

        var cases = new List<CaseRow>();
        var reportCases = new List<JsonObject>();
        foreach (var name in names)
        {
            var c = summary[name];
            cases.Add(c);
            if (c.Status != "passed")
            {
                e.Problem(c.Status, name + ": " + c.Status);
            }

            if (!rows.TryGetValue(name, out var row))
            {
                row = new JsonObject { ["name"] = name, ["passed"] = false };
            }

            reportCases.Add(row);
        }

        e.Aggregate = e.Aggregate with { Cases = cases };
        e.Report = new Report(r.Tier, e.Aggregate.Passed, e.Aggregate.Error, reportCases, new Remote(1, null, r.TestedCommit, r.Bundle.Sha256, null));
        e.Files = [.. t.Used];
    }

    private static bool SameShards(IReadOnlyList<PlannedShard> a, IReadOnlyList<PlannedShard> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || !a[i].Cases.SequenceEqual(b[i].Cases))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonObject RefNode(Ref r) => new() { ["path"] = r.Path, ["sha256"] = r.Sha256 };

    private static JsonObject SuiteRow(JsonObject suite, string name, IReadOnlyList<string> allowed)
    {
        var envelopePassed = StrictJson.Bool(suite, "passed");
        var casesArray = StrictJson.Arr(suite, "cases");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? found = null;
        foreach (var item in casesArray)
        {
            var row = item as JsonObject ?? throw new RemoteAcceptException("null or malformed suite row");
            var rowName = StrictJson.Str(row, "name");
            var rowPassed = StrictJson.Bool(row, "passed");
            var rowExit = StrictJson.Int(row, "exit");
            if (!allowed.Contains(rowName, StringComparer.Ordinal) || !seen.Add(rowName))
            {
                throw new RemoteAcceptException($"extra/duplicate suite case {rowName}");
            }

            if (envelopePassed && (!rowPassed || rowExit != 0))
            {
                throw new RemoteAcceptException($"suite hides failed row {rowName}");
            }

            if (rowName == name)
            {
                found = row;
            }
        }

        return found ?? throw new RemoteAcceptException($"suite omits {name}");
    }

    private static bool TryParseUtcTimestamp(string text, out DateTimeOffset value)
    {
        value = default;
        // RFC 3339 with the Z suffix: yyyy-MM-ddTHH:mm:ss[.fraction]Z
        if (text.Length < 20 || text[^1] != 'Z')
        {
            return false;
        }

        var body = text[..^1];
        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? body : body[..dot];
        var fraction = dot < 0 ? "" : body[(dot + 1)..];
        if (dot >= 0 && (fraction.Length == 0 || !fraction.All(static c => c is >= '0' and <= '9')))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(whole, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            return false;
        }

        if (fraction.Length > 0)
        {
            var digits = fraction.Length > 7 ? fraction[..7] : fraction.PadRight(7, '0');
            value = value.AddTicks(long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        return true;
    }

    private static void ValidateAttempt(Attempt a, Attempt? prev, bool hasPrev, int maxAttempts)
    {
        var prevNumber = hasPrev ? prev!.Number : 0;
        if (a.Number < 1 || a.Number > maxAttempts || a.Number != prevNumber + 1)
        {
            throw new RemoteAcceptException($"duplicate/gapped attempt for {a.Case}");
        }

        if (!TryParseUtcTimestamp(a.Started, out var start) || !TryParseUtcTimestamp(a.Finished, out var end) || end < start)
        {
            throw new RemoteAcceptException("invalid attempt timestamps");
        }

        if (hasPrev)
        {
            if (!TryParseUtcTimestamp(prev!.Finished, out var priorEnd))
            {
                priorEnd = default;
            }

            if (a.RetryOf is not { } retryOf || retryOf != prev.Number || prev.Status != "failed" || prev.Classification != "infrastructure" || start < priorEnd)
            {
                throw new RemoteAcceptException($"ineligible retry: {a.Case}");
            }
        }
        else if (a.RetryOf is not null)
        {
            throw new RemoteAcceptException("first attempt has retry_of");
        }

        switch (a.Status)
        {
            case "passed":
                if (a.Exit is not 0 || a.Classification != "none" || a.Error is not null)
                {
                    throw new RemoteAcceptException("invalid successful attempt");
                }

                break;
            case "failed":
                if (a.Exit is null or 0 || string.IsNullOrEmpty(a.Error)
                    || (a.Classification != "infrastructure" && a.Classification != "assertion" && a.Classification != "unknown"))
                {
                    throw new RemoteAcceptException("invalid failed attempt");
                }

                break;
            case "cancelled":
                if (a.Classification != "cancelled")
                {
                    throw new RemoteAcceptException("invalid cancellation");
                }

                break;
            case "timed_out":
                if (a.Classification != "timeout")
                {
                    throw new RemoteAcceptException("invalid timeout");
                }

                break;
            default:
                throw new RemoteAcceptException("unknown attempt status");
        }
    }
}
