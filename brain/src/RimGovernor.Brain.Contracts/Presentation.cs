using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Presentation;

[JsonConverter(typeof(WireEnumConverter<MediaEncoding>))]
public enum MediaEncoding
{
    [WireName("MEDIA_ENCODING_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("MEDIA_ENCODING_PNG")]
    Png = 1,
    [WireName("MEDIA_ENCODING_JPEG")]
    Jpeg = 2,
    [WireName("MEDIA_ENCODING_H264")]
    H264 = 3,
    [WireName("MEDIA_ENCODING_RGBA32_BOTTOM_UP")]
    Rgba32BottomUp = 4,
    [WireName("MEDIA_ENCODING_BGRA32_TOP_DOWN")]
    Bgra32TopDown = 5,
}

[JsonConverter(typeof(WireEnumConverter<CaptureMethod>))]
public enum CaptureMethod
{
    [WireName("CAPTURE_METHOD_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("CAPTURE_METHOD_PRIVATE_PRESENTED_WINDOW")]
    PrivatePresentedWindow = 1,
    [WireName("CAPTURE_METHOD_ASYNC_GPU")]
    AsyncGpu = 2,
    [WireName("CAPTURE_METHOD_READ_PIXELS")]
    ReadPixels = 3,
}

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

public sealed record ScreenRect
{
    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }
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

    public bool? ZoomExtensionEnabled { get; init; }

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

    public sealed record CameraCase(CameraState Value) : CameraReplyOutcome;

    public static implicit operator CameraReplyOutcome(CameraState value) => new CameraCase(value);

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

    public uint? VisibleGizmoCount { get; init; }
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

    public sealed record SelectionCase(SelectionSnapshot Value) : SelectionReplyOutcome;

    public static implicit operator SelectionReplyOutcome(SelectionSnapshot value) => new SelectionCase(value);

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

    public sealed record RosterCase(ColonistRoster Value) : ColonistRosterReplyOutcome;

    public static implicit operator ColonistRosterReplyOutcome(ColonistRoster value) => new RosterCase(value);

    public sealed record FailureCase(Common.Failure Value) : ColonistRosterReplyOutcome;

    public static implicit operator ColonistRosterReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Capture handles bind server-retained exact selection/window/camera state.
/// Fingerprints alone, and an incomplete list of selected objects, cannot admit input.
/// </summary>
public sealed record CaptureIdentity
{
    public Common.ObservationContext? Context { get; init; }

    public ulong? PlayerDirection { get; init; }

    public string? CaptureId { get; init; }

    public ulong? UiRevision { get; init; }

    public string? SelectionFingerprint { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SelectedObject>))]
    public EquatableArray<SelectedObject> SelectedObjects { get; init; }

    public bool? SelectionComplete { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<WindowIdentity>))]
    public EquatableArray<WindowIdentity> Windows { get; init; }
}

public sealed record WindowIdentity
{
    public int? WindowId { get; init; }

    public string? NativeType { get; init; }
}

public sealed record UiWindow
{
    public WindowIdentity? Identity { get; init; }

    public string? Title { get; init; }

    public string? NativeLayer { get; init; }

    public ScreenRect? ScreenRect { get; init; }

    public bool? ForcePause { get; init; }

    public bool? AbsorbInputAroundWindow { get; init; }

    public bool? Focused { get; init; }

    public bool? Topmost { get; init; }

    public bool? CloseOnAccept { get; init; }

    public bool? CloseOnCancel { get; init; }
}

public sealed record UiState
{
    public string? NativeProgramState { get; init; }

    public bool? EntryScene { get; init; }

    public bool? GameLoaded { get; init; }

    public bool? FloatMenuOpen { get; init; }

    public bool? DialogOpen { get; init; }

    public bool? MainTabOpen { get; init; }

    public string? OpenMainTabId { get; init; }

