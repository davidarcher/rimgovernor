using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// A native reply or a request broke the bridge contract.
/// Callers that tested <c>errors.Is(err, ErrContract)</c> catch this type.
/// </summary>
public sealed partial class BridgeContractException(string message) : Exception("bridge contract failure: " + message);

/// <summary>
/// A native read was refused. <see cref="ReadRefusedException"/> and <see cref="NativeFailureException"/>
/// are the typed forms; the base type itself is thrown where no typed form applies.
/// </summary>
public class BridgeRefusedException(string message) : Exception(message)
{
    public BridgeRefusedException()
        : this("bridge read refused") { }
}

/// <summary>Native observation is unavailable (no game, no map, or a typed unavailable reply).</summary>
public class BridgeUnavailableException(string message) : Exception(message)
{
    public BridgeUnavailableException()
        : this("native observation unavailable") { }
}

/// <summary>
/// A typed unavailable reply whose reason is NotLoaded: no colony map exists yet (main menu, between loads). An
/// expected state a caller may skip on, not a fault.
/// </summary>
public sealed class NotLoadedException(string message) : BridgeUnavailableException(message);

/// <summary>An explicit typed pre-admission refusal, never a transport inference.</summary>
public sealed class NativeFailureException(Failure value, Result receipt) : BridgeRefusedException(Describe(value))
{
    public Failure Value { get; } = value;

    public Result Receipt { get; } = receipt;

    private static string Describe(Failure value) =>
        value.Detail is not null && (value.Detail ?? "").Length != 0
            ? "native read refused: " + value.Code.GetValueOrDefault() + ": " + (value.Detail ?? "")
            : "native read refused: " + value.Code.GetValueOrDefault();
}

/// <summary>A typed unavailable reply.</summary>
public sealed class NativeUnavailableException(Unavailable value, Result receipt) : BridgeUnavailableException(Describe(value))
{
    public Unavailable Value { get; } = value;

    public Result Receipt { get; } = receipt;

    private static string Describe(Unavailable value) =>
        value.Detail is not null && (value.Detail ?? "").Length != 0
            ? "native read unavailable: " + value.Reason.GetValueOrDefault() + ": " + (value.Detail ?? "")
            : "native read unavailable: " + value.Reason.GetValueOrDefault();
}

/// <summary>
/// What a native call throws instead of answering a refusal or an unavailable reply: a facade read returns its payload
/// or throws one of these, and the HTTP channel maps them to a status and back (<c>NativeRoutes</c>).
/// </summary>
public static class NativeFaults
{
    /// <summary>The refusal <paramref name="failure"/> describes: a <see cref="NativeFailureException"/> carrying its code and detail.</summary>
    public static NativeFailureException Refused(Failure failure) => new(failure, Result.Empty);

    /// <summary>
    /// The unavailability <paramref name="unavailable"/> describes: <see cref="NotLoadedException"/> when no colony map is
    /// loaded (an expected state a caller may skip on), else a <see cref="NativeUnavailableException"/>.
    /// </summary>
    public static BridgeUnavailableException Unavailable(Unavailable unavailable)
    {
        ArgumentNullException.ThrowIfNull(unavailable);
        var value = new NativeUnavailableException(unavailable, Result.Empty);
        return unavailable.Reason == UnavailableReason.NotLoaded ? new NotLoadedException(value.Message) : value;
    }
}

/// <summary>A named read the native side refused, with the refusal's own account of itself.</summary>
public sealed class ReadRefusedException : BridgeRefusedException
{
    public ReadRefusedException(string tool, string cause, Result result)
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
