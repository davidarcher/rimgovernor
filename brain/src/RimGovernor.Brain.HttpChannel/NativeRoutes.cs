using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.HttpChannel;

/// <summary>One native tool reply: the ProtoJSON payload and, when the tool reported one, its timing.</summary>
public sealed record NativeToolReply(string Payload, string? Timing);

/// <summary>
/// What <c>POST /native/&lt;tool&gt;</c> calls. The mod implements it over its own tools (the same ones GABP
/// dispatches), so the HTTP route and the GABP route run one implementation with the same admission and
/// main-thread rules. The channel itself knows no tool.
/// </summary>
public interface INativeToolInvoker
{
    /// <summary>The tool names a route exists for, without the <c>rimgovernor/</c> prefix.</summary>
    IReadOnlyCollection<string> ToolNames { get; }

    /// <summary>
    /// Runs one tool with a ProtoJSON request body. <paramref name="admissionClass"/> and <paramref name="trace"/>
    /// are the optional class and trace arguments of the GABP call. A tool failure is thrown.
    /// </summary>
    Task<NativeToolReply> InvokeAsync(string tool, string requestJson, string? admissionClass, string? trace, CancellationToken cancellationToken);
}

/// <summary>The channel's own routes: <c>POST /native/&lt;tool&gt;</c> and <c>GET /api/health</c>.</summary>
public static class NativeRoutes
{
    public const string Prefix = "/native/";

    /// <summary>The largest ProtoJSON request body (a new-colony design with all its parts is far below this).</summary>
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    /// <summary>The longest a native call may run. Lifecycle calls (load, new colony) legitimately take minutes.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

    public const string TimingHeader = "X-RimGovernor-Timing";

    public const string ClassHeader = "X-RimGovernor-Class";

    public const string TraceHeader = "X-RimGovernor-Trace";

    public static IDisposable Register(RouteTable routes, INativeToolInvoker invoker)
    {
        return routes.MapPrefix(
            "POST",
            Prefix,
            (request, ct) => HandleAsync(invoker, request, ct),
            new RouteOptions { MaxBodyBytes = MaxRequestBytes, Timeout = CallTimeout });
    }

    /// <summary>Registers <c>GET /api/health</c>: the channel is up and which process answers.</summary>
    public static IDisposable RegisterHealth(RouteTable routes)
    {
        return routes.Map(
            "GET",
            "/api/health",
            (_, _) =>
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("service", "rimgovernor");
                    writer.WriteString("backend", "csharp");
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writer.WriteEndObject();
                }

                return Task.FromResult(ChannelResponse.Json(200, Encoding.UTF8.GetString(stream.ToArray())));
            });
    }

    private static async Task<ChannelResponse> HandleAsync(INativeToolInvoker invoker, ChannelRequest request, CancellationToken cancellationToken)
    {
        var tool = request.Path[Prefix.Length..];
        if (!IsToolName(tool) || !invoker.ToolNames.Contains(tool))
        {
            return ChannelResponse.Failure(404, "not_found", "No such native tool");
        }

        var body = request.Body.Length == 0 ? "{}" : DecodeUtf8(request.Body);
        request.Headers.TryGetValue(ClassHeader, out var admissionClass);
        request.Headers.TryGetValue(TraceHeader, out var trace);
        NativeToolReply reply;
        try
        {
            reply = await invoker.InvokeAsync(tool, body, admissionClass, trace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The tool's own failure text goes back to the caller as a 500.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return ChannelResponse.Failure(500, "tool_failed", e.Message);
        }

        var headers = new Dictionary<string, string>();
        if (reply.Timing is { Length: > 0 } timing)
        {
            headers[TimingHeader] = timing;
        }

        return ChannelResponse.Json(200, reply.Payload, headers);
    }

    private static string DecodeUtf8(byte[] body)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new ChannelException(400, "bad_request", "Body is not valid UTF-8");
        }
    }

    private static bool IsToolName(string name)
    {
        if (name.Length is 0 or > 96)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
