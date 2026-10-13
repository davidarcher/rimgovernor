namespace RimGovernor.Brain.HttpChannel;

/// <summary>
/// Turns one request into one reply: guard, route, bounded body, handler under its timeout, bounded response. It
/// is independent of the socket so it is tested without one; <see cref="HttpChannelServer"/> feeds it from
/// HttpListener.
/// </summary>
public sealed class Dispatcher(RouteTable routes, int maxResponseBytes)
{
    /// <summary>Reads exactly the declared body length (already checked against the route's bound).</summary>
    public delegate Task<byte[]> BodyReader(int length, CancellationToken cancellationToken);

    public async Task<ChannelResponse> DispatchAsync(
        RequestHead head,
        IReadOnlyDictionary<string, string> forwardedHeaders,
        BodyReader readBody,
        CancellationToken cancellationToken)
    {
        if (RequestGuard.Check(head) is { } refusal)
        {
            return refusal;
        }

        var question = head.Target.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? head.Target : head.Target[..question];
        var query = question < 0 ? "" : head.Target[(question + 1)..];

        var resolution = routes.Resolve(head.Method, path);
        if (resolution.Entry is not { } entry)
        {
            return resolution.Allow is { } allow
                ? ChannelResponse.Failure(405, "method_not_allowed", "This route does not accept that method", new Dictionary<string, string> { ["Allow"] = allow })
                : ChannelResponse.Failure(404, "not_found", "No such route");
        }

        var hasBody = head.Method == "POST";
        byte[] body = [];
        if (hasBody)
        {
            if (head.Chunked || head.ContentLength < 0)
            {
                return ChannelResponse.Failure(411, "length_required", "Send a Content-Length");
            }

            if (head.ContentLength > entry.Options.MaxBodyBytes)
            {
                return ChannelResponse.Failure(413, "body_too_large", "Request body exceeds this route's bound");
            }

            body = await readBody((int)head.ContentLength, cancellationToken).ConfigureAwait(false);
        }
        else if (head.ContentLength > 0 || head.Chunked)
        {
            return ChannelResponse.Failure(400, "bad_request", "This route takes no request body");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(entry.Options.Timeout);
        ChannelResponse response;
        try
        {
            response = await entry.Handler(new ChannelRequest(head.Method, path, query, body, forwardedHeaders), timeout.Token).ConfigureAwait(false);
        }
        catch (ChannelException e)
        {
            return ChannelResponse.Failure(e.Status, e.Code, e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelResponse.Failure(504, "timeout", "The request did not finish in time");
        }
        catch (OperationCanceledException)
        {
            return ChannelResponse.Failure(408, "cancelled", "The request was cancelled");
        }
#pragma warning disable CA1031 // A handler fault must become a reply, never end the listener loop.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return ChannelResponse.Failure(500, "internal", e.GetType().Name);
        }

        if (response.Body.Length > maxResponseBytes)
        {
            return ChannelResponse.Failure(500, "response_limit", "Controller response exceeds its configured bound");
        }

        return response;
    }
}
