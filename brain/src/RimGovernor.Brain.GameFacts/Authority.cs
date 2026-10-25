using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Authority;

/// <summary>
/// There is exactly one bot process and one local human player. Auto means the
/// bot holds authority; Manual means it does not. There is no negotiated lease,
/// no session identity, and no second "direction" concept to arbitrate between
/// controllers, because there is only ever one controller.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Mode>))]
public enum Mode
{
    [WireName("MODE_AUTO")]
    Auto = 1,
    [WireName("MODE_MANUAL")]
    Manual = 2,
}

[JsonConverter(typeof(WireEnumConverter<RevocationReason>))]
public enum RevocationReason
{
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
    public required RevocationReason Reason { get; init; }
}

public sealed record ActiveAuthority
{
    public required Mode Mode { get; init; }
}

public sealed record Status
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

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

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(RimGovernor.Brain.GameFacts.Common.Unavailable Value) : StatusState;

    public static implicit operator StatusState(RimGovernor.Brain.GameFacts.Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Inactive</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public InactiveAuthority? Inactive => (this as InactiveCase)?.Value;

    public sealed record InactiveCase(InactiveAuthority Value) : StatusState;

    public static implicit operator StatusState(InactiveAuthority value) => new InactiveCase(value);

    /// <summary>The <c>Active</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ActiveAuthority? Active => (this as ActiveCase)?.Value;

    public sealed record ActiveCase(ActiveAuthority Value) : StatusState;

    public static implicit operator StatusState(ActiveAuthority value) => new ActiveCase(value);
}

public sealed record Granted
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public ActiveAuthority? Authority { get; init; }
}

public sealed record Revoked
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public InactiveAuthority? Authority { get; init; }
}

public sealed record ControlReply
{
    public ControlReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="ControlReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ControlReplyOutcome.GrantedCase), "granted")]
[JsonDerivedType(typeof(ControlReplyOutcome.RevokedCase), "revoked")]
[JsonDerivedType(typeof(ControlReplyOutcome.FailureCase), "failure")]
public abstract record ControlReplyOutcome
{
    private protected ControlReplyOutcome() { }

    /// <summary>The <c>Granted</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Granted? Granted => (this as GrantedCase)?.Value;

    public sealed record GrantedCase(Granted Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(Granted value) => new GrantedCase(value);

    /// <summary>The <c>Revoked</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Revoked? Revoked => (this as RevokedCase)?.Value;

    public sealed record RevokedCase(Revoked Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(Revoked value) => new RevokedCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}
