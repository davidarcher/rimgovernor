using System.Globalization;

namespace RimGovernor.Brain.HttpChannel;

/// <summary>
/// The channel's only access check (issue #3083: host and origin checks, no token). Every request passes through
/// <see cref="Check"/> before any route is looked up or any body is read; nothing else in the channel decides who
/// may call. The listener also binds loopback only, so this is the second layer, aimed at browsers: a page cannot
/// reach the channel through DNS rebinding (the Host check) or a cross-origin fetch (the Origin and Sec-Fetch-Site
/// checks).
/// </summary>
public static class RequestGuard
{
    /// <summary>The longest request target accepted.</summary>
    public const int MaxTargetLength = 2048;

    /// <summary>Null when the request may proceed; otherwise the refusal.</summary>
    public static ChannelResponse? Check(RequestHead head)
    {
        if (!IsLoopbackAuthority(head.Host))
        {
            return ChannelResponse.Failure(403, "local_only", "Use the local controller address");
        }

        if (head.Target.Length > MaxTargetLength)
        {
            return ChannelResponse.Failure(414, "target_too_long", "Request target is too long");
        }

        if (!head.Target.StartsWith('/'))
        {
            return ChannelResponse.Failure(400, "bad_request", "Request target must be an absolute path");
        }

        // A browser sends Sec-Fetch-Site on every request; a direct client sends none. Only a request the user
        // typed (none) or that comes from this same origin is acceptable.
        if (head.SecFetchSite is { Length: > 0 } site
            && !string.Equals(site, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase))
        {
            return ChannelResponse.Failure(403, "cross_origin", "Cross-site access is unavailable");
        }

        // An Origin is allowed only when it is exactly this controller: http scheme, the Host's own authority, no
        // userinfo, path, query or fragment.
        if (head.Origin is { Length: > 0 } origin
            && !string.Equals(origin, "http://" + head.Host, StringComparison.OrdinalIgnoreCase))
        {
            return ChannelResponse.Failure(403, "cross_origin", "Origin must match this local controller");
        }

        if (string.Equals(head.Method, "POST", StringComparison.Ordinal)
            && !IsJson(head.ContentType))
        {
            return ChannelResponse.Failure(415, "unsupported_media_type", "Send application/json");
        }

        return null;
    }

    /// <summary>
    /// True for <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c>, each with or without a valid port. Anything
    /// else, including an empty or missing Host, is not loopback.
    /// </summary>
    public static bool IsLoopbackAuthority(string? authority)
    {
        if (string.IsNullOrEmpty(authority))
        {
            return false;
        }

        string host;
        string? port = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = authority[1..close];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                port = rest[1..];
            }
        }
        else
        {
            var colon = authority.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                host = authority[..colon];
                port = authority[(colon + 1)..];
            }
            else
            {
                host = authority;
            }
        }

        if (port is not null
            && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > 65535))
        {
            return false;
        }

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1";
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var semi = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = (semi < 0 ? contentType : contentType[..semi]).Trim();
        return string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase);
    }
}
