using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Presentation;

[JsonConverter(typeof(WireEnumConverter<OverlayStyle>))]
public enum OverlayStyle
{
    [WireName("OVERLAY_STYLE_UNSPECIFIED")]
    Unspecified = 0,
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
    public uint? TotalCount { get; init; }

    public uint? ReturnedCount { get; init; }

    public bool? Complete { get; init; }

    public bool? Truncated { get; init; }
}

public sealed record MapPoint
{
    public double? X { get; init; }

    public double? Z { get; init; }
}

public sealed record MapRect
{
    public int? MinX { get; init; }

    public int? MinZ { get; init; }

    public int? MaxX { get; init; }

    public int? MaxZ { get; init; }
}

public sealed record CameraState
{
    public Common.ObservationContext? Context { get; init; }

    public MapPoint? MapPosition { get; init; }

    public double? RootSize { get; init; }

    public double? ZoomRootSize { get; init; }

    public double? MinimumRootSize { get; init; }

    public double? MaximumRootSize { get; init; }

    public string? NativeZoomRange { get; init; }

    public MapRect? ViewRect { get; init; }
}

public sealed record CameraReply
{
    public CameraReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="CameraReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CameraReplyOutcome.CameraCase), "camera")]
[JsonDerivedType(typeof(CameraReplyOutcome.FailureCase), "failure")]
public abstract record CameraReplyOutcome
{
    private protected CameraReplyOutcome() { }

    /// <summary>The <c>Camera</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CameraState? Camera => (this as CameraCase)?.Value;

    public sealed record CameraCase(CameraState Value) : CameraReplyOutcome;

    public static implicit operator CameraReplyOutcome(CameraState value) => new CameraCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : CameraReplyOutcome;

    public static implicit operator CameraReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Open native object kinds/DefNames remain strings. IDs never derive from labels.
/// </summary>
public sealed record SelectedObject
{
    public string? Id { get; init; }

    public string? NativeKind { get; init; }

    public string? NativeType { get; init; }

    public string? Label { get; init; }

    public string? DefName { get; init; }

    public int? MapId { get; init; }

    public Common.Cell? Position { get; init; }

    public string? InspectLabel { get; init; }

    public string? InspectText { get; init; }
}

public sealed record SelectionSnapshot
{
    public Common.ObservationContext? Context { get; init; }

    public string? Fingerprint { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SelectedObject>))]
    public EquatableArray<SelectedObject> SelectedObjects { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record SelectionReply
{
    public SelectionReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="SelectionReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SelectionReplyOutcome.SelectionCase), "selection")]
[JsonDerivedType(typeof(SelectionReplyOutcome.FailureCase), "failure")]
public abstract record SelectionReplyOutcome
{
    private protected SelectionReplyOutcome() { }

    /// <summary>The <c>Selection</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SelectionSnapshot? Selection => (this as SelectionCase)?.Value;

    public sealed record SelectionCase(SelectionSnapshot Value) : SelectionReplyOutcome;

    public static implicit operator SelectionReplyOutcome(SelectionSnapshot value) => new SelectionCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : SelectionReplyOutcome;

    public static implicit operator SelectionReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ColonistRosterRequest
{
    public Common.Identity? Identity { get; init; }

    public bool? CurrentMapOnly { get; init; }

    /// <summary>
    /// include_dossier asks the controller to attach each colonist's row of
    /// its latest bundle pawn table; native never fills dossier.
    /// </summary>
    public bool? IncludeDossier { get; init; }
}

public sealed record ColonistReference
{
    public string? PawnId { get; init; }

    public string? Name { get; init; }

    public int? MapId { get; init; }

    public bool? Spawned { get; init; }

    public Common.Cell? Position { get; init; }

    /// <summary>
    /// Joined by the controller when the request asked for it;
    /// dossier.pawn.id == pawn_id.
    /// </summary>
    public Observations.PawnState? Dossier { get; init; }
}

public sealed record ColonistRoster
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ColonistReference>))]
    public EquatableArray<ColonistReference> Colonists { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record ColonistRosterReply
{
    public ColonistRosterReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ColonistRosterReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ColonistRosterReplyOutcome.RosterCase), "roster")]
