using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The per-invocation configuration shared by every case of a run (Go <c>cases.Options</c>). A record so the runner can
/// derive a variant (<c>with</c>) without touching the caller's.
/// </summary>
public sealed record RunOptions
{
    /// <summary>The per-case safety net when <see cref="Timeout"/> names none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    /// <summary>The private disposable worker root (e.g. <c>.rimgovernor/bridge</c>).</summary>
    public string Root { get; init; } = "";

    /// <summary>The run's output directory; each case writes under Output/&lt;area&gt;/&lt;case&gt;.</summary>
    public string Output { get; init; } = "";

    public string GameId { get; init; } = "";

    public bool Headless { get; init; }

    /// <summary>The per-case safety net.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Overrides every case's own Budget when set.</summary>
    public TimeSpan Budget { get; init; }

    /// <summary>Overrides the shared stall budget when set.</summary>
    public TimeSpan Stall { get; init; }

    /// <summary>The evidence mode; null leaves <see cref="EvidenceBudget.CurrentEvidenceMode"/>'s default.</summary>
    public EvidenceMode? Evidence { get; init; }

    /// <summary>Pins the world seed of a debug or scenario start (a result.json "world" block's seed reproduces that run's world).</summary>
    public string Seed { get; init; } = "";

    /// <summary>Runs each case this many times on the kept process and reports the pass rate and per-attempt seeds; 0 or 1 is one run.</summary>
    public int Repeat { get; init; }

    /// <summary>Numbers the run under Repeat: 1 (or 0) writes the case's usual output directory, later attempts write under Output/repeat/&lt;n&gt;/&lt;case&gt;.</summary>
    public int Attempt { get; init; }

    /// <summary>The append-only metrics series every case's block is appended to (<see cref="MetricSeries.SeriesPath"/> of Output by default).</summary>
    public string Series { get; init; } = "";

    /// <summary>Leaves the series alone.</summary>
    public bool NoSeries { get; init; }

    /// <summary>Skips the runner's doctor preflight (a suite worker whose parent already ran it on the shared root).</summary>
    public bool NoDoctor { get; init; }

    /// <summary>Makes the preflight refuse a stale or fixture-less install instead of rebuilding and reinstalling the mod.</summary>
    public bool NoHeal { get; init; }

    /// <summary>What the preflight healed before this run; the report carries it under "healed" so a slow first run is explained.</summary>
    public IReadOnlyList<string> Healed { get; init; } = [];

    /// <summary>Where the run's series lives: <see cref="Series"/>, or the default beside <see cref="Output"/>.</summary>
    public string SeriesPath() => Series.Length > 0 ? Series : MetricSeries.SeriesPath(Output);

    /// <summary>Names the run in the series: its output directory's base name.</summary>
    public string RunId() => Path.GetFileName(Output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Where a case's evidence and result.json go.</summary>
    public string CaseOutput(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var name = c.Name.Replace('/', Path.DirectorySeparatorChar);
        return Attempt > 1
            ? Path.Combine(Output, "repeat", Attempt.ToString(CultureInfo.InvariantCulture), name)
            : Path.Combine(Output, name);
    }
}
