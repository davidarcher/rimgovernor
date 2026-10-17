using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <see cref="IObservedSession"/> the serve-driven watch (<see cref="SustainedFood.ObserveAsync"/>) drives over a
/// game a driver outside the case runner opened itself (the colony and colony-review subcommands). The service is a
/// <see cref="ServiceProcess"/>, wrapped as an <see cref="IWatchedService"/>.
/// </summary>
public static class ObservedSession
{
    /// <summary>
    /// The watch view of a game a driver outside the case runner opened itself (<see cref="Session.OpenAsync"/>): the
    /// service it serves is launched on that game.
    /// </summary>
    public static IObservedSession Of(Session session, Config config, Report report, ServeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(spec);
        return new OpenedSession(session, config, report, spec);
    }

    /// <summary>
    /// Opens the service's durable-journal reader for the run <paramref name="config"/> describes (the file the
    /// harness points the game at, <c>&lt;output&gt;/service.sqlite</c>), for the watch's samples.
    /// </summary>
    public static async Task<IWatchStore> OpenStoreAsync(Config config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        return await CaseStore.OpenAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private sealed class OpenedSession(Session session, Config config, Report report, ServeSpec spec) : IObservedSession
    {
        public Harness Harness => session.Harness;

        public Config Config => config;

        public Report Report => report;

        public ServeSpec Spec => spec;

        public async Task<IWatchedService> ServeAsync(ServeSpec serve, CancellationToken cancellationToken) =>
            new WatchedService(
                await ServiceProcess.ServeAsync(config, session.Harness, session.Identity, serve, report, cancellationToken).ConfigureAwait(false),
                config);

        public Task<Harness> ReattachAsync(CancellationToken cancellationToken) => Task.FromResult(session.Harness);
    }

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

        public Task AcquireAsync(CancellationToken cancellationToken) => process.AcquireAsync(cancellationToken);

        public Task<IWatchStore> StoreAsync(CancellationToken cancellationToken) => OpenStoreAsync(config, cancellationToken);

        public void KeepAuthority(CancellationToken cancellationToken) => process.KeepAuthority(cancellationToken);

        public Task<bool> StepAdmittedAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            process.StepAdmittedAsync(since, cancellationToken);

        public Task<JsonObject?> StopAsync() => process.StopAsync();
    }
}