[JsonDerivedType(typeof(ColonistRosterReplyOutcome.FailureCase), "failure")]
public abstract record ColonistRosterReplyOutcome
{
    private protected ColonistRosterReplyOutcome() { }

    /// <summary>The <c>Roster</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ColonistRoster? Roster => (this as RosterCase)?.Value;

    public sealed record RosterCase(ColonistRoster Value) : ColonistRosterReplyOutcome;

    public static implicit operator ColonistRosterReplyOutcome(ColonistRoster value) => new RosterCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ColonistRosterReplyOutcome;

    public static implicit operator ColonistRosterReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record LookTarget
{
    public string? Id { get; init; }

    public string? NativeType { get; init; }

    public string? Label { get; init; }

    public string? DefName { get; init; }

    public int? MapId { get; init; }

    public Common.Cell? Position { get; init; }

    /// <summary>
    /// Native PlanetTile textual ID can include a layer; do not parse a guessed int.
    /// </summary>
    public string? WorldTileId { get; init; }
}

public sealed record LookTargets
{
    public LookTarget? Primary { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LookTarget>))]
    public EquatableArray<LookTarget> Targets { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record LetterChoice
{
    public uint? Index { get; init; }

    public string? Text { get; init; }

    public bool? Disabled { get; init; }
}

public sealed record Letter
{
    public string? Id { get; init; }

    public string? NativeType { get; init; }

    public string? DefName { get; init; }

    public string? Label { get; init; }

    public string? Text { get; init; }

    public long? ArrivalTick { get; init; }

    public long? AgeTicks { get; init; }

    public LookTargets? LookTargets { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LetterChoice>))]
    public EquatableArray<LetterChoice> Choices { get; init; }

    public Listing? ChoicesListing { get; init; }
}

public sealed record TransientMessage
{
    public string? Id { get; init; }

    public string? Text { get; init; }

    public long? AgeTicks { get; init; }

    public LookTargets? LookTargets { get; init; }
}

public sealed record Alert
{
    public string? Id { get; init; }

    public string? NativeType { get; init; }

    public string? Label { get; init; }

    public bool? Active { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LookTarget>))]
    public EquatableArray<LookTarget> Targets { get; init; }

    public Listing? Listing { get; init; }

    public Common.Unavailable? ReadIssue { get; init; }
}

