using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The runner's session over the lifecycle's <see cref="Session"/> (Go <c>cases.session</c>) that a
/// <see cref="TypedSession"/> wraps, plus the services a case launched so the runner can stop them before the game
/// closes. The lifecycle session is null for an Owned case, which opens its own game on <see cref="Config"/>.
/// </summary>
public sealed class CaseSession : IRingHost
{
    /// <summary>
    /// The checkpoint state key under which the ring records the last stage a run completed, so a resume from a later
    /// ring bundle skips the stages it already passed.
    /// </summary>
    public const string StageStateKey = "stage_completed";

    private readonly AcceptanceCase _case;
    private readonly Report _report;
    private readonly List<ServiceProcess> _services = [];
    private readonly JsonArray _stageRows = [];
    private Config? _config;
    private Session? _opened;
    private JsonObject? _prepared;

    internal CaseSession(AcceptanceCase c, Report report)
    {
        _case = c;
        _report = report;
    }

    /// <summary>The run's resolved configuration (root, output, profile, startup log); an Owned case opens its own game with it.</summary>
    public Config Config => _config ?? throw new InvalidOperationException("the session has no configuration yet");

    /// <summary>Records evidence under the run's output directory.</summary>
    public Harness Harness => Opened.Harness;

    /// <summary>The loaded colony's identity (colonyId, loadToken, mapId...).</summary>
    public JsonObject Identity => Opened.Identity;

    /// <summary>A Fixture start's reply, null otherwise.</summary>
    public JsonObject? Prepared => _prepared ?? _opened?.Prepared;

    /// <summary>The run's report; the case adds its own fields.</summary>
    public Report Report => _report;

    /// <summary>The failed run's result.json under <c>-postmortem-only</c>, null on any other run or when the ring did not record it.</summary>
    public JsonObject? Prior { get; internal set; }

    /// <summary>What <see cref="RequestId"/> appends on a resumed run, empty on a fresh one.</summary>
    internal string ResumeSuffix { get; set; } = "";

    /// <summary>The run's checkpoint ring, null when it never checkpoints.</summary>
    internal CheckpointRing? Ring { get; set; }

    /// <summary>The ring stage bundles are captured into, null when staging is off.</summary>
    internal CheckpointRing? Stages { get; set; }

    /// <summary>Times the run phase, for a stage bundle's offset when there is no ring.</summary>
    internal Stopwatch RunStarted { get; set; } = Stopwatch.StartNew();

    /// <summary>Cuts the Run body with the given cause (a breakpoint, or the -through stage being cached).</summary>
    internal Action<Exception>? CutRun { get; set; }

    /// <summary>The per-stage rows the report carries.</summary>
    internal JsonArray StageRows => _stageRows;

    /// <summary>The lifecycle session; null for an Owned case.</summary>
    internal Session? OpenedSession => _opened;

    /// <inheritdoc />
    Harness? IRingHost.LiveHarness => _opened?.Harness;

    private Session Opened => _opened ?? throw new AcceptanceException("no game open: an Owned case holds its own session");

    /// <summary>Installs the run's configuration (the runner builds it before anything opens).</summary>
    internal void UseConfig(Config config) => _config = config;

    /// <summary>Installs the opened lifecycle session.</summary>
    internal void UseSession(Session opened) => _opened = opened;

    /// <summary>The fixture op's reply a bundle recorded replaces the one the session found (a resumed or staged run opens on a save, not the op).</summary>
    internal void UsePrepared(JsonObject prepared) => _prepared = prepared;

    /// <summary>Takes the game back once the services have stopped; returns the (unchanged) harness.</summary>
    internal Harness Reattach() => Opened.Harness;

    /// <summary>The full serve lifecycle over the session's game: load, attach to the session's identity.</summary>
    internal async Task<ServiceProcess> ServeAsync(ServeSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opened = Opened;

        // The handle's own request ids (resume, acknowledge, the watch's commands) carry the run suffix: a resumed or
        // staged run's restored store already holds the earlier run's under the case's prefix with that run's identity,
        // and the same requestId with a new loadToken is a conflict (409).
        var prefix = spec.Prefix.Length == 0 ? "serve" : spec.Prefix;
        spec = spec with { Prefix = RequestId(prefix) };
        var service = await ServiceProcess.ServeAsync(Config, opened.Harness, opened.Identity, spec, _report, cancellationToken).ConfigureAwait(false);
        _services.Add(service);
        return service;
    }

    /// <inheritdoc />
    ICheckpointService? IRingHost.LatestService() => ServiceProcess.LatestService() ?? _services.LastOrDefault(p => p.Running());

    /// <summary>Stops any service still running (stopping is idempotent) before the session closes.</summary>
    internal async Task StopServicesAsync()
    {
        foreach (var service in _services)
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the case's Postmortem phase after Run: the services the case launched are stopped and the harness holds the
    /// game again.
    /// </summary>
    internal async Task PostmortemAsync(CancellationToken cancellationToken)
    {
        await StopServicesAsync().ConfigureAwait(false);
        _ = Reattach();
        try
        {
            await _case.Postmortem!(this, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("postmortem: " + e.Message, e);
        }
    }

    internal static string Tag(int value) => value.ToString(CultureInfo.InvariantCulture);

    // <paramref name="baseId"/> on a fresh run and suffixed with the run's id on a resumed one: the restored store
    // already holds the fresh run's submissions.
    private string RequestId(string baseId) => ResumeSuffix.Length == 0 ? baseId : baseId + "-" + ResumeSuffix;
}
