using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The service's HTTP surface and the polls over it (Go serviceproc.go). The Go waits read the serve child's SQLite
// journal through store.Store; the in-mod brain's journal belongs to the game, so they read the same facts from the
// player API instead: /api/routines for the review, /api/plan for one plan, /api/state for the controller.

/// <summary>A method a goal committed, as the player API shows it (the Go <c>domain.Method</c> with its standard).</summary>
/// <param name="Concern">The concern the goal serves.</param>
/// <param name="Method">The goal's current method or ladder rung ("hunt", "cook").</param>
/// <param name="Expected">The observable the method is expected to move.</param>
/// <param name="Plan">The controller's active plan when the method was seen; the player API does not expose a method's own plan.</param>
public sealed record ServiceMethod(string Concern, string Method, string Expected, string Plan);

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
            ["sequence"] = n,
            ["observed_at"] = sent.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["method"] = method,
            ["path"] = path,
            ["request"] = body?.DeepClone(),
            ["status"] = status,
            ["response"] = decodeError is null ? parsed?.DeepClone() : JsonValue.Create(text),
        });
        if (decodeError is not null || (parsed is not null && parsed is not JsonObject))
        {
            throw new AcceptanceException($"decode {method} {path}: {decodeError ?? "the body is not a JSON object"}");
        }

        var reply = parsed as JsonObject;
        if (path == "/api/state" && reply?["game"].AsMap()?["tick"].AsUInt64() is { } tick)
        {
            ObserveTick(tick);
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
    /// <see cref="ApiAsync"/>'s <see cref="HttpGet"/>-shaped adapter for <see cref="Rounds.AssertRoundsRunningAsync"/>:
    /// the body of a 200 reply; any other status throws.
    /// </summary>
    public async Task<JsonObject> GetAsync(string method, string path, CancellationToken cancellationToken)
    {
        var reply = await ApiAsync(method, path, null, "", cancellationToken).ConfigureAwait(false);
        if (reply.Status != 200)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{method} {path}: status {reply.Status}"));
        }

        return reply.Body ?? [];
    }

    /// <summary>
    /// Checks /api/health and the explicit-player session against this process and returns the session token. The
    /// health must name the rimgovernor service on the C# backend in the process the endpoint file named; the
    /// session token of the Go service no longer exists (the channel's request guard is the only access check), so
    /// the token is empty.
    /// </summary>
    public async Task<string> SessionTokenAsync(CancellationToken cancellationToken)
    {
        var health = await ApiAsync("GET", "/api/health", null, "", cancellationToken).ConfigureAwait(false);
        var body = health.Body;
        if (health.Status != 200 || body.Member("service").AsString() != "rimgovernor" || body.Member("backend").AsString() != "csharp" || (int)body.Member("pid").AsNumber() != Pid)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected /api/health: status={health.Status} body={body.ToJson()}"));
        }

        var session = await ApiAsync("GET", "/api/player/session", null, "", cancellationToken).ConfigureAwait(false);
        if (session.Status != 200 || session.Body.Member("mode").AsString() != "explicit-player")
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
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var reply = await ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status is not (200 or 503))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected /api/state status={reply.Status} body={reply.Body.ToJson()}"));
            }

            if (reply.Status == 200 && reply.Body is { } state && state["connected"].AsBool() == true
                && state["identity"].AsMap() is { } served && MatchesIdentity(served, identity))
            {
                return state;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcceptanceException($"service did not attach to the fixture's identity in time: {reply.Body.ToJson()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reports whether <paramref name="value"/> names the same colony/load/map as <paramref name="want"/>.</summary>
    public static bool MatchesIdentity(JsonObject value, JsonObject want)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(want);
        return value["colonyId"].AsString() == want["colonyId"].AsString()
            && value["loadToken"].AsString() == want["loadToken"].AsString()
            && value["mapId"].AsNumber() == want["mapId"].AsNumber();
    }

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
            ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"{prefix}-resume-{UniqueStamp()}"),
            ["expected"] = identity.DeepClone(),
        };
        var reply = await ApiAsync("POST", "/api/player/control/resume", body, token, cancellationToken).ConfigureAwait(false);
        if (reply.Status != 200)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected resume status={reply.Status} body={reply.Body.ToJson()}"));
        }

        var resumed = reply.Body ?? [];
        if (resumed["record"].AsMap()?["phase"].AsString() != "running")
        {
            throw new AcceptanceException($"resume was not running: {resumed.ToJson()}");
        }

        report["resumed"] = resumed.DeepClone();
        var rootPlanId = resumed["state"].AsMap()?["generation"].AsMap()?["plan"].AsString() ?? "";
        if (rootPlanId.Length == 0)
        {
            throw new AcceptanceException($"resume reported no root plan: {resumed.ToJson()}");
        }

        return rootPlanId;
    }

    // A request-id suffix unique per call (Go time.Now().UnixNano()).
    internal static string UniqueStamp() =>
        (DateTimeOffset.UtcNow.Ticks * 100 + Interlocked.Increment(ref s_stampSerial) % 100).ToString(CultureInfo.InvariantCulture);

    private static long s_stampSerial;

    /// <summary>
    /// Polls until the service reaches automate mode and a rounds pass has been persisted (the routines read shows a
    /// last review tick), returning the routines body and the diagnostics it gathered. Besides the timeout the wait
    /// stalls (<see cref="Wait.StallBudget"/>) when neither the service mode nor the last review tick changes.
    /// </summary>
    public async Task<(JsonObject Review, IReadOnlyList<JsonObject> Diagnostics)> WaitRoundsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var diagnostics = new List<JsonObject>();
        var sawAutomate = false;
        var review = new JsonObject();
        var wait = new Wait { Ceiling = timeout, Stall = Wait.StallBudget(), Terminal = Exited };
        try
        {
            await wait.WaitProgressAsync(async ct =>
            {
                var entry = new JsonObject();
                JsonObject? state = null;
                try
                {
                    state = (await ApiAsync("GET", "/api/state", null, "", ct).ConfigureAwait(false)).Body;
                    entry["state"] = state?.DeepClone();
                }
                catch (NativeTransportException e)
                {
                    entry["state_error"] = e.Message;
                }

                try
                {
                    entry["clock"] = (await ApiAsync("GET", "/api/player/clock", null, "", ct).ConfigureAwait(false)).Body?.DeepClone();
                }
                catch (NativeTransportException e)
                {
                    entry["clock_error"] = e.Message;
                }

                diagnostics.Add(entry);
                var mode = state.Member("mode").AsString();
                if (mode == "automate")
                {
                    sawAutomate = true;
                }

                var routines = await ReadRoutinesAsync(ApiAsync, ct).ConfigureAwait(false);
                if (routines is not null)
                {
                    review = routines;
                    if (routines["lastReviewTick"] is not null && sawAutomate)
                    {
                        return ("", true);
                    }
                }

                return (Wait.Signature(mode, review["lastReviewTick"].ToJson()), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e) when (!sawAutomate)
        {
            throw new AcceptanceException($"service never reached automate mode after acquire: {e.Message}", e);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"service reached automate mode but the Rounds pass was never persisted (no last review tick): {e.Message}", e);
        }

        return (review, diagnostics);
    }

    /// <summary>
    /// Reads lifecycle_read_identity and returns the loaded game's identity (colonyId, loadToken, mapId); the main
    /// menu is an error (Go <c>ReadIdentity</c>).
    /// </summary>
    private static async Task<JsonObject> ReadIdentityAsync(Harness harness, string label, CancellationToken cancellationToken)
    {
        var reply = await harness.WireAsync(label, "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var loaded = Harness.Outcome(reply, "loaded").Value;
        return loaded["context"].AsMap()?["identity"].AsMap()?.DeepClone() as JsonObject
            ?? throw new AcceptanceException($"{label}: loaded game reports no identity");
    }

    /// <summary>The environment variable naming the clock speed a serve-driven harness runs at; the default is Ultrafast.</summary>
    public const string ClockSpeedEnv = "RIMGOVERNOR_ACCEPT_CLOCK_SPEED";

    // ClockSpeedEnv or Ultrafast (Go ClockSpeed).
    private static string ClockSpeed() =>
        Environment.GetEnvironmentVariable(ClockSpeedEnv) is { Length: > 0 } speed ? speed : "Ultrafast";

    /// <summary>
    /// Sets the speed the service will follow as the player's own choice: the speed, then a pause, through the
    /// ordinary time controls, so native records it as the player's pre-pause speed. A boosted run (Ultrafast, the
    /// native test acceleration of the headless profile) ignores it and nothing is written. The identity read first
    /// is a typed hop, which installs native's speed hook if nothing has yet.
    /// </summary>
    private static async Task WritePlayerSpeedAsync(Harness harness, ServeSpec spec, CancellationToken cancellationToken)
    {
        var speed = spec.PlayerSpeed;
        if (speed.Length == 0)
        {
            speed = ClockSpeed();
            if (speed == "Ultrafast")
            {
                return;
            }
        }

        await harness.WireAsync("player-speed-hook", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        foreach (var s in new[] { speed, "Paused" })
        {
            await harness.CallAsync("player-speed-" + s.ToLowerInvariant(), "rimgovernor/set_time_speed", new JsonObject { ["speed"] = s, ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads GET /api/routines; null while the brain reports it unavailable (503), any other non-200 throws.</summary>
    internal static async Task<JsonObject?> ReadRoutinesAsync(ServiceApiCall api, CancellationToken cancellationToken)
    {
        var reply = await api("GET", "/api/routines", null, "", cancellationToken).ConfigureAwait(false);
        return reply.Status switch
        {
            200 => reply.Body ?? [],
            503 => null,
            _ => throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"GET /api/routines: status {reply.Status} body={reply.Body.ToJson()}")),
        };
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
    private int _acknowledged;
    private int _acknowledgeFailed;

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
                ["attempts"] = _attempts,
                ["reacquired"] = _reacquired,
                ["acknowledged"] = _acknowledged,
                ["acknowledge_failed"] = _acknowledgeFailed,
                ["generation_advance"] = _generationAdvance,
                ["generations_over_one"] = _generationsOverOne,
            };
            if (_lastError.Length > 0)
            {
                result["last_error"] = _lastError;
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
        while (true)
        {
            try
            {
                await Task.Delay(KeepAliveInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await OnceAsync().ConfigureAwait(false);
        }
    }

    // One pass of the loop, under the pass lock so Hold can wait for it. Its requests run to completion even when the
    // loop is stopping, as the Go service context outlived the loop's: a resume is never abandoned half-way.
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

        if (stateReply.Status != 200 || stateReply.Body is not { } state)
        {
            return;
        }

        if (state["mode"].AsString() == "automate")
        {
            _waitingSince = null;
            _wasAutomate = true;
            return;
        }

        if (_wasAutomate && state["status"].AsMap()?["label"].AsString() == "Player control is waiting for current observations")
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

        var beforeKnown = TryGeneration(state["generation"], out var before);
        await AcknowledgeHoldsAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _attempts++;
        }

        var body = new JsonObject
        {
            ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"{_prefix}-resume-{ServiceProcess.UniqueStamp()}"),
            ["expected"] = _identity.DeepClone(),
        };
        var resumed = await _service.ApiAsync("POST", "/api/player/control/resume", body, _token, CancellationToken.None).ConfigureAwait(false);
        if (resumed.Status != 200)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"resume status={resumed.Status} body={resumed.Body.ToJson()}"));
            return;
        }

        var record = resumed.Body.Member("record").AsMap();
        if (record?["phase"].AsString() != "running")
        {
            Fail($"resume not running: {resumed.Body.ToJson()}");
            return;
        }

        lock (_gate)
        {
            _reacquired++;
            _lastError = "";
            if (record["nativeGeneration"].AsUInt64() is { } after && beforeKnown && after > before)
            {
                _generationAdvance += after - before;
                if (after - before > 1)
                {
                    _generationsOverOne++;
                }
            }
        }
    }

    // Acknowledges any clock hold before the resume, so the resume is not refused over an unreviewed hold.
    private async Task AcknowledgeHoldsAsync()
    {
        ServiceApiReply clock;
        try
        {
            clock = await _service.ApiAsync("GET", "/api/player/clock", null, "", CancellationToken.None).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            return;
        }

        if (clock.Status != 200 || clock.Body is not { } review || review["holds"].AsSlice().Count == 0)
        {
            return;
        }

        var ack = new JsonObject
        {
            ["requestId"] = string.Create(CultureInfo.InvariantCulture, $"{_prefix}-ack-{ServiceProcess.UniqueStamp()}"),
            ["expectedRevision"] = review["revision"].AsString(),
            ["throughCursor"] = review["inboxCursor"].AsString(),
        };
        string? failure = null;
        try
        {
            var reply = await _service.ApiAsync("POST", "/api/player/clock/acknowledge", ack, _token, CancellationToken.None).ConfigureAwait(false);
            if (reply.Status != 200)
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"acknowledge status={reply.Status}");
            }
        }
        catch (AcceptanceException e)
        {
            failure = "acknowledge: " + e.Message;
        }

        lock (_gate)
        {
            if (failure is null)
            {
                _acknowledged++;
            }
            else
            {
                _lastError = failure;
                _acknowledgeFailed++;
            }
        }
    }

    // Reads the native generation out of /api/state's generation object, if it carries one.
    private static bool TryGeneration(JsonNode? value, out ulong native)
    {
        native = 0;
        if (value.AsMap()?["native"].AsUInt64() is { } found)
        {
            native = found;
            return true;
        }

        return false;
    }
}

