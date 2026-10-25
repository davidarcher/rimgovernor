using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Presentation;

public sealed record Listing
{
    public required uint TotalCount { get; init; }

    public required uint ReturnedCount { get; init; }

    public required bool Complete { get; init; }
}

public sealed record MapRect
{
    public required int MinX { get; init; }

    public required int MinZ { get; init; }

    public required int MaxX { get; init; }

    public required int MaxZ { get; init; }
}

public sealed record CameraState
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }
}

public sealed record SelectionSnapshot
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }
}

public sealed record ColonistReference
{
    public required PawnId PawnId { get; init; }

    public required int MapId { get; init; }
}

public sealed record ColonistRoster
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ColonistReference>))]
    public required EquatableArray<ColonistReference> Colonists { get; init; }
}

public sealed record Letter
{
    public required string Id { get; init; }

    public required string NativeType { get; init; }

    public required string DefName { get; init; }

    public required string Label { get; init; }

    public required long ArrivalTick { get; init; }
}

public sealed record Letters
{
    [JsonPropertyName("letters")]
    [JsonConverter(typeof(EquatableArrayJsonConverter<Letter>))]
    public required EquatableArray<Letter> Items { get; init; }

    public required Listing Listing { get; init; }
}

public sealed record Messages
{
    public required Listing Listing { get; init; }
}

public sealed record Alerts
{
    public required Listing Listing { get; init; }

    public required string SnapshotFingerprint { get; init; }
}

public sealed record LetterSection
{
    public required LetterSectionOutcome Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="LetterSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LetterSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(LetterSectionOutcome.UnavailableCase), "unavailable")]
public abstract record LetterSectionOutcome
{
    private protected LetterSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Letters? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(Letters Value) : LetterSectionOutcome;

    public static implicit operator LetterSectionOutcome(Letters value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(RimGovernor.Brain.GameFacts.Common.Unavailable Value) : LetterSectionOutcome;

    public static implicit operator LetterSectionOutcome(RimGovernor.Brain.GameFacts.Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record MessageSection
{
    public required MessageSectionOutcome Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="MessageSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(MessageSectionOutcome.UnavailableCase), "unavailable")]
public abstract record MessageSectionOutcome
{
    private protected MessageSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Messages? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(Messages Value) : MessageSectionOutcome;

    public static implicit operator MessageSectionOutcome(Messages value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(RimGovernor.Brain.GameFacts.Common.Unavailable Value) : MessageSectionOutcome;

    public static implicit operator MessageSectionOutcome(RimGovernor.Brain.GameFacts.Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record AlertSection
{
    public required AlertSectionOutcome Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="AlertSection"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AlertSectionOutcome.ObservedCase), "observed")]
[JsonDerivedType(typeof(AlertSectionOutcome.UnavailableCase), "unavailable")]
public abstract record AlertSectionOutcome
{
    private protected AlertSectionOutcome() { }

    /// <summary>The <c>Observed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Alerts? Observed => (this as ObservedCase)?.Value;

    public sealed record ObservedCase(Alerts Value) : AlertSectionOutcome;

    public static implicit operator AlertSectionOutcome(Alerts value) => new ObservedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(RimGovernor.Brain.GameFacts.Common.Unavailable Value) : AlertSectionOutcome;

    public static implicit operator AlertSectionOutcome(RimGovernor.Brain.GameFacts.Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// An absent section was not requested; a present unavailable arm records a failed read.
/// </summary>
public sealed record NotificationsSnapshot
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public LetterSection? Letters { get; init; }

    public MessageSection? Messages { get; init; }

    public AlertSection? Alerts { get; init; }
}

/// <summary>
/// A claimed viewer field never authenticates its own authority.
/// </summary>
public sealed record PlayerIdentity
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public string? ViewerId { get; init; }
}

public sealed record RenderStatus
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public required bool Supported { get; init; }

    public required bool Suspended { get; init; }

    public required bool WindowVisible { get; init; }

    public required uint RemainingLeaseMs { get; init; }
}

public sealed record RenderReply
{
    public required RenderReplyOutcome Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="RenderReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RenderReplyOutcome.StatusCase), "status")]
[JsonDerivedType(typeof(RenderReplyOutcome.FailureCase), "failure")]
public abstract record RenderReplyOutcome
{
    private protected RenderReplyOutcome() { }

    /// <summary>The <c>Status</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RenderStatus? Status => (this as StatusCase)?.Value;

    public sealed record StatusCase(RenderStatus Value) : RenderReplyOutcome;

    public static implicit operator RenderReplyOutcome(RenderStatus value) => new StatusCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : RenderReplyOutcome;

    public static implicit operator RenderReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}

public sealed record OverlayReply
{
    public required OverlayReplyOutcome Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="OverlayReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OverlayReplyOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(OverlayReplyOutcome.FailureCase), "failure")]
public abstract record OverlayReplyOutcome
{
    private protected OverlayReplyOutcome() { }

    /// <summary>The layer was applied; the case carries no fields.</summary>
    public sealed record AppliedCase : OverlayReplyOutcome;

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : OverlayReplyOutcome;

    public static implicit operator OverlayReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}
