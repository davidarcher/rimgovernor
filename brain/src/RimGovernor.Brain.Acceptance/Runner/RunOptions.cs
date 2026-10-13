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

    /// <summary>The checkpoint ring's cadence in run phase; zero turns the ring, and resuming, off.</summary>
    public TimeSpan CheckpointEvery { get; init; }

    /// <summary>Discards the case's ring in this root and starts from scratch.</summary>
    public bool Fresh { get; init; }

    /// <summary>Resumes that many ring entries earlier than the ring's next.</summary>
    public int Rewind { get; init; }

    /// <summary>
    /// Ends the run once the named declared stage's bundle is cached: the Run body is cut there, the case's Postmortem
    /// does not run, and the report carries staged_through. A suite scheduling stages across workers runs each stage
    /// this way; the run appends no series row.
    /// </summary>
    public string Through { get; init; } = "";

    /// <summary>Discards the case's stage bundles in this root and stages again (Fresh leaves them: a stage is deterministic setup, not the failed attempt).</summary>
    public bool Restage { get; init; }

    /// <summary>
    /// Runs only the case's Postmortem phase over a staged bundle: <see cref="From"/> names the bundle (a ring label such
    /// as "t+7m" or "failed", or a bundle directory), the ring's failed bundle when empty. The ring is read, never changed.
    /// </summary>
    public bool PostmortemOnly { get; init; }

    public string From { get; init; } = "";

    /// <summary>
    /// The <c>acceptance dev</c> iteration: the case runs as a resumed run over the bundle <see cref="From"/> names, Run
    /// and Postmortem both, with the ring read but never written, no stage captures and no series row. Output goes under
    /// Output/dev/&lt;Attempt&gt;/&lt;case&gt; and the request ids carry dev&lt;Attempt&gt;.
    /// </summary>
    public bool Dev { get; init; }

    /// <summary>Pins the world seed of a debug or scenario start (a result.json "world" block's seed reproduces that run's world). Implies Fresh.</summary>
    public string Seed { get; init; } = "";

    /// <summary>Reseeds the game RNG once the run has opened (combatlab/metrics-*); 0 leaves the RNG as the save carried it.</summary>
    public int RunSeed { get; init; }

    /// <summary>Runs each case this many times on the kept process and reports the pass rate and per-attempt seeds; 0 or 1 is one run.</summary>
    public int Repeat { get; init; }

    /// <summary>Numbers the run under Repeat: 1 (or 0) writes the case's usual output directory, later attempts write under Output/repeat/&lt;n&gt;/&lt;case&gt;.</summary>
    public int Attempt { get; init; }

    /// <summary>Receives the run's progress lines (the resume decision); null discards them.</summary>
    public TextWriter? Log { get; init; }

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

    /// <summary>Stops the run at a breakpoint (after a declared stage, at a game tick or at a run-phase minute); needs the ring.</summary>
    public Breakpoint Break { get; init; }

    /// <summary>Where the run's series lives: <see cref="Series"/>, or the default beside <see cref="Output"/>.</summary>
    public string SeriesPath() => Series.Length > 0 ? Series : MetricSeries.SeriesPath(Output);

    /// <summary>Names the run in the series: its output directory's base name.</summary>
    public string RunId() => Path.GetFileName(Output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Where a case's evidence and result.json go.</summary>
    public string CaseOutput(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var name = c.Name.Replace('/', Path.DirectorySeparatorChar);
        if (Dev)
        {
            return Path.Combine(Output, "dev", Math.Max(Attempt, 1).ToString(CultureInfo.InvariantCulture), name);
        }

        return Attempt > 1
            ? Path.Combine(Output, "repeat", Attempt.ToString(CultureInfo.InvariantCulture), name)
            : Path.Combine(Output, name);
    }

    /// <summary>Where c's checkpoint ring lives under the run's root.</summary>
    public string RingDir(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Path.Combine(Root, "checkpoints", c.Name.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Where c's stage bundles live under the run's root.</summary>
    public string StagesDir(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Path.Combine(Root, "stages", c.Name.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>The root's prepared configuration directory for this run's profile, present once any run has prepared the root.</summary>
    public string ConfigDir(AcceptanceCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Path.Combine(Root, Headless && !c.Rendered ? "config-headless" : "config");
    }
}
