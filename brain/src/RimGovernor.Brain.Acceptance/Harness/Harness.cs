using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Owns evidence recording and generic native calls for the acceptance runs: every native call/reply pair is written
/// to <c>&lt;output&gt;/NNNN-&lt;label&gt;.json</c> for post-mortem review (see <see cref="Evidence"/>).
/// </summary>
public sealed partial class Harness : IAsyncDisposable
{
    /// <summary>How much longer than the wait it asked for a call is given: the native side has to run the work to its own timeout and report it back.</summary>
    public static readonly TimeSpan NativeWaitMargin = TimeSpan.FromSeconds(30);

    private int _closed;

    public Harness(HttpNativeTransport transport, string output)
    {
        Transport = transport;
        Output = output;
    }

    /// <summary>How the harness reaches the game.</summary>
    public HttpNativeTransport Transport { get; }

    public string Output { get; }

    /// <summary>The discovered tool catalog (the session sets it); RunUntil consults it for DismissLetterTool.</summary>
    public IReadOnlyList<string> Tools { get; set; } = [];

    /// <summary>Whether <see cref="CloseAsync"/> ran; calls through a closed harness are a stale case reference.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Invokes a native tool by name with JSON-shaped arguments (null means <c>{}</c>), records the request/reply pair
    /// as evidence, and returns the decoded structured reply as a generic JSON object.
    /// </summary>
    public Task<JsonObject> CallAsync(string label, string tool, JsonObject? arguments, CancellationToken cancellationToken) =>
        CallCoreAsync(label, tool, arguments ?? [], retry: true, cancellationToken);

    /// <summary>
    /// Calls the native op <paramref name="op"/> with its Contracts request record and returns its Contracts reply record:
    /// the typed edge (<see cref="OrdersJson"/> both ways), recorded as evidence like every call.
    /// </summary>
    public async Task<TReply> CallAsync<TRequest, TReply>(string label, NativeOp<TRequest, TReply> op, TRequest request, CancellationToken cancellationToken)
        where TRequest : class
        where TReply : class
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(request);
        var envelope = await CallAsync(label, op.Name, new JsonObject { { "request", OrdersJson.Serialize(request) } }, cancellationToken).ConfigureAwait(false);
        if (HarnessJson.TryRead(envelope, HarnessJsonContext.Default.ReplyPayload)?.Payload is not { } payload)
        {
            throw new AcceptanceException($"{label}: {op.Name}: reply did not preserve the JSON string envelope");
        }