    public string? OpenMainTabType { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UiWindow>))]
    public EquatableArray<UiWindow> Windows { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record UiScroll
{
    public double? OffsetX { get; init; }

    public double? OffsetY { get; init; }

    public double? MaximumOffsetX { get; init; }

    public double? MaximumOffsetY { get; init; }

    public bool? CanScrollX { get; init; }

    public bool? CanScrollY { get; init; }

    public bool? AtLeft { get; init; }

    public bool? AtRight { get; init; }

    public bool? AtTop { get; init; }

    public bool? AtBottom { get; init; }

    public ScreenRect? ViewportRect { get; init; }

    public ScreenRect? ViewportScreenRect { get; init; }

    public ScreenRect? ContentRect { get; init; }
}

public sealed record UiElement
{
    public string? TargetId { get; init; }

    public uint? ElementIndex { get; init; }

    public string? NativeKind { get; init; }

    public string? Source { get; init; }

    public string? Label { get; init; }

    public string? ValueText { get; init; }

    public bool? Actionable { get; init; }

    public bool? ClipCapable { get; init; }

    public bool? Checked { get; init; }

    public bool? Disabled { get; init; }

    public uint? Depth { get; init; }

    public string? ParentTargetId { get; init; }

    public ScreenRect? Rect { get; init; }

    public ScreenRect? ScreenRect { get; init; }

    public UiScroll? Scroll { get; init; }
}

public sealed record UiSurface
{
    public string? SurfaceId { get; init; }

    public string? CaptureTargetId { get; init; }

    public uint? SurfaceIndex { get; init; }

    public string? NativeKind { get; init; }

    public string? NativeType { get; init; }

    public string? Title { get; init; }

    public string? Label { get; init; }

    public string? SemanticKind { get; init; }

    public ScreenRect? Rect { get; init; }

    public ScreenRect? ScreenRect { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UiElement>))]
    public EquatableArray<UiElement> Elements { get; init; }

    public Listing? Listing { get; init; }

    /// <summary>
    /// SDK semanticDetails is heterogeneous; no opaque passthrough is permitted.
    /// </summary>
    public Common.Unavailable? SemanticDetailsUnavailable { get; init; }
}

public sealed record UiSnapshot
{
    public CaptureIdentity? Capture { get; init; }

    public UiState? State { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<UiSurface>))]
    public EquatableArray<UiSurface> Surfaces { get; init; }

    public Listing? Listing { get; init; }

    public long? CapturedUnixMs { get; init; }

    public ulong? CapturedFrame { get; init; }
}

public sealed record UiReadRequest
{
    public Common.Identity? Identity { get; init; }

    public string? SurfaceId { get; init; }

    public uint? TimeoutMs { get; init; }
}

public sealed record UiReply
{
    public UiReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="UiReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UiReplyOutcome.UiCase), "ui")]
[JsonDerivedType(typeof(UiReplyOutcome.FailureCase), "failure")]
public abstract record UiReplyOutcome
{
    private protected UiReplyOutcome() { }

    public sealed record UiCase(UiSnapshot Value) : UiReplyOutcome;

    public static implicit operator UiReplyOutcome(UiSnapshot value) => new UiCase(value);

    public sealed record FailureCase(Common.Failure Value) : UiReplyOutcome;

    public static implicit operator UiReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record ScreenTarget
{
    public string? TargetId { get; init; }

    public string? NativeKind { get; init; }

    public string? Label { get; init; }

    public bool? Actionable { get; init; }

    public bool? Disabled { get; init; }

    public ScreenRect? ScreenRect { get; init; }

    public WindowIdentity? Window { get; init; }
}

public sealed record ScreenTargets
{
    public CaptureIdentity? Capture { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ScreenTarget>))]
    public EquatableArray<ScreenTarget> Targets { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record ScreenTargetsReply
{
    public ScreenTargetsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ScreenTargetsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScreenTargetsReplyOutcome.TargetsCase), "targets")]
[JsonDerivedType(typeof(ScreenTargetsReplyOutcome.FailureCase), "failure")]
public abstract record ScreenTargetsReplyOutcome
{
    private protected ScreenTargetsReplyOutcome() { }

