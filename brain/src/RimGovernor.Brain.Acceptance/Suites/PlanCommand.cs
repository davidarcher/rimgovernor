using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

// Remote planning uses the tested checkout's registry and affected rules. It never starts a game or silently reduces a
// selection to fit runner limits.

/// <summary>A file named by its slash path under the evidence root and its SHA-256 (Go <c>planReference</c>).</summary>
public sealed record PlanReference(string Path, string Sha256);

/// <summary>The run's capacity limits (Go <c>planLimits</c>).</summary>
public sealed record PlanLimits
{
    public string Runner { get; init; } = "";

    public int Shards { get; init; }

    public int Parallel { get; init; }

    public int Workers { get; init; }

    public int JobMinutes { get; init; }

    public int SuiteMinutes { get; init; }

    public int Attempts { get; init; }

    public int Retention { get; init; }

    public long ArtifactBytes { get; init; }

    public bool Paid { get; init; }
}

/// <summary>The run.json a remote dispatch carries (Go <c>planRun</c>).</summary>
public sealed record PlanRun
{
    public int Version { get; init; }

    public string Id { get; init; } = "";

    public string Repository { get; init; } = "";

    public string Event { get; init; } = "";

    public string Actor { get; init; } = "";

    public string PublishedRef { get; init; } = "";

    public long ActionsRunId { get; init; }

    public int ActionsRunAttempt { get; init; }

    public string Workflow { get; init; } = "";

    public string Head { get; init; } = "";

    public string Tier { get; init; } = "";

    public IReadOnlyList<string> Cases { get; init; } = [];

    public PlanReference Bundle { get; init; } = new("", "");

    public PlanLimits Limits { get; init; } = new();

    /// <summary>Refuses a run outside remote v1's capabilities (Go <c>planRun.validate</c>).</summary>
    public void Validate()
    {
        var zero = new string('0', 40);
        if (Version != 1 || !PlanCommand.Oid.IsMatch(Head) || !PlanCommand.Oid.IsMatch(Workflow) || Head == zero || Workflow == zero)
        {
            throw new AcceptanceException("run requires schema_version 1 and nonzero full commit identities");
        }

        if (Tier is not ("smoke" or "nightly" or "cases"))
        {
            throw new AcceptanceException($"unsupported remote tier \"{Tier}\"");
        }

        if ((Tier == "cases") != (Cases.Count > 0))
        {
            throw new AcceptanceException("the cases tier, and only it, takes a nonempty case list");
        }

        if (Event is not ("workflow_dispatch" or "schedule"))
        {
            throw new AcceptanceException($"unsupported trigger \"{Event}\"");
        }

        if (Event == "schedule" && PublishedRef != "refs/heads/main")
        {
            throw new AcceptanceException("scheduled planning requires refs/heads/main");
        }

        if (!PlanCommand.Repo.IsMatch(Repository) || Actor.Length == 0 || PublishedRef.Length == 0 || ActionsRunId < 1 || ActionsRunAttempt < 1
            || Id != string.Create(CultureInfo.InvariantCulture, $"gh:{Repository}:{ActionsRunId}:{ActionsRunAttempt}"))
        {
            throw new AcceptanceException("invalid run provenance");
        }

        if (!PlanCommand.ValidPlanPath(Bundle.Path) || !PlanCommand.Digest.IsMatch(Bundle.Sha256))
        {
            throw new AcceptanceException("invalid bundle reference");
        }

        var l = Limits;
        if (l.Runner != "windows-2022" || l.Shards < 1 || l.Shards > 32 || l.Parallel < 1 || l.Parallel > 20 || l.Workers != 1 || l.Paid
            || l.JobMinutes < 15 || l.JobMinutes > 360 || l.SuiteMinutes < 1 || l.SuiteMinutes > 345 || l.JobMinutes - l.SuiteMinutes < 5
            || l.Attempts < 1 || l.Attempts > 2 || l.Retention < 1 || l.Retention > 7 || l.ArtifactBytes < 0)
        {
            throw new AcceptanceException("run limits exceed remote v1 capabilities");
        }
    }
}

