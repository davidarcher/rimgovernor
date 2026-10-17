using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Adapts a registry case's <see cref="ISession"/> to the <see cref="IObservedSession"/> the serve-driven watch
/// (<see cref="SustainedFood.ObserveAsync"/>) drives (Go: <c>cases.Session</c> satisfied <c>sustainedfood.Observed</c>
/// directly). The service is the session's <see cref="ServiceProcess"/>, wrapped as an <see cref="IWatchedService"/>.
/// </summary>
public sealed class ObservedSession : IObservedSession
{
    private readonly ISession _session;

    private ObservedSession(ISession session) => _session = session;

    /// <summary>
    /// Opens the service's durable-journal reader for the case's run (Go <c>store.Open</c> on
    /// <c>&lt;output&gt;/service.sqlite</c>), for the watch's samples and for a case's own audit after the service
    /// stopped. The in-game brain's journal location is owned by the service/journal slice (#3085, #3087): until it
    /// assigns this hook, <see cref="OpenStoreAsync"/> fails with a message naming the missing reader.
    /// </summary>
    public static Func<Config, CancellationToken, Task<IWatchStore>>? StoreOpener { get; set; }

    /// <summary>
    /// Reads the layout plan the journal recorded for its latest persisted review (Go <c>store.LoadRounds</c> then
    /// <c>store.LayoutPlan</c>): the review tick and the plan, null when none was recorded by then. Assigned with
    /// <see cref="StoreOpener"/>.
    /// </summary>
    public static Func<Config, CancellationToken, Task<(ulong Tick, LayoutPlan? Plan)>>? LayoutPlanReader { get; set; }

    /// <summary>The watch view of <paramref name="session"/>.</summary>
    public static IObservedSession Of(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new ObservedSession(session);
    }

    /// <summary>
    /// The watch view of a service the case launched itself (<see cref="ISession.ServeAsync"/>), for a case that drives
    /// <see cref="SustainedFood.WatchAsync"/> phase by phase and stops the service between them.
    /// </summary>
    public static IWatchedService Watched(ISession session, ServiceProcess process)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Watched(process, session.Config);
    }

    /// <summary>The watch view of a service a driver outside the case runner launched on the run <paramref name="config"/> describes.</summary>
    public static IWatchedService Watched(ServiceProcess process, Config config)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(config);
        return new WatchedService(process, config);
    }

    /// <summary>Opens the journal reader of the run <paramref name="config"/> describes.</summary>
    public static Task<IWatchStore> OpenStoreAsync(Config config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var open = StoreOpener
            ?? throw new AcceptanceException("no journal reader is wired for the in-game brain's store (ObservedSession.StoreOpener): the case cannot read the goal's durable state");
        return open(config, cancellationToken);
    }

    /// <summary>Reads the layout plan of the run <paramref name="config"/> describes (see <see cref="LayoutPlanReader"/>).</summary>
    public static Task<(ulong Tick, LayoutPlan? Plan)> ReadLayoutPlanAsync(Config config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var read = LayoutPlanReader
            ?? throw new AcceptanceException("no layout-plan reader is wired for the in-game brain's store (ObservedSession.LayoutPlanReader)");
        return read(config, cancellationToken);
    }

    public Harness Harness => _session.Harness;

    public Config Config => _session.Config;

    public Report Report => _session.Report;

    public ServeSpec Spec => _session.Spec;

    public async Task<IWatchedService> ServeAsync(ServeSpec spec, CancellationToken cancellationToken) =>
        new WatchedService(await _session.ServeAsync(spec, cancellationToken).ConfigureAwait(false), _session.Config);

    public Task<Harness> ReattachAsync(CancellationToken cancellationToken) => Task.FromResult(_session.Reattach());

    private sealed class WatchedService(ServiceProcess process, Config config) : IWatchedService
    {
        public string LogPath => process.LogPath;

        public string Label => process.Label;

        public JsonObject? Identity => process.Identity;

        public string Token => process.Token;

        public Exception? Exited() => process.Exited();

        public void HoldAuthority(bool hold) => process.HoldAuthority(hold);

        public Task<ServiceApiReply> ApiAsync(string method, string path, JsonObject? body, string token, CancellationToken cancellationToken) =>
            process.ApiAsync(method, path, body, token, cancellationToken);

        public async Task<JsonObject> AcquireAsync(CancellationToken cancellationToken) =>
            new() { ["plan"] = await process.AcquireAsync(cancellationToken).ConfigureAwait(false) };

        public Task<IWatchStore> StoreAsync(CancellationToken cancellationToken) => OpenStoreAsync(config, cancellationToken);

        public void KeepAuthority(CancellationToken cancellationToken) => process.KeepAuthority(cancellationToken);

        public Task<bool> StepAdmittedAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            process.StepAdmittedAsync(since, cancellationToken);

        public Task<JsonObject?> StopAsync() => process.StopAsync();
    }
}