/// <summary>
/// The shared polls over the player API (Go package-level waits of <c>serviceproc.go</c>): the durable rounds, a
/// plan and the methods a goal commits, each read through one <see cref="ServiceApiCall"/> and bounded by a
/// <see cref="Wait"/>.
/// </summary>
public static class ServiceWaits
{
    private const string UnavailableSignature = "unavailable";

    /// <summary>
    /// Polls the rounds under <paramref name="w"/> until <paramref name="ready"/> accepts the routines body. The
    /// progress signature is the review's latches and the concern-method bindings, so a review that keeps revising
    /// without moving either stalls.
    /// </summary>
    public static async Task<JsonObject> WaitReviewAsync(ServiceApiCall api, Wait w, Func<JsonObject, bool> ready, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(ready);
        var review = new JsonObject();
        if (w.Interval <= TimeSpan.Zero)
        {
            w = w with { Interval = TimeSpan.FromSeconds(1) };
        }

        await w.WaitProgressAsync(async ct =>
        {
            if (await ServiceProcess.ReadRoutinesAsync(api, ct).ConfigureAwait(false) is not { } routines)
            {
                return (UnavailableSignature, false);
            }

            review = routines;
            if (ready(routines))
            {
                return ("", true);
            }

            var needs = routines["progress"].AsSlice()
                .Select(row => row.AsMap())
                .Select(row => row?["concern"].AsString() + "=" + row?["method"].AsString())
                .Order(StringComparer.Ordinal);
            var latches = Wait.Signature(routines["reviewsEnabled"].ToJson(), routines["methodsEnabled"].ToJson(), string.Join(',', routines["emergency"].AsSlice().Select(c => c.AsString())));
            return (Wait.Signature(latches, string.Join(';', needs)), false);
        }, cancellationToken).ConfigureAwait(false);
        return review;
    }

