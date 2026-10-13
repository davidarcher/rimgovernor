using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;

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
