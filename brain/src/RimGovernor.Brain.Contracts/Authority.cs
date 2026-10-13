using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Authority;

/// <summary>
/// There is exactly one bot process and one local human player. Auto means the
/// bot holds authority; Manual means it does not. There is no negotiated lease,
/// no session identity, and no second "direction" concept to arbitrate between
/// controllers, because there is only ever one controller.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Mode>))]
public enum Mode
{
    [WireName("MODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("MODE_AUTO")]
    Auto = 1,
    [WireName("MODE_MANUAL")]
    Manual = 2,
}

[JsonConverter(typeof(WireEnumConverter<RevocationReason>))]
public enum RevocationReason
{
    [WireName("REVOCATION_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("REVOCATION_REASON_NONE")]
    None = 1,
    [WireName("REVOCATION_REASON_MANUAL")]
    Manual = 2,
    /// <summary>
    /// Native player action detected by the Harmony hooks (order issued, drafted,
    /// bill/schedule/settings edited directly, etc.) while the bot held authority.
    /// </summary>
    [WireName("REVOCATION_REASON_PLAYER_CONTROL")]
    PlayerControl = 3,
    /// <summary>
    /// A specific native player order observed by the hooks; kept distinct from
    /// PLAYER_CONTROL because acceptance tests assert on this exact reason and it
    /// carries a different in-flight-write carve-out (see NativeControlAuthority).
    /// </summary>
    [WireName("REVOCATION_REASON_EXTERNAL_ORDER")]
    ExternalOrder = 4,
    [WireName("REVOCATION_REASON_IDENTITY_CHANGED")]
    IdentityChanged = 5,
    [WireName("REVOCATION_REASON_DISCONNECT")]
    Disconnect = 6,
    [WireName("REVOCATION_REASON_SHUTDOWN")]
    Shutdown = 7,
    [WireName("REVOCATION_REASON_HOOKS_UNAVAILABLE")]
    HooksUnavailable = 8,
    [WireName("REVOCATION_REASON_GENERATION_EXHAUSTED")]
    GenerationExhausted = 9,
    /// <summary>
    /// Authority ended because its control prerequisites became unavailable.
    /// </summary>
    [WireName("REVOCATION_REASON_UNAVAILABLE")]
    Unavailable = 10,
}

public sealed record InactiveAuthority
{
    public RevocationReason? Reason { get; init; }
}

public sealed record ActiveAuthority
{
    public Mode? Mode { get; init; }
}

public sealed record Status
{
    public Common.ObservationContext? Context { get; init; }

    public StatusState? State { get; init; }
}

/// <summary>The <c>state</c> choice of <see cref="Status"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusState.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(StatusState.InactiveCase), "inactive")]
[JsonDerivedType(typeof(StatusState.ActiveCase), "active")]
public abstract record StatusState
{
    private protected StatusState() { }

    public sealed record UnavailableCase(Common.Unavailable Value) : StatusState;

    public static implicit operator StatusState(Common.Unavailable value) => new UnavailableCase(value);

    public sealed record InactiveCase(InactiveAuthority Value) : StatusState;

    public static implicit operator StatusState(InactiveAuthority value) => new InactiveCase(value);

    public sealed record ActiveCase(ActiveAuthority Value) : StatusState;

    public static implicit operator StatusState(ActiveAuthority value) => new ActiveCase(value);
}

public sealed record StatusRequest
{
    public Common.Identity? Identity { get; init; }
}

public sealed record StatusReply
{
    public StatusReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="StatusReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusReplyOutcome.StatusCase), "status")]
[JsonDerivedType(typeof(StatusReplyOutcome.FailureCase), "failure")]
public abstract record StatusReplyOutcome
{
    private protected StatusReplyOutcome() { }

    public sealed record StatusCase(Status Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(Status value) => new StatusCase(value);

    public sealed record FailureCase(Common.Failure Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// SetMode is the only way to change authority explicitly. Auto grants the bot
/// authority outright; Manual revokes it. There is no acquire/renew handshake.
/// </summary>
public sealed record SetMode
{
    public Common.Identity? Identity { get; init; }

    public ulong? ExpectedGeneration { get; init; }

    public Mode? Mode { get; init; }
}

public sealed record Revoke
{
    public Common.Identity? Identity { get; init; }

    public ulong? ExpectedGeneration { get; init; }

    public RevocationReason? Reason { get; init; }
}

/// <summary>
/// Trusted control-plane view change. Neither moves pawns nor grants authority.
/// </summary>
public sealed record FocusMap
{
    public Common.Identity? Identity { get; init; }

    public ulong? ExpectedGeneration { get; init; }

    public Common.Identity? Target { get; init; }
}

public sealed record FocusedMap
{
    public Common.ObservationContext? Context { get; init; }
}

public sealed record ControlRequest
{
    public ControlRequestOperation? Operation { get; init; }
}

/// <summary>The <c>operation</c> choice of <see cref="ControlRequest"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ControlRequestOperation.SetModeCase), "setMode")]
[JsonDerivedType(typeof(ControlRequestOperation.RevokeCase), "revoke")]
[JsonDerivedType(typeof(ControlRequestOperation.FocusMapCase), "focusMap")]
public abstract record ControlRequestOperation
{
    private protected ControlRequestOperation() { }

    public sealed record SetModeCase(SetMode Value) : ControlRequestOperation;

    public static implicit operator ControlRequestOperation(SetMode value) => new SetModeCase(value);

    public sealed record RevokeCase(Revoke Value) : ControlRequestOperation;

    public static implicit operator ControlRequestOperation(Revoke value) => new RevokeCase(value);

    public sealed record FocusMapCase(FocusMap Value) : ControlRequestOperation;

    public static implicit operator ControlRequestOperation(FocusMap value) => new FocusMapCase(value);
}

public sealed record Granted
{
    public Common.ObservationContext? Context { get; init; }

    public ActiveAuthority? Authority { get; init; }
}

public sealed record Revoked
{
    public Common.ObservationContext? Context { get; init; }

    public InactiveAuthority? Authority { get; init; }
}

public sealed record ControlReply
{
    public ControlReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ControlReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ControlReplyOutcome.GrantedCase), "granted")]
[JsonDerivedType(typeof(ControlReplyOutcome.RevokedCase), "revoked")]
[JsonDerivedType(typeof(ControlReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(ControlReplyOutcome.FocusedMapCase), "focusedMap")]
public abstract record ControlReplyOutcome
{
    private protected ControlReplyOutcome() { }

    public sealed record GrantedCase(Granted Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(Granted value) => new GrantedCase(value);

    public sealed record RevokedCase(Revoked Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(Revoked value) => new RevokedCase(value);

    public sealed record FailureCase(Common.Failure Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(Common.Failure value) => new FailureCase(value);

    public sealed record FocusedMapCase(FocusedMap Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(FocusedMap value) => new FocusedMapCase(value);
}

/// <summary>
/// Writes atomically require current colony/load/map identity, a fresh attempt,
/// and the generation used to plan the command. Player interruptions advance
/// the generation; Auto has a single bot owner.
/// </summary>
public sealed record WritePrecondition
{
    public Common.Identity? Identity { get; init; }

    public ulong? ExpectedGeneration { get; init; }

    public Common.AttemptKey? Attempt { get; init; }
}

