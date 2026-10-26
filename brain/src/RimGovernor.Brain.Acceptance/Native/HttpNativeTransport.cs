using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.HttpChannel;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The native transport over the mod's local HTTP/JSON channel (#3083). The channel binds an ephemeral loopback port
/// and announces it in <c>http-endpoint.json</c>, which this transport polls (see
/// <see cref="EndpointFile"/>). <c>POST /native/&lt;op&gt;</c> carries the op's JSON request record (or, for a host or
/// fixture tool, a JSON object of its arguments) and answers its reply body; a body the op cannot read is a 400.
/// </summary>
public sealed class HttpNativeTransport : IAsyncDisposable
{
    /// <summary>The longest a call may be asked to wait.</summary>
    public static readonly TimeSpan MaxCallTimeout = TimeSpan.FromMinutes(10);

    private const string RequestArgument = "request";

    private readonly string _endpointDirectory;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private int _failures;
    private string? _lost;

    /// <param name="endpointDirectory">The directory holding <c>http-endpoint.json</c> (beside the OTLP rings).</param>
    /// <param name="timeout">The default deadline of one call, including the wait for the endpoint file.</param>
    /// <param name="client">An HTTP client to use, for tests; null makes one.</param>
    public HttpNativeTransport(string endpointDirectory, TimeSpan timeout, HttpClient? client = null)
    {
        _endpointDirectory = endpointDirectory;
        _timeout = timeout;
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The directory the endpoint file is read from.</summary>
    public string EndpointDirectory => _endpointDirectory;

    /// <summary>
    /// How many transport failures in a row mean the game is not coming back (a call's own deadline is a minute, so the
    /// second failure is a bounded two minutes into a lost run, not the run's wall budget).
    /// </summary>
    public const int LostAfterFailures = 2;

    /// <summary>
    /// Returns why the game process is gone (its exit code and log tail), null while it runs or when nothing records
    /// it. Consulted before every call and after every transport failure, so a dead game ends the run at its next call.
    /// </summary>
    public Func<string?>? Gone { get; init; }

    /// <summary>
    /// Runs <paramref name="tool"/> (a name such as <c>observations_read_status</c>) with its JSON
    /// arguments. A native op is called with an object holding only the string member <c>request</c> (its JSON
    /// request record), and its reply's structured content is <c>{"payload": "&lt;JSON reply record&gt;"}</c>.
    /// </summary>
    /// <exception cref="NativeRefusedException">The game refused the call.</exception>
    /// <exception cref="NativeFailureException">A native op refused (a 422 from <see cref="NativeRoutes"/>).</exception>
    /// <exception cref="BridgeUnavailableException">A native op found the game unavailable (a 503 from <see cref="NativeRoutes"/>).</exception>
    /// <exception cref="NativeTransportException">The call did not reach the game or the channel does not route the tool.</exception>
    public async Task<NativeResult> CallAsync(string tool, JsonObject arguments, NativeCallOptions options, CancellationToken cancellationToken)
    {
        if ((Volatile.Read(ref _lost) ?? Gone?.Invoke()) is { } gone)
        {
            throw Lose(gone);
        }

        try
        {
            var result = await SendAsync(tool, arguments, options, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _failures, 0);
            return result;
        }
        catch (Exception e) when (e is NativeRefusedException or NativeFailureException or BridgeUnavailableException)
        {
            // The game answered.
            Interlocked.Exchange(ref _failures, 0);
            throw;
        }
        catch (NativeToolNotRoutedException)
        {
            // The game answered, without this tool.
            Interlocked.Exchange(ref _failures, 0);
            throw;
        }
        catch (NativeTransportException e)
        {
            if (Gone?.Invoke() is { } gone2)
            {
                throw Lose(gone2);
            }

            var failures = Interlocked.Increment(ref _failures);
            if (failures >= LostAfterFailures)
            {
                throw Lose($"native unreachable: {failures} consecutive transport failures, the last: {e.Message}");
            }

            throw;
        }
    }

    /// <summary>
    /// Declares the run lost: this and every later call fail at once with the first reason given, so the calls a failing
    /// run still makes (its report reads, the kept game's close) do not each wait out their deadline.
    /// </summary>
    public RunLostException Lose(string reason)
    {
        return new RunLostException(Interlocked.CompareExchange(ref _lost, reason, null) ?? reason);
    }

    /// <summary>
    /// One exchange with the channel, outside the lost-run accounting: only the connect probe, which expects a booting
    /// game to miss replies, calls it directly.
    /// </summary>
    internal async Task<NativeResult> SendAsync(string tool, JsonObject arguments, NativeCallOptions options, CancellationToken cancellationToken)
    {
        var path = RoutePath(tool);
        string? requestText = null;
        var requestForm = arguments.Count == 1 && arguments.TryGetPropertyValue(RequestArgument, out var request) && request is JsonValue value && value.TryGetValue(out requestText);
        var body = requestForm ? requestText! : arguments.ToJsonString();
        var deadline = options.Timeout is { } asked && asked > _timeout ? (asked > MaxCallTimeout ? MaxCallTimeout : asked) : _timeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        try
        {
            var address = await ResolveAddressAsync(timeout.Token).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, address + path)
            {
                Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
            };
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Interpret(tool, response.StatusCode, text, requestForm);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NativeTransportException($"native transport failure: {tool}: no reply within {deadline.TotalSeconds:0.#}s");
        }
        catch (HttpRequestException e)
        {
            throw new NativeTransportException($"native transport failure: {tool}: {e.Message}", e);
        }
    }

