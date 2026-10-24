using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using PbClock = RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Lifecycle;
using RimGovernor.Contracts.Wire.Presentation;

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

/// <summary>The speed the brain requests while it owns the clock; a change applies from the next run.</summary>
public interface IClockSpeedControl
{
    PbClock.Speed Speed { get; }

    /// <summary>Sets the target speed (Normal through Hyperfast).</summary>
    void Change(PbClock.Speed speed);
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

    /// <summary>The world the last good observation found; the tick it reports is the identity's.</summary>
    public required ControllerWorld World { get; init; }

    /// <summary>Whether the player's control state names the observed world; its generation carries the active plan.</summary>
    public required ControllerControl Control { get; init; }

    public bool Stale { get; init; }
}

/// <summary>What the controller has observed of the game: a world, or none (no game loaded, or no observation yet).</summary>
public abstract record ControllerWorld
{
    private ControllerWorld()
    {
    }

    /// <summary>The identity, pause state and read time of the last good observation.</summary>
    public sealed record Observed(ObservedIdentity Identity, bool Paused, DateTimeOffset ObservedAt) : ControllerWorld;

    /// <summary>No game is loaded, or none has been observed.</summary>
    public sealed record Unobserved : ControllerWorld;
}

/// <summary>Whether the player's control state names the world the controller observes.</summary>
public abstract record ControllerControl
{
    private ControllerControl()
    {
    }

    /// <summary>The control state names the observed world; <paramref name="Generation"/> is its generation, and its plan the active one.</summary>
    public sealed record Matching(GenerationSnapshot Generation) : ControllerControl;

    /// <summary>The control state names another world, or none.</summary>
    public sealed record Unmatched : ControllerControl;
}

/// <summary>The review cursor's progress: the tick of the most recent review, or no review yet.</summary>
public abstract record ReviewProgress
{
    private ReviewProgress()
    {
    }

    /// <summary>A review has run; <paramref name="Tick"/> is the tick it reviewed.</summary>
    public sealed record Reviewed(Tick Tick) : ReviewProgress;

    /// <summary>No review has run.</summary>
    public sealed record NotReviewed : ReviewProgress;
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
/// A runtime snapshot of the composed routine runtime, for read-only diagnostics.
/// <see cref="ActiveFamilies"/> names every routine planner family this process enabled, whether or not it has
/// pending work; <see cref="LastReviewTick"/> is the durable review cursor's most recent reviewed tick, known once
/// a review has run.
/// </summary>
public sealed record RoundsStatus
{
    public ExtentEligibilityView ExtentEligibility { get; init; } = ExtentEligibilityView.NoHistory;

    /// <summary>The derived colony extent, or the evidence it lacks (also while no colony census is held).</summary>
    public ExtentDerivation Extent { get; init; } = new ExtentDerivation.Incomplete("colony census");

    public IReadOnlyList<ResourceRunway> ResourceRunways { get; init; } = [];

    public bool ReviewsEnabled { get; init; }

    public bool MethodsEnabled { get; init; }

    public IReadOnlyList<string> ActiveFamilies { get; init; } = [];

    public required ReviewProgress LastReview { get; init; }

    /// <summary>Every active concern's progress record from the last review.</summary>
    public IReadOnlyList<ConcernProgress> Progress { get; init; } = [];

    /// <summary>The roster planner's last recorded report; null until an enabled review planned work.</summary>
    public WorkRosterReport? Roster { get; init; }

    /// <summary>The state store's held census sections with the tick each describes.</summary>
    public IReadOnlyList<SectionStatus> Sections { get; init; } = [];

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
    Task<CameraState> ReadCameraAsync(ReadRequest request, CancellationToken cancellationToken);

    Task<SelectionSnapshot> ReadSelectionAsync(ReadRequest request, CancellationToken cancellationToken);

    Task<ColonistRoster> ReadColonistRosterAsync(ColonistRosterRequest request, CancellationToken cancellationToken);

    Task<RenderStatus> ReadRenderStateAsync(ReadRequest request, CancellationToken cancellationToken);
}

/// <summary>The one active presentation capture the player API serves: a rendering lease. A native failure is thrown.</summary>
public interface IPresentationMediaWriter
{
    Task<RenderReply> DemandRenderingAsync(RenderDemand request, CancellationToken cancellationToken);
}

/// <summary>Notification reads preserve unavailable sections and grant no acknowledgement. A native failure is thrown.</summary>
public interface INotificationReader
{
    Task<NotificationsSnapshot> ReadNotificationsAsync(NotificationsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The explicit session lifecycle capability: one trusted checkpoint save, one native load, one new-colony
/// generation and one return to the main menu, plus recovery reads for a request the caller never received a reply for. Each call returns the
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

    /// <summary>Ends the loaded game without saving and returns the running process to a fresh main menu.</summary>
    Task<MenuReply> MenuAsync(MenuRequest request, CancellationToken cancellationToken);

    Task<MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken);
}

/// <summary>
/// One immutable snapshot of the player's goals: the stored record and the statuses the goals Family derived at its
/// last review of this colony (#3494). The statuses can lag an edit; a goal with none has not been reviewed yet.
/// </summary>
public sealed record GoalsSnapshot(GoalsRecord Record, IReadOnlyList<GoalStatus> Statuses, IReadOnlyList<ThemeStatus> Themes);

/// <summary>
/// The in-process read of the player's goals for the in-game panel (#3492). It returns the cached snapshot of the
/// colony the player is in and never triggers inference or a native call, so the panel polls it off the game thread.
/// </summary>
public interface IGoalsView
{
    GoalsSnapshot Current();
}

/// <summary>
/// The in-process write of the player's goals. A save is validated, stored for the colony the player is in and
/// wakes the goals Family for its next review. An invalid record, or a save while no colony is observed, throws
/// <see cref="DomainException"/> with the reason and leaves the stored record unchanged.
/// </summary>
public interface IGoalsEditor
{
    Task SaveAsync(GoalsRecord record, CancellationToken cancellationToken);
}
