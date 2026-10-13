using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    public Harness(INativeTransport transport, string output)
    {
        Transport = transport;
        Output = output;
    }

    /// <summary>How the harness reaches the game (Go <c>Harness.Client</c>).</summary>
    public INativeTransport Transport { get; }

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
    /// Calls a <c>rimgovernor/*</c> ProtoJSON tool: wraps <paramref name="request"/> as <c>{"request": json}</c> and
    /// unwraps the ProtoJSON string payload from the reply's <c>payload</c> field.
    /// </summary>
    public async Task<JsonObject> WireAsync(string label, string method, JsonNode? request, CancellationToken cancellationToken)
    {
        var (message, _) = await WireBytesAsync(label, method, request, cancellationToken).ConfigureAwait(false);
        return message;
    }

    /// <summary>Wire returning the reply payload's ProtoJSON byte count beside the decoded message.</summary>
    public async Task<(JsonObject Message, int Bytes)> WireBytesAsync(string label, string method, JsonNode? request, CancellationToken cancellationToken)
    {
        var encoded = request is null ? "null" : request.ToJsonString();
        var reply = await CallAsync(label, "rimgovernor/" + method, new JsonObject { ["request"] = encoded }, cancellationToken).ConfigureAwait(false);
        if (reply["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload))
        {
            throw new AcceptanceException($"{method}: reply did not preserve the ProtoJSON string envelope");
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject message)
            {
                throw new AcceptanceException($"{method}: invalid ProtoJSON reply: not an object");
            }

            return (message, System.Text.Encoding.UTF8.GetByteCount(payload));
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{method}: invalid ProtoJSON reply: {e.Message}", e);
        }
    }

    /// <summary>Adapts <see cref="WireAsync"/> to the <see cref="WireFunc"/> the authority helpers and the scenario clock take.</summary>
    public WireFunc WireFunc() =>
        (label, method, request, cancellationToken) => WireAsync(label, method, request, cancellationToken);

    /// <summary>
    /// Asserts the wire reply is exactly the single-field oneof named by one of <paramref name="cases"/> and returns
    /// the case name and its value.
    /// </summary>
    public static (string Case, JsonObject Value) Outcome(JsonObject message, params string[] cases)
    {
        if (message.Count != 1)
        {
            throw new AcceptanceException($"expected exactly one oneof case, found [{string.Join(' ', Keys(message))}]");
        }

        foreach (var name in cases)
        {
            if (message.TryGetPropertyValue(name, out var value))
            {
                return value is JsonObject nested
                    ? (name, nested)
                    : throw new AcceptanceException($"oneof case {name} must be an object");
            }
        }

        throw new AcceptanceException($"expected one of [{string.Join(' ', cases)}], found [{string.Join(' ', Keys(message))}]");
    }

    /// <summary>The keys of an object in ordinal order.</summary>
    public static IReadOnlyList<string> Keys(JsonObject map) => [.. map.Select(pair => pair.Key).Order(StringComparer.Ordinal)];

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
        arguments["timeoutMs"].AsUInt64() is { } ms and > 0 ? TimeSpan.FromMilliseconds(ms) + NativeWaitMargin : null;

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
        catch (NativeTransportException e)
        {
            callError = e;
        }

        var row = Evidence.Row(sequence, tool, arguments, sent, clock.Elapsed, result?.Envelope, callError, null);
        try
        {
            if (callError is not null && BlockingAttention(callError))
            {
                var attention = await ReadAndAckAttentionAsync(
                    (partial) =>
                    {
                        row["attention"] = partial.DeepClone();
                        EvidenceBudget.WriteEvidence(path, row);
                    },
                    cancellationToken).ConfigureAwait(false);
                row["attention"] = attention.Row;
                if (attention.Error is not null)
                {
                    callError = new AggregateException(callError, attention.Error);
                }
                else if (retry)
                {
                    // The bridge explicitly refused execution, so one retry cannot duplicate a mutation.
                    EvidenceBudget.WriteEvidence(path, row);
                    var payload = await CallCoreAsync(label + "-retry", tool, arguments, retry: false, cancellationToken).ConfigureAwait(false);
                    row["retried"] = true;
                    return payload;
                }
            }

            if (callError is not null)
            {
                callError = Refine(tool, callError, result, hadAttention: row["attention"] is not null);
                row["error"] = callError.Message;
                throw callError is NativeRefusedException or NativeTransportException ? callError : new AcceptanceException(callError.Message, callError);
            }

            var structured = result?.Structured;
            var tick = TickStats.ReplyTick(tool, structured);
            if (tick is { } observed)
            {
                row["tick"] = (long)observed;
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

    /// <summary>Adds the refusal's own cause to its message, as the Go harness does for a named native refusal.</summary>
    private static Exception Refine(string tool, Exception error, NativeResult? result, bool hadAttention)
    {
        if (error is NativeRefusedException refused && !hadAttention && StepResult.Parse(result?.Envelope) is { } failure)
        {
            var kind = failure.Kind == "native exception" && tool.StartsWith("test/", StringComparison.Ordinal) ? "fixture exception" : failure.Kind;
            return new NativeRefusedException(refused.Tool, refused.Cause, refused.Result, $"native call refused: {tool}: {failure.Summary} ({kind})");
        }

        return error;
    }
}

/// <summary>
/// The rimgovernor/* ProtoJSON transport: ordinarily <see cref="Harness.WireAsync"/> (Go <c>WireFunc</c>).
/// </summary>
public delegate Task<JsonObject> WireFunc(string label, string method, JsonObject request, CancellationToken cancellationToken);
