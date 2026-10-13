using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Small helpers the food, medical and mood families share (Go <c>nativeaccept</c> reads the cases call). Each one is
/// a thin wrapper over the harness's public API; none reaches an internal.
/// </summary>
internal static class NativeReads
{
    /// <summary>A JSON number as a double; null for anything else (Go's strict <c>v.(float64)</c> assertion: a numeric string is not a number).</summary>
    internal static double? Strict(this JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<double>(out var number) ? number : null;

    /// <summary>Whether <paramref name="value"/> is a JSON boolean true (Go <c>flag</c>).</summary>
    internal static bool Flag(this JsonNode? value) => value.AsBool() == true;

    /// <summary>Go <c>fmt.Sprint</c> of a decoded value: a string as itself, <c>&lt;nil&gt;</c> for none, else its JSON text.</summary>
    internal static string ToText(this JsonNode? value) => value switch
    {
        null => "<nil>",
        JsonValue json when json.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString(),
    };

    /// <summary>An invariant decimal rendering for messages and labels.</summary>
    internal static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A copy that can be added to another node (a node has one parent).</summary>
    internal static JsonNode? Copy(this JsonNode? value) => value?.DeepClone();

    /// <summary>The object rows of a JSON array (non-objects skipped).</summary>
    internal static IEnumerable<JsonObject> Rows(this JsonNode? value) => value.AsSlice().Select(row => row.AsMap()).OfType<JsonObject>();

    /// <summary>A string array as a JSON array node.</summary>
    internal static JsonArray Strings(params string[] values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>
    /// <c>observations_read_colony_facts</c> for the session's identity (optionally with the planning sections) and its
    /// <c>observed</c> body.
    /// </summary>
    internal static async Task<JsonObject> ColonyFactsAsync(ISession s, string label, bool planning, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() } };
        if (planning)
        {
            request["planning"] = true;
        }

        var reply = await s.Harness.WireAsync(label, "observations_read_colony_facts", request, cancellationToken).ConfigureAwait(false);
        return Harness.Outcome(reply, "observed").Value;
    }

    /// <summary>The value a staging run or a ring checkpoint recorded under <paramref name="key"/> (Go <c>cases.RestoredState</c>).</summary>
    internal static JsonNode? RestoredState(ISession s, string key) => StagePlanning.RestoredState(s.Resumed, s.Staged, key);

    /// <summary>Go <c>na.RunUntil</c>: runs the game and polls <paramref name="probe"/> until done, under the shared stall budget unless <paramref name="wait"/> says otherwise.</summary>
    internal static Task<ulong> RunUntilAsync(ISession s, string label, ulong ticks, Wait wait, Probe probe, bool quiet, CancellationToken cancellationToken) =>
        RunAt.RunUntilAsync(s.Harness, label, ticks, wait, probe, quiet, s.Report, cancellationToken);

    /// <summary><see cref="RunUntilAsync(ISession, string, ulong, Wait, Probe, bool, CancellationToken)"/> on the shared stall budget.</summary>
    internal static Task<ulong> RunUntilAsync(ISession s, string label, ulong ticks, Probe probe, bool quiet, CancellationToken cancellationToken) =>
        RunUntilAsync(s, label, ticks, new Wait { Stall = Wait.StallBudget() }, probe, quiet, cancellationToken);

    /// <summary>
    /// Go <c>context.WithTimeout</c>: runs <paramref name="body"/> under a token that cancels after <paramref name="limit"/>
    /// and reports that expiry as an <see cref="AcceptanceException"/> (the run's own cancellation still propagates).
    /// </summary>
    internal static async Task<T> WithinAsync<T>(TimeSpan limit, string what, Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(limit);
        try
        {
            return await body(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcceptanceException($"{what}: not done within {limit}");
        }
    }

    /// <summary>Sets the game speed to Paused: a drain-style probe refuses an unpaused game.</summary>
    internal static async Task PauseAsync(Harness harness, string label, CancellationToken cancellationToken) =>
        _ = await harness.CallAsync(label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// One launch of the brain's player API under authority: the Go sequence of launch, session token, wait for the
/// attach, resume and the authority keep-alive. The brain is in-process in the mod, so <see cref="StopAsync"/> also
/// parks it (manual control) the way the Go serve child's death left the game: nothing drives the clock afterwards.
/// </summary>
internal sealed class BrainRun
{
    private readonly ISession _session;
    private readonly string _prefix;
    private Func<Task<JsonObject>>? _stopKeep;
    private bool _stopped;

    private BrainRun(ISession session, ServiceProcess service, string token, string prefix)
    {
        _session = session;
        Service = service;
        Token = token;
        _prefix = prefix;
    }

    internal ServiceProcess Service { get; }

    /// <summary>A handle <see cref="ISession.ServeAsync"/> started (its keep-alive, if any, belongs to the service).</summary>
    internal static BrainRun Wrap(ISession s, ServiceProcess service, string prefix) => new(s, service, service.Token, prefix);

    internal string Token { get; }

    /// <summary>Launches the player API handle, waits for the brain to attach to the session's identity and resumes automatic control; a non-null <paramref name="keepPrefix"/> also keeps it granted under that request-id prefix.</summary>
    internal static async Task<BrainRun> StartAsync(ISession s, IReadOnlyList<string>? families, string prefix, string? keepPrefix, CancellationToken cancellationToken)
    {
        var service = await s.LaunchAsync(new ServiceLaunch { Families = families }, cancellationToken).ConfigureAwait(false);
        var token = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
        _ = await service.WaitAttachedAsync(s.Identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        _ = await service.ResumeAsync(prefix, s.Identity, token, s.Report, cancellationToken).ConfigureAwait(false);
        var run = new BrainRun(s, service, token, prefix);
        if (keepPrefix is not null)
        {
            run._stopKeep = new AuthorityKeepAlive(service, keepPrefix, s.Identity, token).Start(cancellationToken);
        }

        return run;
    }

    /// <summary>The live game tick the service reports; null before its first snapshot.</summary>
    internal Task<ulong?> TickAsync(CancellationToken cancellationToken) => SustainedFood.LiveTickAsync(Service.ApiAsync, cancellationToken);

    /// <summary>GET of a player API path (the body of a 200 reply).</summary>
    internal Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken) => Service.GetAsync("GET", path, cancellationToken);

    /// <summary>Ends the keep-alive and the handle, then parks the brain (a best effort whose outcome is recorded on the report).</summary>
    internal async Task<JsonObject?> StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped)
        {
            return null;
        }

        _stopped = true;
        JsonObject? keep = null;
        if (_stopKeep is { } stopKeep)
        {
            _stopKeep = null;
            keep = await stopKeep().ConfigureAwait(false);
        }

        keep = await Service.StopAsync().ConfigureAwait(false) ?? keep;
        var body = new JsonObject
        {
            ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"{_prefix}-park-{DateTimeOffset.UtcNow.Ticks}"),
            ["expected"] = _session.Identity.DeepClone(),
        };
        try
        {
            var reply = await Service.ApiAsync("POST", "/api/player/control/pause", body, Token, cancellationToken).ConfigureAwait(false);
            _session.Report[_prefix + "_park"] = reply.Status;
        }
        catch (AcceptanceException e)
        {
            _session.Report[_prefix + "_park_error"] = e.Message;
        }

        return keep;
    }
}
