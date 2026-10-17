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
    /// Marks a run restored from a checkpoint bundle: the open relaunches a kept process rather than reuse it.
    /// </summary>
    public bool Resumed { get; set; }

    /// <summary>
    /// The environment entries a fresh game launch under this configuration adds: <c>RIMGOVERNOR_STATE</c> (the brain's
    /// state database, <c>&lt;Output&gt;/service.sqlite</c>) and the case's serve flags (<c>ServeSpec.Env</c> entries named
    /// like environment variables). A kept process launched without them is replaced (relaunch reason "env").
    /// </summary>
    public Dictionary<string, string> LaunchEnv { get; set; } = new(StringComparer.Ordinal)
    {
        // The brain is hosted in the game (-rimgovernor-inproc), but a case starts it through the player API: the host's
        // auto-resume would race that start and run the bot in native-only cases. A ServeSpec.Env entry overrides this.
        [HeadlessProfile.ObserveVariable] = "1",
    };
}
