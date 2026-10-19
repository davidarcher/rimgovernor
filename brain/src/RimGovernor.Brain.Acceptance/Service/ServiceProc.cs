using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The service's HTTP surface and the polls over it. The in-mod brain's journal belongs to the
// game, so the harness reads the controller through the player API: /api/state, /api/routines and the control routes,
// each into its ServiceReplies record.

public sealed partial class ServiceProcess
{
    private static readonly HttpClient s_http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding Utf8 = new(false);

    private ulong _firstTick;
    private ulong _lastTick;
    private DateTimeOffset _firstTickAt;
    private DateTimeOffset _lastTickAt;
    private int _tickSamples;

    /// <summary>
    /// Issues one HTTP request against the service, recording every exchange as a capped evidence row under
    /// output/service/http-NNNN.json: NNNN is the run's evidence sequence (shared with the harness's native rows, so
    /// the two streams interleave by number) and the row carries the same observed_at and elapsed_ms stamps. The
    /// <paramref name="token"/> is accepted for the checkpoint interface and ignored: the channel has no token.
    /// Transport failures throw; a non-2xx status is a reply.
    /// </summary>
    public async Task<ServiceApiReply> ApiAsync(string method, string path, JsonObject? body, string token, CancellationToken cancellationToken)
    {
        _ = token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);
        linked.CancelAfter(ApiTimeout);
        using var message = new HttpRequestMessage(new HttpMethod(method), Url + path);
        if (body is not null)
        {
            message.Content = new StringContent(body.ToJsonString(), Utf8, "application/json");
        }

        message.Headers.TryAddWithoutValidation("Origin", Url);
        var sent = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int status;
        string text;
        try
        {
            using var response = await s_http.SendAsync(message, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new NativeTransportException($"{method} {path}: no reply within {ApiTimeout.TotalSeconds:0.#}s");
        }
        catch (HttpRequestException e)
        {
            throw new NativeTransportException($"{method} {path}: {e.Message}", e);
        }

        JsonNode? parsed = null;
        string? decodeError = null;
        if (text.Length > 0)
        {
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                decodeError = e.Message;
            }
        }

