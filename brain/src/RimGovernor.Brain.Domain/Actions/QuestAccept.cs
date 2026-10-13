namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to accept one already-observed quest offer. AccepterPawn only matters when the quest
/// requires an accepter (native rejects a mismatch); RewardChoice selects the exact option index of a quest's
/// single choice part, or -1 when the quest carries none. Native eligibility is established at inspection, not
/// here. This action never selects which quest to accept, nor which reward is "best".
/// </summary>
public sealed record QuestAccept : IActionPayload
{
    /// <summary>Go <c>NewQuestAccept</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public QuestAccept(QuestId quest, PawnId accepterPawn, int rewardChoice)
    {
        DomainException.Require(quest.IsValid, "quest accept requires a valid quest identity");
        DomainException.Require(accepterPawn.IsEmpty || accepterPawn.IsValid, "invalid quest accept accepter pawn identity");
        DomainException.Require(rewardChoice >= -1, "invalid quest accept reward choice");
        Quest = quest;
        AccepterPawn = accepterPawn;
        RewardChoice = rewardChoice;
    }

    public QuestId Quest { get; }

    /// <summary>Empty when the quest needs no accepter.</summary>
    public PawnId AccepterPawn { get; }

    /// <summary>-1 when the quest carries no reward-choice part.</summary>
    public int RewardChoice { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewQuestAcceptAction(ActionId id, QuestAccept accept) => Create(id, ActionKind.QuestAccept, accept);

    public QuestAccept? QuestAccept => PayloadOf<QuestAccept>(ActionKind.QuestAccept);
}