/// <summary>
/// <c>acceptance plan</c> (Go <c>cmd/acceptance/plan.go</c>): reads a remote run.json from the evidence root and prints the
/// case selection and shard plan as JSON. It never starts a game.
/// </summary>
public static partial class PlanCommand
{
    /// <summary>The usage line for the command's help text.</summary>
    public const string Usage = "  acceptance plan -evidence <root> -run <relative run.json>";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OidPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigestPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepoPattern();

    internal static Regex Oid { get; } = OidPattern();

    internal static Regex Digest { get; } = DigestPattern();

    internal static Regex Repo { get; } = RepoPattern();

    /// <summary>Runs the subcommand; returns the process exit code (0, or 2 on any refusal).</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            var fs = new FlagSet("plan");
            var path = fs.String("run", "");
            var root = fs.String("evidence", ".");
            fs.Parse(args);
            if (fs.Args.Count != 0 || !ValidPlanPath(path.Value))
            {
                throw new UsageException("usage: acceptance plan -evidence <root> -run <relative run.json>");
            }

            var raw = await ReadRootedAsync(root.Value, path.Value, cancellationToken).ConfigureAwait(false);
            var run = DecodePlanRun(raw);
            var reference = new PlanReference(path.Value, Convert.ToHexStringLower(SHA256.HashData(raw)));
            var plan = BuildSelection(run, reference, registry);
            await stdout.WriteLineAsync(plan.ToJsonString(Indented)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>Whether <paramref name="s"/> is a relative slash path with no empty, dot or dot-dot element (Go <c>validPlanPath</c>).</summary>
    public static bool ValidPlanPath(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0 || s.AsSpan().IndexOfAny('\\', ':') >= 0 || s.StartsWith('/'))
        {
            return false;
        }

        foreach (var p in s.Split('/'))
        {
            if (p is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads <paramref name="relative"/> under <paramref name="root"/> without following a link out of it (Go used
    /// <c>os.OpenRoot</c>): any element that is a reparse point or symbolic link refuses.
    /// </summary>
    internal static async Task<byte[]> ReadRootedAsync(string root, string relative, CancellationToken cancellationToken)
    {
        var current = Path.GetFullPath(root);
        if (!Directory.Exists(current))
        {
            throw new AcceptanceException($"open {root}: no such directory");
        }

        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null))
            {
                throw new AcceptanceException($"openat {relative}: path escapes from parent");
            }
        }

        return await File.ReadAllBytesAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes run.json: duplicate keys at any depth, trailing data and an unstated <c>paid_usage_authorized</c> refuse before the run is validated (Go <c>decodePlanRun</c>).</summary>
    public static PlanRun DecodePlanRun(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        using (doc)
        {
            var top = doc.RootElement;
            if (top.ValueKind != JsonValueKind.Object)
            {
                throw new AcceptanceException("json: cannot unmarshal into Go value of type main.planRun");
            }

            var trigger = Obj(top, "trigger");
            var limitsElement = Obj(top, "limits");

            // false is a valid value, but omission and null are not authorization.
            if (!(limitsElement is { } le && le.TryGetProperty("paid_usage_authorized", out var paid) && paid.ValueKind == JsonValueKind.False))
            {
                throw new AcceptanceException("paid_usage_authorized must explicitly be false");
            }

            var bundle = Obj(top, "bundle");
            var cases = new List<string>();
            if (top.TryGetProperty("cases", out var caseList) && caseList.ValueKind != JsonValueKind.Null)
            {
                if (caseList.ValueKind != JsonValueKind.Array)
                {
                    throw new AcceptanceException("json: cannot unmarshal cases into []string");
                }

                foreach (var c in caseList.EnumerateArray())
                {
                    cases.Add(c.ValueKind == JsonValueKind.String ? c.GetString()! : throw new AcceptanceException("json: cannot unmarshal cases element into string"));
                }
            }

            var run = new PlanRun
            {
                Version = Int(top, "schema_version"),
                Id = Str(top, "run_id"),
                Repository = Str(top, "repository"),
                Event = Str(trigger, "event"),
                Actor = Str(trigger, "actor"),
                PublishedRef = Str(trigger, "published_ref"),
                ActionsRunId = Long(trigger, "actions_run_id"),
                ActionsRunAttempt = Int(trigger, "actions_run_attempt"),
                Workflow = Str(top, "workflow_commit"),
                Head = Str(top, "tested_commit"),
                Tier = Str(top, "tier"),
                Cases = cases,
                Bundle = new PlanReference(Str(bundle, "path"), Str(bundle, "sha256")),
                Limits = new PlanLimits
                {
                    Runner = Str(limitsElement, "runner_label"),
                    Shards = Int(limitsElement, "shards"),
                    Parallel = Int(limitsElement, "max_parallel"),
                    Workers = Int(limitsElement, "workers_per_shard"),
                    JobMinutes = Int(limitsElement, "job_timeout_minutes"),
                    SuiteMinutes = Int(limitsElement, "suite_timeout_minutes"),
                    Attempts = Int(limitsElement, "max_attempts"),
                    Retention = Int(limitsElement, "artifact_retention_days"),
                    ArtifactBytes = Long(limitsElement, "artifact_max_bytes"),
                    Paid = false,
                },
            };
            run.Validate();
            return run;
        }
    }

    private static JsonElement? Obj(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new AcceptanceException($"json: cannot unmarshal {name} into an object");
    }

    private static string Str(JsonElement? parent, string name)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new AcceptanceException($"json: cannot unmarshal {value.ValueKind} into field {name} of type string");
    }

    private static long Long(JsonElement? parent, string name)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : throw new AcceptanceException($"json: cannot unmarshal {value.GetRawText()} into field {name} of type int64");
    }

