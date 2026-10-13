using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The player session, control and clock-review routes (Go <c>httpapi/player.go</c> and <c>clock_review.go</c>),
/// registered into the channel's <see cref="RouteTable"/>. The routes carry no token: the channel's request guard
/// is the only access check (issue #3083). A route family whose provider is not given is not registered.
/// </summary>
public static class PlayerRoutes
{
    public const string SessionPath = "/api/player/session";

    public const string ControlPath = "/api/player/control";

    public const string ResumePath = "/api/player/control/resume";

    public const string PausePath = "/api/player/control/pause";

    public const string ClockPath = "/api/player/clock";

    public const string AcknowledgePath = "/api/player/clock/acknowledge";

    private static readonly RouteOptions Bounded = new() { MaxBodyBytes = StrictJson.Limit };

    /// <summary>Registers the session route and the routes of each provider given; dispose the result to remove them all.</summary>
    public static IDisposable Register(RouteTable routes, IPlayerControl? player, IControlReader? controls, IClockReview? clock)
    {
        var registrations = new List<IDisposable>
        {
            routes.Map("GET", SessionPath, (request, _) => Task.FromResult(NoQuery(request) ?? Session()), Bounded),
        };
        if (player is not null && controls is not null)
        {
            registrations.Add(routes.Map("GET", ControlPath, (request, ct) => ReadControlAsync(controls, player, request, ct), Bounded));
            registrations.Add(routes.Map("POST", ResumePath, (request, ct) => WriteControlAsync(player, ControlKind.Resume, request, ct), Bounded));
            registrations.Add(routes.Map("POST", PausePath, (request, ct) => WriteControlAsync(player, ControlKind.Pause, request, ct), Bounded));
        }

        if (clock is not null)
        {
            registrations.Add(routes.Map("GET", ClockPath, (request, ct) => ClockAsync(clock, request, null, ct), Bounded));
            registrations.Add(routes.Map("POST", AcknowledgePath, (request, ct) => ClockAsync(clock, request, request.Body, ct), Bounded));
        }

        return new Registrations(registrations);
    }

    private static ChannelResponse? NoQuery(ChannelRequest request) =>
        request.Query.Length == 0 ? null : ChannelResponse.Failure(400, "invalid_query", "This route accepts no query");

