using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Lifecycle;

[JsonConverter(typeof(WireEnumConverter<Readiness>))]
public enum Readiness
{
    [WireName("READINESS_MAP")]
    Map = 1,
    [WireName("READINESS_VISUAL")]
    Visual = 2,
}

/// <summary>
/// Native phases only; restarting the game is launcher-side.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<NewColonyPhase>))]
public enum NewColonyPhase
{
    [WireName("NEW_COLONY_PHASE_GENERATING_WORLD")]
    GeneratingWorld = 1,
    [WireName("NEW_COLONY_PHASE_CHOOSING_TILE")]
    ChoosingTile = 2,
    [WireName("NEW_COLONY_PHASE_ROLLING_COLONISTS")]
    RollingColonists = 3,
    [WireName("NEW_COLONY_PHASE_GENERATING_MAP")]
    GeneratingMap = 4,
    [WireName("NEW_COLONY_PHASE_FINISHING")]
    Finishing = 5,
    [WireName("NEW_COLONY_PHASE_SAVING")]
    Saving = 6,
}

/// <summary>The identity read's payload; a game with no colony map throws the unavailable exception instead.</summary>
public sealed record LoadedIdentity
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public required bool Paused { get; init; }
}

/// <summary>
/// An open window that force-pauses the game (Window.forcePause).
/// </summary>
public sealed record PausingWindow
{
    public string? NativeType { get; init; }

    public string? Title { get; init; }
}

/// <summary>
/// paused is TickManager.Paused: the speed control at Paused, or
/// force_paused (a window or other native force-pause request). time_speed
/// is TickManager.CurTimeSpeed's name.
/// </summary>
public sealed record LoadedTick
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public required bool Paused { get; init; }

    public required bool ForcePaused { get; init; }

    public string? TimeSpeed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PausingWindow>))]
    public EquatableArray<PausingWindow> PausingWindows { get; init; }
}

/// <summary>
/// These requests belong to explicit session lifecycle, outside gameplay/model dispatch.
/// </summary>
public sealed record PlayerLifecycleContext
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public required ulong PlayerDirection { get; init; }

    public string? RequestId { get; init; }
}

public sealed record SaveCompleted
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public required bool Paused { get; init; }

    public required ulong PlayerDirection { get; init; }

    public required ulong ByteLength { get; init; }
}

public sealed record SaveUncertain
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public RimGovernor.Brain.GameFacts.Common.ObservationContext? ObservedContext { get; init; }

    public string? Detail { get; init; }
}

public sealed record SaveReply
{
    public SaveReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="SaveReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SaveReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(SaveReplyOutcome.UncertainCase), "uncertain")]
[JsonDerivedType(typeof(SaveReplyOutcome.FailureCase), "failure")]
public abstract record SaveReplyOutcome
{
    private protected SaveReplyOutcome() { }

    /// <summary>The <c>Completed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SaveCompleted? Completed => (this as CompletedCase)?.Value;

    public sealed record CompletedCase(SaveCompleted Value) : SaveReplyOutcome;

    public static implicit operator SaveReplyOutcome(SaveCompleted value) => new CompletedCase(value);

    /// <summary>The <c>Uncertain</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SaveUncertain? Uncertain => (this as UncertainCase)?.Value;

    public sealed record UncertainCase(SaveUncertain Value) : SaveReplyOutcome;

    public static implicit operator SaveReplyOutcome(SaveUncertain value) => new UncertainCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : SaveReplyOutcome;

    public static implicit operator SaveReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}

public sealed record LoadCompleted
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public LoadedIdentity? Loaded { get; init; }

    public required Readiness Readiness { get; init; }
}

public sealed record LoadPending
{
    public string? RequestId { get; init; }

    public string? Detail { get; init; }
}

public sealed record LoadSuperseded
{
    public string? Detail { get; init; }
}

