using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The narrow interfaces the runner needs from machinery other slices own: the checkpoint ring and stage planning
// (Go checkpoint.go and stage.go), the pre-run doctor/heal pass (cmd/acceptance heal.go) and the scenario clock
// (scenario.go). The host wires an implementation into RunDependencies; nothing here is Go-shaped beyond the names.

/// <summary>What <see cref="IRunCheckpointing.PlanResume"/> decided: the bundle to resume from (null entry when starting fresh) and the ring index it came from (Go <c>resumption</c>).</summary>
public sealed class ResumePlan
{
    /// <summary>The bundle the run resumes from; null when it starts fresh.</summary>
    public Checkpoint? Entry { get; init; }

    /// <summary>The ring the entry came from; null when there was none.</summary>
    public Ring? Previous { get; init; }

    /// <summary>Whether the run resumes from <see cref="Entry"/>.</summary>
    public bool Resuming => Entry is { Label.Length: > 0 };
}

/// <summary>What <see cref="IRunCheckpointing.PlanStage"/> decided for a run (Go <c>staging</c>): the stage bundle to open on and the key this run captures under.</summary>
public sealed class StagePlan
{
    /// <summary>The key this run captures stage bundles under; empty when staging is off.</summary>
    public string Key { get; init; } = "";

    /// <summary>The index of the declared stage the run opens on; -1 when none.</summary>
    public int Hit { get; init; } = -1;

    /// <summary>The bundle of stage <see cref="Hit"/>.</summary>
    public Checkpoint? Entry { get; init; }

    /// <summary>Why staging is off, empty when it is on. A dev iteration sets it ("dev iteration") through <see cref="WithOff"/>.</summary>
    public string Off { get; init; } = "";

    /// <summary>Whether the run opens on a stage bundle.</summary>
    public bool Staged => Hit >= 0;

    /// <summary>A plan that starts nothing staged (the postmortem-only run's).</summary>
    public static StagePlan None { get; } = new();

    /// <summary>This plan with staging switched off for <paramref name="reason"/>.</summary>
    public StagePlan WithOff(string reason) => new() { Key = Key, Hit = Hit, Entry = Entry, Off = reason };
}

/// <summary>What a checkpoint ring's capture needs from the run's session (Go <c>baseRing</c> consulted the runner's session at every capture).</summary>
public interface IRingHost
{
    /// <summary>The harness while a game is open, else null.</summary>
    Harness? LiveHarness { get; }

    /// <summary>The service a capture saves through: the latest launched one while it runs, else null.</summary>
    ICheckpointService? LatestService();

    /// <summary>The fixture op's reply the bundles record.</summary>
    JsonObject? Prepared { get; }
}

/// <summary>
/// The checkpoint ring and stage-bundle planning the runner calls (Go <c>checkpoint.go</c> and <c>stage.go</c>, minus
/// <c>Session.Stage</c>, which <see cref="CaseSession"/> implements over <see cref="IRingHost"/>'s ring).
/// </summary>
public interface IRunCheckpointing
{
    /// <summary>Why <paramref name="c"/> never checkpoints (owns its process, opts out, measures clock timing), or empty when it may.</summary>
    string RingExcluded(AcceptanceCase c);

    /// <summary>What the current tree would stamp on a bundle of c. Throws <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> for a root nothing has prepared yet.</summary>
    Fingerprint Fingerprint(AcceptanceCase c, string configDir);

    /// <summary>Reads c's ring and decides whether this run resumes, printing the decision on <paramref name="log"/>; -fresh removes the ring and -rewind steps back.</summary>
    ResumePlan PlanResume(AcceptanceCase c, RunOptions options, TextWriter log);

    /// <summary>Picks the last declared stage whose bundle exists and matches the tree (a pending ring resume wins); -restage discards the bundles first.</summary>
    StagePlan PlanStage(AcceptanceCase c, RunOptions options, ResumePlan resumed, TextWriter log);

    /// <summary>Validates options.Break against c before the game opens; returns the options with the cadence defaulted when a breakpoint needs the ring.</summary>
    RunOptions CheckBreak(AcceptanceCase c, RunOptions options);

    /// <summary>Validates options.Through against c before the game opens.</summary>
    void CheckThrough(AcceptanceCase c, RunOptions options);

    /// <summary>The ring the run captures into, null when c never checkpoints (report["checkpointing"] says why).</summary>
    CheckpointRing? NewRing(AcceptanceCase c, RunOptions options, IRingHost host, Config config, string output, ResumePlan resumed, StagePlan staged, Report report);

    /// <summary>The ring stage bundles are captured into (never activated), null when the run stages nothing or staging is off (report["staging"] says why).</summary>
    CheckpointRing? NewStages(AcceptanceCase c, RunOptions options, IRingHost host, Config config, string output, StagePlan plan, Report report);

