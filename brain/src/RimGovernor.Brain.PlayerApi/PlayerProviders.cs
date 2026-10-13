using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Protocol.Lifecycle;
using RimGovernor.Protocol.Presentation;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// What the player routes ask of the brain (#3083 defines the seams; the brain host, #3082, implements them
/// over the Player, the Store and the clock). Each interface is the narrow slice one route family reads or writes,
/// so the routes know no host type.
/// </summary>
public interface IPlayerControl
{
    /// <summary>Runs the bot for the exact observed world. A failure after the intent was journaled throws <see cref="ControlFailureException"/>.</summary>
    Task<ControlRecord> ResumeAsync(ControlRequest request, CancellationToken cancellationToken);

    /// <summary>Stops the bot for the exact observed world. Failure as for <see cref="ResumeAsync"/>.</summary>
    Task<ControlRecord> PauseAsync(ControlRequest request, CancellationToken cancellationToken);

    /// <summary>The actual permission and the live generation, as the executor sees them now.</summary>
    PlayerState State();
}

/// <summary>Reads the current control record. Throws <see cref="NotFoundException"/> when no intent was ever journaled.</summary>
public interface IControlReader
{
    Task<ControlRecord> CurrentControlAsync(CancellationToken cancellationToken);
}

/// <summary>Clock review: the unreviewed clock events and the player's acknowledgement of them.</summary>
public interface IClockReview
{
    Task<ClockReviewState> ReadAsync(CancellationToken cancellationToken);

    Task<ClockReviewState> AcknowledgeAsync(ClockAcknowledgement acknowledgement, CancellationToken cancellationToken);
}

/// <summary>
/// Whether the bot may act now, and in which generation: the fields of the executor's control state the
/// player routes show. <paramref name="Snapshot"/> is meaningful only when <paramref name="ObservationKnown"/>.
/// </summary>
public readonly record struct PlayerState(bool Enabled, bool ObservationKnown, GenerationSnapshot Snapshot);

/// <summary>A control intent that was journaled but whose outcome failed; the record says how far it got.</summary>
public sealed class ControlFailureException(ControlRecord record, Exception cause)
    : Exception("control intent failed: " + cause.Message, cause)
{
    public ControlRecord Record { get; } = record;
}

/// <summary>
/// What the state route shows of the controller: the cached snapshot the brain host keeps. Reading it never
/// triggers inference or a native refresh. <see cref="Mode"/> is <c>manual</c> or <c>automate</c>; empty reads as manual.
/// </summary>
public sealed record ControllerSnapshot
{
    public string SessionId { get; init; } = "";

    public bool Connected { get; init; }

    public string Mode { get; init; } = "";

    public string Status { get; init; } = "";

    public Fact<GenerationSnapshot> Generation { get; init; }

    public Fact<ObservedIdentity> Identity { get; init; }

    public Fact<Tick> Tick { get; init; }

    public Fact<bool> Paused { get; init; }

    public Fact<DateTimeOffset> ObservedAt { get; init; }

    public bool Stale { get; init; }

    public Fact<PlanId> ActivePlanId { get; init; }
}

/// <summary>Owned snapshots of the controller; implementations honor cancellation.</summary>
public interface ISnapshotProvider
{
    Task<ControllerSnapshot> SnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Loads one stored plan with its progress. Throws <see cref="NotFoundException"/> for an unknown plan.</summary>
public interface IPlanReader
{
    Task<PlanState> LoadPlanAsync(PlanId id, CancellationToken cancellationToken);
}

/// <summary>
/// A runtime snapshot of the composed routine runtime, for read-only diagnostics (Go <c>RoundsStatus</c>).
/// <see cref="ActiveFamilies"/> names every routine planner family this process enabled, whether or not it has
/// pending work; <see cref="LastReviewTick"/> is the durable review cursor's most recent reviewed tick, known once
/// a review has run.
/// </summary>
public sealed record RoundsStatus
{
    public IReadOnlyList<ConcernId> Emergency { get; init; } = [];

