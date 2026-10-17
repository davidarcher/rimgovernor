using System.Text.Json.Nodes;
using System.Xml.Linq;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One round of <see cref="ITypedSession.RunClockUntilHeldAsync(string, int, Clock.RunRequest, ClockRound, CancellationToken)"/>
/// while the clock runs, given that round's status; true ends the wait without a hold.
/// </summary>
public delegate Task<bool> ClockRound(Clock.Status running, CancellationToken cancellationToken);

/// <summary>
/// What a <see cref="TypedCase"/>'s body receives: the opened, staged game behind typed calls. Contracts records go in
/// and come out (no flat JSON view), waits spend the case's declared ticks and end on a tick count, a probe's signature
/// or a telemetry event (never on wall time), and telemetry is selected by a registered kind. <see cref="TypedSession"/>
/// implements it over the runner's session.
/// </summary>
public interface ITypedSession
{
    /// <summary>The loaded colony's identity.</summary>
    Common.Identity Identity { get; }

    /// <summary>A read scope pinned to <see cref="Identity"/>, for a request's <c>Scope</c>.</summary>
    Observations.ReadScope Scope { get; }

    /// <summary>The typed reply of the case's <see cref="TypedCase.Stage"/>, null when it declared none.</summary>
    Fx.StageReply? Staged { get; }

    /// <summary>The declared ticks the waits have not spent yet.</summary>
    int TicksLeft { get; }

