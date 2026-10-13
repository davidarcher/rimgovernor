using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.HttpChannel;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The native transport over the mod's local HTTP/JSON channel (#3083). The channel binds an ephemeral loopback port
/// and announces it in <c>http-endpoint.json</c>, which this transport polls (see
/// <see cref="EndpointFile"/>). <c>POST /native/&lt;tool&gt;</c> carries the tool's ProtoJSON request (or, for a tool
/// that takes other arguments, a JSON object of them) and answers its reply body.
/// </summary>
public sealed class HttpNativeTransport : INativeTransport
{
    /// <summary>The longest a call may be asked to wait (Go <c>bridge.MaxCallTimeout</c>).</summary>
    public static readonly TimeSpan MaxCallTimeout = TimeSpan.FromMinutes(10);

    private const string ToolPrefix = "rimgovernor/";
    private const string RequestArgument = "request";

    private readonly string _endpointDirectory;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

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

    public async Task<NativeResult> CallAsync(string tool, JsonObject arguments, NativeCallOptions options, CancellationToken cancellationToken)
    {
        var path = RoutePath(tool);
        var requestForm = arguments.Count == 1 && arguments.TryGetPropertyValue(RequestArgument, out var request) && request is JsonValue value && value.TryGetValue<string>(out _);
        var body = requestForm ? arguments[RequestArgument]!.GetValue<string>() : arguments.ToJsonString();
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
            if (!string.IsNullOrEmpty(options.AdmissionClass))
            {
                message.Headers.TryAddWithoutValidation(NativeRoutes.ClassHeader, options.AdmissionClass);
            }

            if (!string.IsNullOrEmpty(options.Trace))
            {
                message.Headers.TryAddWithoutValidation(NativeRoutes.TraceHeader, options.Trace);
            }

            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var timing = response.Headers.TryGetValues(NativeRoutes.TimingHeader, out var timings) ? timings.FirstOrDefault() : null;
            return Interpret(tool, response.StatusCode, text, timing, requestForm);
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

    public Task<IReadOnlyList<string>?> ToolNamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The route of a tool name: <c>rimgovernor/x</c> is <c>/native/x</c>, any other name keeps its path under <c>/native/</c>.</summary>
    internal static string RoutePath(string tool)
    {
        if (tool.Length == 0)
        {
            throw new NativeTransportException("native transport failure: empty tool name");
        }

        return NativeRoutes.Prefix + (tool.StartsWith(ToolPrefix, StringComparison.Ordinal) ? tool[ToolPrefix.Length..] : tool);
    }

    /// <summary>Turns one HTTP reply into the transport's result, or the failure it reports.</summary>
    internal static NativeResult Interpret(string tool, HttpStatusCode status, string text, string? timing, bool requestForm)
    {
        if (status == HttpStatusCode.OK)
        {
            JsonObject? structured;
            if (requestForm)
            {
                structured = new JsonObject { ["payload"] = text };
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

            var envelope = new JsonObject
            {
                ["isError"] = false,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["structuredContent"] = structured?.DeepClone(),
            };
            return new NativeResult(envelope, structured, timing);
        }

        var (code, detail) = ReadFailure(text);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw new NativeTransportException($"native transport failure: {tool}: the channel does not route this tool ({(int)status} {code}: {detail})");
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            throw new NativeTransportException($"native transport failure: {tool}: {code}: {detail}");
        }

        var refusal = new JsonObject { ["reason"] = detail, ["code"] = code, ["status"] = (int)status };
        var failed = new NativeResult(
            new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["structuredContent"] = refusal,
            },
            refusal,
            timing);
        throw new NativeRefusedException(tool, Refusals.Cause(refusal), failed);
    }

    private static (string Code, string Detail) ReadFailure(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject failure)
            {
                return (failure.Member("code").AsString(), failure.Member("detail").AsString());
            }
        }
        catch (JsonException)
        {
            // A non-JSON failure body is reported as it is.
        }

        return ("", text.Trim());
    }

    private async Task<string> ResolveAddressAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
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

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Lifts a refusal's own account out of a structured result (Go <c>refusalCause</c>).</summary>
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

        foreach (var key in new[] { "reason", "exception", "error", "message" })
        {
            var line = FirstLine(structured.Member(key).AsString());
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
