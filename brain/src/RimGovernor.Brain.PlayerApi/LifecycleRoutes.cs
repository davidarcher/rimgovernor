using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The explicit session lifecycle routes (Go <c>httpapi</c> <c>lifecycle.go</c> and <c>lifecycle_new.go</c>): save,
/// load and new colony, each a POST that starts the operation and a GET by request id that recovers its outcome.
/// The routes carry no token; the channel's request guard is the only access check (issue #3083). The
/// <see cref="ILifecycleWriter"/> returns the native reply as it is and owns the retry after a blocking-attention
/// acknowledgement; the routes map each outcome (completed, pending, superseded, failure) to a status.
/// </summary>
public static class LifecycleRoutes
{
    public const string SavePath = "/api/lifecycle/save";

    public const string LoadPath = "/api/lifecycle/load";

    public const string NewPath = "/api/lifecycle/new";

    private const int MinLoadTimeoutMs = 1000;

    private const int MaxLoadTimeoutMs = 120_000;

    private static readonly RouteOptions Bounded = new() { MaxBodyBytes = StrictJson.Limit };

    /// <summary>Registers the three lifecycle routes (POST and GET each); dispose the result to remove them all.</summary>
    public static IDisposable Register(RouteTable routes, ISnapshotProvider snapshots, ILifecycleWriter lifecycle) => new ReadRoutes.Registrations(
    [
        routes.Map("POST", SavePath, (request, ct) => SaveAsync(snapshots, lifecycle, request, ct), Bounded),
        routes.Map("GET", SavePath, (request, ct) => ReadAsync(request, id => ReadSaveAsync(lifecycle, id, ct), ct), Bounded),
        routes.Map("POST", LoadPath, (request, ct) => LoadAsync(snapshots, lifecycle, request, ct), Bounded),
        routes.Map("GET", LoadPath, (request, ct) => ReadAsync(request, id => ReadLoadAsync(lifecycle, id, ct), ct), Bounded),
        routes.Map("POST", NewPath, (request, ct) => NewColonyAsync(lifecycle, request, ct), Bounded),
        routes.Map("GET", NewPath, (request, ct) => ReadAsync(request, id => ReadNewAsync(lifecycle, id, ct), ct), Bounded),
    ]);