    /// <summary>Calls the production op <paramref name="op"/> with its request record and returns its reply record.</summary>
    Task<TReply> CallAsync<TRequest, TReply>(string label, NativeOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
        where TRequest : class
        where TReply : class;

    /// <summary>Calls the fixture op <paramref name="op"/>, which the case declares in <see cref="TypedCase.Ops"/>, and returns its typed reply.</summary>
    Task<TReply> FixtureAsync<TRequest, TReply>(string label, FixtureOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken);

    /// <summary>Stages <paramref name="spec"/> on the running game (<c>test/stage</c>, declared in <see cref="TypedCase.Ops"/>) and returns its typed reply.</summary>
    Task<Fx.StageReply> StageAsync(string label, Fx.StageSpec spec, CancellationToken cancellationToken);

    /// <summary>The <c>observations_list_buildings</c> rows <paramref name="request"/> selects; its Scope defaults to <see cref="Scope"/>.</summary>
    Task<IReadOnlyList<Observations.BuildingState>> BuildingsAsync(string label, Observations.ListBuildingsRequest request, CancellationToken cancellationToken);

    /// <summary>Every zone row <c>observations_list_zones</c> answers.</summary>
    Task<IReadOnlyList<Observations.ZoneState>> ZonesAsync(string label, CancellationToken cancellationToken);

    /// <summary>Each growing zone with its crop, cells and planted cells (the zones read joined to the cells read).</summary>
    Task<IReadOnlyList<GrowingZone>> GrowingZonesAsync(string label, CancellationToken cancellationToken);

    /// <summary>Every row of the registered <paramref name="kind"/> the game's log ring holds, oldest first.</summary>
    IReadOnlyList<LogRow> Telemetry(TelemetryKind kind);

    /// <summary>Runs the game <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>, then pauses.</summary>
    Task RunTicksAsync(string label, int ticks, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the game until <paramref name="probe"/> is done, at most <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>,
    /// then pauses; a signature that stops changing stalls the wait. Returns the ticks it ran.
    /// </summary>
    Task<int> RunUntilAsync(string label, int ticks, Probe probe, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the game until a row of <paramref name="kind"/> written after the call satisfies <paramref name="match"/>, at
    /// most <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>, then pauses; returns the matching row.
    /// </summary>
    Task<LogRow> RunUntilEventAsync(string label, int ticks, TelemetryKind kind, Func<LogRow, bool> match, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the native clock under <paramref name="request"/> (its watch policy decides what holds it) until the clock
    /// holds itself, at most <paramref name="ticks"/> ticks from <see cref="TicksLeft"/>, beating while it runs; returns
    /// the held status. A wait that ends otherwise holds the clock before it throws.
    /// </summary>
    Task<Clock.Status> RunClockUntilHeldAsync(string label, int ticks, Clock.RunRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="RunClockUntilHeldAsync(string, int, Clock.RunRequest, CancellationToken)"/> that calls
    /// <paramref name="whileRunning"/> once per round the clock still runs, before the heartbeat: the hook that acts
    /// mid-run (an injection at a tick) and can end the wait without a hold by returning true. Returns the held status, or
    /// the running status of the round that ended the wait (<see cref="Clock.ClockState.Running"/>); either way the clock
    /// is held when it returns.
    /// </summary>
    Task<Clock.Status> RunClockUntilHeldAsync(string label, int ticks, Clock.RunRequest request, ClockRound whileRunning, CancellationToken cancellationToken);

    /// <summary>
    /// The native clock's acceptance-only accounting now (<see cref="ClockAccounting"/>): the supervisor's declared hazard
    /// bounds beside the widest gap it observed. A read; it spends no ticks.
    /// </summary>
    Task<ClockAccounting> ClockAccountingAsync(string label, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>clock_hold</c> row of the hold <paramref name="held"/> reports (a held status, as
    /// <see cref="RunClockUntilHeldAsync"/> returns): the row whose <c>stop_at_ms</c> is its <c>HeldAtUnixMs</c>. The
    /// game is held, so the wait spends no ticks; it ends when the row reaches the log ring, under the hang guard.
    /// </summary>
    Task<LogRow> HoldRowAsync(string label, Clock.Status held, CancellationToken cancellationToken);

    /// <summary>Records <paramref name="value"/> in the report under <paramref name="key"/>: the evidence a reviewer reads.</summary>
    void Record(string key, JsonNode? value);

    /// <summary>Records the Contracts record <paramref name="value"/> in the report under <paramref name="key"/>.</summary>
    void RecordWire<T>(string key, T value);

    /// <summary>
    /// Hands the clock to the case's declared <see cref="TypedCase.Serve"/>: starts the brain service, resumes automatic
    /// control under a kept authority and observes while the brain drives the game until <paramref name="probe"/> is done,
    /// at most <paramref name="ticks"/> game ticks from <see cref="TicksLeft"/>; then stops the service and parks the brain.
    /// The service's exit or a signature that stops changing ends the wait, and the hang guard is its wall-clock backstop.
    /// Returns the ticks the game advanced.
    /// </summary>
    Task<int> ServeUntilAsync(string label, int ticks, Probe probe, CancellationToken cancellationToken);

    /// <summary>
    /// The review the brain last persisted in the run's durable journal (its world, cursor tick, enabled flag and
    /// progress records), null while none was persisted: what a served case's probe reads of the brain's own state.
    /// Spends no ticks.
    /// </summary>
    Task<Store.Rounds?> ReviewAsync(CancellationToken cancellationToken);

    /// <summary>Every need_transitions row the run's durable journal holds, oldest first. Spends no ticks.</summary>
    Task<IReadOnlyList<Store.NeedTransition>> NeedTransitionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A real reload: saves the paused game under a fresh name (<c>lifecycle_save</c>) and loads that save back to map
    /// readiness (<c>lifecycle_load</c>, polled through <c>lifecycle_read_load</c>). <see cref="Identity"/> and
    /// <see cref="Scope"/> follow the loaded game. Spends no ticks.
    /// </summary>
    Task ReloadAsync(string label, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="OwnedStart"/> only (<see cref="TypedCase.ClaimsStart"/>): launches a fresh game at the main menu on the
    /// case's prepared profile, at most <see cref="StartClaim.Launches"/> times, and returns its reusable-game lifecycle.
    /// Until the next launch every call, read and wait of this session goes to that game (to its open reuse case while
    /// one is open). The case retires it.
    /// </summary>
    Task<GameReuse> LaunchAsync(string label, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a colony from the launched game's main menu (<c>lifecycle_new_colony</c>) and polls it
    /// (<c>lifecycle_read_new_colony</c>) to its terminal outcome. Each phase is one long event outside the tick loop, so
    /// the wait ends on the outcome or a stalled phase, not on ticks, and spends none. Phases must not go backwards, and a
    /// completed start must have reported SAVING. A completed start moves <see cref="Identity"/> and <see cref="Scope"/>
    /// to the new colony.
    /// </summary>
    Task<NewColonyResult> NewColonyAsync(string label, Lifecycle.NewColonyRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The profile save <c>&lt;saveName&gt;.rws</c> parsed as XML: the evidence a start claim reads for what no
    /// observation reports (a scenario part, the saved storyteller). Spends no ticks.
    /// </summary>
    Task<XDocument> ReadSaveFileAsync(string saveName, CancellationToken cancellationToken);

    /// <summary>The profile path of save <c>&lt;saveName&gt;.rws</c>: where a start claim checks the file a start wrote, or squats it.</summary>
    string ProfileSave(string saveName);
}

/// <summary>A new-colony start's terminal outcome (<see cref="ITypedSession.NewColonyAsync"/>).</summary>
public sealed record NewColonyResult
{
    /// <summary>The phases its pending replies reported, in wire order and each once.</summary>
    public IReadOnlyList<Lifecycle.NewColonyPhase> Phases { get; init; } = [];

    /// <summary>The completed reply, null when the start failed.</summary>
    public Lifecycle.NewColonyCompleted? Completed { get; init; }

    /// <summary>The failure the start ended with, null when it completed.</summary>
    public Common.Failure? Failure { get; init; }
}
