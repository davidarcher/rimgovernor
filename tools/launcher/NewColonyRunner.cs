using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Launcher;

/// <summary>What the generation flow needs of the launcher app.</summary>
public interface INewColonyHost
{
    bool Observe { get; }

    /// <summary>The saves folder the launcher lists.</summary>
    string SavesDir { get; }

    /// <summary>
    /// The base address of the running game when New colony can reuse it: this launcher started it with the mod list,
    /// package, arguments and host environment a start now would use, and its channel answers. Null, after logging why,
    /// when nothing runs or a restart is needed.
    /// </summary>
    Task<string?> ReusableGameAsync(CancellationToken ct);

    /// <summary>Closes the game.</summary>
    Task CloseGameAsync();

    /// <summary>Starts the game afresh (no load) and returns the base address of its HTTP channel once it answers, or throws why it did not.</summary>
    Task<string> StartGameAsync(CancellationToken ct);

    void SaveSpec(NewColonySpec spec);

    void Log(string line);
}

/// <summary>
/// The launcher's orchestration of <c>POST /api/lifecycle/new</c> against the game's in-process brain (Go
/// <c>colonyRunner</c>). One generation at a time. Native generates only from a fresh main menu: a running game the
/// host can reuse returns there (<c>/api/lifecycle/menu</c>), any other is closed and started afresh. Then it posts
/// the spec and polls the request until native saves the colony. The page holds no labels, ordering or ranges: it
/// renders <see cref="View"/> and calls <see cref="Generate"/> and <see cref="CancelAsync"/>.
/// </summary>
public sealed class NewColonyRunner(INewColonyHost host, HttpClient http, NewColonySpec spec)
{
    public const string ObserveNotice = "generating a colony needs control: turn off Observe mode";

    private const string NewPath = "/api/lifecycle/new";

    private const string MenuPath = "/api/lifecycle/menu";

    private readonly Lock _mu = new();
    private NewColonySpec _spec = spec;
    private NewColonyProgress _progress = new();
    private DateTimeOffset _started;
    private DateTimeOffset _ended;
    private CancellationTokenSource? _cancel;
    private bool _closeOnCancel;

    /// <summary>The wait between requests.</summary>
    internal TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounds the generation once the game answers; it is also the wire's timeoutMs.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(NewColonyBounds.MaxTimeoutMs);

    /// <summary>Bounds the return to the main menu, retries of a busy game included (the harness's own menu ceiling).</summary>
    internal TimeSpan MenuTimeout { get; init; } = TimeSpan.FromSeconds(90);

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    private bool Active =>
        _progress.State is ColonyState.Returning or ColonyState.Restarting or ColonyState.Connecting or ColonyState.Generating;

    /// <summary>The page's snapshot.</summary>
    public NewColonyView View()
    {
        lock (_mu)
        {
            var progress = _progress;
            if (progress.State != ColonyState.Idle)
            {
                var end = Active ? Now() : _ended;
                progress = progress with { ElapsedMs = (long)(end - _started).TotalMilliseconds };
            }

            return new NewColonyView(
                NewColony.Options, NewColony.Choices, NewColony.PartDefaults, NewColony.Presets, NewColony.Ranges,
                NewColonySpec.Default, _spec, progress)
            {
                Unavailable = host.Observe ? ObserveNotice : null,
            };
        }
    }

