using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.HttpChannel;

/// <summary>The settings of one channel server.</summary>
public sealed record HttpChannelOptions
{
    /// <summary>Where the endpoint file is written (beside the OTLP rings); null writes none (tests).</summary>
    public string? EndpointDirectory { get; init; }

    /// <summary>The run this server belongs to; recorded in the endpoint file.</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The largest reply body; a larger one is answered 500 <c>response_limit</c>.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>How many requests run at once; the rest wait in the listener's queue.</summary>
    public int MaxConcurrentRequests { get; init; } = 16;

    /// <summary>How long a client has to deliver its declared body.</summary>
    public TimeSpan BodyReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a stop waits for running requests.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The local HTTP/JSON control channel (issue #3083): an <see cref="HttpListener"/> on 127.0.0.1 and an ephemeral
/// port, announced through the endpoint file. It owns the listener loop, the access rows and the security headers;
/// everything else is in <see cref="RequestGuard"/>, <see cref="RouteTable"/> and the route modules registered on
/// <see cref="Routes"/>.
/// </summary>
public sealed class HttpChannelServer : IAsyncDisposable
{
    private const int BindAttempts = 8;

    /// <summary>A polled GET at or above this duration is still recorded.</summary>
    internal static readonly TimeSpan SlowAccess = TimeSpan.FromMilliseconds(250);

    private readonly HttpChannelOptions _options;
    private readonly TelemetryEmitter? _telemetry;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _inFlight = [];
    private HttpListener? _listener;
    private Task? _loop;

    public HttpChannelServer(HttpChannelOptions options, RouteTable routes, TelemetryEmitter? telemetry = null)
    {
        _options = options;
        _telemetry = telemetry;
        Routes = routes;
        _dispatcher = new Dispatcher(routes, options.MaxResponseBytes);
        _slots = new SemaphoreSlim(options.MaxConcurrentRequests);
    }

    public RouteTable Routes { get; }

    /// <summary>The bound address, <c>http://127.0.0.1:PORT</c>; null before <see cref="Start"/>.</summary>
    public string? Address { get; private set; }

    /// <summary>Binds an ephemeral loopback port, writes the endpoint file and starts serving.</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The channel is already started");
        }

        var listener = BindEphemeral(out var address);
        _listener = listener;
        Address = address;
        if (_options.EndpointDirectory is { } directory)
        {
            EndpointFile.Write(directory, address, Environment.ProcessId, _options.RunId);
        }

        _loop = Task.Run(() => AcceptLoopAsync(listener));
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not { } listener)
        {
            return;
        }