    /// <summary>
    /// POSTs <paramref name="body"/> to the in-process brain's player route <paramref name="path"/> (an <c>/api/</c> path
    /// on the same channel) and returns the reply's status and text, whatever the status. Outside the lost-run accounting:
    /// the caller reads the status.
    /// </summary>
    internal async Task<(HttpStatusCode Status, string Text)> PostRouteAsync(string path, string body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var address = await ResolveAddressAsync(timeout.Token).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, address + path)
            {
                Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
            };
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NativeTransportException($"route transport failure: POST {path}: no reply within {_timeout.TotalSeconds:0.#}s");
        }
        catch (HttpRequestException e)
        {
            throw new NativeTransportException($"route transport failure: POST {path}: {e.Message}", e);
        }
    }

    /// <summary>
    /// The operations the channel routes right now (<c>GET /api/tools</c>), with their exact registered names.
    /// Null when the channel cannot say,
    /// so callers assume a tool is present.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ToolNamesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _lost) is not null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var address = await ResolveAddressAsync(timeout.Token).ConfigureAwait(false);
            using var response = await _client.GetAsync(address + "/api/tools", timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (JsonSerializer.Deserialize(text, ChannelJsonContext.Default.ChannelTools) is not { Tools: { } tools })
            {
                return null;
            }

            return [.. tools.Select(t => t ?? "").Where(t => t.Length > 0)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NativeTransportException or InvalidOperationException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The registered operation's route under <c>/native/</c>.</summary>
    internal static string RoutePath(string tool)
    {
        if (tool.Length == 0)
        {
            throw new NativeTransportException("native transport failure: empty tool name");
        }

        return NativeRoutes.Prefix + tool;
    }

    /// <summary>Turns one HTTP reply into the transport's result, or the failure it reports.</summary>
    internal static NativeResult Interpret(string tool, HttpStatusCode status, string text, bool requestForm)
    {
        if (status == HttpStatusCode.OK)
        {
            JsonObject? structured;
            if (requestForm)
            {
                structured = new JsonObject { { "payload", text } };
            }
            else
            {
                try
                {
                    structured = JsonNode.Parse(text) as JsonObject;
                }
                catch (JsonException e)
                {
                    throw new NativeTransportException($"native transport failure: {tool}: the reply was not JSON: {e.Message}", e);
                }
            }

            return new NativeResult(Envelope(false, text, structured?.DeepClone()), structured);
        }

        var (code, detail) = ReadFailure(text);
        if ((int)status == NativeRoutes.RefusedStatus && TryName<RimGovernor.Brain.GameFacts.Common.FailureCode>(code, out var failureCode))
        {
            throw NativeFaults.Refused(new RimGovernor.Brain.GameFacts.Common.Failure { Code = failureCode, RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Detail = detail });
        }

        if ((int)status == NativeRoutes.UnavailableStatus && TryName<RimGovernor.Brain.GameFacts.Common.UnavailableReason>(code, out var reason))
        {
            throw reason == RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded
                ? new NotLoadedException(detail)
                : new NativeUnavailableException(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = reason, Detail = detail }, RimGovernor.Brain.GameAccess.Result.Empty);
        }

        if (status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw new NativeToolNotRoutedException($"native transport failure: {tool}: the channel does not route this tool ({(int)status} {code}: {detail})");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            throw new NativeTransportException($"native transport failure: {tool}: {code}: {detail}");
        }

        var refusal = new JsonObject { { "reason", detail }, { "code", code }, { "status", (int)status } };
        var failed = new NativeResult(Envelope(true, text, refusal), refusal);
        throw new NativeRefusedException(tool, Refusals.Cause(refusal), failed);
    }

    /// <summary>Reads an enum member by its exact C# name (the code <see cref="NativeRoutes"/> writes), never a number.</summary>
    private static bool TryName<T>(string code, out T value) where T : struct, Enum =>
        Enum.TryParse(code, ignoreCase: false, out value) && string.Equals(value.ToString(), code, StringComparison.Ordinal);

    /// <summary>The tool-call envelope of one reply: its error flag, the raw text as content and the structured body.</summary>
    private static JsonObject Envelope(bool isError, string text, JsonNode? structured) => new()
    {
        { "isError", isError },
        { "content", new JsonArray(new JsonObject { { "type", "text" }, { "text", text } }) },
        { "structuredContent", structured },
    };

    private static (string Code, string Detail) ReadFailure(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject failure && failure.Deserialize(ChannelJsonContext.Default.ChannelFailure) is { } read)
            {
                return (ChannelJson.Text(read.Code), ChannelJson.Text(read.Detail));
            }
        }
        catch (JsonException)
        {
            // A non-JSON failure body is reported as it is.
            return ("", text.Trim());
        }

        return ("", text.Trim());
    }

    private async Task<string> ResolveAddressAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var pace = Wait.Pace(TimeSpan.FromMilliseconds(100));
        while (true)
        {
            if (EndpointFile.Read(_endpointDirectory) is { } endpoint && endpoint.Address.Length > 0)
            {
                return endpoint.Address.TrimEnd('/');
            }

            if (stopwatch.Elapsed > _timeout)
            {
                throw new NativeTransportException($"native transport failure: no {EndpointFile.FileName} appeared in {_endpointDirectory}: the game has not started its channel");
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Lifts a refusal's own account out of a structured result.</summary>
internal static class Refusals
{
    /// <summary>The longest a lifted cause is: a native exception carries its whole stack, and the first line names the failure.</summary>
    internal const int CauseBytes = 240;

    private static readonly string[] LineSeparators = ["\r", "\n", "/r/n", "/n", "/r"];

    /// <summary>The first non-empty of the structured result's <c>reason</c>, <c>exception</c>, <c>error</c> and <c>message</c>, first line only.</summary>
    internal static string Cause(JsonObject? structured)
    {
        if (structured is null)
        {
            return "";
        }

        foreach (var candidate in structured.Deserialize(ChannelJsonContext.Default.RefusalAccount)?.Candidates() ?? [])
        {
            var line = FirstLine(candidate);
            if (line.Length > 0)
            {
                return line;
            }
        }

        return "";
    }

    /// <summary>
    /// The candidate's first line, bounded. The native payload escapes its backslashes to forward slashes, so a
    /// managed stack trace arrives with literal <c>/r/n</c> separators as well as real newlines; both end the line.
    /// </summary>
    internal static string FirstLine(string candidate)
    {
        var line = candidate;
        foreach (var separator in LineSeparators)
        {
            var at = line.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0)
            {
                line = line[..at];
            }
        }

        line = line.Trim();
        if (Encoding.UTF8.GetByteCount(line) > CauseBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            line = Encoding.UTF8.GetString(bytes, 0, CauseBytes).TrimEnd('�').Trim() + "...";
        }

        return line;
    }
}