    public sealed record TargetsCase(ScreenTargets Value) : ScreenTargetsReplyOutcome;

    public static implicit operator ScreenTargetsReplyOutcome(ScreenTargets value) => new TargetsCase(value);

    public sealed record FailureCase(Common.Failure Value) : ScreenTargetsReplyOutcome;

    public static implicit operator ScreenTargetsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record TabsRequest
{
    public Common.Identity? Identity { get; init; }

    public bool? IncludeHidden { get; init; }
}

public sealed record Tab
{
    public string? Id { get; init; }

    public string? NativeType { get; init; }

    public string? Label { get; init; }

    public bool? Visible { get; init; }

    public bool? Open { get; init; }

    public string? DefName { get; init; }

    public double? Order { get; init; }

    public bool? Disabled { get; init; }

    public bool? ValidWithoutMap { get; init; }

    public bool? Minimized { get; init; }

    public ScreenRect? Rect { get; init; }

    public uint? Ordinal { get; init; }

    public string? SelectionFingerprint { get; init; }

    public string? ShortType { get; init; }

    public string? LabelKey { get; init; }

    public string? TutorTag { get; init; }

    public bool? Hidden { get; init; }
}

public sealed record TabsSnapshot
{
    public CaptureIdentity? Capture { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Tab>))]
    public EquatableArray<Tab> Tabs { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record TabsReply
{
    public TabsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="TabsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TabsReplyOutcome.TabsCase), "tabs")]
[JsonDerivedType(typeof(TabsReplyOutcome.FailureCase), "failure")]
public abstract record TabsReplyOutcome
{
    private protected TabsReplyOutcome() { }

    public sealed record TabsCase(TabsSnapshot Value) : TabsReplyOutcome;

    public static implicit operator TabsReplyOutcome(TabsSnapshot value) => new TabsCase(value);

    public sealed record FailureCase(Common.Failure Value) : TabsReplyOutcome;

    public static implicit operator TabsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record Gizmo
{
    public string? Id { get; init; }

    public string? NativeType { get; init; }

    public string? Label { get; init; }

    public string? Description { get; init; }

    public bool? Disabled { get; init; }

    public string? DisabledReason { get; init; }

    public bool? Visible { get; init; }

    public string? SelectionFingerprint { get; init; }

    public uint? Ordinal { get; init; }

    public string? DescriptionPostfix { get; init; }

    public string? TopRightLabel { get; init; }

    public double? Order { get; init; }

    public uint? GroupSize { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SelectedObject>))]
    public EquatableArray<SelectedObject> Owners { get; init; }

    public Listing? OwnerListing { get; init; }

    public string? HotKeyDef { get; init; }

    public string? HotKey { get; init; }

    public bool? SupportsRightClick { get; init; }

    public bool? ReverseDesignator { get; init; }
}

public sealed record GizmosSnapshot
{
    public CaptureIdentity? Capture { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Gizmo>))]
    public EquatableArray<Gizmo> Gizmos { get; init; }

    public Listing? Listing { get; init; }
}

public sealed record GizmosReply
{
    public GizmosReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="GizmosReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GizmosReplyOutcome.GizmosCase), "gizmos")]
[JsonDerivedType(typeof(GizmosReplyOutcome.FailureCase), "failure")]
public abstract record GizmosReplyOutcome
{
    private protected GizmosReplyOutcome() { }

    public sealed record GizmosCase(GizmosSnapshot Value) : GizmosReplyOutcome;

    public static implicit operator GizmosReplyOutcome(GizmosSnapshot value) => new GizmosCase(value);

    public sealed record FailureCase(Common.Failure Value) : GizmosReplyOutcome;

    public static implicit operator GizmosReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record DialogField
{
    public string? Name { get; init; }

    public string? Value { get; init; }

    public uint? MaximumLength { get; init; }
}

public sealed record DialogSnapshot
{
    public CaptureIdentity? Capture { get; init; }

    public WindowIdentity? Window { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<DialogField>))]
    public EquatableArray<DialogField> Fields { get; init; }
}

