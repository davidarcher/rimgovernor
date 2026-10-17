using System.Text.Json.Nodes;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Telemetry;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

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
    /// A real reload: saves the paused game under a fresh name (<c>lifecycle_save</c>) and loads that save back to map
    /// readiness (<c>lifecycle_load</c>, polled through <c>lifecycle_read_load</c>). <see cref="Identity"/> and
    /// <see cref="Scope"/> follow the loaded game. Spends no ticks.
    /// </summary>
    Task ReloadAsync(string label, CancellationToken cancellationToken);
}
