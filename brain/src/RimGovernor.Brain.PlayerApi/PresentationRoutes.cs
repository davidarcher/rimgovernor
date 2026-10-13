using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Presentation;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The presentation routes (Go <c>httpapi</c> <c>presentation.go</c>, <c>presentation_media.go</c>,
/// <c>notifications.go</c>): fixed native reads of camera, selection, colonists, render state and notifications,
/// and the one active render-demand lease. Every reply is checked against the fresh controller identity before it
/// is shown, so a world that changed during the read is never presented as the current one. The render-demand lease
/// is not token-gated here; the channel's request guard is the only access check (issue #3083).
/// </summary>
public static class PresentationRoutes
{
    public const string CameraPath = "/api/presentation/camera";

    public const string SelectionPath = "/api/presentation/selection";

    public const string ColonistsPath = "/api/presentation/colonists";

    public const string RenderStatePath = "/api/presentation/render-state";

    public const string NotificationsPath = "/api/presentation/notifications";

    public const string RenderDemandPath = "/api/presentation/render-demand";

    private const int MaxLeaseSeconds = 30;

    private const int MaxReplyBytes = 1 << 20;

    private static readonly RouteOptions Bounded = new() { MaxBodyBytes = StrictJson.Limit };

    /// <summary>Registers the routes of each provider given; dispose the result to remove them all.</summary>
    public static IDisposable Register(
        RouteTable routes,
        ISnapshotProvider snapshots,
        IPresentationReader? reader,
        IPresentationMediaWriter? media,
        INotificationReader? notifications)
    {
        var registrations = new List<IDisposable>();
        if (reader is not null)
        {
            registrations.Add(routes.Map("GET", CameraPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadCameraAsync(new ReadRequest { Identity = id.Clone() }, token).ConfigureAwait(false);
                return (reply, reply.Camera?.Context);
            }, ct), Bounded));
            registrations.Add(routes.Map("GET", SelectionPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadSelectionAsync(new ReadRequest { Identity = id.Clone() }, token).ConfigureAwait(false);
                return (reply, reply.Selection?.Context);
            }, ct), Bounded));
            registrations.Add(routes.Map("GET", ColonistsPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadColonistRosterAsync(new ColonistRosterRequest { Identity = id.Clone(), CurrentMapOnly = true, IncludeDossier = true }, token).ConfigureAwait(false);
                return (reply, reply.Roster?.Context);
            }, ct), Bounded));
            registrations.Add(routes.Map("GET", RenderStatePath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadRenderStateAsync(new ReadRequest { Identity = id.Clone() }, token).ConfigureAwait(false);
                return (reply, reply.Status?.Context);
            }, ct), Bounded));
        }

        if (notifications is not null)
        {
            registrations.Add(routes.Map("GET", NotificationsPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await notifications.ReadNotificationsAsync(
                    new NotificationsRequest
                    {
                        Identity = id.Clone(),
                        IncludeLetters = true,
                        IncludeMessages = true,
                        IncludeAlerts = true,
                        LetterLimit = 40,
                        MessageLimit = 12,
                        AlertLimit = 40,
                    },
                    token).ConfigureAwait(false);
                var snapshot = reply.Notifications;
                if (snapshot is null
                    || (snapshot.Letters?.Observed is null && snapshot.Letters?.Unavailable is null)
                    || (snapshot.Messages?.Observed is null && snapshot.Messages?.Unavailable is null)
                    || (snapshot.Alerts?.Observed is null && snapshot.Alerts?.Unavailable is null))
                {
                    throw new InvalidOperationException("requested notification section missing");
                }

                return (reply, snapshot.Context);
            }, ct), Bounded));
        }

        if (media is not null)
        {
            registrations.Add(routes.Map("POST", RenderDemandPath, (request, ct) => DemandAsync(snapshots, media, request, ct), Bounded));
        }

        return new ReadRoutes.Registrations(registrations);
    }

    private static async Task<ChannelResponse> ReadAsync(
        ISnapshotProvider snapshots,
        ChannelRequest request,
        Func<WireIdentity, CancellationToken, Task<(IMessage Reply, ObservationContext? Observed)>> read,
        CancellationToken ct)
    {
        if (request.Query.Length != 0)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Presentation reads require a bounded URL, no query and no body");
        }

        try
        {
            var identity = await FreshIdentityAsync(snapshots, ct).ConfigureAwait(false);
            var wire = Wire(identity);
            var (reply, observed) = await read(wire, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await WriteAsync(snapshots, identity, wire, reply, observed, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadRoutes.ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> WriteAsync(
        ISnapshotProvider snapshots,
        ObservedIdentity identity,
        WireIdentity wire,
        IMessage reply,
        ObservationContext? observed,
        CancellationToken ct)
    {
        BridgeFunctions.ValidateContext(observed);
        if (!wire.Equals(observed!.Identity) || observed.Tick < identity.Tick.Value)
        {
            throw new InvalidOperationException("presentation context changed");
        }

        var latest = await FreshIdentityAsync(snapshots, ct).ConfigureAwait(false);
        if (!latest.SameContext(identity))
        {
            throw new InvalidOperationException("presentation world changed");
        }

        // The provider validates native domain facts. Reject unrepresentable values before the JSON formatter
        // can silently discard unknown binary fields or stringify NaN.
        if (BridgeFunctions.HasUnknownFields(reply) || !Representable(reply))
        {
            throw new InvalidOperationException("invalid presentation wire");
        }

        if (reply.CalculateSize() > MaxReplyBytes)
        {
            return ChannelResponse.Failure(503, "response_limit", "Presentation response exceeds its configured bound");
        }

        ct.ThrowIfCancellationRequested();
        return ChannelResponse.Json(200, new JsonFormatter(JsonFormatter.Settings.Default).Format(reply) + "\n");
    }

    private static async Task<ChannelResponse> DemandAsync(ISnapshotProvider snapshots, IPresentationMediaWriter media, ChannelRequest request, CancellationToken ct)
    {
        if (request.Query.Length != 0)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Mutation requires a bounded body and no query");
        }

        uint seconds;
        try
        {
            var fields = StrictJson.Object(request.Body, "leaseSeconds");
            var element = fields["leaseSeconds"];
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt32(out seconds) || seconds > MaxLeaseSeconds)
            {
                throw new StrictJsonException("invalid lease");
            }
        }
        catch (StrictJsonException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "leaseSeconds (0-30) is required");
        }

        try
        {
            var identity = await FreshIdentityAsync(snapshots, ct).ConfigureAwait(false);
            var reply = await media.DemandRenderingAsync(
                new RenderDemand { Viewer = new PlayerIdentity { Identity = Wire(identity) }, LeaseSeconds = seconds },
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var status = reply.Status;
            return ChannelResponse.Json(200, PlayerJson.Serialize(new RenderStatusDto(
                status?.Supported ?? false,
                status?.Suspended ?? false,
                status?.WindowVisible ?? false,
                status?.RemainingLeaseMs ?? 0)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadRoutes.ReadFailure(e);
        }
    }

    /// <summary>Go <c>presentationIdentity</c>: the connected, fresh, known and valid world identity.</summary>
    private static async Task<ObservedIdentity> FreshIdentityAsync(ISnapshotProvider snapshots, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = await snapshots.SnapshotAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!snapshot.Connected || snapshot.Stale || !snapshot.Identity.TryGetValue(out var identity))
        {
            throw new InvalidOperationException("fresh identity unavailable");
        }

        identity.Validate();
        return identity;
    }

    private static WireIdentity Wire(ObservedIdentity identity) => new()
    {
        ColonyId = identity.Colony.Value,
        LoadToken = identity.Load.Value,
        MapId = identity.Map.Value,
    };

    /// <summary>Go <c>presentationWire</c>: no field outside the descriptor and no non-finite float anywhere in the reply.</summary>
    private static bool Representable(IMessage message)
    {
        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            var value = field.Accessor.GetValue(message);
            if (field.IsMap)
            {
                if (value is System.Collections.IDictionary map && !map.Values.Cast<object>().All(item => Representable(field, item)))
                {
                    return false;
                }
            }
            else if (field.IsRepeated)
            {
                if (value is System.Collections.IEnumerable list && !list.Cast<object>().All(item => Representable(field, item)))
                {
                    return false;
                }
            }
            else if (field.FieldType == FieldType.Message ? value is not null && !Representable(field, value) : !Representable(field, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Representable(FieldDescriptor field, object? value) => value switch
    {
        double d => double.IsFinite(d),
        float f => float.IsFinite(f),
        IMessage nested => Representable(nested),
        _ => true,
    };
}

internal sealed record RenderStatusDto(bool Supported, bool Suspended, bool WindowVisible, uint RemainingLeaseMs);