public sealed record LoadReply
{
    public LoadReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="LoadReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LoadReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(LoadReplyOutcome.PendingCase), "pending")]
[JsonDerivedType(typeof(LoadReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(LoadReplyOutcome.SupersededCase), "superseded")]
public abstract record LoadReplyOutcome
{
    private protected LoadReplyOutcome() { }

    /// <summary>The <c>Completed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public LoadCompleted? Completed => (this as CompletedCase)?.Value;

    public sealed record CompletedCase(LoadCompleted Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(LoadCompleted value) => new CompletedCase(value);

    /// <summary>The <c>Pending</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public LoadPending? Pending => (this as PendingCase)?.Value;

    public sealed record PendingCase(LoadPending Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(LoadPending value) => new PendingCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);

    public sealed record SupersededCase(LoadSuperseded Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(LoadSuperseded value) => new SupersededCase(value);
}

public sealed record RequestStatus
{
    public string? RequestId { get; init; }
}

/// <summary>
/// The game is at a fresh main menu (no game, no queued long event). <c>unloaded</c> is false when it already was.
/// </summary>
public sealed record MenuCompleted
{
    public string? RequestId { get; init; }

    public required bool Unloaded { get; init; }
}

public sealed record MenuPending
{
    public string? RequestId { get; init; }

    public string? Detail { get; init; }
}

public sealed record MenuReply
{
    public MenuReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="MenuReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MenuReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(MenuReplyOutcome.PendingCase), "pending")]
[JsonDerivedType(typeof(MenuReplyOutcome.FailureCase), "failure")]
public abstract record MenuReplyOutcome
{
    private protected MenuReplyOutcome() { }

    /// <summary>The <c>Completed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public MenuCompleted? Completed => (this as CompletedCase)?.Value;

    public sealed record CompletedCase(MenuCompleted Value) : MenuReplyOutcome;

    public static implicit operator MenuReplyOutcome(MenuCompleted value) => new CompletedCase(value);

    /// <summary>The <c>Pending</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public MenuPending? Pending => (this as PendingCase)?.Value;

    public sealed record PendingCase(MenuPending Value) : MenuReplyOutcome;

    public static implicit operator MenuReplyOutcome(MenuPending value) => new PendingCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : MenuReplyOutcome;

    public static implicit operator MenuReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}

public sealed record NewColonyCompleted
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public required bool Paused { get; init; }

    public required ulong ByteLength { get; init; }

    /// <summary>
    /// The seed the colony was generated from, after any reroll native made.
    /// </summary>
    public string? Seed { get; init; }
}

public sealed record NewColonyPending
{
    public string? RequestId { get; init; }

    public required NewColonyPhase Phase { get; init; }

    public string? Detail { get; init; }

    public required ulong ElapsedMs { get; init; }

    public required uint RerollCount { get; init; }
}

public sealed record NewColonySuperseded
{
    public string? Detail { get; init; }
}

public sealed record NewColonyReply
{
    public NewColonyReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="NewColonyReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.PendingCase), "pending")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.SupersededCase), "superseded")]
public abstract record NewColonyReplyOutcome
{
    private protected NewColonyReplyOutcome() { }

    /// <summary>The <c>Completed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public NewColonyCompleted? Completed => (this as CompletedCase)?.Value;

    public sealed record CompletedCase(NewColonyCompleted Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(NewColonyCompleted value) => new CompletedCase(value);

    /// <summary>The <c>Pending</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public NewColonyPending? Pending => (this as PendingCase)?.Value;

    public sealed record PendingCase(NewColonyPending Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(NewColonyPending value) => new PendingCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);

    public sealed record SupersededCase(NewColonySuperseded Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(NewColonySuperseded value) => new SupersededCase(value);
}

public sealed record GovernorStateBlobs
{
    public IReadOnlyDictionary<string, string> Blobs { get; init; } = new Dictionary<string, string>();
}

/// <summary>The PutBatch reply; <c>Loaded</c> is absent when native staged nothing (native logs the reason).</summary>
public sealed record GovernorStateReply
{
    public GovernorStateBlobs? Loaded { get; init; }
}

public sealed record SaveSignal
{
    public string? Token { get; init; }
}

/// <summary>The wait's answer; <c>Signal</c> is absent when the wait timed out or was cancelled.</summary>
public sealed record WaitSaveSignalReply
{
    public SaveSignal? Signal { get; init; }
}

/// <summary>The flush ack: empty when acked, a failure otherwise.</summary>
public sealed record FlushDoneReply
{
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure { get; init; }
}