public sealed record Letters
{
    [JsonPropertyName("letters")]
    [JsonConverter(typeof(EquatableArrayJsonConverter<Letter>))]
    public EquatableArray<Letter> Items { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record Messages
{
    [JsonPropertyName("messages")]
    [JsonConverter(typeof(EquatableArrayJsonConverter<TransientMessage>))]
    public EquatableArray<TransientMessage> Items { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record Alerts
{
    [JsonPropertyName("alerts")]
    [JsonConverter(typeof(EquatableArrayJsonConverter<Alert>))]
    public EquatableArray<Alert> Items { get; init; }

    public Listing? Listing { get; init; }

    public string? SnapshotFingerprint { get; init; }
}

public sealed record LetterSection
{
    public LetterSectionOutcome? Outcome { get; init; }
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
    public MessageSectionOutcome? Outcome { get; init; }
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
    public AlertSectionOutcome? Outcome { get; init; }
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

    public uint? LetterLimit { get; init; }

    public uint? MessageLimit { get; init; }

    public uint? AlertLimit { get; init; }

    /// <summary>
    /// Inclusion defaults true; explicit false omits that section.
    /// </summary>
    public bool? IncludeLetters { get; init; }

    public bool? IncludeMessages { get; init; }

    public bool? IncludeAlerts { get; init; }
}

public sealed record NotificationsReply
{
    public NotificationsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="NotificationsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NotificationsReplyOutcome.NotificationsCase), "notifications")]
[JsonDerivedType(typeof(NotificationsReplyOutcome.FailureCase), "failure")]
public abstract record NotificationsReplyOutcome
{
    private protected NotificationsReplyOutcome() { }

    /// <summary>The <c>Notifications</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public NotificationsSnapshot? Notifications => (this as NotificationsCase)?.Value;

    public sealed record NotificationsCase(NotificationsSnapshot Value) : NotificationsReplyOutcome;

    public static implicit operator NotificationsReplyOutcome(NotificationsSnapshot value) => new NotificationsCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : NotificationsReplyOutcome;

    public static implicit operator NotificationsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Claimed direction/viewer fields never authenticate their own authority.
/// </summary>
public sealed record PlayerIdentity
{
    public Common.Identity? Identity { get; init; }

    public ulong? PlayerDirection { get; init; }

    public string? ViewerId { get; init; }
}

public sealed record RenderStatus
{
    public Common.ObservationContext? Context { get; init; }

    public bool? Supported { get; init; }

    public bool? Suspended { get; init; }

    public bool? WindowVisible { get; init; }

    public uint? RemainingLeaseMs { get; init; }

    public Common.Unavailable? Unavailable { get; init; }
}

public sealed record RenderDemand
{
    public PlayerIdentity? Viewer { get; init; }

    public uint? LeaseSeconds { get; init; }
}

public sealed record RenderReply
{
    public RenderReplyOutcome? Outcome { get; init; }
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
/// the one layer it names and leaves the others; the Go side resends after
/// a load. Layer ids group producers: "layout", "heat.colonist", ...
/// </summary>
public sealed record OverlayColor
{
    public float? R { get; init; }

    public float? G { get; init; }

    public float? B { get; init; }

    public float? A { get; init; }
}

/// <summary>
/// A row run: cells (x .. x+length-1, z).
/// </summary>
public sealed record OverlayRun
{
    public int? X { get; init; }

    public int? Z { get; init; }

    public int? Length { get; init; }
}

public sealed record OverlayShape
{
    public OverlayColor? Color { get; init; }

    public OverlayStyle Style { get; init; }

    /// <summary>
    /// Inclusive.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<MapRect>))]
    public EquatableArray<MapRect> Rects { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayRun>))]
    public EquatableArray<OverlayRun> Runs { get; init; }

    /// <summary>
    /// Drawn centre to centre in the shape's color.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayLine>))]
    public EquatableArray<OverlayLine> Lines { get; init; }
}

/// <summary>
/// A segment between two map cells; arrow puts a head on `to`.
/// </summary>
public sealed record OverlayLine
{
    public Common.Cell? From { get; init; }

    public Common.Cell? To { get; init; }

    public bool? Arrow { get; init; }
}

/// <summary>
/// Text drawn at a map cell while the layer is visible.
/// </summary>
public sealed record OverlayLabel
{
    public string? Text { get; init; }

    public Common.Cell? Cell { get; init; }
}

public sealed record OverlayRequest
{
    public Common.Identity? Identity { get; init; }

    public string? LayerId { get; init; }

    /// <summary>
    /// false removes the layer.
    /// </summary>
    public bool? Enabled { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayShape>))]
    public EquatableArray<OverlayShape> Shapes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayLabel>))]
    public EquatableArray<OverlayLabel> Labels { get; init; }
}

public sealed record OverlayApplied
{
    public Common.ObservationContext? Context { get; init; }

    /// <summary>
    /// Cells the layer covers.
    /// </summary>
    public uint? Cells { get; init; }
}

public sealed record OverlayReply
{
    public OverlayReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="OverlayReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OverlayReplyOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(OverlayReplyOutcome.FailureCase), "failure")]
public abstract record OverlayReplyOutcome
{
    private protected OverlayReplyOutcome() { }

    /// <summary>The <c>Applied</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public OverlayApplied? Applied => (this as AppliedCase)?.Value;

    public sealed record AppliedCase(OverlayApplied Value) : OverlayReplyOutcome;

    public static implicit operator OverlayReplyOutcome(OverlayApplied value) => new AppliedCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : OverlayReplyOutcome;

    public static implicit operator OverlayReplyOutcome(Common.Failure value) => new FailureCase(value);
}