public sealed record DialogReply
{
    public DialogReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DialogReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DialogReplyOutcome.DialogCase), "dialog")]
[JsonDerivedType(typeof(DialogReplyOutcome.FailureCase), "failure")]
public abstract record DialogReplyOutcome
{
    private protected DialogReplyOutcome() { }

    public sealed record DialogCase(DialogSnapshot Value) : DialogReplyOutcome;

    public static implicit operator DialogReplyOutcome(DialogSnapshot value) => new DialogCase(value);

    public sealed record FailureCase(Common.Failure Value) : DialogReplyOutcome;

    public static implicit operator DialogReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record LookTarget
{
    public string? NativeKind { get; init; }

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
    public bool? Valid { get; init; }

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

    public string? DisabledReason { get; init; }

    public bool? ClosesDialog { get; init; }

    public bool? HasAction { get; init; }

    public bool? HasLink { get; init; }
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

    public bool? Dismissible { get; init; }

    public bool? AutomaticallyOpens { get; init; }

    public string? RelatedFactionLabel { get; init; }

    public LookTargets? LookTargets { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LetterChoice>))]
    public EquatableArray<LetterChoice> Choices { get; init; }

    public Listing? ChoicesListing { get; init; }
}

public sealed record TransientMessage
{
    public string? Id { get; init; }

    public string? Text { get; init; }

    public string? MessageDefName { get; init; }

    public double? Alpha { get; init; }

    public bool? Expired { get; init; }

    public long? StartingTick { get; init; }

    public ulong? StartingFrame { get; init; }

    public long? AgeTicks { get; init; }

    public bool? HasQuest { get; init; }

    public LookTargets? LookTargets { get; init; }

    public double? AgeSeconds { get; init; }
}

public sealed record Alert
{
    public string? Id { get; init; }

    public uint? Ordinal { get; init; }

    public string? NativeType { get; init; }

    public string? NativePriority { get; init; }

    public string? Label { get; init; }

    public string? Explanation { get; init; }

    public string? JumpToTargetsText { get; init; }

    public bool? Active { get; init; }

    public bool? EnabledWithActiveExpansions { get; init; }

    public bool? AnyCulpritValid { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<LookTarget>))]
    public EquatableArray<LookTarget> Targets { get; init; }

    public Listing? Listing { get; init; }

    public bool? Activatable { get; init; }

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

    public sealed record ObservedCase(Letters Value) : LetterSectionOutcome;

    public static implicit operator LetterSectionOutcome(Letters value) => new ObservedCase(value);

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

    public sealed record ObservedCase(Messages Value) : MessageSectionOutcome;

    public static implicit operator MessageSectionOutcome(Messages value) => new ObservedCase(value);

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

    public sealed record ObservedCase(Alerts Value) : AlertSectionOutcome;

    public static implicit operator AlertSectionOutcome(Alerts value) => new ObservedCase(value);

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

    public sealed record NotificationsCase(NotificationsSnapshot Value) : NotificationsReplyOutcome;

    public static implicit operator NotificationsReplyOutcome(NotificationsSnapshot value) => new NotificationsCase(value);

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

public sealed record InputState
{
    public PlayerIdentity? Player { get; init; }

    public bool? Active { get; init; }

    public bool? Ready { get; init; }

    public bool? DirectInput { get; init; }

    public uint? RemainingLeaseMs { get; init; }

    public ulong? LastOrder { get; init; }

    public uint? HeldButtons { get; init; }

    public uint? HeldKeys { get; init; }

    public bool? Released { get; init; }

    public bool? PauseVerified { get; init; }

    public bool? OwnedDraftsReleased { get; init; }

    /// <summary>
    /// Native mailbox acknowledgement facts, without filesystem channel names.
    /// </summary>
    public ulong? SentSequence { get; init; }

    public ulong? ReceivedSequence { get; init; }

    public uint? ChannelCapacityBytes { get; init; }
}

