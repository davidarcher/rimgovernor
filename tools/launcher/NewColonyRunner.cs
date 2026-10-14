using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Launcher;

/// <summary>What the generation flow needs of the launcher app.</summary>
public interface INewColonyHost
{
    bool Observe { get; }

    /// <summary>The saves folder the launcher lists.</summary>
    string SavesDir { get; }

    /// <summary>Closes the game.</summary>
    Task CloseGameAsync();

    /// <summary>Starts the game afresh (no load) and returns the base address of its HTTP channel once it answers, or throws why it did not.</summary>
    Task<string> StartGameAsync(CancellationToken ct);

    void SaveSpec(NewColonySpec spec);

    void Log(string line);
}

/// <summary>
/// The launcher's orchestration of <c>POST /api/lifecycle/new</c> against the game's in-process brain (Go
/// <c>colonyRunner</c>). One generation at a time: close the game, start it afresh, post the spec, poll the request
/// until native saves the colony. The page holds no labels, ordering or ranges: it renders <see cref="View"/> and calls
/// <see cref="Generate"/> and <see cref="CancelAsync"/>.
/// </summary>
public sealed class NewColonyRunner(INewColonyHost host, HttpClient http, NewColonySpec spec)
{
    public const string ObserveNotice = "generating a colony needs control: turn off Observe mode";

    private readonly Lock _mu = new();
    private NewColonySpec _spec = spec;
    private NewColonyProgress _progress = new();
    private DateTimeOffset _started;
    private DateTimeOffset _ended;
    private CancellationTokenSource? _cancel;

    /// <summary>The wait between requests.</summary>
    internal TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Bounds the generation once the game answers; it is also the wire's timeoutMs.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(NewColonyBounds.MaxTimeoutMs);

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    private bool Active =>
        _progress.State is ColonyState.Restarting or ColonyState.Connecting or ColonyState.Generating;

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

    /// <summary>Ends the game (native has no cancel op) and reports cancelled. A no-op when nothing is generating.</summary>
    public async Task CancelAsync()
    {
        CancellationTokenSource? cancel;
        lock (_mu)
        {
            if (!Active)
            {
                return;
            }

            _progress = _progress with { State = ColonyState.Cancelled, Error = null };
            _ended = Now();
            cancel = _cancel;
        }

        await cancel!.CancelAsync().ConfigureAwait(false);
        host.Log("new colony: cancelled; closing the game");
        await host.CloseGameAsync().ConfigureAwait(false);
    }

    /// <summary>Applies <paramref name="change"/> unless the run already ended (Cancel wins).</summary>
    private bool Update(Func<NewColonyProgress, NewColonyProgress> change)
    {
        lock (_mu)
        {
            if (!Active)
            {
                return false;
            }

            _progress = change(_progress);
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
        await host.CloseGameAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        string baseUrl;
        try
        {
            baseUrl = (await host.StartGameAsync(ct).ConfigureAwait(false)).TrimEnd('/');
        }
        catch (LauncherException e)
        {
            throw new LauncherException("The game did not start: " + e.Message, e);
        }

        if (!Update(p => p with { State = ColonyState.Connecting }))
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        var token = deadline.Token;
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new NewColonyRequestBody(id, (uint)Timeout.TotalMilliseconds, chosen.ForWire(seed)), LauncherJson.Compact);
        var posted = false;
        while (true)
        {
            try
            {
                if (!await StepAsync(baseUrl, id, body, posted, token).ConfigureAwait(false))
                {
                    return;
                }

                posted = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Fail("Timed out waiting for the colony.");
                return;
            }
            catch (HttpRequestException e)
            {
                Uncertain(e.Message); // an unresolved POST is retried with the same request id
            }

            try
            {
                await Task.Delay(Poll, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Fail("Timed out waiting for the colony.");
                return;
            }
        }
    }

    /// <summary>One request. Returns false when the run is over; throws on a transport failure.</summary>
    private async Task<bool> StepAsync(string baseUrl, string id, byte[] body, bool posted, CancellationToken ct)
    {
        using var request = posted
            ? new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/lifecycle/new?requestId=" + Uri.EscapeDataString(id))
            : new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/lifecycle/new") { Content = JsonBody(body) };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var status = response.StatusCode;
        if (status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)
        {
            Reply? reply;
            try
            {
                reply = JsonSerializer.Deserialize<Reply>(data, LauncherJson.Compact);
            }
            catch (JsonException)
            {
                reply = null;
            }

            if (reply is null)
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

            Update(p => p with
            {
                State = ColonyState.Generating, Stale = false, StaleError = null, Phase = reply.Phase, Detail = reply.Detail,
                NativeElapsedMs = reply.ElapsedMs, RerollCount = reply.RerollCount,
            });
            return true;
        }

        Failure? failure = null;
        try
        {
            failure = JsonSerializer.Deserialize<Failure>(data, LauncherJson.Compact);
        }
        catch (JsonException)
        {
        }

        var code = failure?.Code ?? "";
        var code4xx = (int)status is >= 400 and < 500;
        if (status == HttpStatusCode.NotFound && code == "not_found" && !posted)
        {
            // The channel answers from mod load, but the brain registers /api/lifecycle only after the Defs load.
            throw new HttpRequestException("the game is still starting");
        }

        var terminal = status == HttpStatusCode.Conflict || code4xx
            || (status == HttpStatusCode.BadGateway && code.StartsWith("native_", StringComparison.Ordinal));
        if (terminal)
        {
            Fail(string.IsNullOrEmpty(failure?.Detail) ? $"HTTP {(int)status}" : failure.Detail);
            return false;
        }

        Uncertain($"HTTP {(int)status}");
        return true;
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

    private sealed record Failure(string? Code, string? Detail);
}