    private static int Int(JsonElement? parent, string name)
    {
        var n = Long(parent, name);
        return n is >= int.MinValue and <= int.MaxValue
            ? (int)n
            : throw new AcceptanceException($"json: cannot unmarshal {n} into field {name} of type int");
    }

    private static int Int(JsonElement parent, string name) => Int((JsonElement?)parent, name);

    private static string Str(JsonElement parent, string name) => Str((JsonElement?)parent, name);

    /// <summary>
    /// Builds the selection: the tier's cases (or the requested ones) minus the rendered ones, the shard plan and the
    /// per-case rows. Refuses a selection that cannot fit the run's limits instead of truncating it (Go <c>buildSelection</c>).
    /// </summary>
    public static JsonObject BuildSelection(PlanRun r, PlanReference reference, CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(registry);
        r.Validate();
        var all = registry.All();
        var smoke = Tiers.CasesOf(registry, "smoke");
        IReadOnlyList<AcceptanceCase> chosen = smoke;
        if (r.Tier == "nightly")
        {
            chosen = Tiers.CasesOf(registry, r.Tier);
        }

        if (r.Tier == "cases")
        {
            chosen = RequestedCases(all, r.Cases);
        }

        var skipped = new List<SkippedCase>();

        // Hosted Windows has no usable GPU. Keep exclusions explicit and remove them before assigning shards or charging
        // execution budgets.
        var selected = chosen.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        selected.RemoveAll(c =>
        {
            if (!c.Rendered)
            {
                return false;
            }

            skipped.Add(new SkippedCase(c.Name, "rendered"));
            return true;
        });
        if (selected.Count == 0)
        {
            throw new AcceptanceException("empty remote selection");
        }

        var names = selected.Select(c => c.Name).ToList();
        var costs = new Dictionary<string, long>(StringComparer.Ordinal);
        var ceilings = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var c in selected)
        {
            costs[c.Name] = RemotePlanning.ShardCost(c.Name, c.Budget);
            ceilings[c.Name] = c.Budget.Ticks * 100;
        }

        var algorithm = RemotePlanning.BudgetAlgorithm;
        var shards = RemotePlanning.PlanShards(names, r.Limits.Shards, algorithm, costs);

        // Measured times balance real wall time, but every shard's budgets must still fit the suite allowance; when they do
        // not, plan on the budgets themselves.
        var allowance = TimeSpan.FromMinutes(r.Limits.SuiteMinutes).Ticks * 100 / r.Limits.Attempts;
        foreach (var shard in shards)
        {
            var load = shard.Cases.Sum(name => ceilings[name]);
            if (load > allowance)
            {
                shards = RemotePlanning.PlanShards(names, r.Limits.Shards, algorithm, ceilings);
                break;
            }
        }