public sealed record InputStateReply
{
    public InputStateReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="InputStateReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InputStateReplyOutcome.StateCase), "state")]
[JsonDerivedType(typeof(InputStateReplyOutcome.FailureCase), "failure")]
public abstract record InputStateReplyOutcome
{
    private protected InputStateReplyOutcome() { }

    public sealed record StateCase(InputState Value) : InputStateReplyOutcome;

    public static implicit operator InputStateReplyOutcome(InputState value) => new StateCase(value);

    public sealed record FailureCase(Common.Failure Value) : InputStateReplyOutcome;

    public static implicit operator InputStateReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record FrameReference
{
    public string? SourceId { get; init; }

    public ulong? Sequence { get; init; }
}

public sealed record PlayerPrecondition
{
    public CaptureIdentity? Captured { get; init; }
}

public sealed record MoveCamera
{
    public double? DeltaX { get; init; }

    public double? DeltaZ { get; init; }
}

public sealed record SetCameraZoom
{
    public double? RootSize { get; init; }
}

public sealed record SelectPawn
{
    public string? PawnId { get; init; }

    public bool? Append { get; init; }
}

public sealed record ClearSelection;

public sealed record CapturedTarget
{
    public string? TargetId { get; init; }

    public uint? TimeoutMs { get; init; }
}

public sealed record ScrollAxis
{
    public ScrollAxisAdjustment? Adjustment { get; init; }
}

/// <summary>The <c>adjustment</c> choice of <see cref="ScrollAxis"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScrollAxisAdjustment.DeltaCase), "delta")]
[JsonDerivedType(typeof(ScrollAxisAdjustment.TargetCase), "target")]
public abstract record ScrollAxisAdjustment
{
    private protected ScrollAxisAdjustment() { }

    public sealed record DeltaCase(double Value) : ScrollAxisAdjustment;

    public sealed record TargetCase(double Value) : ScrollAxisAdjustment;
}

public sealed record ScrollTarget
{
    public string? TargetId { get; init; }

    public ScrollAxis? Horizontal { get; init; }

    public ScrollAxis? Vertical { get; init; }

    public uint? TimeoutMs { get; init; }
}

public sealed record MainTab
{
    public string? TabId { get; init; }
}

public sealed record CloseMainTab
{
    public string? ExpectedTabId { get; init; }
}

public sealed record LetterTarget
{
    public string? LetterId { get; init; }
}

public sealed record SetDialogText
{
    public WindowIdentity? Window { get; init; }

    public string? FieldName { get; init; }

    public string? Text { get; init; }
}

public sealed record DialogTextChange
{
    public WindowIdentity? Window { get; init; }

    public string? FieldName { get; init; }

    public string? Before { get; init; }

    public string? After { get; init; }
}

public sealed record DialogTextPreviewRequest
{
    public CaptureIdentity? Captured { get; init; }

    public SetDialogText? Edit { get; init; }
}

public sealed record DialogTextPreviewReply
{
    public DialogTextPreviewReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="DialogTextPreviewReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DialogTextPreviewReplyOutcome.PreviewCase), "preview")]
[JsonDerivedType(typeof(DialogTextPreviewReplyOutcome.RefusalCase), "refusal")]
public abstract record DialogTextPreviewReplyOutcome
{
    private protected DialogTextPreviewReplyOutcome() { }

    public sealed record PreviewCase(DialogTextChange Value) : DialogTextPreviewReplyOutcome;

    public static implicit operator DialogTextPreviewReplyOutcome(DialogTextChange value) => new PreviewCase(value);

    public sealed record RefusalCase(Common.Failure Value) : DialogTextPreviewReplyOutcome;

    public static implicit operator DialogTextPreviewReplyOutcome(Common.Failure value) => new RefusalCase(value);
}

public sealed record ShowWorld
{
    public int? Tile { get; init; }

    public uint? WatchSeconds { get; init; }
}

public sealed record WorldViewResult
{
    public bool? Shown { get; init; }

    public string? NativeMode { get; init; }

    public uint? WatchSeconds { get; init; }

