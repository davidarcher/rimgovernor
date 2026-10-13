using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What a case's Run receives: the open, prepared game (Go <c>cases.Session</c>); <see cref="CaseSession"/> implements it
/// over the session opener. The brain is in-process in the mod, so the Go <c>Rimgovernor</c> member (the prebuilt serve
/// binary) is gone, and <see cref="Release"/> / <see cref="Reattach"/> are trivial: there is no sole GABP slot to hand over.
/// </summary>
public interface ISession
{
    /// <summary>The run's resolved configuration (root, output, profile, startup log); an Owned case opens its own game with it.</summary>
    Config Config { get; }

    /// <summary>Records evidence under the run's output directory.</summary>
    Harness Harness { get; }

    /// <summary>The discovered native tool names.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The loaded colony's identity (colonyId, loadToken, mapId...).</summary>
    JsonObject Identity { get; }

    /// <summary>A Fixture start's reply, null otherwise.</summary>
    JsonObject? Prepared { get; }

    /// <summary>The run's report; the case adds its own fields.</summary>
    Report Report { get; }

    /// <summary>The case's declared Serve spec, or the zero spec for a native-only case.</summary>
    ServeSpec Spec { get; }

    /// <summary>The run-level RNG seed (-run-seed), 0 for none.</summary>
    int RunSeed { get; }

    /// <summary>
    /// <paramref name="baseId"/> on a fresh run and suffixed with the run's id on a resumed one: the restored store
    /// already holds the fresh run's submissions, and a replay under the resumed world's identity is a different request
    /// the store answers 409 conflict. Stable within a run. Use it for every deterministic requestId a Run body submits.
    /// </summary>
    string RequestId(string baseId);

    /// <summary>
    /// Runs <paramref name="stage"/>, the staging block of the declared stage <paramref name="name"/>, unless the run
    /// opened on a bundle of that stage or a later one, in which case it is skipped and the code after the call cannot
    /// tell the two apart. The block returns with every service it launched stopped; the game is paused for the capture,
    /// the state a hit continues from. Stages run in declared order; an undeclared or out-of-order name fails the run.
    /// </summary>
    Task StageAsync(string name, Func<CancellationToken, Task> stage, CancellationToken cancellationToken);

    /// <summary>
    /// The checkpoint entry this run resumed from, null on a fresh run. Its State is what the case recorded through
    /// <c>Checkpoints.SetState</c> before the capture: a Run body that stages its own fixture reads it to skip the prep
    /// the save carries.
    /// </summary>
    Checkpoint? Resumed { get; }

    /// <summary>
    /// The stage bundle this run opened on, null when it opened fresh or on a ring checkpoint. Its State is what the
    /// staging run recorded before the capture: a staging block's outcome the code after Stage needs.
    /// </summary>
    Checkpoint? Staged { get; }

    /// <summary>The failed run's result.json under <c>-postmortem-only</c>, null on any other run or when the ring did not record it.</summary>
    JsonObject? Prior { get; }

    /// <summary>Hands the game to a service (Go closed the bridge session so a service could take the sole slot). A no-op here.</summary>
    void Release();

    /// <summary>Takes the game back once the service has stopped; returns the (unchanged) harness.</summary>
    Harness Reattach();

    /// <summary>
    /// Runs the case's Start again over the running game: a world change that keeps the durable journal (routine goals of
    /// the old world invalidate), with the fixture op, frozen needs and <see cref="Identity"/> repeated for the reloaded
    /// world and the crew staffed again. A Serve afterwards opens on the new identity.
    /// </summary>
    Task<Harness> ReloadAsync(CancellationToken cancellationToken);

    /// <summary>Starts the player-API handle for a case that manages the attach and authority steps itself. The runner stops whatever is still running when Run returns.</summary>
    Task<ServiceProcess> LaunchAsync(ServiceLaunch launch, CancellationToken cancellationToken);

    /// <summary>The full serve lifecycle over the session's game: load, attach to the session's identity.</summary>
    Task<ServiceProcess> ServeAsync(ServeSpec spec, CancellationToken cancellationToken);

    /// <summary>A scenario runtime over a controller clock the runner acquires on first use, for the cases that advance the game themselves.</summary>
    Task<IScenarioRuntime> RuntimeAsync(CancellationToken cancellationToken);

    /// <summary>Advances the game <paramref name="ticks"/> on <see cref="RuntimeAsync"/> with the case's Letters as the expected interruption letters; the reply is the advance report.</summary>
    Task<JsonObject> AdvanceAsync(ulong ticks, IReadOnlyList<IScenarioAdvanceOption>? options, CancellationToken cancellationToken);
}
