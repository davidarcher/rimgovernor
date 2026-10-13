using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.HttpChannel;

/// <summary>The parts of one request the guard and the router look at, before any body is read.</summary>
public sealed record RequestHead
{
    public string Method { get; init; } = "";

    /// <summary>The raw request target: path and optional query.</summary>
    public string Target { get; init; } = "";

    public string? Host { get; init; }

    public string? Origin { get; init; }

    public string? SecFetchSite { get; init; }

    public string? ContentType { get; init; }

    /// <summary>The declared body length, or -1 when the request is chunked or does not say.</summary>
    public long ContentLength { get; init; } = -1;

    /// <summary>True when the request carries a Transfer-Encoding header.</summary>
    public bool Chunked { get; init; }
}

/// <summary>One request as a route handler sees it: guard-checked, path and query split, body read and bounded.</summary>
public sealed class ChannelRequest
{
    public ChannelRequest(string method, string path, string query, byte[] body, IReadOnlyDictionary<string, string> headers)
    {
        Method = method;
        Path = path;
        Query = query;
        Body = body;
        Headers = headers;
    }

    public string Method { get; }

    /// <summary>The path without the query, as sent (not unescaped).</summary>
    public string Path { get; }

    /// <summary>The raw query without the leading question mark; empty when there is none.</summary>
    public string Query { get; }

    public byte[] Body { get; }

    /// <summary>The optional request headers the channel forwards (X-RimGovernor-*), case-insensitive.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The unescaped value of one query parameter; null when absent. A repeated parameter is a bad request.</summary>
    public string? QueryValue(string name)
    {
        string? found = null;
        foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            if (!string.Equals(key, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                throw new ChannelException(400, "bad_request", $"Query parameter {name} is repeated");
            }

            found = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }

        return found;
    }
}

/// <summary>One reply. The body is complete (no streaming over this channel).</summary>
public sealed class ChannelResponse
{
    public int Status { get; init; } = 200;

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    public byte[] Body { get; init; } = [];

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public static ChannelResponse Json(int status, string json, IReadOnlyDictionary<string, string>? headers = null) =>
        new() { Status = status, Body = Encoding.UTF8.GetBytes(json), Headers = headers ?? new Dictionary<string, string>() };

    /// <summary>The channel's failure shape: <c>{"code","detail"}</c>.</summary>
    public static ChannelResponse Failure(int status, string code, string detail, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return new ChannelResponse { Status = status, Body = stream.ToArray(), Headers = headers ?? new Dictionary<string, string>() };
    }
}

/// <summary>A failure a handler or the pipeline reports with a status and the failure shape.</summary>
public sealed class ChannelException : Exception
{
    public ChannelException(int status, string code, string detail)
        : base(detail)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }

    public string Code { get; }
}

/// <summary>A route handler. Throw <see cref="ChannelException"/> to answer with a specific failure.</summary>
public delegate Task<ChannelResponse> RouteHandler(ChannelRequest request, CancellationToken cancellationToken);
