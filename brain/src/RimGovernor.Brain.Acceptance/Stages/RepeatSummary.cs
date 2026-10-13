using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// acceptance run -repeat N: a case runs N times fresh on the kept process, each attempt with its own output directory
// (Options.CaseOutput), and the summary beside the first attempt's directory says how many passed and which world each
// attempt ran on, so a flake shows as a pass rate under one build rather than as a rerun by hand. The summary is
// bookkeeping: the exit code is the attempts'.

/// <summary>One attempt's row of the summary.</summary>
public sealed record RepeatAttempt(int Attempt, bool Passed, long WallMs, string Output, string Error, WorldRecord? World)
{
    /// <summary>The attempt's row for <paramref name="report"/> (the run's) and exit <paramref name="code"/>.</summary>
    public static RepeatAttempt From(int attempt, string output, Report report, int code, TimeSpan wall)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new RepeatAttempt(attempt, code == 0, (long)wall.TotalMilliseconds, output, report["error"].AsString(), WorldRecords.WorldOf(report));
    }

    internal JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["attempt"] = Attempt,
            ["passed"] = Passed,
            ["wall_ms"] = WallMs,
            ["output"] = Output,
        };
        if (Error.Length > 0)
        {
            row["error"] = Error;
        }

        if (World is not null)
        {
            row["world"] = World.ToJson();
        }

        return row;
    }
}

/// <summary><c>&lt;output&gt;/&lt;case&gt;.repeat.json</c>.</summary>
public sealed class RepeatSummary
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private RepeatSummary(string caseName, IReadOnlyList<RepeatAttempt> attempts)
    {
        Case = caseName;
        Attempts = attempts;
    }

    public string Case { get; }

    public IReadOnlyList<RepeatAttempt> Attempts { get; }

    public int Passed { get; private set; }

    public int Failed { get; private set; }

    public double PassRate { get; private set; }

    /// <summary>
    /// The distinct seeds the attempts ran on, in attempt order; one seed over every attempt means the world did not
    /// vary (a cached start, a save), so a mixed verdict is not the roll's.
    /// </summary>
    public IReadOnlyList<string> Seeds { get; private set; } = [];

    public static RepeatSummary Summarize(string name, IReadOnlyList<RepeatAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var s = new RepeatSummary(name, attempts);
        var seeds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RepeatAttempt a in attempts)
        {
            if (a.Passed)
            {
                s.Passed++;
            }
            else
            {
                s.Failed++;
            }

            if (a.World is { Seed.Length: > 0 } world && seen.Add(world.Seed))
            {
                seeds.Add(world.Seed);
            }
        }

        s.Seeds = seeds;
        if (attempts.Count > 0)
        {
            s.PassRate = (double)s.Passed / attempts.Count;
        }

        return s;
    }

    /// <summary>The summary's line on stdout: the pass count and the seeds each attempt ran on.</summary>
    public override string ToString()
    {
        var seeds = new List<string>(Attempts.Count);
        foreach (RepeatAttempt a in Attempts)
        {
            string seed = a.World is { Seed.Length: > 0 } world ? world.Seed : "-";
            seeds.Add(a.Passed ? seed : seed + "(fail)");
        }

        return string.Create(CultureInfo.InvariantCulture, $"pass={Passed}/{Attempts.Count} seeds=[{string.Join(' ', seeds)}]");
    }

    public void Write(string path)
    {
        var attempts = new JsonArray();
        foreach (RepeatAttempt a in Attempts)
        {
            attempts.Add(a.ToJson());
        }

        var seeds = new JsonArray();
        foreach (string seed in Seeds)
        {
            seeds.Add(seed);
        }

        var doc = new JsonObject
        {
            ["case"] = Case,
            ["attempts"] = attempts,
            ["passed"] = Passed,
            ["failed"] = Failed,
            ["pass_rate"] = PassRate,
            ["seeds"] = seeds,
        };
        File.WriteAllText(path, doc.ToJsonString(Indented));
    }
}