    private static ChannelResponse Session() => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("mode", "explicit-player");
        w.WriteEndObject();
    });

    private static async Task<ChannelResponse> ReadControlAsync(IControlReader controls, IPlayerControl player, ChannelRequest request, CancellationToken ct)
    {
        if (NoQuery(request) is { } refusal)
        {
            return refusal;
        }

        ControlRecord? record = null;
        Exception? cause = null;
        try
        {
            record = await controls.CurrentControlAsync(ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // No intent was ever journaled: an empty record is a normal answer.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            cause = e;
        }

        return ControlResponse(record, player, cause);
    }

    private static async Task<ChannelResponse> WriteControlAsync(IPlayerControl player, ControlKind kind, ChannelRequest request, CancellationToken ct)
    {
        if (NoQuery(request) is { } refusal)
        {
            return refusal;
        }

        ControlRequest decoded;
        try
        {
            var fields = StrictJson.Object(request.Body, "requestId", "expected");
            var id = StrictJson.String(fields["requestId"]);
            if (!new PlanId(id).IsValid)
            {
                throw new StrictJsonException("invalid request id");
            }

            decoded = new ControlRequest(id, kind, World(fields["expected"]));
        }
        catch (StrictJsonException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Invalid control request");
        }

        ControlRecord? record = null;
        Exception? cause = null;
        try
        {
            record = await (kind == ControlKind.Resume ? player.ResumeAsync(decoded, ct) : player.PauseAsync(decoded, ct)).ConfigureAwait(false);
        }
        catch (ControlFailureException e)
        {
            record = e.Record;
            cause = e.InnerException;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            cause = e;
        }

        return ControlResponse(record, player, cause);
    }

    private static World World(JsonElement expected)
    {
        var fields = StrictJson.Fields(expected, "colonyId", "loadToken", "mapId");
        var world = new World(new ColonyId(StrictJson.String(fields["colonyId"])), new LoadId(StrictJson.String(fields["loadToken"])), new MapId(StrictJson.Int32(fields["mapId"])));
        if (!world.IsValid)
        {
            throw new StrictJsonException("invalid world");
        }

        return world;
    }

    private static ChannelResponse ControlResponse(ControlRecord? record, IPlayerControl player, Exception? cause)
    {
        try
        {
            ValidateRecord(record);
            var state = player.State();
            ValidateState(state);
            var status = 200;
            (string Code, string Detail)? failure = null;
            if (cause is not null)
            {
                (status, failure) = PlayerFailure(cause);
                if (record is { Phase: ControlPhase.Uncertain or ControlPhase.Pending })
                {
                    status = 503;
                    failure = ("uncertain", "Control outcome is unresolved; inspect this request ID");
                }
            }

            return Json(status, w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("record");
                if (record is null)
                {
                    w.WriteNullValue();
                }
                else
                {
                    WriteRecord(w, record);
                }

                w.WritePropertyName("state");
                WriteState(w, state);
                w.WritePropertyName("error");
                if (failure is { } f)
                {
                    w.WriteStartObject();
                    w.WriteString("code", f.Code);
                    w.WriteString("detail", f.Detail);
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteNullValue();
                }

                w.WriteEndObject();
            });
        }
        catch (DomainException)
        {
            // A record or state that breaks its own invariants is an unavailable read, never a guessed answer.
            return ChannelResponse.Failure(503, "unavailable", "Controller state is unavailable");
        }
    }

    // Go projectControl: a record must be internally consistent before it is shown.
    private static void ValidateRecord(ControlRecord? record)
    {
        if (record is null)
        {
            return;
        }

        var request = record.Request;
        DomainException.Require(new PlanId(request.RequestId).IsValid && request.World.IsValid, "invalid control record");
        DomainException.Require(Wire.IsDefined(request.Kind), "unknown control kind");
        var generation = record.NativeGeneration.Value;
        switch (record.Phase)
        {
            case ControlPhase.Running:
                DomainException.Require(request.Kind == ControlKind.Resume && generation != 0, "invalid running record");
                break;
            case ControlPhase.Paused:
                DomainException.Require(request.Kind == ControlKind.Pause && generation == 0, "invalid paused record");
                break;
            case ControlPhase.Pending or ControlPhase.Refused or ControlPhase.Uncertain:
                DomainException.Require(generation == 0, "invalid control generation");
                break;
            default:
                throw new DomainException("unknown control phase");
        }
    }

    // Go projectPlayerState.
    private static void ValidateState(PlayerState state)
    {
        if (!state.ObservationKnown)
        {
            DomainException.Require(!state.Enabled, "enabled without observation");
            return;
        }

        state.Snapshot.Validate();
        DomainException.Require(!state.Enabled || (state.Snapshot.Native.Value != 0 && state.Snapshot.Revision.Value != 0), "invalid live authority");
    }

    // Go playerFailure.
    private static (int Status, (string Code, string Detail) Failure) PlayerFailure(Exception cause) => cause switch
    {
        CapacityException => (409, ("capacity", "Intent history is full")),
        NotFoundException => (404, ("not_found", "Requested intent was not found")),
        ConflictException => (409, ("conflict", "Request conflicts with current intent")),
        _ => (503, ("unavailable", "Player operation is unavailable; check its request ID before retrying")),
    };

    private static void WriteRecord(Utf8JsonWriter w, ControlRecord record)
    {
        w.WriteStartObject();
        w.WriteString("requestId", record.Request.RequestId);
        w.WriteString("kind", Wire.Name(record.Request.Kind));
        w.WritePropertyName("expected");
        w.WriteStartObject();
        w.WriteString("colonyId", record.Request.World.Colony.Value);
        w.WriteNumber("mapId", record.Request.World.Map.Value);
        w.WriteString("loadToken", record.Request.World.Load.Value);
        w.WriteEndObject();
        w.WriteString("phase", Wire.Name(record.Phase));
        w.WriteString("nativeGeneration", record.NativeGeneration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteEndObject();
    }

    private static void WriteState(Utf8JsonWriter w, PlayerState state)
    {
        w.WriteStartObject();
        w.WriteBoolean("enabled", state.Enabled);
        w.WriteBoolean("observationKnown", state.ObservationKnown);
        w.WritePropertyName("generation");
        if (!state.ObservationKnown)
        {
            w.WriteNullValue();
        }
        else
        {
            var s = state.Snapshot;
            w.WriteStartObject();
            w.WriteString("colony", s.Colony.Value);
            w.WriteNumber("map", s.Map.Value);
            w.WriteString("load", s.Load.Value);
            w.WriteString("plan", s.Plan.Value);
            w.WriteString("revision", s.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("native", s.Native.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static async Task<ChannelResponse> ClockAsync(IClockReview clock, ChannelRequest request, byte[]? body, CancellationToken ct)
    {
        if (NoQuery(request) is { } refusal)
        {
            return refusal;
        }

        ClockReviewState value;
        try
        {
            if (body is null)
            {
                value = await clock.ReadAsync(ct).ConfigureAwait(false);
            }
            else
            {
                ClockAcknowledgement acknowledgement;
                try
                {
                    var fields = StrictJson.Object(body, "requestId", "expectedRevision", "throughCursor");
                    var id = StrictJson.String(fields["requestId"]);
                    if (!new PlanId(id).IsValid)
                    {
                        throw new StrictJsonException("invalid request id");
                    }

                    acknowledgement = new ClockAcknowledgement(id, StrictJson.CanonicalUInt64(fields["expectedRevision"]), StrictJson.CanonicalNonNegativeInt64(fields["throughCursor"]));
                }
                catch (StrictJsonException)
                {
                    return ChannelResponse.Failure(400, "invalid_request", "Provide a request ID and the displayed review revision and cursor");
                }

                value = await clock.AcknowledgeAsync(acknowledgement, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var (status, failure) = PlayerFailure(e);
            return ChannelResponse.Failure(status, failure.Code, failure.Detail);
        }

        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("revision", value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("inboxCursor", value.InboxCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("reviewedCursor", value.ReviewedCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("acknowledgedCursor", value.AcknowledgedCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteStartArray("holds");
            foreach (var hold in value.Holds)
            {
                w.WriteStartObject();
                w.WriteString("kind", Wire.Name(hold.Kind));
                w.WriteString("fromCursor", hold.FromCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("throughCursor", hold.ThroughCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static ChannelResponse Json(Action<Utf8JsonWriter> write) => Json(200, write);

    private static ChannelResponse Json(int status, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return ChannelResponse.Json(status, Encoding.UTF8.GetString(stream.ToArray()) + "\n");
    }

    private sealed class Registrations(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}
