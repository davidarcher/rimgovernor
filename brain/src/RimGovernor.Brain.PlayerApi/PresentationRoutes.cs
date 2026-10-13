using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Presentation;

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
                var reply = await reader.ReadCameraAsync(new ReadRequest { Identity = id }, token).ConfigureAwait(false);
                return (reply, reply.Outcome?.Camera?.Context);
            }, ct), Bounded));
            registrations.Add(routes.Map("GET", SelectionPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadSelectionAsync(new ReadRequest { Identity = id }, token).ConfigureAwait(false);
                return (reply, reply.Outcome?.Selection?.Context);
            }, ct), Bounded));
            registrations.Add(routes.Map("GET", ColonistsPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadColonistRosterAsync(new ColonistRosterRequest { Identity = id, CurrentMapOnly = true, IncludeDossier = true }, token).ConfigureAwait(false);
                return (reply, reply.Outcome?.Roster?.Context);
            }, ct), Bounded));
            registrations.Add(routes.Map("GET", RenderStatePath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await reader.ReadRenderStateAsync(new ReadRequest { Identity = id }, token).ConfigureAwait(false);
                return (reply, reply.Outcome?.Status?.Context);
            }, ct), Bounded));
        }

        if (notifications is not null)
        {
            registrations.Add(routes.Map("GET", NotificationsPath, (request, ct) => ReadAsync(snapshots, request, async (id, token) =>
            {
                var reply = await notifications.ReadNotificationsAsync(
                    new NotificationsRequest
                    {
                        Identity = id,
                        IncludeLetters = true,
                        IncludeMessages = true,
                        IncludeAlerts = true,
                        LetterLimit = 40,
                        MessageLimit = 12,
                        AlertLimit = 40,
                    },
                    token).ConfigureAwait(false);
                var snapshot = reply.Outcome?.Notifications;
                if (snapshot is null
                    || (snapshot.Letters?.Outcome?.Observed is null && snapshot.Letters?.Outcome?.Unavailable is null)
                    || (snapshot.Messages?.Outcome?.Observed is null && snapshot.Messages?.Outcome?.Unavailable is null)
                    || (snapshot.Alerts?.Outcome?.Observed is null && snapshot.Alerts?.Outcome?.Unavailable is null))
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

    private static async Task<ChannelResponse> ReadAsync<TReply>(
        ISnapshotProvider snapshots,
        ChannelRequest request,
        Func<WireIdentity, CancellationToken, Task<(TReply Reply, ObservationContext? Observed)>> read,
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

    private static async Task<ChannelResponse> WriteAsync<TReply>(
        ISnapshotProvider snapshots,
        ObservedIdentity identity,
        WireIdentity wire,
        TReply reply,
        ObservationContext? observed,
        CancellationToken ct)
    {
        BridgeFunctions.ValidateContext(observed);
        if (!wire.Equals(observed!.Identity) || observed.Tick.GetValueOrDefault() < identity.Tick.Value)
        {
            throw new InvalidOperationException("presentation context changed");
        }

        var latest = await FreshIdentityAsync(snapshots, ct).ConfigureAwait(false);
        if (!latest.SameContext(identity))
        {
            throw new InvalidOperationException("presentation world changed");
        }

        // The provider validates native domain facts. Reject unrepresentable values before the JSON writer
        // can stringify NaN.
        if (!Representable(reply))
        {
            throw new InvalidOperationException("invalid presentation wire");
        }

        var json = WireJson.Serialize(reply);
        if (Encoding.UTF8.GetByteCount(json) > MaxReplyBytes)
        {
            return ChannelResponse.Failure(503, "response_limit", "Presentation response exceeds its configured bound");
        }

        ct.ThrowIfCancellationRequested();
        return ChannelResponse.Json(200, json + "\n");
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
            var status = reply.Outcome?.Status;
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

    /// <summary>Go <c>presentationWire</c>: no non-finite float anywhere in the reply. (Unknown fields cannot exist on a record.)</summary>
    private static bool Representable(object? value)
    {
        switch (value)
        {
            case null:
                return true;
            case double d:
                return double.IsFinite(d);
            case float f:
                return float.IsFinite(f);
            case string or Enum or bool or decimal:
                return true;
        }

        var type = value.GetType();
        if (type.IsPrimitive)
        {
            return true;
        }

        if (value is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (!Representable(item))
                {
                    return false;
                }
            }

            return true;
        }

        var isPair = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        if (!isPair && type.Namespace?.StartsWith("RimGovernor.Contracts.Wire", StringComparison.Ordinal) != true)
        {
            return true;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead && !Representable(property.GetValue(value)))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record RenderStatusDto(bool Supported, bool Suspended, bool WindowVisible, uint RemainingLeaseMs);
