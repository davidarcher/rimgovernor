using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The service-driven half of a checkpoint save (Go checkpoint.go serviceSave, ServiceSave and friends). The Go
// harness reached the service through its `serve` child process (ServiceProcess); that process type belongs to the
// slice that owns the service launch, so this file declares only the narrow surface the ring needs from it.

/// <summary>A service's HTTP reply: the decoded JSON body (null when none) and the status code.</summary>
public sealed record ServiceApiReply(JsonObject? Body, int Status);

/// <summary>
/// One call against the running service's API (Go <c>ServiceProcess.API</c>): method, path, optional JSON body and
/// the bearer token ("" for none). Transport failures throw <see cref="AcceptanceException"/>; a non-2xx status is
/// a reply.
/// </summary>
public delegate Task<ServiceApiReply> ServiceApiCall(string method, string path, JsonObject? body, string token, CancellationToken cancellationToken);

/// <summary>What a checkpoint capture needs from the running service that holds the game (Go <c>*ServiceProcess</c>).</summary>
public interface ICheckpointService
{
    /// <summary>The prefix of the request ids the capture sends.</summary>
    string Label { get; }

    /// <summary>The player identity the service acts under.</summary>
    JsonObject? Identity { get; }

    /// <summary>The service's API token.</summary>
    string Token { get; }

    /// <summary>The service's exit as an error once it has ended, null while it runs.</summary>
    Exception? Exited();

    /// <summary>Holds (or releases) the keep-alive so it does not re-acquire authority under a save.</summary>
    void HoldAuthority(bool hold);

    /// <summary>One API call (Go <c>ServiceProcess.API</c>).</summary>
    Task<ServiceApiReply> ApiAsync(string method, string path, JsonObject? body, string token, CancellationToken cancellationToken);
}

public sealed partial class CheckpointRing
{
    /// <summary>
    /// Saves through the running service (<see cref="ServiceSaveAsync"/>), which pauses to manual control and
    /// resumes afterwards.
    /// </summary>
    private async Task<SaveResult> ServiceSaveCoreAsync(ICheckpointService service, string name, string label, bool force, CancellationToken cancellationToken)
    {
        if (service.Exited() is { } exited)
        {
            throw new AcceptanceException(exited.Message, exited);
        }

        // A service that is not automating yet (its first resume still in flight), that a case holds in manual, or
        // that has not observed a tick yet (its save would 503 "Controller data is unavailable") cannot be paused
        // for a save; a periodic capture waits for the next cadence, a forced one tries.
        if (!force)
        {
            ServiceApiReply? state = null;
            try
            {
                state = await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException)
            {
                // Treated as not capturable below.
            }

            if (state is not { Status: 200 } || !ServiceCanCapture(state.Body))
            {
                throw new CaptureDeclinedException(notPaused: true);
            }
        }

        var prefix = service.Label + "-checkpoint-" + label;
        var path = await ServiceSaveAsync(Config, name, service.HoldAuthority, service.ApiAsync, service.Identity, service.Token, prefix, cancellationToken).ConfigureAwait(false);
        var tick = await ServiceTickAsync(service.ApiAsync, cancellationToken).ConfigureAwait(false) ?? 0;
        return new SaveResult(path, tick, service.Identity);
    }

    /// <summary>
    /// Says whether a service's /api/state body admits a periodic capture: automating, with a fresh (non-stale) game
    /// read, since /api/lifecycle/save refuses until the service knows the game's tick and identity.
    /// </summary>
    internal static bool ServiceCanCapture(JsonObject? state)
    {
        if (!string.Equals(state.Member("mode").AsString(), "automate", StringComparison.Ordinal))
        {
            return false;
        }

        var game = state.Member("game").AsMap();
        if (game?["stale"].AsBool() == true)
        {
            return false;
        }

        return IsJsonNumber(game?["tick"]);
    }