        _listener = null;
        if (_options.EndpointDirectory is { } directory)
        {
            EndpointFile.Remove(directory, _options.RunId);
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        listener.Close();
        if (_loop is { } loop)
        {
            await Task.WhenAny(loop, Task.Delay(_options.ShutdownTimeout)).ConfigureAwait(false);
        }

        Task[] pending;
        lock (_gate)
        {
            pending = [.. _inFlight];
        }

        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(_options.ShutdownTimeout)).ConfigureAwait(false);
        _stopping.Dispose();
        _slots.Dispose();
    }

    private static HttpListener BindEphemeral(out string address)
    {
        // HttpListener cannot bind port 0, so ask the OS for a free loopback port and bind it straight away. Another
        // process can take the port in between; try again.
        Exception? last = null;
        for (var attempt = 0; attempt < BindAttempts; attempt++)
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                address = $"http://127.0.0.1:{port}";
                return listener;
            }
            catch (HttpListenerException e)
            {
                last = e;
                listener.Close();
            }
        }

        throw new IOException("Could not bind a loopback port for the control channel", last);
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                await _slots.WaitAsync(_stopping.Token).ConfigureAwait(false);
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            var work = Task.Run(() => ServeAsync(context));
            lock (_gate)
            {
                _inFlight.Add(work);
            }

            _ = work.ContinueWith(
                finished =>
                {
                    lock (_gate)
                    {
                        _inFlight.Remove(finished);
                    }

                    _slots.Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var request = context.Request;
        var method = request.HttpMethod;
        var target = request.RawUrl ?? "/";
        ChannelResponse response;
        try
        {
            var head = new RequestHead
            {
                Method = method,
                Target = target,
                Host = request.Headers["Host"],
                Origin = request.Headers["Origin"],
                SecFetchSite = request.Headers["Sec-Fetch-Site"],
                ContentType = request.ContentType,
                ContentLength = request.ContentLength64,
                Chunked = request.Headers["Transfer-Encoding"] is { Length: > 0 },
            };
            response = await _dispatcher.DispatchAsync(head, Forwarded(request), (length, token) => ReadBodyAsync(request, length, token), _stopping.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            response = ChannelResponse.Failure(408, "read_timeout", "The request body did not arrive in time");
        }
        catch (EndOfStreamException)
        {
            response = ChannelResponse.Failure(400, "bad_request", "The request body was shorter than its Content-Length");
        }
        catch (OperationCanceledException)
        {
            response = ChannelResponse.Failure(503, "unavailable", "The channel is stopping");
        }
        catch (IOException)
        {
            response = ChannelResponse.Failure(400, "bad_request", "The request could not be read");
        }

        var path = target.Split('?', 2)[0];
        try
        {
            Write(context, method, response);
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The client went away; nothing to answer.
        }

        clock.Stop();
        RecordAccess(method, path, response.Status, started, clock.Elapsed, response.Body.Length);
    }

    private async Task<byte[]> ReadBodyAsync(HttpListenerRequest request, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.BodyReadTimeout);
        var read = 0;
        try
        {
            while (read < length)
            {
                var n = await request.InputStream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new EndOfStreamException();
                }

                read += n;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }

        return buffer;
    }

    private static Dictionary<string, string> Forwarded(HttpListenerRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Headers.AllKeys)
        {
            if (key is not null && key.StartsWith("X-RimGovernor-", StringComparison.OrdinalIgnoreCase)
                && request.Headers[key] is { } value)
            {
                headers[key] = value;
            }
        }

        return headers;
    }

    private static void Write(HttpListenerContext context, string method, ChannelResponse response)
    {
        var http = context.Response;
        http.StatusCode = response.Status;
        http.ContentType = response.ContentType;
        http.Headers["Cache-Control"] = "no-store";
        http.Headers["X-Content-Type-Options"] = "nosniff";
        http.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        foreach (var (name, value) in response.Headers)
        {
            http.Headers[name] = value;
        }

        http.ContentLength64 = response.Body.Length;
        if (method != "HEAD" && response.Body.Length > 0)
        {
            http.OutputStream.Write(response.Body);
        }

        http.Close();
    }

    /// <summary>Whether a finished request is recorded: errors, slow requests and every non-GET are; polled GETs that succeeded fast are not.</summary>
    internal static bool KeepAccess(string method, string path, int status, TimeSpan duration)
    {
        if (!IsPolled(method, path))
        {
            return true;
        }

        return status is < 200 or > 299 || duration >= SlowAccess;
    }

    private static bool IsPolled(string method, string path) =>
        method == "GET"
        && (path is "/api/state" or "/api/routines" or "/api/health" || path.StartsWith("/api/presentation/", StringComparison.Ordinal));

    private void RecordAccess(string method, string path, int status, DateTimeOffset started, TimeSpan duration, long bytes)
    {
        if (_telemetry is null || !KeepAccess(method, path, status, duration))
        {
            return;
        }

        _telemetry.EmitRow(
            "http_access",
            new RowContext { Level = status >= 500 ? TelemetryLevel.Warn : TelemetryLevel.Info, At = started, Component = "httpapi" },
            new Dictionary<string, object?>
            {
                ["method"] = method,
                ["path"] = path,
                ["status"] = status,
                ["dur_ms"] = duration.TotalMilliseconds,
                ["bytes"] = bytes,
                ["long_lived"] = false,
            });
    }
}
