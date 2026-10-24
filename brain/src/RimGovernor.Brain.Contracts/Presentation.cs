using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Presentation;

[JsonConverter(typeof(WireEnumConverter<OverlayStyle>))]
public enum OverlayStyle
{
    /// <summary>
    /// Every cell filled.
    /// </summary>
    [WireName("OVERLAY_STYLE_FILL")]
    Fill = 1,
    /// <summary>
    /// Only the boundary edges of the shape's cells.
    /// </summary>
    [WireName("OVERLAY_STYLE_OUTLINE")]
    Outline = 2,
}

/// <summary>
/// Contract-only: capabilities must deny all player-control RPCs to automation.
/// Every optional fact requires Has* checks. Bounds/admission live in normal code.
/// </summary>
public sealed record ReadRequest
{
    public Common.Identity? Identity { get; init; }
}

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
    public Common.ObservationContext? Context { get; init; }
}

public sealed record SelectionSnapshot
{
    public Common.ObservationContext? Context { get; init; }
}

public sealed record ColonistRosterRequest
{
    public Common.Identity? Identity { get; init; }

    public required bool CurrentMapOnly { get; init; }
}

public sealed record ColonistReference
{
    public required PawnId PawnId { get; init; }

    public required int MapId { get; init; }
}

public sealed record ColonistRoster
{
    public required Common.ObservationContext Context { get; init; }

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
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : LetterSectionOutcome;

    public static implicit operator LetterSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
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
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : MessageSectionOutcome;

    public static implicit operator MessageSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
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
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : AlertSectionOutcome;

    public static implicit operator AlertSectionOutcome(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// An absent section was not requested; a present unavailable arm records a failed read.
/// </summary>
public sealed record NotificationsSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public LetterSection? Letters { get; init; }

    public MessageSection? Messages { get; init; }

    public AlertSection? Alerts { get; init; }
}

public sealed record NotificationsRequest
{
    public Common.Identity? Identity { get; init; }

    /// <summary>The newest letters to list, at least 1; read only when <see cref="IncludeLetters"/> is set.</summary>
    public required uint LetterLimit { get; init; }

    public required uint MessageLimit { get; init; }

    public required uint AlertLimit { get; init; }

    /// <summary>
    /// False omits that section from the snapshot.
    /// </summary>
    public required bool IncludeLetters { get; init; }

    public required bool IncludeMessages { get; init; }

    public required bool IncludeAlerts { get; init; }
}

/// <summary>
/// A claimed viewer field never authenticates its own authority.
/// </summary>
public sealed record PlayerIdentity
{
    public Common.Identity? Identity { get; init; }

    public string? ViewerId { get; init; }
}

public sealed record RenderStatus
{
    public Common.ObservationContext? Context { get; init; }

    public required bool Supported { get; init; }

    public required bool Suspended { get; init; }

    public required bool WindowVisible { get; init; }

    public required uint RemainingLeaseMs { get; init; }
}

public sealed record RenderDemand
{
    public required PlayerIdentity Viewer { get; init; }

    public required uint LeaseSeconds { get; init; }
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
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : RenderReplyOutcome;

    public static implicit operator RenderReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// RimGovernor map overlay: a native MapComponent draws named layers
/// as meshes and labels. Output only and never saved: each call replaces
/// the one layer it names and leaves the others; the brain resends after
/// a load. Layer ids group producers: "layout", "heat.colonist", ...
/// </summary>
public sealed record OverlayColor
{
    public required float R { get; init; }

    public required float G { get; init; }

    public required float B { get; init; }

    public required float A { get; init; }
}

/// <summary>
/// A row run: cells (x .. x+length-1, z).
/// </summary>
public sealed record OverlayRun
{
    public required int X { get; init; }

    public required int Z { get; init; }

    public required int Length { get; init; }
}

public sealed record OverlayShape
{
    public required OverlayColor Color { get; init; }

    public required OverlayStyle Style { get; init; }

    /// <summary>
    /// Inclusive.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<MapRect>))]
    public required EquatableArray<MapRect> Rects { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayRun>))]
    public required EquatableArray<OverlayRun> Runs { get; init; }

    /// <summary>
    /// Drawn centre to centre in the shape's color.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayLine>))]
    public required EquatableArray<OverlayLine> Lines { get; init; }
}

/// <summary>
/// A segment between two map cells; arrow puts a head on `to`.
/// </summary>
public sealed record OverlayLine
{
    public required Common.Cell From { get; init; }

    public required Common.Cell To { get; init; }

    public required bool Arrow { get; init; }
}

/// <summary>
/// Text drawn at a map cell while the layer is visible.
/// </summary>
public sealed record OverlayLabel
{
    public required string Text { get; init; }

    public required Common.Cell Cell { get; init; }
}

public sealed record OverlayRequest
{
    public required Common.Identity Identity { get; init; }

    public required string LayerId { get; init; }

    /// <summary>
    /// false removes the layer.
    /// </summary>
    public required bool Enabled { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayShape>))]
    public required EquatableArray<OverlayShape> Shapes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayLabel>))]
    public required EquatableArray<OverlayLabel> Labels { get; init; }
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
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : OverlayReplyOutcome;

    public static implicit operator OverlayReplyOutcome(Common.Failure value) => new FailureCase(value);
}