    // Go handleLifecycleRead: exactly one parameter, requestId, with a valid id.
    private static async Task<ChannelResponse> ReadAsync(ChannelRequest request, Func<string, Task<ChannelResponse>> read, CancellationToken ct)
    {
        var parts = request.Query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var id = parts.Length == 1 && parts[0].StartsWith("requestId=", StringComparison.Ordinal) ? request.QueryValue("requestId") : null;
        if (id is null || !new PlanId(id).IsValid)
        {
            return ChannelResponse.Failure(400, "invalid_query", "One requestId is required");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            return await read(id).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadRoutes.ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> ReadSaveAsync(ILifecycleWriter lifecycle, string id, CancellationToken ct) =>
        SaveResponse(await lifecycle.ReadSaveAsync(id, ct).ConfigureAwait(false), 200);

    private static async Task<ChannelResponse> ReadLoadAsync(ILifecycleWriter lifecycle, string id, CancellationToken ct) =>
        LoadResponse(await lifecycle.ReadLoadAsync(id, ct).ConfigureAwait(false), 200);

    private static async Task<ChannelResponse> ReadNewAsync(ILifecycleWriter lifecycle, string id, CancellationToken ct) =>
        NewColonyResponse(await lifecycle.ReadNewColonyAsync(id, ct).ConfigureAwait(false), 200, 200);

    private static ChannelResponse? NoQuery(ChannelRequest request) =>
        request.Query.Length == 0 ? null : ChannelResponse.Failure(400, "invalid_request", "Mutation requires a bounded body and no query");

    private static async Task<ChannelResponse> SaveAsync(ISnapshotProvider snapshots, ILifecycleWriter lifecycle, ChannelRequest request, CancellationToken ct)
    {
        if (NoQuery(request) is { } refusal)
        {
            return refusal;
        }

        string id;
        string saveName;
        try
        {
            var fields = StrictJson.Object(request.Body, "requestId", "saveName");
            id = StrictJson.String(fields["requestId"]);
            saveName = StrictJson.String(fields["saveName"]);
            if (!new PlanId(id).IsValid || saveName.Length == 0)
            {
                throw new StrictJsonException("invalid save request");
            }
        }
        catch (StrictJsonException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "requestId and saveName are required");
        }

        try
        {
            var (wire, mode) = await CurrentIdentityAsync(snapshots, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("no current lifecycle identity");
            if (mode != "manual")
            {
                return ChannelResponse.Failure(409, "conflict", "Save requires manual control");
            }

            // No expected tick: the snapshot's tick is a cached observation up to a refresh interval old, so a save
            // posted right after a pause carried the pre-pause tick and native refused it as moved. The completed
            // reply must report the game paused and the identity above unchanged; its tick is the checkpoint's.
            var save = new SaveRequest
            {
                Player = new PlayerLifecycleContext { Identity = wire, PlayerDirection = NewColonyBounds.LifecycleDirection, RequestId = id },
                SaveName = saveName,
            };
            var reply = await lifecycle.SaveAsync(save, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return SaveResponse(reply, 201);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadRoutes.ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> LoadAsync(ISnapshotProvider snapshots, ILifecycleWriter lifecycle, ChannelRequest request, CancellationToken ct)
    {
        if (NoQuery(request) is { } refusal)
        {
            return refusal;
        }

        string id;
        string saveName;
        uint timeoutMs;
        Readiness readiness;
        try
        {
            var fields = StrictJson.Object(request.Body, "requestId", "saveName", "readiness", "timeoutMs");
            id = StrictJson.String(fields["requestId"]);
            saveName = StrictJson.String(fields["saveName"]);
            var timeout = fields["timeoutMs"];
            if (!new PlanId(id).IsValid
                || saveName.Length == 0
                || timeout.ValueKind != JsonValueKind.Number
                || !timeout.TryGetUInt32(out timeoutMs)
                || timeoutMs < MinLoadTimeoutMs
                || timeoutMs > MaxLoadTimeoutMs)
            {
                throw new StrictJsonException("invalid load request");
            }

            readiness = StrictJson.String(fields["readiness"]) switch
            {
                "map" => Readiness.Map,
                "visual" => Readiness.Visual,
                _ => throw new FormatException("readiness"),
            };
        }
        catch (StrictJsonException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "requestId, saveName and a timeoutMs of 1000-120000 are required");
        }
        catch (FormatException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "readiness must be map or visual");
        }

        try
        {
            // A cold bootstrap load (no colony currently observed, e.g. the game is still at its main menu) has no
            // prior identity to assert against, so the expected player is left out in that case; the contract treats
            // it as optional. Any other read failure still aborts the request.
            PlayerLifecycleContext? expected = null;
            if (await CurrentIdentityAsync(snapshots, ct).ConfigureAwait(false) is { } current)
            {
                expected = new PlayerLifecycleContext { Identity = current.Wire, PlayerDirection = NewColonyBounds.LifecycleDirection, RequestId = id };
            }

            var load = new LoadRequest { RequestId = id, SaveName = saveName, Readiness = readiness, TimeoutMs = timeoutMs };
            if (expected is not null)
            {
                load = load with { ExpectedPlayer = expected };
            }

            var reply = await lifecycle.LoadAsync(load, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return LoadResponse(reply, 201);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadRoutes.ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> NewColonyAsync(ILifecycleWriter lifecycle, ChannelRequest request, CancellationToken ct)
    {
        if (NoQuery(request) is { } refusal)
        {
            return refusal;
        }

        NewColonyRequest decoded;
        try
        {
            decoded = NewColonyJson.Decode(request.Body);
            if (!new PlanId((decoded.RequestId ?? "")).IsValid)
            {
                throw new StrictJsonException("invalid request id");
            }
        }
        catch (StrictJsonException)
        {
            return ChannelResponse.Failure(400, "invalid_request", "requestId and a spec are required");
        }
        catch (ScenarioPartException e)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Invalid new colony request: scenario part " + e.Message);
        }

        try
        {
            BridgeFunctions.ValidateNewColonyRequest(decoded);
        }
        catch (BridgeContractException e)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Invalid new colony request: " + e.Message.Replace("bridge contract failure: ", "", StringComparison.Ordinal));
        }

        try
        {
            var reply = await lifecycle.NewColonyAsync(decoded, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return NewColonyResponse(reply, 201, 202);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadRoutes.ReadFailure(e);
        }
    }

    /// <summary>
    /// Go <c>currentLifecycleIdentity</c>: the fresh, connected world identity and control mode the controller observes,
    /// or null when it has none (disconnected, stale, or identity or generation unknown). It never reaches native.
    /// </summary>
    private static async Task<(WireIdentity Wire, string Mode)?> CurrentIdentityAsync(ISnapshotProvider snapshots, CancellationToken ct)
    {
        var snapshot = await snapshots.SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.Connected || snapshot.Stale || !snapshot.Identity.TryGetValue(out var identity) || !snapshot.Generation.TryGetValue(out _))
        {
            return null;
        }

        var wire = new WireIdentity { ColonyId = identity.Colony.Value, LoadToken = identity.Load.Value, MapId = identity.Map.Value };
        return (wire, snapshot.Mode.Length == 0 ? "manual" : snapshot.Mode);
    }

    // A save is completed or it is not shown: an uncertain or refused save is an unavailable read.
    private static ChannelResponse SaveResponse(SaveReply reply, int completedStatus)
    {
        if (reply.Outcome?.Completed is not { } done)
        {
            return ReadRoutes.ReadFailure(new InvalidOperationException("save not completed"));
        }

        var observed = done.Context;
        var identity = observed?.Identity;
        return ChannelResponse.Json(completedStatus, PlayerJson.Serialize(new SaveReplyDto(
            (done.RequestId ?? ""),
            (done.SaveName ?? ""),
            new IdentityDto(identity?.ColonyId ?? "", identity?.MapId ?? 0, identity?.LoadToken ?? ""),
            observed?.Tick ?? 0,
            done.Paused.GetValueOrDefault(),
            done.ByteLength.GetValueOrDefault())));
    }

    // A load is completed, still in progress (202, the poller's ordinary case), or superseded (409).
    private static ChannelResponse LoadResponse(LoadReply reply, int completedStatus)
    {
        switch (reply.Outcome)
        {
            case LoadReplyOutcome.PendingCase { Value: var pending }:
                return ChannelResponse.Json(202, PlayerJson.Serialize(new LoadPendingDto("pending", (pending.RequestId ?? ""), (pending.Detail ?? ""))));
            case LoadReplyOutcome.SupersededCase { Value: var superseded }:
                return ChannelResponse.Failure(409, "superseded", (superseded.Detail ?? ""));
            case LoadReplyOutcome.CompletedCase { Value: var done }:
                var readiness = done.Readiness.GetValueOrDefault() switch
                {
                    Readiness.Map => "map",
                    Readiness.Visual => "visual",
                    _ => null,
                };
                if (readiness is null)
                {
                    return ReadRoutes.ReadFailure(new InvalidOperationException("unspecified readiness"));
                }

                var loaded = done.Loaded;
                var identity = loaded?.Context?.Identity;
                return ChannelResponse.Json(completedStatus, PlayerJson.Serialize(new LoadReplyDto(
                    (done.RequestId ?? ""),
                    (done.SaveName ?? ""),
                    new IdentityDto(identity?.ColonyId ?? "", identity?.MapId ?? 0, identity?.LoadToken ?? ""),
                    loaded?.Paused ?? false,
                    readiness)));
            default:
                return ReadRoutes.ReadFailure(new InvalidOperationException("load not completed"));
        }
    }

    // A new colony is completed, still pending (the poller's ordinary case), superseded (409) or refused by native.
    private static ChannelResponse NewColonyResponse(NewColonyReply reply, int completedStatus, int pendingStatus)
    {
        switch (reply.Outcome)
        {
            case NewColonyReplyOutcome.PendingCase { Value: var pending }:
                return ChannelResponse.Json(pendingStatus, PlayerJson.Serialize(new NewColonyPendingDto(
                    "pending",
                    (pending.RequestId ?? ""),
                    PhaseName(pending.Phase.GetValueOrDefault()),
                    (pending.Detail ?? ""),
                    pending.ElapsedMs.GetValueOrDefault(),
                    pending.RerollCount.GetValueOrDefault())));
            case NewColonyReplyOutcome.SupersededCase { Value: var superseded }:
                return ChannelResponse.Failure(409, "superseded", (superseded.Detail ?? ""));
            case NewColonyReplyOutcome.FailureCase { Value: var failure }:
                return ChannelResponse.Failure(RefusalStatus(failure.Code.GetValueOrDefault()), "native_" + CodeName(failure.Code.GetValueOrDefault()), (failure.Detail ?? ""));
            case NewColonyReplyOutcome.CompletedCase { Value: var done }:
                var observed = done.Context;
                var identity = observed?.Identity;
                return ChannelResponse.Json(completedStatus, PlayerJson.Serialize(new NewColonyCompletedDto(
                    "completed",
                    (done.RequestId ?? ""),
                    (done.SaveName ?? ""),
                    new IdentityDto(identity?.ColonyId ?? "", identity?.MapId ?? 0, identity?.LoadToken ?? ""),
                    observed?.Tick ?? 0,
                    done.Paused.GetValueOrDefault(),
                    done.ByteLength.GetValueOrDefault(),
                    (done.Seed ?? ""))));
            default:
                return ReadRoutes.ReadFailure(new InvalidOperationException("new colony not completed"));
        }
    }

    private static string PhaseName(NewColonyPhase phase) => phase switch
    {
        NewColonyPhase.GeneratingWorld => "generating_world",
        NewColonyPhase.ChoosingTile => "choosing_tile",
        NewColonyPhase.RollingColonists => "rolling_colonists",
        NewColonyPhase.GeneratingMap => "generating_map",
        NewColonyPhase.Finishing => "finishing",
        NewColonyPhase.Saving => "saving",
        _ => "",
    };

    private static int RefusalStatus(FailureCode code) => code switch
    {
        FailureCode.InvalidRequest => 400,
        FailureCode.NotFound => 404,
        FailureCode.OwnerConflict or FailureCode.AttemptConflict => 409,
        _ => 502,
    };

    // Go lower-cases the enum name after FAILURE_CODE_.
    private static string CodeName(FailureCode code) =>
        Domain.Wire.Name(code)
        .Replace("FAILURE_CODE_", "", StringComparison.Ordinal)
        .ToLowerInvariant();
}

internal sealed record SaveReplyDto(
    string RequestId,
    string SaveName,
    IdentityDto Identity,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long Tick,
    bool Paused,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] ulong ByteLength);

internal sealed record LoadPendingDto(string Status, string RequestId, string Detail);

internal sealed record LoadReplyDto(string RequestId, string SaveName, IdentityDto Identity, bool Paused, string Readiness);

internal sealed record NewColonyPendingDto(string Status, string RequestId, string Phase, string Detail, ulong ElapsedMs, uint RerollCount);

internal sealed record NewColonyCompletedDto(
    string Status,
    string RequestId,
    string SaveName,
    IdentityDto Identity,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long Tick,
    bool Paused,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] ulong ByteLength,
    string Seed);