    /// <summary>Records the run's bundles on the report and leaves the ring for the next run: a passing run clears it; a failing one keeps the entries up to the resume point, the failed bundle and the index naming what the next run resumes from. <paramref name="reclaim"/> takes the game back for the failed bundle when nobody holds it.</summary>
    Task CloseRingAsync(CheckpointRing? ring, ResumePlan resumed, Exception? runError, Report report, Func<CancellationToken, Task>? reclaim);

    /// <summary>Takes the break bundle on <paramref name="ring"/> (pausing the game) and writes the ring the paused run leaves, with the report's "break", "checkpoints" and ring note. Throws <see cref="AcceptanceException"/> when the bundle cannot be taken or the ring written.</summary>
    Task BreakRingAsync(AcceptanceCase c, RunOptions options, CheckpointRing ring, ResumePlan resumed, BreakException broke, Report report, CancellationToken cancellationToken);

    /// <summary>The close for a resumed run whose game never opened on the bundle: the next run steps back one entry (fresh past the ring) rather than retrying the same bundle forever.</summary>
    void RewindRing(string dir, ResumePlan resumed, Exception openError, Report report);
}

/// <summary>What the pre-run doctor/heal pass decided (Go <c>preflight</c>'s two results).</summary>
public sealed record PreflightResult(bool Ok, IReadOnlyList<string> Healed);

/// <summary>The doctor preflight: only its failing checks print and a failure refuses the run, except a stale or fixture-less install, which it rebuilds and reinstalls unless <see cref="RunOptions.NoHeal"/> (Go <c>cmd/acceptance/heal.go</c>).</summary>
public interface IRunPreflight
{
    Task<PreflightResult> RunAsync(IReadOnlyList<AcceptanceCase> selected, RunOptions options, TextWriter stdout, CancellationToken cancellationToken);
}

/// <summary>A scenario runtime built over a controller clock (Go <c>na.ScenarioRuntime</c>); the scenario port's type implements it.</summary>
public interface IScenarioRuntime
{
}

/// <summary>An option to <see cref="ISession.AdvanceAsync"/> (Go <c>na.AdvanceOption</c>); the scenario port's option types implement it.</summary>
public interface IScenarioAdvanceOption
{
}

/// <summary>The scenario clock the session's <see cref="ISession.RuntimeAsync"/> and <see cref="ISession.AdvanceAsync"/> bind (Go scenario.go's <c>ScenarioClock</c>, <c>ScenarioRuntime</c>, <c>AdvanceGame</c>).</summary>
public interface IScenarioDriver
{
    /// <summary>Acquires the controller clock on <paramref name="harness"/> and builds the runtime (the discovered tools let an advance dismiss the letters it acknowledges).</summary>
    Task<IScenarioRuntime> CreateRuntimeAsync(Harness harness, JsonObject identity, Report report, IReadOnlyList<string> tools, CancellationToken cancellationToken);

    /// <summary>AdvanceGame on <paramref name="runtime"/>. <paramref name="expectedLetters"/>, when not null, makes the advance a strict window over exactly those letters.</summary>
    Task<JsonObject> AdvanceAsync(
        IScenarioRuntime runtime,
        ulong ticks,
        IReadOnlyList<(string Label, string LetterDef)>? expectedLetters,
        IReadOnlyList<IScenarioAdvanceOption> options,
        CancellationToken cancellationToken);
}

/// <summary>Everything the runner needs wired from outside this folder.</summary>
public sealed class RunDependencies
{
    /// <summary>The ring and stage planner. Required.</summary>
    public required IRunCheckpointing Checkpointing { get; init; }

    /// <summary>The doctor/heal pass; null skips the preflight.</summary>
    public IRunPreflight? Preflight { get; init; }

    /// <summary>The scenario clock; null makes <see cref="ISession.RuntimeAsync"/> fail with a wiring message.</summary>
    public IScenarioDriver? Scenario { get; init; }

    /// <summary>The profile and process half of the session lifecycle; <see cref="SessionHost.Default"/> when null.</summary>
    public ISessionHost? Host { get; init; }

    /// <summary>The dependencies <see cref="RunCommand.RunAsync(IReadOnlyList{string}, CaseRegistry, TextWriter, TextWriter, CancellationToken)"/> uses; the tool host sets it once at startup.</summary>
    public static RunDependencies? Current { get; set; }
}

/// <summary>A run paused at its breakpoint (Go <c>errBroke</c>): neither passed nor failed.</summary>
public sealed class RunBrokeException : AcceptanceException
{
    public RunBrokeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The cause a run ended after its <c>-through</c> stage cancels the Run body with (Go <c>throughError</c>): the
/// stage's bundle is cached, so the rest of the chain is another run's.
/// </summary>
public sealed class StagedThroughException : OperationCanceledException
{
    public StagedThroughException(string stage, string outcome)
        : base("staged through " + stage + " (" + outcome + ")")
    {
        Stage = stage;
        Outcome = outcome;
    }

    public string Stage { get; }

    public string Outcome { get; }
}