    /// <summary>
    /// Polls plan <paramref name="planId"/> under <paramref name="w"/> until <paramref name="check"/> accepts its
    /// body; check's signature is whatever of the plan must move (usually its action stages and attempts, see
    /// <see cref="PlanSignature"/>).
    /// </summary>
    public static async Task<JsonObject> WaitPlanAsync(ServiceApiCall api, Wait w, string planId, Func<JsonObject, (string Signature, bool Done)> check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(check);
        var state = new JsonObject();
        if (w.Interval <= TimeSpan.Zero)
        {
            w = w with { Interval = TimeSpan.FromSeconds(1) };
        }

        await w.WaitProgressAsync(async ct =>
        {
            if (await LoadPlanAsync(api, planId, ct).ConfigureAwait(false) is not { } plan)
            {
                return (UnavailableSignature, false);
            }

            state = plan;
            return check(plan);
        }, cancellationToken).ConfigureAwait(false);
        return state;
    }

    // GET /api/plan?id=...; null while unavailable (503), an unknown plan (404) and any other failure throw.
    private static async Task<JsonObject?> LoadPlanAsync(ServiceApiCall api, string planId, CancellationToken cancellationToken)
    {
        var reply = await api("GET", "/api/plan?id=" + Uri.EscapeDataString(planId), null, "", cancellationToken).ConfigureAwait(false);
        return reply.Status switch
        {
            200 => reply.Body ?? [],
            503 => null,
            _ => throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"load plan {planId}: status {reply.Status} body={reply.Body.ToJson()}")),
        };
    }

    /// <summary>Each action's stage, attempt and receipt.</summary>
    public static string PlanSignature(JsonObject state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var parts = new List<object?>();
        foreach (var action in state["actions"].AsSlice())
        {
            var progress = action.AsMap()?["progress"].AsMap();
            parts.Add(progress?["stage"].AsString());
            parts.Add(progress?["attempt"].AsString());
            parts.Add(progress?["receipt"].AsString());
        }

        return Wait.Signature([.. parts]);
    }

    /// <summary>
    /// Polls the routines for <paramref name="need"/>'s goal binding and a committed method on it other than the one
    /// following <paramref name="previousPlan"/>. The player API shows a goal's current method name and the
    /// controller's active plan, not the goal's method history, so a method counts as new when the active plan is not
    /// one already followed. The wait stalls (<see cref="Wait.StallBudget"/>) when the binding and its method stop
    /// changing.
    /// </summary>
    public static Task<ServiceMethod> WaitMethodAsync(ServiceApiCall api, string need, string? previousPlan, CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? seen = previousPlan is null ? null : new HashSet<string>([previousPlan], StringComparer.Ordinal);
        return WaitMethodExcludingAsync(api, need, seen, cancellationToken);
    }

    /// <summary>
    /// <see cref="WaitMethodAsync"/> over a set of already-followed plans: a goal whose history holds several
    /// settled methods would otherwise hand the same old plan back on every call that names only the last one.
    /// </summary>
    public static Task<ServiceMethod> WaitMethodExcludingAsync(ServiceApiCall api, string need, IReadOnlySet<string>? seen, CancellationToken cancellationToken) =>
        WaitMethodCoreAsync(api, new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(1) }, need, seen, cancellationToken);

    /// <summary>The wait of <see cref="WaitMethodExcludingAsync"/> under an explicit <see cref="Wait"/> (Go <c>waitMethod</c>).</summary>
    internal static async Task<ServiceMethod> WaitMethodCoreAsync(ServiceApiCall api, Wait w, string need, IReadOnlySet<string>? seen, CancellationToken cancellationToken)
    {
        ServiceMethod? found = null;
        try
        {
            await w.WaitProgressAsync(async ct =>
            {
                if (await ServiceProcess.ReadRoutinesAsync(api, ct).ConfigureAwait(false) is not { } routines)
                {
                    return (UnavailableSignature, false);
                }

                var row = routines["progress"].AsSlice().Select(r => r.AsMap()).FirstOrDefault(r => r?["concern"].AsString() == need);
                if (row is null)
                {
                    return (Wait.Signature("unbound", routines["lastReviewTick"] is not null), false);
                }

                var method = row["method"].AsString();
                var state = await api("GET", "/api/state", null, "", ct).ConfigureAwait(false);
                var plan = state.Body?["activePlanId"].AsString() ?? "";
                if (method.Length > 0 && plan.Length > 0 && seen?.Contains(plan) != true)
                {
                    found = new ServiceMethod(need, method, row["expected"].AsString(), plan);
                    return ("", true);
                }

                return (Wait.Signature(need, method, plan), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"standard method for {need}: {e.Message}", e);
        }

        return found!;
    }

    /// <summary>
    /// Polls plan <paramref name="planId"/> until every action reaches a terminal stage. The incidental flag reports
    /// the executor's own authority-discontinuity cancellation (Cancelled with Effect Absent), which the caller
    /// should follow with a fresh <see cref="WaitMethodAsync"/> rather than fail on. The wait stalls
    /// (<see cref="Wait.StallBudget"/>) when no action's stage or attempt changes.
    /// </summary>
    public static Task<(JsonObject State, bool Incidental)> WaitPlanTerminalAsync(ServiceApiCall api, string planId, CancellationToken cancellationToken) =>
        WaitPlanTerminalCoreAsync(api, new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(1) }, planId, cancellationToken);

    /// <summary>The wait of <see cref="WaitPlanTerminalAsync"/> under an explicit <see cref="Wait"/> (Go <c>waitPlanTerminal</c>).</summary>
    internal static async Task<(JsonObject State, bool Incidental)> WaitPlanTerminalCoreAsync(ServiceApiCall api, Wait w, string planId, CancellationToken cancellationToken)
    {
        var state = new JsonObject();
        var incidental = false;
        await w.WaitProgressAsync(async ct =>
        {
            if (await LoadPlanAsync(api, planId, ct).ConfigureAwait(false) is not { } plan)
            {
                return (UnavailableSignature, false);
            }

            state = plan;
            var actions = plan["actions"].AsSlice();
            var terminal = actions.Count > 0;
            var signature = new List<object?>();
            foreach (var action in actions)
            {
                var progress = action.AsMap()?["progress"].AsMap();
                var stage = progress?["stage"].AsString();
                var attempt = progress?["attempt"].AsUInt64() ?? 0;
                var unresolved = progress?["unresolved"].AsBool() ?? false;
                signature.Add(stage);
                signature.Add(attempt);
                signature.Add(unresolved);
                switch (stage)
                {
                    case "completed":
                        break;
                    case "unsuccessful":
                        throw new AcceptanceException($"plan {planId} reached unsuccessful instead of completed");
                    case "cancelled":
                        // Never dispatched, or dispatched with a known absent effect: the executor's own no-effect rule.
                        if (!unresolved && (attempt == 0 || progress?["effect"].AsString() == "absent"))
                        {
                            // Incidental, but a sibling action may still be running.
                            incidental = true;
                            break;
                        }

                        throw new AcceptanceException($"plan {planId} reached cancelled instead of completed");
                    default:
                        terminal = false;
                        break;
                }
            }

            return (Wait.Signature([.. signature]), terminal);
        }, cancellationToken).ConfigureAwait(false);
        return (state, incidental);
    }
}