    public bool? Hidden { get; init; }

    public string? Reason { get; init; }
}

public sealed record PlayerCommand
{
    public PlayerPrecondition? Precondition { get; init; }

    public PlayerCommandOperation? Operation { get; init; }
}

/// <summary>The <c>operation</c> choice of <see cref="PlayerCommand"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlayerCommandOperation.MoveCameraCase), "moveCamera")]
[JsonDerivedType(typeof(PlayerCommandOperation.SetCameraZoomCase), "setCameraZoom")]
[JsonDerivedType(typeof(PlayerCommandOperation.SelectPawnCase), "selectPawn")]
[JsonDerivedType(typeof(PlayerCommandOperation.ClearSelectionCase), "clearSelection")]
[JsonDerivedType(typeof(PlayerCommandOperation.ClickScreenTargetCase), "clickScreenTarget")]
[JsonDerivedType(typeof(PlayerCommandOperation.ClickUiTargetCase), "clickUiTarget")]
[JsonDerivedType(typeof(PlayerCommandOperation.ScrollUiTargetCase), "scrollUiTarget")]
[JsonDerivedType(typeof(PlayerCommandOperation.OpenMainTabCase), "openMainTab")]
[JsonDerivedType(typeof(PlayerCommandOperation.CloseMainTabCase), "closeMainTab")]
[JsonDerivedType(typeof(PlayerCommandOperation.OpenLetterCase), "openLetter")]
[JsonDerivedType(typeof(PlayerCommandOperation.DismissLetterCase), "dismissLetter")]
[JsonDerivedType(typeof(PlayerCommandOperation.SetDialogTextCase), "setDialogText")]
[JsonDerivedType(typeof(PlayerCommandOperation.ShowWorldCase), "showWorld")]
public abstract record PlayerCommandOperation
{
    private protected PlayerCommandOperation() { }

    public sealed record MoveCameraCase(MoveCamera Value) : PlayerCommandOperation;

    public sealed record SetCameraZoomCase(SetCameraZoom Value) : PlayerCommandOperation;

    public sealed record SelectPawnCase(SelectPawn Value) : PlayerCommandOperation;

    public sealed record ClearSelectionCase(ClearSelection Value) : PlayerCommandOperation;

    public sealed record ClickScreenTargetCase(CapturedTarget Value) : PlayerCommandOperation;

    public sealed record ClickUiTargetCase(CapturedTarget Value) : PlayerCommandOperation;

    public sealed record ScrollUiTargetCase(ScrollTarget Value) : PlayerCommandOperation;

    public sealed record OpenMainTabCase(MainTab Value) : PlayerCommandOperation;

    public sealed record CloseMainTabCase(CloseMainTab Value) : PlayerCommandOperation;

    public sealed record OpenLetterCase(LetterTarget Value) : PlayerCommandOperation;

    public sealed record DismissLetterCase(LetterTarget Value) : PlayerCommandOperation;

    public sealed record SetDialogTextCase(SetDialogText Value) : PlayerCommandOperation;

    public sealed record ShowWorldCase(ShowWorld Value) : PlayerCommandOperation;
}

public sealed record PlayerApplied
{
    public bool? Applied { get; init; }

    public CaptureIdentity? After { get; init; }

    public CameraState? Camera { get; init; }

    public SelectionSnapshot? Selection { get; init; }

    public UiSnapshot? Ui { get; init; }

    public DialogSnapshot? Dialog { get; init; }

    public bool? TargetConsumed { get; init; }

    public DialogTextChange? TextChange { get; init; }

    public WorldViewResult? WorldView { get; init; }
}

public sealed record PlayerObserved
{
    public CaptureIdentity? Capture { get; init; }

    public CameraState? Camera { get; init; }

    public SelectionSnapshot? Selection { get; init; }

    public UiSnapshot? Ui { get; init; }

    public DialogSnapshot? Dialog { get; init; }

