using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One row of a suite: a registry case (Go <c>entry</c>).</summary>
public sealed record SuiteEntry
{
    public SuiteEntry(string name, string acceptance = "")
    {
        Name = name;
        Acceptance = acceptance;
    }

    public string Name { get; }

    /// <summary>Names the criterion this row stands for (documentation echoed into the suite report), if the suite file says.</summary>
    public string Acceptance { get; }

    /// <summary>The registry case the row resolves to.</summary>
    public AcceptanceCase? Registered { get; init; }

    /// <summary>Under <c>-stages</c>, makes the row one stage item of its case (<c>run -through</c>) rather than the run to its verdict.</summary>
    public string Through { get; init; } = "";

    /// <summary>Whether the row hosts the brain service itself.</summary>
    public bool ServeDriven => Registered is not null && Tiers.ServeDriven(Registered);

    /// <summary>
    /// The row's scheduling tier: 0 keeps the process, 1 ends or replaces it (NoKeep, Rendered), 2 hosts a service and
    /// keeps the process, 3 hosts a service and ends it. The row after an ending one boots cold, so the ending
    /// serve-driven rows go behind the keeping ones.
    /// </summary>
    public int Tier
    {
        get
        {
            if (ServeDriven && Registered!.NoKeep)
            {
                return 3;
            }

            if (ServeDriven)
            {
                return 2;
            }

            return Registered is { } c && (c.NoKeep || c.Rendered) ? 1 : 0;
        }
    }
}

/// <summary>The suite subcommand's resolved configuration (Go <c>suiteOptions</c>).</summary>
public sealed record SuiteOptions
{
    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string Baseline { get; init; } = "";

    public string GameId { get; init; } = "rimgovernor-trial";

    /// <summary>The metrics series the rows append to; <see cref="NoSeries"/> skips it.</summary>
    public string Series { get; init; } = "";

    public bool NoSeries { get; init; }

    public int Workers { get; init; } = 2;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Per-case safety net, passed through to <c>acceptance run</c>; zero leaves the runner's default.</summary>
    public TimeSpan CaseTimeout { get; init; }

    /// <summary>Per-case budget override, passed through; zero leaves the runner's default.</summary>
    public TimeSpan Budget { get; init; }

    /// <summary>Stall budget override, passed through; zero leaves the runner's default.</summary>
    public TimeSpan Stall { get; init; }

    /// <summary>Passed through to <c>acceptance run -evidence</c>; empty leaves the runner's default.</summary>
    public string Evidence { get; init; } = "";

    /// <summary>The tier the set came from (<c>-tier</c>), recorded in the report; empty for -all, -cases and -suite.</summary>
    public string Tier { get; init; } = "";

    /// <summary>Carries each case's checkpoint ring from Root into its worker and lets the row resume from it instead of forcing -fresh.</summary>
    public bool Resume { get; init; }

    /// <summary>
    /// Schedules each staged case's declared stages as their own work items from the bundles cached in Root
    /// (see <see cref="SuiteStages"/>); rows that opened on a stage bundle pass and are listed.
    /// </summary>
    public bool Stages { get; init; }
}

/// <summary>What an earlier suite's result.json contributes: wall and boot times and the flake record by case name (Go <c>baseline</c>).</summary>
public sealed class SuiteBaseline
{
    private readonly Dictionary<string, double> _wall = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _boot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flake> _flake = new(StringComparer.Ordinal);

    private SuiteBaseline(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>Loads a suite result.json; throws <see cref="AcceptanceException"/> with the <c>-baseline</c> prefix on a missing or malformed file.</summary>
    public static SuiteBaseline Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            throw new AcceptanceException($"-baseline: {e.Message}", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new AcceptanceException($"-baseline: {e.Message}", e);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"-baseline: {path} is not a suite result.json: {e.Message}", e);
        }

        if (root is not JsonObject top)
        {
            throw new AcceptanceException($"-baseline: {path} is not a suite result.json: not an object");
        }

        var b = new SuiteBaseline(path);
        foreach (var row in top["cases"].AsSlice())
        {
            if (row.AsMap() is not { } r)
            {
                continue;
            }

            var name = r["name"].AsString();
            b._wall[name] = r["wall_ms"].AsNumber();
            b._boot[name] = r["boot_ms"].AsNumber();
            if (r["flake"] is JsonObject flake)
            {
                b._flake[name] = new Flake(flake["rate"].AsNumber(), (int)flake["failures"].AsNumber(), (int)flake["samples"].AsNumber());
            }
        }

        return b;
    }

    /// <summary>The baseline wall time of the case, when it has one.</summary>
    public bool TryWall(string name, out double wallMs) => _wall.TryGetValue(name, out wallMs);

    public double BootOf(string name) => _boot.GetValueOrDefault(name);

    public Flake? FlakeOf(string name) => _flake.TryGetValue(name, out var f) ? f : null;

    /// <summary>The queue key: untimed rows sort as if longest (-1).</summary>
    public static double Rank(SuiteBaseline? b, SuiteEntry e) => b is not null && b._wall.TryGetValue(e.Name, out var ms) ? ms : -1;
}

/// <summary>
/// One case whose run time (wall time net of boot) grew over its baseline's by more than
/// <see cref="SuiteRunner.RegressionRatio"/> and <see cref="SuiteRunner.RegressionFloorMs"/>.
/// </summary>
public sealed record SuiteRegression(string Name, long WallMs, long BaselineMs, long RunMs, long BaselineRunMs, double Ratio, Flake? BaselineFlake)
{
    public override string ToString()
    {
        var s = string.Create(CultureInfo.InvariantCulture, $"{Name} {Ratio:0.00}x");
        if (BaselineFlake is { Failures: > 0 } f)
        {
            s += string.Create(CultureInfo.InvariantCulture, $" (baseline flake {f.Rate * 100:0}%)");
        }

        return s;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["name"] = Name,
            ["wall_ms"] = WallMs,
            ["baseline_ms"] = BaselineMs,
            ["run_ms"] = RunMs,
            ["baseline_run_ms"] = BaselineRunMs,
            ["ratio"] = Ratio,
        };
        if (BaselineFlake is { } flake)
        {
            o["baseline_flake"] = flake.ToJson();
        }

        return o;
    }
}