        var n = Evidence.NextSequence(Path.GetDirectoryName(_dir) ?? _dir);
        EvidenceBudget.WriteEvidence(Path.Combine(_dir, string.Create(CultureInfo.InvariantCulture, $"http-{n:0000}.json")), new JsonObject
        {
            { "sequence", n },
            { "observed_at", sent.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) },
            { "elapsed_ms", clock.ElapsedMilliseconds },
            { "method", method },
            { "path", path },
            { "request", body?.DeepClone() },
            { "status", status },
            { "response", decodeError is null ? parsed?.DeepClone() : JsonValue.Create(text) },
        });
        if (decodeError is not null || (parsed is not null && parsed is not JsonObject))
        {
            throw new AcceptanceException($"decode {method} {path}: {decodeError ?? "the body is not a JSON object"}");
        }

        var reply = parsed as JsonObject;
        if (path == "/api/state" && status == 200 && ServiceJson.State(reply)?.Game?.Tick is long tick && tick >= 0)
        {
            ObserveTick((ulong)tick);
        }

        return new ServiceApiReply(reply, status);
    }

    private void ObserveTick(ulong tick)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_tickSamples == 0)
            {
                _firstTick = tick;
                _firstTickAt = now;
            }

            _lastTick = tick;
            _lastTickAt = now;
            _tickSamples++;
        }
    }

    /// <summary>
    /// Checks /api/health and the explicit-player session against this process and returns the session token. The
    /// health must name the rimgovernor service on the C# backend in the process the endpoint file named; there is no
    /// session token (the channel's request guard is the only access check), so
    /// the token is empty.
    /// </summary>
    public async Task<string> SessionTokenAsync(CancellationToken cancellationToken)
    {
        var health = await ApiAsync("GET", "/api/health", null, "", cancellationToken).ConfigureAwait(false);
        var body = health.Body;
        if (health.Status != 200 || ServiceJson.Health(body) is not { Service: "rimgovernor", Backend: "csharp" } served || served.Pid != Pid)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected /api/health: status={health.Status} body={body.ToJson()}"));
        }

        var session = await ApiAsync("GET", "/api/player/session", null, "", cancellationToken).ConfigureAwait(false);
        if (session.Status != 200 || ServiceJson.Session(session.Body)?.Mode != "explicit-player")
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected /api/player/session: status={session.Status} body={session.Body.ToJson()}"));
        }

        return "";
    }

    /// <summary>
    /// Polls /api/state until the brain has attached to the same running game identity the harness prepared. A 503
    /// (the controller has no snapshot yet: the brain starts before the game has loaded) is not an attachment yet.
    /// </summary>
    public async Task<JsonObject> WaitAttachedAsync(JsonObject identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var want = Harness.IdentityOf(identity);
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var pace = Wait.Pace(TimeSpan.FromMilliseconds(500));
        while (true)
        {
            var reply = await ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status is not (200 or 503))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected /api/state status={reply.Status} body={reply.Body.ToJson()}"));
            }

            if (reply.Status == 200 && reply.Body is { } state
                && ServiceJson.State(state) is { Connected: true, Identity: { } served } && Matches(served, want))
            {
                return state;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcceptanceException($"service did not attach to the fixture's identity in time: {reply.Body.ToJson()}");
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Whether the identity the service attached to names the same colony/load/map as the harness's.
    private static bool Matches(ServiceIdentity served, RimGovernor.Contracts.Wire.Common.Identity want) =>
        served.ColonyId == want.ColonyId && served.LoadToken == want.LoadToken && served.MapId == want.MapId;

    /// <summary>
    /// Enters automate mode without an anchor plan: authority is the world's own root plan, created on first resume.
    /// It returns that root plan id. The request ID is unique per call: a run resumed from a checkpoint restores the
    /// store, whose earlier resume under the same prefix names another load and would replay as a conflict.
    /// </summary>
    public async Task<string> ResumeAsync(string prefix, JsonObject identity, string token, Report report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var body = new JsonObject
        {
            { "requestId", string.Create(CultureInfo.InvariantCulture, $"{prefix}-resume-{UniqueStamp()}") },
            { "expected", identity.DeepClone() },
        };
        var reply = await ApiAsync("POST", "/api/player/control/resume", body, token, cancellationToken).ConfigureAwait(false);
        if (reply.Status != 200)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected resume status={reply.Status} body={reply.Body.ToJson()}"));
        }

        var resumed = reply.Body ?? [];
        var control = ServiceJson.Control(resumed);
        if (control?.Record?.Phase != "running")
        {
            throw new AcceptanceException($"resume was not running: {resumed.ToJson()}");
        }

        report["resumed"] = resumed.DeepClone();
        var rootPlanId = control.State?.Generation?.Plan ?? "";
        if (rootPlanId.Length == 0)
        {
            throw new AcceptanceException($"resume reported no root plan: {resumed.ToJson()}");
        }

        return rootPlanId;
    }

    /// <summary>
    /// Parks the brain (manual control) for <paramref name="identity"/>, so nothing drives the clock afterwards, and
    /// returns the reply status. The in-mod brain outlives the handle: a stopped handle leaves it running until parked.
    /// </summary>
    public async Task<int> ParkAsync(string prefix, JsonObject identity, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var body = new JsonObject
        {
            { "requestId", string.Create(CultureInfo.InvariantCulture, $"{prefix}-park-{UniqueStamp()}") },
            { "expected", identity.DeepClone() },
        };
        var reply = await ApiAsync("POST", "/api/player/control/pause", body, token, cancellationToken).ConfigureAwait(false);
        return reply.Status;
    }

    // A request-id suffix unique per call.
    internal static string UniqueStamp() =>
        (DateTimeOffset.UtcNow.Ticks * 100 + Interlocked.Increment(ref s_stampSerial) % 100).ToString(CultureInfo.InvariantCulture);

    private static long s_stampSerial;

    /// <summary>
    /// Reads lifecycle_read_identity and returns the loaded game's identity (colonyId, loadToken, mapId); the main
    /// menu is an error.
    /// </summary>
    private static Task<JsonObject> ReadIdentityAsync(Harness harness, string label, CancellationToken cancellationToken) =>
        Session.ReadIdentityAsync(harness, label, cancellationToken);

    /// <summary>The environment variable naming the clock speed a serve-driven harness runs at; the default is Hyperfast.</summary>
    public const string ClockSpeedEnv = "RIMGOVERNOR_ACCEPT_CLOCK_SPEED";

    // ClockSpeedEnv or Hyperfast.
    private static string ClockSpeed() =>
        Environment.GetEnvironmentVariable(ClockSpeedEnv) is { Length: > 0 } speed ? speed : "Hyperfast";

    /// <summary>
    /// Sets the speed the brain requests while it owns the clock (POST /api/player/clock-speed), taking effect from the
    /// next window. The in-mod brain outlives a service handle, so every launch states its speed (the run's clock speed
    /// when <paramref name="speed"/> is empty) and a row never inherits the previous row's. The route exists once the
    /// brain has composed, which can trail the endpoint file, so a 404 or 503 is retried for a bounded time.
    /// </summary>
    internal async Task SetClockSpeedAsync(string speed, CancellationToken cancellationToken)
    {
        var name = speed.Length > 0 ? speed : ClockSpeed();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        using var pace = Wait.Pace(TimeSpan.FromMilliseconds(500));
        while (true)
        {
            var reply = await ApiAsync("POST", "/api/player/clock-speed", new JsonObject { { "speed", name } }, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status == 200)
            {
                lock (_gate)
                {
                    _entry.Set("clock_speed", name);
                }

                return;
            }

            if (reply.Status is not (404 or 503) || DateTimeOffset.UtcNow > deadline)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"POST /api/player/clock-speed {name}: status {reply.Status} body={reply.Body.ToJson()}"));
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Resumes native player authority whenever the service drops out of automate mode, acknowledging any clock hold
/// first, exactly as cases/routinehaul's own keep-alive does: a bounded native generation legitimately lapses on any
/// unrecognised clock event (a random world letter), and nothing resumes it automatically.
/// </summary>
public sealed class AuthorityKeepAlive : IDisposable
{
    /// <summary>
    /// How often a keep-alive checks the service is still in automate mode: a lapse costs the colony that many ticks
    /// of no governor, so the check is cheap (/api/state) and frequent.
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMilliseconds(500);

