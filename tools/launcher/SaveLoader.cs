using System.Net;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Launcher;

/// <summary>
/// Loads a save through the game's lifecycle channel (<c>/api/lifecycle/load</c>; Go <c>ReloadSave</c> and
/// <c>loadSlot</c>). Two loads on one game each carry their own request id and native answers a load with a different
/// id by superseding the first and loading again, so only one load runs at a time: starting the next cancels the
/// last, or a stale retry loop would reload the save for as long as it ran.
/// </summary>
public sealed class SaveLoader : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _client;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    public SaveLoader(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Asks the channel at <paramref name="baseUrl"/> to load <paramref name="save"/>, retrying (same request id) while
    /// the game is not ready yet, until <paramref name="limit"/> passes. Once the channel accepts the load (202) it
    /// polls that request instead of posting it again. Cancels the load in flight first.
    /// </summary>
    public async Task LoadAsync(string baseUrl, string save, TimeSpan limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(save);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.CancelAfter(limit);
        lock (_gate)
        {
            _current?.Cancel();
            _current = run;
        }

        try
        {
            await ReloadAsync(_client, baseUrl.TrimEnd('/'), save, run.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, run))
                {
                    _current = null;
                }
            }
        }
    }

    /// <summary>Cancels the load in flight, if any.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
        }
    }

    public void Dispose() => Stop();

    private static async Task ReloadAsync(HttpClient client, string baseUrl, string save, CancellationToken ct)
    {
        var requestId = "launcher-reload-" + Convert.ToString(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 36);
        var body = JsonSerializer.Serialize(new { requestId, saveName = save, readiness = "map", timeoutMs = 30000 });
        Exception last = new InvalidOperationException("no reply");
        var accepted = false;
        while (true)
        {
            try
            {
                var status = accepted
                    ? await ReadOnceAsync(client, baseUrl, requestId, ct).ConfigureAwait(false)
                    : await PostOnceAsync(client, baseUrl, body, ct).ConfigureAwait(false);
                if (status == HttpStatusCode.Created || (accepted && status == HttpStatusCode.OK))
                {
                    return;
                }

                if (status == HttpStatusCode.Accepted)
                {
                    accepted = true;
                    last = new InvalidOperationException("load still in progress");
                }
                else if ((int)status < 500)
                {
                    throw new LauncherException($"the game refused the load (HTTP {(int)status})");
                }
                else
                {
                    last = new InvalidOperationException($"HTTP {(int)status}");
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = e;
            }
            catch (OperationCanceledException)
            {
                throw new LauncherException($"load {save} did not complete: {last.Message}");
            }

            try
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new LauncherException($"load {save} did not complete: {last.Message}");
            }
        }
    }

    private static async Task<HttpStatusCode> ReadOnceAsync(HttpClient client, string baseUrl, string requestId, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(baseUrl + "/api/lifecycle/load?requestId=" + Uri.EscapeDataString(requestId)), ct).ConfigureAwait(false);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> PostOnceAsync(HttpClient client, string baseUrl, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(baseUrl + "/api/lifecycle/load"), content, ct).ConfigureAwait(false);
        return response.StatusCode;
    }
}
