namespace RimGovernor.Brain.Acceptance;

/// <summary>The resolved acceptance-run configuration common to every acceptance command (Go <c>nativeaccept.Config</c>).</summary>
public sealed partial class Config
{
    /// <summary>The absolute disposable worker root.</summary>
    public string Root { get; set; } = "";

    public string Output { get; set; } = "";

    public bool Headless { get; set; }

    /// <summary>
    /// Keeps a graphics device (no -batchmode headless): on 1.7 that also opens the game window, but a camera can
    /// render on demand (<c>Case.Graphics</c>).
    /// </summary>
    public bool Graphics { get; set; }

    public string GameId { get; set; } = "";

    public TimeSpan Timeout { get; set; }

    /// <summary>The configuration directory, resolved by the prepare step.</summary>
    public string Configuration { get; set; } = "";

    /// <summary>
    /// The official expansions to keep active (short names or package ids); null defers to the expansions
    /// environment variable, and when that is unset every installed expansion loads; an empty list pins Core-only.
    /// </summary>
    public IReadOnlyList<string>? Expansions { get; set; }

    /// <summary>
    /// The test ops the run's start calls; the session fills it from the start when unset. The stale-package check
    /// names their fixtures in its rebuild hint.
    /// </summary>
    public IReadOnlyList<string> FixtureOps { get; set; } = [];

    /// <summary>
    /// Marks the opened game quiet-world: under the headless profiles' test-acceleration launch, wild plants and
    /// animals outside the home area stop ticking and the wild spawners stop. The marker persists with the game's
    /// saves. A production build carries no op; the session then leaves the world live and the report says so.
    /// </summary>
    public bool QuietWorld { get; set; }

    /// <summary>
    /// Leaves a reused process's loaded game in place instead of returning it to the main menu, on open and on a kept
    /// close: a fixture-development session works on one loaded world across several calls.
    /// </summary>
    public bool KeepLoaded { get; set; }

    /// <summary>
    /// Marks a run restored from a checkpoint bundle: its store holds clock cursors from the process that saved it,
    /// so the open relaunches a kept process, whose cursors could be older.
    /// </summary>
    public bool Resumed { get; set; }

    /// <summary>
    /// The profile directory every service this session launches uses instead of <c>&lt;Output&gt;/service-profile</c>:
    /// a resumed run keeps its bundle's, which the restored store is bound to.
    /// </summary>
    public string ServiceProfile { get; set; } = "";

    /// <summary>The profile directory services launched under this configuration use.</summary>
    public string ServiceProfileDir() => ServiceProfile.Length > 0 ? ServiceProfile : Path.Combine(Output, "service-profile");
}
