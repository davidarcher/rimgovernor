using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.NativeFacade;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The typed native client over <see cref="INativeFacade"/> (#3113): what Go's <c>bridge.Client</c> was to the
/// planners, minus the transport (GABP, the shared-memory ring, leases, reconnect, blind ticks). Each Go
/// <c>Client.ReadX</c> is one <c>ReadXAsync</c> core method here that builds the request, makes the facade call,
/// unwraps the reply's outcome and validates it the way the Go decoder did.
/// </summary>
/// <remarks>
/// <para>
/// Conventions every core method follows:
/// </para>
/// <list type="bullet">
/// <item>A Go method returning <c>(T, Result, error)</c> is <c>Task&lt;(T Value, BridgeResult Receipt)&gt;</c>; the receipt is
/// <see cref="NoReceipt"/> because nothing crosses a transport in this process. A Go method with no <c>Result</c>
/// is <c>Task&lt;T&gt;</c>.</item>
/// <item>A failure arm throws what <see cref="Failure"/> builds, an unavailable arm what <see cref="Unavailable"/>
/// builds, a malformed reply what <see cref="BridgeFunctions.Contract"/> builds (Go <c>failure</c>, <c>unavailable</c>
/// and <c>contract</c>). Cancellation is the token's <see cref="OperationCanceledException"/>.</item>
/// <item>A reply is never mutated; values handed back are immutable by convention.</item>
/// <item>The Native/Source interfaces the planners are written against are bound to these core methods in
/// <c>FacadeNativeClient.Interfaces.cs</c> (explicit implementations where the interface shape differs from the
/// core method's).</item>
/// </list>
/// </remarks>
internal sealed partial class FacadeNativeClient(INativeFacade facade)
{
    /// <summary>The facade every core method calls.</summary>
    internal INativeFacade Facade { get; } = facade ?? throw new ArgumentNullException(nameof(facade));

    /// <summary>The receipt of every call: no transport envelope exists in process (Go <c>Result{}</c>).</summary>
    internal static BridgeResult NoReceipt => BridgeResult.Empty;

    /// <summary>Go <c>failure(v, raw)</c>: the typed refusal of a failure arm, after checking the failure itself is well formed.</summary>
    internal static Exception Failure(PbCommon.Failure? value)
    {
        BridgeFunctions.ValidateFailure(value);
        return new NativeFailureException(value!, NoReceipt);
    }

    /// <summary>Go <c>unavailable(v, raw)</c>: the typed unavailability of an unavailable arm, after checking it is well formed.</summary>
    internal static Exception Unavailable(PbCommon.Unavailable? value)
    {
        BridgeFunctions.ValidateUnavailable(value);
        return new NativeUnavailableException(value!, NoReceipt);
    }
}