    // How long a keep-alive tolerates the service reporting that it holds authority but waits for current
    // observations: a long planner step on a loaded box leaves the snapshot older than its max age while the game is
    // frozen, and a resume then bumps the native generation under that very step, failing it.
    private static readonly TimeSpan KeepAliveStaleGrace = TimeSpan.FromMinutes(3);

    private readonly ServiceProcess _service;
    private readonly string _prefix;
    private readonly JsonObject _identity;
    private readonly string _token;

    // pass is held for one pass of the loop, so Hold(true) returns only when no pass is between its requests: a
    // checkpoint's own pause and resume then never fall inside a pass, whose generation accounting would charge them
    // to its resume.
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly Lock _gate = new();
    private volatile bool _hold;
    private DateTimeOffset? _waitingSince;
    private bool _wasAutomate;
    private int _attempts;
    private int _reacquired;

    // The sum, over reacquisitions, of the native generations each resume cost: the grant's generation less the one
    // the service reported before it. A resume from a plain clock hold costs one; generationsOverOne counts those
    // that cost more (a revoke first, the redundant Manual->Auto pair, or a native revocation in between).
    private ulong _generationAdvance;
    private int _generationsOverOne;
    private string _lastError = "";

    /// <param name="service">The service whose API the loop drives.</param>
    /// <param name="prefix">The requestId prefix.</param>
    /// <param name="identity">The identity every resume expects.</param>
    /// <param name="token">The service token (empty for the channel).</param>
    public AuthorityKeepAlive(ServiceProcess service, string prefix, JsonObject identity, string token)
    {
        _service = service;
        _prefix = prefix;
        _identity = identity;
        _token = token;
    }