        var plannedShards = new JsonArray();
        var assignment = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < shards.Count; i++)
        {
            plannedShards.Add(new JsonObject
            {
                ["id"] = shards[i].Id,
                ["cases"] = new JsonArray([.. shards[i].Cases.Select(n => (JsonNode?)JsonValue.Create(n))]),
            });
            foreach (var name in shards[i].Cases)
            {
                assignment[name] = i;
            }
        }

        var rows = new JsonArray();
        var budgets = new TimeSpan[shards.Count];
        foreach (var c in selected)
        {
            if (c.Matrix)
            {
                throw new AcceptanceException($"{c.Name} requires a separate matrix selection; selection cannot be truncated");
            }

            var roles = new JsonArray("bridge");
            if (Tiers.ServeDriven(c))
            {
                roles.Add("controller");
            }

            var reasons = new List<string>();
            if (smoke.Any(s => string.Equals(s.Name, c.Name, StringComparison.Ordinal)))
            {
                reasons.Add("smoke");
            }

            if (r.Tier == "nightly")
            {
                reasons.Add(r.Tier);
            }

            if (r.Tier == "cases")
            {
                reasons.Add("requested");
            }

            reasons.Sort(StringComparer.Ordinal);
            var ops = c.FixtureOps().Order(StringComparer.Ordinal).ToList();
            if (reasons.Count == 0 || c.Budget <= TimeSpan.Zero)
            {
                throw new AcceptanceException($"{c.Name} lacks selection reason or budget");
            }

            var row = new JsonObject();
            row["budget_ns"] = c.Budget.Ticks * 100;
            row["name"] = c.Name;
            row["reasons"] = new JsonArray([.. reasons.Select(s => (JsonNode?)JsonValue.Create(s))]);
            row["fixture_ops"] = new JsonArray([.. ops.Select(s => (JsonNode?)JsonValue.Create(s))]);
            row["roles"] = roles;
            row["mod_role"] = "fixture";
            row["rendered"] = c.Rendered;
            rows.Add(row);
            budgets[assignment[c.Name]] += c.Budget * r.Limits.Attempts;
        }

        for (var i = 0; i < budgets.Length; i++)
        {
            if (budgets[i] > TimeSpan.FromMinutes(r.Limits.SuiteMinutes))
            {
                throw new AcceptanceException(
                    $"{shards[i].Id} known case budgets including retries ({GoDuration.Format(budgets[i])}) exceed suite allowance; increase shards within v1 limits");
            }
        }

        var plan = new JsonObject
        {
            ["schema_version"] = 1,
            ["run"] = new JsonObject { ["path"] = reference.Path, ["sha256"] = reference.Sha256 },
            ["planner_commit"] = r.Head,
            ["cases"] = rows,
        };
        if (skipped.Count > 0)
        {
            plan["skipped"] = new JsonArray([.. skipped.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["reason"] = s.Reason })]);
        }

        plan["algorithm"] = algorithm;
        plan["shards"] = plannedShards;
        return plan;
    }

    /// <summary>
    /// Resolves an on-demand dispatch list. Each entry is an exact registry name or a bare area naming every case in it; an
    /// entry that matches nothing refuses the plan rather than quietly running less (Go <c>requestedCases</c>).
    /// </summary>
    public static IReadOnlyList<AcceptanceCase> RequestedCases(IReadOnlyList<AcceptanceCase> all, IReadOnlyList<string> want)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(want);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AcceptanceCase>();
        foreach (var raw in want)
        {
            var w = raw.Trim();
            var hit = false;
            foreach (var c in all)
            {
                var slash = c.Name.IndexOf('/', StringComparison.Ordinal);
                var area = slash < 0 ? c.Name : c.Name[..slash];
                if (!string.Equals(c.Name, w, StringComparison.Ordinal) && !string.Equals(area, w, StringComparison.Ordinal))
                {
                    continue;
                }

                hit = true;
                if (seen.Add(c.Name))
                {
                    result.Add(c);
                }
            }

            if (!hit)
            {
                throw new AcceptanceException($"requested case \"{w}\" matches no registered case or area");
            }
        }

        return result;
    }
}
