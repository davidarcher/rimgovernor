using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The runner's session over the lifecycle's <see cref="Session"/> (Go <c>cases.session</c>) that a
/// <see cref="TypedSession"/> wraps, plus the services a case launched so the runner can stop them before the game
/// closes. The lifecycle session is null for an Owned case, which opens its own game on <see cref="Config"/>.
/// </summary>
public sealed class CaseSession
{
    private readonly AcceptanceCase _case;
    private readonly Report _report;
    private readonly List<ServiceProcess> _services = [];
    private Config? _config;
    private Session? _opened;

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
    public JsonObject? Prepared => _opened?.Prepared;

    /// <summary>The run's report; the case adds its own fields.</summary>
    public Report Report => _report;

    /// <summary>The lifecycle session; null for an Owned case.</summary>
    internal Session? OpenedSession => _opened;

    private Session Opened => _opened ?? throw new AcceptanceException("no game open: an Owned case holds its own session");

    /// <summary>Installs the run's configuration (the runner builds it before anything opens).</summary>
    internal void UseConfig(Config config) => _config = config;

    /// <summary>Installs the opened lifecycle session.</summary>
    internal void UseSession(Session opened) => _opened = opened;

    /// <summary>The full serve lifecycle over the session's game: load, attach to the session's identity.</summary>
    internal async Task<ServiceProcess> ServeAsync(ServeSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opened = Opened;
        spec = spec with { Prefix = spec.Prefix.Length == 0 ? "serve" : spec.Prefix };
        var service = await ServiceProcess.ServeAsync(Config, opened.Harness, opened.Identity, spec, _report, cancellationToken).ConfigureAwait(false);
        _services.Add(service);
        return service;
    }

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
        try
        {
            await _case.Postmortem!(this, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("postmortem: " + e.Message, e);
        }
    }
}