    public ExtentEligibilityRequest ExtentEligibility { get; init; } = new();

    /// <summary>The derived colony extent; absent stays unknown.</summary>
    public Fact<ColonyExtent> Extent { get; init; }

    public IReadOnlyList<ResourceRunway> ResourceRunways { get; init; } = [];

    public bool ReviewsEnabled { get; init; }

    public bool MethodsEnabled { get; init; }

    public IReadOnlyList<string> ActiveFamilies { get; init; } = [];

    public Fact<Tick> LastReviewTick { get; init; }

    /// <summary>Every active concern's progress record from the last review.</summary>
    public IReadOnlyList<ConcernProgress> Progress { get; init; } = [];

    /// <summary>The colony stage the last review derived; null until an enabled review filed one.</summary>
    public ColonyStageRecord? Stage { get; init; }

    /// <summary>The roster planner's last recorded report; null until an enabled review planned work.</summary>
    public WorkRosterReport? Roster { get; init; }

    /// <summary>The state store's held census sections with the tick each describes.</summary>
    public IReadOnlyList<SectionStatus> Sections { get; init; } = [];

    /// <summary>The inspections that raised nothing in the last enabled review, with the typed reason.</summary>
    public IReadOnlyList<NoOpRecord> NoOps { get; init; } = [];

    /// <summary>The safe forbidden stacks the last review's safety hold kept forbidden, with reasons.</summary>
    public IReadOnlyList<LootHold> LootHolds { get; init; } = [];
}

/// <summary>Reports the composed routine runtime. Implementations must not trigger native calls or mutate state.</summary>
public interface IRoundsProvider
{
    Task<RoundsStatus> RoundsStatusAsync(CancellationToken cancellationToken);
}

/// <summary>The read-only live colony census the colony route fronts: food stock and runway plus every living home colonist's state.</summary>
public interface IColonyStatusReader
{
    Task<ColonyStatusReport> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Validated fixed native presentation reads; owns no input capability. A native failure is thrown.</summary>
public interface IPresentationReader
{
    Task<CameraReply> ReadCameraAsync(ReadRequest request, CancellationToken cancellationToken);

    Task<SelectionReply> ReadSelectionAsync(ReadRequest request, CancellationToken cancellationToken);

    Task<ColonistRosterReply> ReadColonistRosterAsync(ColonistRosterRequest request, CancellationToken cancellationToken);

    Task<RenderReply> ReadRenderStateAsync(ReadRequest request, CancellationToken cancellationToken);
}

/// <summary>The one active presentation capture the player API serves: a rendering lease. A native failure is thrown.</summary>
public interface IPresentationMediaWriter
{
    Task<RenderReply> DemandRenderingAsync(RenderDemand request, CancellationToken cancellationToken);
}

/// <summary>Notification reads preserve unavailable sections and grant no acknowledgement. A native failure is thrown.</summary>
public interface INotificationReader
{
    Task<NotificationsReply> ReadNotificationsAsync(NotificationsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The explicit session lifecycle capability: one trusted checkpoint save, one native load and one new-colony
/// generation, plus recovery reads for a request the caller never received a reply for. Each call returns the
/// native reply as it is, with its outcome (completed, pending, superseded, failure); the route maps the outcome.
/// A transport failure is thrown. The implementation owns any retry after a blocking-attention acknowledgement.
/// </summary>
public interface ILifecycleWriter
{
    Task<SaveReply> SaveAsync(SaveRequest request, CancellationToken cancellationToken);

    Task<SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken);

    Task<LoadReply> LoadAsync(LoadRequest request, CancellationToken cancellationToken);

    Task<LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken);

    Task<NewColonyReply> NewColonyAsync(NewColonyRequest request, CancellationToken cancellationToken);

    Task<NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken);
}