        try
        {
            return OrdersJson.Deserialize<TReply>(payload);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{label}: {op.Name}: invalid reply: {e.Message}", e);
        }
    }

    /// <summary>
    /// Starts <paramref name="request"/> through the in-process brain's player route (<c>POST /api/lifecycle/new</c>, the
    /// launcher's route), which resolves what the brain owns before it dispatches <c>lifecycle_new_colony</c> (the
    /// ideoligion design a <c>governorIdeoligion</c> spec asks the governor to choose); the caller follows the start with
    /// <c>lifecycle_read_new_colony</c>. The brain registers its routes after the channel's native ops, so a 404 or 503 is
    /// asked again with the same request id until <paramref name="patience"/> is spent; any other non-2xx status is the
    /// start's refusal. Each exchange is recorded as evidence.
    /// </summary>
    public async Task StartNewColonyThroughBrainAsync(string label, RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, TimeSpan patience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsClosed)
        {
            throw Closed(NewColonyRoute, null);
        }

        var body = OrdersJson.Serialize(request);
        var arguments = JsonNode.Parse(body) as JsonObject ?? [];
        var waited = Stopwatch.StartNew();
        using var pace = Wait.Pace(RouteRetryInterval);
        while (true)
        {
            var sequence = Evidence.NextSequence(Output);
            var sent = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            HttpStatusCode status;
            string text;
            try
            {
                (status, text) = await Transport.PostRouteAsync(NewColonyRoute, body, cancellationToken).ConfigureAwait(false);
            }
            catch (NativeTransportException e)
            {
                EvidenceBudget.WriteEvidence(Evidence.PathFor(Output, sequence, label), Evidence.Row(sequence, "POST " + NewColonyRoute, arguments, sent, clock.Elapsed, null, e, null));
                throw;
            }

            var reply = new JsonObject { { "status", (int)status }, { "text", text } };
            EvidenceBudget.WriteEvidence(Evidence.PathFor(Output, sequence, label), Evidence.Row(sequence, "POST " + NewColonyRoute, arguments, sent, clock.Elapsed, reply, null, null));
            if (status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)
            {
                return;
            }

            if (status is not (HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable) || waited.Elapsed >= patience)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: POST {NewColonyRoute} answered {(int)status}: {text}"));
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // The brain's new-colony route (PlayerApi's LifecycleRoutes.NewPath; this assembly does not reference the brain's
    // player API, which pulls in its runtime, so the path is spelled here as the launcher spells it).
    private const string NewColonyRoute = "/api/lifecycle/new";

    // How often a start the brain's routes are not ready for yet is asked again.
    private static readonly TimeSpan RouteRetryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The game's OTLP log ring: the channel directory the opened game writes its rings to, where native's clock and
    /// colony event rows and the in-mod brain's rows land.
    /// </summary>
    public string LogsPath() => Transport is HttpNativeTransport http
        ? OtlpFiles.RingPath(http.EndpointDirectory, OtlpSignal.Logs)
        : throw new AcceptanceException("the harness transport has no channel directory to read the game's log ring from");

    /// <summary>Every row of <paramref name="kind"/> in the game's log ring, oldest first.</summary>
    public IReadOnlyList<LogRow> Telemetry(TelemetryKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return [.. ServiceLogs.ReadLogs(LogsPath()).Where(row => row.Kind == kind.Name)];
    }

    /// <summary>Returns the game's native tool names, bounded; null when the transport cannot enumerate them.</summary>
    public Task<IReadOnlyList<string>?> DiscoveryAsync(CancellationToken cancellationToken) => Transport.ToolNamesAsync(cancellationToken);

    /// <summary>Marks the harness closed and disposes its transport. Calls afterwards throw <see cref="HarnessClosedException"/>.</summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>
    /// Raises the call's deadline to cover a native wait the arguments asked for. The lifecycle tools
    /// (load_game_ready, new-colony) take a <c>timeoutMs</c> the caller sized for the work; the native timeout stays
    /// the one that decides the outcome, this only stops the transport from ending the call first.
    /// </summary>
    internal static TimeSpan? CoverNativeWait(JsonObject arguments) =>
        HarnessJson.TryRead(arguments, HarnessJsonContext.Default.NativeWaitArguments)?.TimeoutMs is { } ms and > 0 ? TimeSpan.FromMilliseconds(ms) + NativeWaitMargin : null;

    private async Task<JsonObject> CallCoreAsync(string label, string tool, JsonObject arguments, bool retry, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            throw Closed(tool, null);
        }

        // Every native call of a bridge-only case is a natural pause for the checkpoint ring; a capture in progress
        // is not re-entered.
        await Checkpoints.PauseAsync(cancellationToken).ConfigureAwait(false);
        var sequence = Evidence.NextSequence(Output);
        var path = Evidence.PathFor(Output, sequence, label);
        var sent = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var options = new NativeCallOptions { Timeout = CoverNativeWait(arguments), Phase = label };
        NativeResult? result = null;
        Exception? callError = null;
        try
        {
            result = await Transport.CallAsync(tool, arguments, options, cancellationToken).ConfigureAwait(false);
        }
        catch (NativeRefusedException e)
        {
            result = e.Result;
            callError = e;
        }
        catch (Exception e) when (e is NativeTransportException or OperationCanceledException or NativeFailureException or BridgeUnavailableException)
        {
            // A native op's thrown refusal reaches the caller as itself, recorded like every other call error.
            callError = e;
        }

        var row = Evidence.Row(sequence, tool, arguments, sent, clock.Elapsed, result?.Envelope, callError, null);
        try
        {
            if (callError is not null)
            {
                callError = Refine(tool, callError, result);
                row.Set("error", callError.Message);
                throw callError is NativeRefusedException or NativeTransportException or OperationCanceledException or NativeFailureException or BridgeUnavailableException ? callError : new AcceptanceException(callError.Message, callError);
            }

            var structured = result?.Structured;
            var tick = TickStats.ReplyTick(tool, structured);
            if (tick is { } observed)
            {
                row.Set("tick", (long)observed);
            }

            TickStats.ObserveReply(tool, tick);
            return structured ?? [];
        }
        finally
        {
            EvidenceBudget.WriteEvidence(path, row);
        }
    }

    /// <summary>The error a closed harness reports: a released session cannot be called, and the case must take the harness its Reattach returns.</summary>
    private static HarnessClosedException Closed(string tool, Exception? cause) => new(
        $"harness closed: {tool}: this harness session was released to a service; Reattach after the service stops, and take the Harness the case's Reattach returns{(cause is null ? "" : ": " + cause.Message)}");

    /// <summary>Adds the refusal's own cause to its message.</summary>
    private static Exception Refine(string tool, Exception error, NativeResult? result)
    {
        if (error is NativeRefusedException refused && StepResult.Parse(result?.Envelope) is { } failure)
        {
            var kind = failure.Kind == "native exception" && tool.StartsWith("test/", StringComparison.Ordinal) ? "fixture exception" : failure.Kind;
            return new NativeRefusedException(refused.Tool, refused.Cause, refused.Result, $"native call refused: {tool}: {failure.Summary} ({kind})");
        }

        return error;
    }
}
