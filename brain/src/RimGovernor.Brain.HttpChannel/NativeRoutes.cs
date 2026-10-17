using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.HttpChannel;

/// <summary>
/// What <c>POST /native/&lt;op&gt;</c> calls: the mod's explicit op table (the native facade's ops, the acceptance-only
/// reads, and the host's own and fixture tools). The channel itself knows no op.
/// </summary>
public interface INativeOpInvoker
{
    /// <summary>The op names a route exists for, without the <c>rimgovernor/</c> prefix.</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>
    /// Runs one op on its JSON request body and answers its JSON reply. A body that does not fit the op's request
    /// throws <see cref="JsonException"/> (a 400); any other failure is the op's own (a 500).
    /// </summary>
    Task<string> InvokeAsync(string op, string body, CancellationToken cancellationToken);
}
/// <summary>The channel's own routes: <c>POST /native/&lt;tool&gt;</c> and <c>GET /api/health</c>.</summary>
public static class NativeRoutes
{
    public const string Prefix = "/native/";

    /// <summary>The largest ProtoJSON request body (a new-colony design with all its parts is far below this).</summary>
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    /// <summary>The longest a native call may run. Lifecycle calls (load, new colony) legitimately take minutes.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

    public static IDisposable Register(RouteTable routes, INativeOpInvoker invoker)
    {
        return routes.MapPrefix(
            "POST",
            Prefix,
            (request, ct) => HandleAsync(invoker, request, ct),
            new RouteOptions { MaxBodyBytes = MaxRequestBytes, Timeout = CallTimeout });
    }

    /// <summary>
    /// Registers <c>GET /api/tools</c>: the route names <c>POST /native/&lt;name&gt;</c> answers right now (the list can
    /// grow while the game starts, as the host registers its tools), for a controller that checks a tool exists.
    /// </summary>
    public static IDisposable RegisterToolList(RouteTable routes, INativeOpInvoker invoker)
    {
        return routes.Map(
            "GET",
            "/api/tools",
            (_, _) =>
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("tools");
                    foreach (var name in invoker.Names.OrderBy(n => n, StringComparer.Ordinal))
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                return Task.FromResult(ChannelResponse.Json(200, Encoding.UTF8.GetString(stream.ToArray())));
            });
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

    private static async Task<ChannelResponse> HandleAsync(INativeOpInvoker invoker, ChannelRequest request, CancellationToken cancellationToken)
    {
        var tool = request.Path[Prefix.Length..];
        if (!IsToolName(tool) || !invoker.Names.Contains(tool))
        {
            return ChannelResponse.Failure(404, "not_found", "No such native tool");
        }

        var body = request.Body.Length == 0 ? "{}" : DecodeUtf8(request.Body);
        try
        {
            return ChannelResponse.Json(200, await invoker.InvokeAsync(tool, body, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException e)
        {
            return ChannelResponse.Failure(400, "invalid_request", e.Message);
        }
#pragma warning disable CA1031 // The op's own failure text goes back to the caller as a 500.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return ChannelResponse.Failure(500, "tool_failed", e.Message);
        }
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

    /// <summary>
    /// A route name is one or more <c>/</c>-separated segments of lowercase letters, digits and <c>_ - .</c>, none
    /// empty: <c>operations_apply</c> for a <c>rimgovernor/</c> tool, <c>test/lab_start</c> for a fixture op.
    /// </summary>
    internal static bool IsToolName(string name)
    {
        if (name.Length is 0 or > 96)
        {
            return false;
        }

        var segmentLength = 0;
        foreach (var c in name)
        {
            if (c == '/')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                continue;
            }

            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.'))
            {
                return false;
            }

            segmentLength++;
        }

        return segmentLength > 0 && !name.Split('/').Any(segment => segment.All(c => c == '.'));
    }
}