    /// <summary>Reads the live game tick from the service's /api/state.</summary>
    internal static async Task<ulong?> ServiceTickAsync(ServiceApiCall apiCall, CancellationToken cancellationToken)
    {
        ServiceApiReply reply;
        try
        {
            reply = await apiCall("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            return null;
        }

        if (reply.Status != 200 || reply.Body?["game"].AsMap()?["tick"] is not JsonValue value || !IsJsonNumber(value))
        {
            return null;
        }

        var tick = value.AsNumber();
        return tick < 0 ? null : (ulong)tick;
    }

    /// <summary>
    /// Pauses the service's clock, saves the live game as name through /api/lifecycle/save (which needs manual
    /// control) and resumes, holding the keep-alive (<paramref name="hold"/>) throughout so it does not re-acquire
    /// under the save. It returns the save's path in the game's profile.
    /// </summary>
    public static async Task<string> ServiceSaveAsync(
        Config cfg,
        string name,
        Action<bool> hold,
        ServiceApiCall apiCall,
        JsonObject? identity,
        string token,
        string prefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(apiCall);
        hold(true);
        try
        {
            var stamp = DateTimeOffset.UtcNow.Ticks;
            var paused = await apiCall("POST", "/api/player/control/pause", RequestBody($"{prefix}-pause-{stamp}", identity), token, cancellationToken).ConfigureAwait(false);
            if (paused.Status != 200)
            {
                throw new AcceptanceException($"pause status={paused.Status} body={paused.Body.ToJson()}");
            }

            // Once the pause is acknowledged the case's play is stopped: every exit below resumes, or a failed save
            // would strand the game paused for the rest of the run.
            Exception? failure = null;
            string src = "";
            try
            {
                src = await ServiceSaveWhilePausedAsync(cfg, name, apiCall, token, prefix, stamp, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is AcceptanceException or OperationCanceledException or IOException)
            {
                failure = e;
            }

            Exception? resumeFailure = null;
            try
            {
                var resumed = await apiCall("POST", "/api/player/control/resume", RequestBody($"{prefix}-resume-{stamp}", identity), token, CancellationToken.None).ConfigureAwait(false);
                if (resumed.Status != 200)
                {
                    resumeFailure = new AcceptanceException($"resume status={resumed.Status} body={resumed.Body.ToJson()}");
                }
            }
            catch (AcceptanceException e)
            {
                resumeFailure = new AcceptanceException($"resume: {e.Message}", e);
            }

            if (resumeFailure is not null)
            {
                throw failure is null
                    ? resumeFailure
                    : new AcceptanceException($"{failure.Message}; then {resumeFailure.Message}", failure);
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return src;
        }
        finally
        {
            hold(false);
        }
    }

    /// <summary>Waits for manual control, posts the save and checks the file; <see cref="ServiceSaveAsync"/> resumes play afterwards whatever it returns.</summary>
    private static async Task<string> ServiceSaveWhilePausedAsync(Config cfg, string name, ServiceApiCall apiCall, string token, string prefix, long stamp, CancellationToken cancellationToken)
    {
        // The pause is acknowledged before the mode reads manual; wait for it.
        var manualDeadline = Now() + ToTimestamp(TimeSpan.FromSeconds(30));
        while (true)
        {
            ServiceApiReply? state = null;
            try
            {
                state = await apiCall("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException)
            {
                // Polled again until the deadline.
            }

            if (state is { Status: 200 } && string.Equals(state.Body?["mode"].AsString(), "manual", StringComparison.Ordinal))
            {
                break;
            }

            if (Now() > manualDeadline)
            {
                throw new AcceptanceException($"service did not reach manual control after pause: {state?.Body.ToJson() ?? "null"}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        var saved = await apiCall("POST", "/api/lifecycle/save", new JsonObject { ["requestId"] = $"{prefix}-save-{stamp}", ["saveName"] = name }, token, cancellationToken).ConfigureAwait(false);
        if (saved.Status != 201)
        {
            throw new AcceptanceException($"save status={saved.Status} body={saved.Body.ToJson()}");
        }

        var src = cfg.ProfileSave(name);
        if (!File.Exists(src))
        {
            throw new AcceptanceException($"save completed but {src} is missing");
        }

        return src;
    }

    private static JsonObject RequestBody(string requestId, JsonObject? identity) =>
        new() { ["requestId"] = requestId, ["expected"] = identity?.DeepClone() };

    private static bool IsJsonNumber(JsonNode? value) => value is JsonValue json && json.TryGetValue<double>(out _);
}
