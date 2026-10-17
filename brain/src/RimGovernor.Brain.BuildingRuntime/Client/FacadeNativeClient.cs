using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The typed native client over <see cref="INativeFacade"/> (#3113): what Go's <c>bridge.Client</c> was to the
/// planners, minus the transport (there is none: the facade is an in-process call). Each Go
/// <c>Client.ReadX</c> is one <c>ReadXAsync</c> core method here that builds and checks the request, makes the facade
/// call and unwraps the reply's outcome. The reply is native's own typed record, built in process, so it is not
/// re-validated.
/// </summary>
/// <remarks>
/// <para>
/// Conventions every core method follows:
/// </para>
/// <list type="bullet">
/// <item>A Go method returning <c>(T, Result, error)</c> is <c>Task&lt;T&gt;</c>: nothing crosses a transport in this
/// process, so there is no receipt to return.</item>
/// <item>A failure arm throws what <see cref="Failure"/> builds, an unavailable arm what <see cref="Unavailable"/>
/// builds, a malformed request what <see cref="BridgeFunctions.Contract"/> builds (Go <c>failure</c>, <c>unavailable</c>
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

    /// <summary>The receipt the typed native exceptions carry: no transport envelope exists in process (Go <c>Result{}</c>).</summary>
    internal static BridgeResult NoReceipt => BridgeResult.Empty;

    /// <summary>Go <c>failure(v, raw)</c>: the typed refusal of a failure arm.</summary>
    internal static Exception Failure(PbCommon.Failure value) => new NativeFailureException(value, NoReceipt);

    /// <summary>Go <c>unavailable(v, raw)</c>: the typed unavailability of an unavailable arm.</summary>
    internal static Exception Unavailable(PbCommon.Unavailable value) => new NativeUnavailableException(value, NoReceipt);
}
