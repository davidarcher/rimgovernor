using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One native tool reply at the transport boundary. <see cref="Envelope"/> is the complete receipt in the shape
/// the evidence rows record (an MCP-style <c>{isError, content, structuredContent}</c> object), and
/// <see cref="Structured"/> the decoded structured content (an object), null when the reply had none.
/// </summary>
public sealed record NativeResult(JsonNode? Envelope, JsonObject? Structured, string? Timing = null);

/// <summary>Per-call settings the transport honours.</summary>
public sealed record NativeCallOptions
{
    /// <summary>A deadline longer than the transport's default (Go <c>bridge.WithCallTimeout</c>); a shorter one is ignored.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The call's phase in a recorded transcript (the evidence label).</summary>
    public string? Phase { get; init; }

    /// <summary>The optional admission class header (<c>X-RimGovernor-Class</c>).</summary>
    public string? AdmissionClass { get; init; }

    /// <summary>The optional trace header (<c>X-RimGovernor-Trace</c>).</summary>
    public string? Trace { get; init; }
}

/// <summary>The base of every failure the harness reports for a game or case; the message is the report's error text.</summary>
public class AcceptanceException : Exception
{
    public AcceptanceException(string message)
        : base(message)
    {
    }

    public AcceptanceException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>The transport failed: no endpoint, a refused connection, a timeout or a tool the channel does not route (Go <c>ErrTransport</c>).</summary>
public class NativeTransportException : AcceptanceException
{
    public NativeTransportException(string message)
        : base(message)
    {
    }

    public NativeTransportException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>The game was reachable and refused or failed the call (Go <c>ErrRefused</c> / <c>bridge.Refusal</c>).</summary>
public sealed class NativeRefusedException : AcceptanceException
{
    public NativeRefusedException(string tool, string cause, NativeResult result, string? message = null)
        : base(message ?? (cause.Length == 0 ? "native call refused: " + tool : "native call refused: " + tool + ": " + cause))
    {
        Tool = tool;
        Cause = cause;
        Result = result;
    }

    public string Tool { get; }

    /// <summary>The refusal's own account of itself: the native reason or the first line of the game's exception; empty when it said nothing.</summary>
    public string Cause { get; }

    public NativeResult Result { get; }
}

/// <summary>The session was closed or released (Go <c>ErrClosed</c>); calls through that harness are a stale reference.</summary>
public sealed class HarnessClosedException : AcceptanceException
{
    public HarnessClosedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// How the harness reaches the game: one call per native tool. The shipped implementation is
/// <see cref="HttpNativeTransport"/> over the mod's local channel (#3083); tests and replays substitute their own.
/// </summary>
public interface INativeTransport : IAsyncDisposable
{
    /// <summary>
    /// Runs <paramref name="tool"/> (a name such as <c>rimgovernor/observations_read_status</c>) with its JSON
    /// arguments. A tool that takes the ProtoJSON <c>request</c> argument is called with an object holding only a
    /// string member <c>request</c>, and its reply's structured content is <c>{"payload": "&lt;ProtoJSON&gt;"}</c>.
    /// </summary>
    /// <exception cref="NativeRefusedException">The game refused the call.</exception>
    /// <exception cref="NativeTransportException">The call did not reach the game or the channel does not route the tool.</exception>
    Task<NativeResult> CallAsync(string tool, JsonObject arguments, NativeCallOptions options, CancellationToken cancellationToken);

    /// <summary>The tool names the game registers, or null when the transport cannot enumerate them (callers then assume a tool is present).</summary>
    Task<IReadOnlyList<string>?> ToolNamesAsync(CancellationToken cancellationToken);

    /// <summary>The blocking attention the game is holding, as the receipt shows it; null when the transport has no attention channel.</summary>
    Task<NativeResult?> GetAttentionAsync(CancellationToken cancellationToken) => Task.FromResult<NativeResult?>(null);

    /// <summary>Acknowledges one attention by id; null when the transport has no attention channel.</summary>
    Task<NativeResult?> AckAttentionAsync(string attentionId, CancellationToken cancellationToken) => Task.FromResult<NativeResult?>(null);
}
