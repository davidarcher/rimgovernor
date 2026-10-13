using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ErrContract</c>: a native reply or a request broke the bridge contract (Go's <c>contract(format, ...)</c>).
/// Callers that tested <c>errors.Is(err, ErrContract)</c> catch this type.
/// </summary>
public sealed class BridgeContractException(string message) : Exception("bridge contract failure: " + message);

/// <summary>
/// Go's <c>ErrRefused</c>: a native read was refused. <see cref="Refusal"/> and <see cref="NativeFailureException"/>
/// are the typed forms; the base type itself is thrown where Go returned the bare sentinel.
/// </summary>
public class BridgeRefusedException(string message) : Exception(message)
{
    public BridgeRefusedException()
        : this("bridge read refused") { }
}

/// <summary>Go's <c>ErrUnavailable</c>: native observation is unavailable (no game, no map, or a typed unavailable reply).</summary>
public class BridgeUnavailableException(string message) : Exception(message)
{
    public BridgeUnavailableException()
        : this("native observation unavailable") { }
}

/// <summary>Go's <c>NativeFailure</c>: an explicit typed pre-admission refusal, never a transport inference.</summary>
public sealed class NativeFailureException(Failure value, Result receipt) : BridgeRefusedException(Describe(value))
{
    public Failure Value { get; } = value;

    public Result Receipt { get; } = receipt;

    private static string Describe(Failure value) =>
        value.Detail is not null && (value.Detail ?? "").Length != 0
            ? "native read refused: " + value.Code.GetValueOrDefault() + ": " + (value.Detail ?? "")
            : "native read refused: " + value.Code.GetValueOrDefault();
}

/// <summary>Go's <c>NativeUnavailable</c>: a typed unavailable reply.</summary>
public sealed class NativeUnavailableException(Unavailable value, Result receipt) : BridgeUnavailableException(Describe(value))
{
    public Unavailable Value { get; } = value;

    public Result Receipt { get; } = receipt;

    private static string Describe(Unavailable value) =>
        value.Detail is not null && (value.Detail ?? "").Length != 0
            ? "native read unavailable: " + value.Reason.GetValueOrDefault() + ": " + (value.Detail ?? "")
            : "native read unavailable: " + value.Reason.GetValueOrDefault();
}

/// <summary>Go's <c>Refusal</c>: a named read the native side refused, with the refusal's own account of itself.</summary>
public sealed class Refusal : BridgeRefusedException
{
    public Refusal(string tool, string cause, Result result)
        : base(cause.Length == 0 ? "bridge read refused: " + tool : "bridge read refused: " + tool + ": " + cause)
    {
        Tool = tool;
        Cause = cause;
        Result = result;
    }

    public string Tool { get; }

    /// <summary>The refusal's own account of itself; empty when the result says nothing.</summary>
    public string Cause { get; }

    public Result Result { get; }
}

/// <summary>
/// Go's <c>ClockUncertain</c>: a possibly applied clock operation. A retained canonical uncertain receipt is
/// carried alongside when available.
/// </summary>
public sealed class ClockUncertainException(Exception cause, Result receipt) : Exception("clock outcome uncertain: " + cause.Message, cause)
{
    public Result Receipt { get; } = receipt;
}
