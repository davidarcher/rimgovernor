using Common = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The slice of a registry case's session <see cref="SustainedFood.ObserveAsync"/> drives: the loaded, paused, frozen
/// game the shared runner opened, here the view of a game a driver outside the case
/// runner opened itself (the colony and colony-review subcommands). The service it serves is a
/// <see cref="ServiceProcess"/> launched on that game.
/// </summary>
public sealed class ObservedSession
{
    private readonly Session _session;

    private ObservedSession(Session session, Config config, Report report, ServeSpec spec)
    {
        _session = session;
        Config = config;
        Report = report;
        Spec = spec;
    }

    public Harness Harness => _session.Harness;

    /// <summary>The opened colony's identity, what the clock reads name.</summary>
    public Common.Identity Identity => Harness.IdentityOf(_session.Identity);

    public Config Config { get; }

    public Report Report { get; }

    public ServeSpec Spec { get; }

    /// <summary>
    /// The watch view of a game a driver outside the case runner opened itself (<see cref="Session.OpenAsync"/>): the
    /// service it serves is launched on that game.
    /// </summary>
    public static ObservedSession Of(Session session, Config config, Report report, ServeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(spec);
        return new ObservedSession(session, config, report, spec);
    }

    /// <summary>Starts the in-mod service under <paramref name="serve"/>.</summary>
    public Task<ServiceProcess> ServeAsync(ServeSpec serve, CancellationToken cancellationToken) =>
        ServiceProcess.ServeAsync(Config, _session.Harness, _session.Identity, serve, Report, cancellationToken);

    /// <summary>Re-opens the harness after the service stopped (trivial in the in-process brain: there is no sole bridge slot to give back).</summary>
    public Task<Harness> ReattachAsync(CancellationToken cancellationToken) => Task.FromResult(_session.Harness);
}