    /// <summary>
    /// Validates <paramref name="form"/>, then (in the background) restarts the game and generates the colony. Throws a
    /// <see cref="LauncherException"/> refusal before anything is stopped.
    /// </summary>
    public void Generate(NewColonySpec form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (host.Observe)
        {
            throw new LauncherException(ObserveNotice);
        }

        var seed = form.Seed.Trim();
        var chosen = form with { Seed = seed };
        if (seed.Length == 0)
        {
            seed = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        }

        chosen = chosen with { SaveName = NewColony.DeriveSaveName(chosen, seed, Settings.ListSaves(host.SavesDir)) };
        NewColony.WireSpec(chosen.SaveName, chosen, seed);
        CancellationTokenSource cts;
        string id;
        lock (_mu)
        {
            if (Active)
            {
                throw new LauncherException("a colony is already being generated");
            }

            cts = new CancellationTokenSource();
            id = "launcher-new-" + Now().ToUnixTimeMilliseconds().ToString("x", CultureInfo.InvariantCulture);
            _cancel = cts;
            _closeOnCancel = false;
            _spec = chosen;
            _started = Now();
            _progress = new NewColonyProgress { State = ColonyState.Restarting, RequestId = id, Seed = seed, SaveName = chosen.SaveName };
        }

        try
        {
            host.SaveSpec(chosen);
        }
        catch (LauncherException e)
        {
            host.Log($"new colony: save the spec: {e.Message}");
        }

        host.Log($"new colony: generating {chosen.SaveName} (scenario {chosen.Scenario}, seed {seed}, request {id})");
        _ = Task.Run(() => RunAsync(chosen, seed, id, cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Reports cancelled. Once the game restarts or generates it is ended (native has no cancel op); before that,
    /// while a reused game only returns to the main menu, it keeps running. A no-op when nothing is generating.
    /// </summary>
    public async Task CancelAsync()
    {
        CancellationTokenSource? cancel;
        bool close;
        lock (_mu)
        {
            if (!Active)
            {
                return;
            }

            close = _closeOnCancel;
            _progress = _progress with
            {
                State = ColonyState.Cancelled,
                Error = close ? "The game was closed." : "The game was left running.",
            };
            _ended = Now();
            cancel = _cancel;
        }

        await cancel!.CancelAsync().ConfigureAwait(false);
        if (!close)
        {
            host.Log("new colony: cancelled before generating; the game keeps running");
            return;
        }

        host.Log("new colony: cancelled; closing the game");
        await host.CloseGameAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies <paramref name="change"/> unless the run already ended (Cancel wins); <paramref name="closeOnCancel"/>
    /// marks the point from which a cancel must end the game.
    /// </summary>
    private bool Update(Func<NewColonyProgress, NewColonyProgress> change, bool closeOnCancel = false)
    {
        lock (_mu)
        {
            if (!Active)
            {
                return false;
            }

            _progress = change(_progress);
            _closeOnCancel |= closeOnCancel;
            return true;
        }
    }

    private void Fail(string message)
    {
        if (Update(p => p with { State = ColonyState.Failed, Error = message, Stale = false }))
        {
            lock (_mu)
            {
                _ended = Now();
            }

            host.Log($"new colony: failed: {message}");
        }
    }

    private async Task RunAsync(NewColonySpec chosen, string seed, string id, CancellationToken ct)
    {
        try
        {
            await GenerateAsync(chosen, seed, id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or IOException or InvalidOperationException)
        {
            Fail(e.Message);
        }
    }

    private async Task GenerateAsync(NewColonySpec chosen, string seed, string id, CancellationToken ct)
    {
        var trace = new LaunchTrace("launcher");
        string? baseUrl;
        if (await host.ReusableGameAsync(ct).ConfigureAwait(false) is { } running)
        {
            using (trace.Time("launch.to_main_menu"))
            {
                baseUrl = await ReturnToMenuAsync(running.TrimEnd('/'), id, ct).ConfigureAwait(false);
            }
        }
        else
        {
            baseUrl = await RestartAsync(ct).ConfigureAwait(false);
        }

        // From the new colony post on, only ending the game stops native.
        if (baseUrl is null || !Update(p => p with { State = ColonyState.Connecting, Stale = false, StaleError = null, Detail = null }, closeOnCancel: true))
        {
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new NewColonyRequestBody(id, (uint)Timeout.TotalMilliseconds, chosen.ForWire(seed)), LauncherJson.Compact);
        var accepted = false;
        var generating = trace.Time("start.generate");
        await RepeatAsync(
            async token =>
            {
                var (status, data) = await SendAsync(baseUrl + NewPath, id, body, accepted, token).ConfigureAwait(false);
                if (!Succeeded(status))
                {
                    return Retry(status, data, accepted, "");
                }

                if (Decode<Reply>(data) is not { } reply)
                {
                    Uncertain($"undecodable reply (HTTP {(int)status})");
                    return true;
                }

                if (reply.Status == "completed")
                {
                    Update(p => p with
                    {
                        State = ColonyState.Completed, Stale = false, StaleError = null, SaveName = reply.SaveName,
                        Seed = string.IsNullOrEmpty(reply.Seed) ? p.Seed : reply.Seed, Phase = null, Detail = null,
                    });
                    lock (_mu)
                    {
                        _ended = Now();
                    }

                    host.Log($"new colony: {reply.SaveName} saved (seed {reply.Seed})");
                    return false;
                }

                accepted = true;
                Update(p => p with
                {
                    State = ColonyState.Generating, Stale = false, StaleError = null, Phase = reply.Phase, Detail = reply.Detail,
                    NativeElapsedMs = reply.ElapsedMs, RerollCount = reply.RerollCount,
                });
                return true;
            },
            Timeout,
            "Timed out waiting for the colony.",
            ct).ConfigureAwait(false);

        // The phases go to the game's brain like Play's launch phases; a drop is logged, never a failure.
        generating.Dispose();
        if (!ct.IsCancellationRequested && await trace.PostAsync(http, baseUrl, LaunchTrace.PostWait, CancellationToken.None).ConfigureAwait(false) is { } dropped)
        {
            host.Log("new colony: " + dropped);
        }
    }

    /// <summary>Closes the game and starts it afresh; its channel's address, or null when the run already ended.</summary>
    private async Task<string?> RestartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Update(p => p with { State = ColonyState.Restarting }, closeOnCancel: true))
        {
            return null;
        }

        await host.CloseGameAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            return (await host.StartGameAsync(ct).ConfigureAwait(false)).TrimEnd('/');
        }
        catch (LauncherException e)
        {
            throw new LauncherException("The game did not start: " + e.Message, e);
        }
    }

    /// <summary>
    /// Ends the reused game's loaded colony without saving (<c>/api/lifecycle/menu</c>) and waits for the fresh main
    /// menu new colony starts from. Returns <paramref name="baseUrl"/> once there, or null when the run ended (failed,
    /// timed out or cancelled). A busy game (a load or save queued) is asked again with the same request id.
    /// </summary>
    private async Task<string?> ReturnToMenuAsync(string baseUrl, string id, CancellationToken ct)
    {
        if (!Update(p => p with { State = ColonyState.Returning }))
        {
            return null;
        }

        host.Log("new colony: reusing the running game; returning it to the main menu");
        var menuId = id + "-menu";
        var body = JsonSerializer.SerializeToUtf8Bytes(new MenuRequestBody(menuId), LauncherJson.Compact);
        var accepted = false;
        var done = false;
        await RepeatAsync(
            async token =>
            {
                var (status, data) = await SendAsync(baseUrl + MenuPath, menuId, body, accepted, token).ConfigureAwait(false);
                if (!Succeeded(status))
                {
                    return Retry(status, data, accepted, "The game did not return to the main menu: ");
                }

                if (Decode<MenuReply>(data) is not { } reply)
                {
                    Uncertain($"undecodable reply (HTTP {(int)status})");
                    return true;
                }

                if (reply.Status == "completed")
                {
                    done = true;
                    host.Log(reply.Unloaded ? "new colony: the game is back at the main menu" : "new colony: the game was at the main menu");
                    return false;
                }

                accepted = true;
                Update(p => p with { Stale = false, StaleError = null, Detail = reply.Detail });
                return true;
            },
            MenuTimeout,
            "Timed out returning the game to the main menu.",
            ct).ConfigureAwait(false);
        return done ? baseUrl : null;
    }

    /// <summary>
    /// Runs <paramref name="step"/> (one request; false when the run is over) every <see cref="Poll"/> within
    /// <paramref name="budget"/>, keeping the last good progress across transport failures. Fails with
    /// <paramref name="timedOut"/> when the budget runs out; a cancel just ends it.
    /// </summary>
    private async Task RepeatAsync(Func<CancellationToken, Task<bool>> step, TimeSpan budget, string timedOut, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var token = deadline.Token;
        try
        {
            while (true)
            {
                try
                {
                    if (!await step(token).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (HttpRequestException e)
                {
                    Uncertain(e.Message); // an unresolved POST is retried with the same request id
                }

                await Task.Delay(Poll, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail(timedOut);
        }
    }

    /// <summary>The POST of <paramref name="body"/> to <paramref name="url"/>, or once <paramref name="accepted"/>, the GET of request <paramref name="id"/>.</summary>
    private async Task<(HttpStatusCode Status, byte[] Data)> SendAsync(string url, string id, byte[] body, bool accepted, CancellationToken ct)
    {
        using var request = accepted
            ? new HttpRequestMessage(HttpMethod.Get, url + "?requestId=" + Uri.EscapeDataString(id))
            : new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(body) };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    private static bool Succeeded(HttpStatusCode status) => status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted;

    /// <summary>
    /// A failure reply: a refusal (409, any other 4xx, a native 502) fails the run with <paramref name="prefix"/> and
    /// returns false; anything else (the routes not registered yet, a busy game's 503) is retried.
    /// </summary>
    private bool Retry(HttpStatusCode status, byte[] data, bool accepted, string prefix)
    {
        var failure = Decode<Failure>(data);
        var code = failure?.Code ?? "";
        if (status == HttpStatusCode.NotFound && code == "not_found" && !accepted)
        {
            // The channel answers from mod load, but the brain registers /api/lifecycle only after the Defs load.
            Uncertain("the game is still starting");
            return true;
        }

        var detail = string.IsNullOrEmpty(failure?.Detail) ? $"HTTP {(int)status}" : failure.Detail;
        if (status == HttpStatusCode.Conflict || (int)status is >= 400 and < 500
            || (status == HttpStatusCode.BadGateway && code.StartsWith("native_", StringComparison.Ordinal)))
        {
            Fail(prefix + detail);
            return false;
        }

        Uncertain(detail);
        return true;
    }

    private static T? Decode<T>(byte[] data)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(data, LauncherJson.Compact);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ByteArrayContent JsonBody(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Keeps the last good progress and marks it stale; the caller retries (a POST with the same request id, a GET again).</summary>
    private void Uncertain(string message) => Update(p => p with { Stale = true, StaleError = message });

    private sealed record NewColonyRequestBody(string RequestId, uint TimeoutMs, NewColonySpec Spec);

    /// <summary>The reply body of <c>/api/lifecycle/new</c>.</summary>
    private sealed record Reply(string Status, string? Phase, string? Detail, ulong ElapsedMs, uint RerollCount, string? SaveName, string? Seed);

    private sealed record MenuRequestBody(string RequestId);

    /// <summary>The reply body of <c>/api/lifecycle/menu</c>: pending with a detail, or completed.</summary>
    private sealed record MenuReply(string Status, string? Detail, bool Unloaded);

    private sealed record Failure(string? Code, string? Detail);
}