    public WorldViewResult? WorldView { get; init; }
}

/// <summary>
/// A native exception or failed readback after possible effect is an application
/// outcome, even when transport completed successfully. It is not a safe refusal.
/// </summary>
public sealed record PlayerCommandUncertain
{
    public PlayerCommand? Request { get; init; }

    public string? Detail { get; init; }

    public PlayerObserved? LastObserved { get; init; }
}

public sealed record PlayerCommandReply
{
    public PlayerCommandReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="PlayerCommandReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlayerCommandReplyOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(PlayerCommandReplyOutcome.RefusalCase), "refusal")]
[JsonDerivedType(typeof(PlayerCommandReplyOutcome.UncertainCase), "uncertain")]
public abstract record PlayerCommandReplyOutcome
{
    private protected PlayerCommandReplyOutcome() { }

    public sealed record AppliedCase(PlayerApplied Value) : PlayerCommandReplyOutcome;

    public static implicit operator PlayerCommandReplyOutcome(PlayerApplied value) => new AppliedCase(value);

    public sealed record RefusalCase(Common.Failure Value) : PlayerCommandReplyOutcome;

    public static implicit operator PlayerCommandReplyOutcome(Common.Failure value) => new RefusalCase(value);

    public sealed record UncertainCase(PlayerCommandUncertain Value) : PlayerCommandReplyOutcome;

    public static implicit operator PlayerCommandReplyOutcome(PlayerCommandUncertain value) => new UncertainCase(value);
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

    public sealed record StatusCase(RenderStatus Value) : RenderReplyOutcome;

    public static implicit operator RenderReplyOutcome(RenderStatus value) => new StatusCase(value);

    public sealed record FailureCase(Common.Failure Value) : RenderReplyOutcome;

    public static implicit operator RenderReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record MediaFrame
{
    public FrameReference? Frame { get; init; }

    public CaptureIdentity? Captured { get; init; }

    public uint? Width { get; init; }

    public uint? Height { get; init; }

    public MediaEncoding? Encoding { get; init; }

    public CaptureMethod? CaptureMethod { get; init; }

    public long? CapturedUnixMs { get; init; }

    public double? ReadbackMs { get; init; }

    /// <summary>
    /// Body &lt;=32 MiB; the dedicated media ProtoJSON envelope is &lt;=48 MiB.
    /// </summary>
    public byte[]? Data { get; init; }
}

public sealed record ScreenshotRequest
{
    public CaptureIdentity? Captured { get; init; }

    public string? ClipTargetId { get; init; }

    public uint? ClipPadding { get; init; }

    public bool? IncludeTargets { get; init; }
}

public sealed record Screenshot
{
    public MediaFrame? Frame { get; init; }

    public ScreenTargets? Targets { get; init; }

    public ScreenRect? ClipRect { get; init; }
}

public sealed record ScreenshotReply
{
    public ScreenshotReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ScreenshotReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScreenshotReplyOutcome.ScreenshotCase), "screenshot")]
[JsonDerivedType(typeof(ScreenshotReplyOutcome.FailureCase), "failure")]
public abstract record ScreenshotReplyOutcome
{
    private protected ScreenshotReplyOutcome() { }

    public sealed record ScreenshotCase(Screenshot Value) : ScreenshotReplyOutcome;

    public static implicit operator ScreenshotReplyOutcome(Screenshot value) => new ScreenshotCase(value);

    public sealed record FailureCase(Common.Failure Value) : ScreenshotReplyOutcome;

    public static implicit operator ScreenshotReplyOutcome(Common.Failure value) => new FailureCase(value);
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

    /// <summary>
    /// Layers held after the call.
    /// </summary>
    public uint? Layers { get; init; }
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

    public sealed record AppliedCase(OverlayApplied Value) : OverlayReplyOutcome;

    public static implicit operator OverlayReplyOutcome(OverlayApplied value) => new AppliedCase(value);

    public sealed record FailureCase(Common.Failure Value) : OverlayReplyOutcome;

    public static implicit operator OverlayReplyOutcome(Common.Failure value) => new FailureCase(value);
}

