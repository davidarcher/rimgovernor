#nullable enable

using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the quest fixtures (#3404): a minimal quest the random generator cannot pin, an untouched Ideology
// terminal, a beggar lord's request and two quest shuttles, and the native state no production read covers for them.
// A record carries only what a case reads.

/// <summary><c>test/quest_accept_prepare</c>: a not-yet-accepted quest with a two-option reward choice and the free colonists.</summary>
public sealed record QuestAcceptPrepared
{
    public bool Success { get; init; }

    public string QuestId { get; init; } = "";

    public IReadOnlyList<string> PawnIds { get; init; } = [];
}

/// <summary><c>test/quest_hack_prepare</c>: an untouched terminal, the one Research worker and a thing that cannot be hacked.</summary>
public sealed record QuestHackPrepared
{
    public bool Success { get; init; }

    public string TerminalId { get; init; } = "";

    public string HackerId { get; init; } = "";

    public string UnhackableItemId { get; init; } = "";
}

/// <summary><c>test/quest_hack_read</c>: the terminal's CompHackable, with the pawn whose work last progressed it.</summary>
public sealed record QuestHackRead
{
    public bool Success { get; init; }

    public bool Autohack { get; init; }

    public bool Hacked { get; init; }

    public double Progress { get; init; }

    public string HackerId { get; init; } = "";
}

/// <summary><c>test/quest_give_prepare</c>: the hauler, the beggar asking twenty silver and the native count still asked.</summary>
public sealed record QuestGivePrepared
{
    public bool Success { get; init; }

    public string HaulerId { get; init; } = "";

    public string RecipientId { get; init; } = "";

    public int Remaining { get; init; }
}

/// <summary><c>test/quest_give_read</c>: the silver the beggar holds, the native count still asked and the hauler's job.</summary>
public sealed record QuestGiveRead
{
    public bool Success { get; init; }

    public int Received { get; init; }

    public int Remaining { get; init; }

    public string Job { get; init; } = "";

    public string TargetId { get; init; } = "";
}

/// <summary><c>test/quest_shuttle_prepare</c>: one accepted quest per shuttle, one naming its pawn and one taking any colonist.</summary>
public sealed record QuestShuttlePrepared
{
    public bool Success { get; init; }

    public string NamedQuest { get; init; } = "";

    public string NamedPawn { get; init; } = "";

    public string CountedQuest { get; init; } = "";

    public string CountedPawn { get; init; } = "";
}
