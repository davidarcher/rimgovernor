using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Whether the colony is calm enough to send a quest party out. It follows the emergency review: a hold for
/// anything but stale facts, pods about to open, or an unsettled combat read makes it busy.
/// </summary>
public abstract record QuestCalm
{
    private QuestCalm()
    {
    }

    /// <summary>The emergency review is clear and the combat read cleared: a quest may take pawns.</summary>
    public sealed record Calm : QuestCalm;

    /// <summary>A live hold, an incoming drop pod or an uncleared fight keeps every pawn home.</summary>
    public sealed record Busy : QuestCalm;

    /// <summary>The emergency review is stale or not clear yet, so calm cannot be claimed either way.</summary>
    public sealed record Unsettled : QuestCalm;

    /// <summary>The single calm state.</summary>
    public static QuestCalm Settled { get; } = new Calm();
}

/// <summary>
/// The colony's pawns as quest capacity reads them from one complete pawn census: who is home, who can leave
/// with what defense, and the work pawns the accepter is chosen from.
/// </summary>
public abstract record QuestPawns
{
    private QuestPawns()
    {
    }

    /// <summary>This review has no complete pawn census: no quest takes a pawn and none is judged by its workers.</summary>
    public sealed record Unseen : QuestPawns;

    /// <summary>The census, read whole.</summary>
    public sealed record Seen : QuestPawns
    {
        /// <summary>The living colonists on the home map who are not away with a caravan.</summary>
        public required int AtHome { get; init; }

        /// <summary>Every colonist on the colony map, with what it can carry, negotiate and defend with.</summary>
        public required EquatableArray<QuestDeparturePawn> Departures { get; init; }

        /// <summary>The work pawns, for the quest accepter's skill order.</summary>
        public required EquatableArray<WorkPawn> Workers { get; init; }
    }
}

/// <summary>
/// The roster split the work plan implies: who is spare, how many essential owners stay home, and each pawn's
/// intended priorities.
/// </summary>
public abstract record QuestRoster
{
    private QuestRoster()
    {
    }

    /// <summary>The review planned no work (or planned none that covers doctor, cooking and construction), so no pawn is spare.</summary>
    public sealed record Unplanned : QuestRoster;

    /// <summary>The planned split.</summary>
    public sealed record Planned : QuestRoster
    {
        /// <summary>The pawns no essential work needs, sorted.</summary>
        public required EquatableArray<PawnId> Spare { get; init; }

        /// <summary>The sole essential owners who stay home.</summary>
        public required int HomeFloor { get; init; }

        /// <summary>The roster's intended priorities, including changes not yet applied.</summary>
        public required EquatableArray<PawnWorkAssignment> Work { get; init; }
    }
}