    /// <summary>Runs the keep-alive loop until the token is cancelled or the returned stop is awaited; stop returns the counters.</summary>
    public Func<Task<JsonObject>> Start(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loop = Task.Run(() => RunAsync(cts.Token), CancellationToken.None);
        return async () =>
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await loop.ConfigureAwait(false);
            cts.Dispose();
            Dispose();
            return Snapshot();
        };
    }

    /// <summary>Reports the loop's counters for the report.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var result = new JsonObject
            {
                { "attempts", _attempts },
                { "reacquired", _reacquired },
                { "generation_advance", _generationAdvance },
                { "generations_over_one", _generationsOverOne },
            };
            if (_lastError.Length > 0)
            {
                result.Set("last_error", _lastError);
            }

            return result;
        }
    }

    /// <summary>Pauses (true) or resumes (false) re-acquisition. Pausing returns once the pass in flight, if any, has finished.</summary>
    public void Hold(bool held)
    {
        _pass.Wait();
        try
        {
            _hold = held;
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>Releases the pass lock; <see cref="Start"/>'s stop calls it once the loop has ended.</summary>
    public void Dispose() => _pass.Dispose();

    private void Fail(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var pace = Wait.Pace(KeepAliveInterval);
        while (true)
        {
            try
            {
                await pace.NextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await OnceAsync().ConfigureAwait(false);
        }
    }

    // One pass of the loop, under the pass lock so Hold can wait for it. Its requests run to completion even when the
    // loop is stopping: a resume is never abandoned half-way.
    private async Task OnceAsync()
    {
        await _pass.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hold)
            {
                return;
            }

            await PassAsync().ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            Fail(e.Message);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task PassAsync()
    {
        ServiceApiReply stateReply;
        try
        {
            stateReply = await _service.ApiAsync("GET", "/api/state", null, "", CancellationToken.None).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            return;
        }

        if (stateReply.Status != 200 || ServiceJson.State(stateReply.Body) is not { } state)
        {
            return;
        }

        if (state.Mode == "automate")
        {
            _waitingSince = null;
            _wasAutomate = true;
            return;
        }

        if (_wasAutomate && state.Status?.Label == "Player control is waiting for current observations")
        {
            _waitingSince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _waitingSince < KeepAliveStaleGrace)
            {
                return;
            }
        }
        else
        {
            _waitingSince = null;
        }

        var beforeKnown = state.Generation is not null;
        var before = state.Generation?.Native ?? 0;
        lock (_gate)
        {
            _attempts++;
        }

        var body = new JsonObject
        {
            { "requestId", string.Create(CultureInfo.InvariantCulture, $"{_prefix}-resume-{ServiceProcess.UniqueStamp()}") },
            { "expected", _identity.DeepClone() },
        };
        var resumed = await _service.ApiAsync("POST", "/api/player/control/resume", body, _token, CancellationToken.None).ConfigureAwait(false);
        if (resumed.Status != 200)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"resume status={resumed.Status} body={resumed.Body.ToJson()}"));
            return;
        }

        var record = ServiceJson.Control(resumed.Body)?.Record;
        if (record?.Phase != "running")
        {
            Fail($"resume not running: {resumed.Body.ToJson()}");
            return;
        }

        lock (_gate)
        {
            _reacquired++;
            _lastError = "";
            var after = record.NativeGeneration;
            if (beforeKnown && after > before)
            {
                _generationAdvance += after - before;
                if (after - before > 1)
                {
                    _generationsOverOne++;
                }
            }
        }
    }
}

