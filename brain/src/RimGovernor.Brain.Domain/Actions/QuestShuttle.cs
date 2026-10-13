namespace RimGovernor.Brain.Domain;

public enum ShuttleLoading
{
    [WireName("none")]
    LaunchOnly,
    Autoload,
    ExplicitPawns,
}

/// <summary>Requests loading orders or send, not completed boarding or departure.</summary>
public sealed record QuestShuttle : IActionPayload
{
    /// <summary>Go <c>NewQuestShuttle</c>; throws <see cref="DomainException"/> when invalid. Pawns are stored sorted.</summary>
    public QuestShuttle(QuestId quest, ShuttleLoading loading, bool autoload, IEnumerable<PawnId> pawns, bool launch)
    {
        DomainException.Require(quest.IsValid, "invalid shuttle quest");
        var rows = pawns.OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
        switch (loading)
        {
            case ShuttleLoading.LaunchOnly:
                DomainException.Require(launch && !autoload && rows.Count == 0, "launch-only shuttle requires launch without loading");
                break;
            case ShuttleLoading.Autoload:
                DomainException.Require(rows.Count == 0, "autoload cannot carry explicit pawns");
                break;
            case ShuttleLoading.ExplicitPawns:
                DomainException.Require(!autoload && rows.Count != 0 && rows.Count <= 256, "explicit shuttle loading requires 1 to 256 pawns");
                break;
            default:
                throw new DomainException("invalid shuttle loading mode");
        }
        for (var i = 0; i < rows.Count; i++)
        {
            DomainException.Require(rows[i].IsValid && !(i > 0 && rows[i - 1] == rows[i]), "invalid or duplicate shuttle pawn");
        }
        Quest = quest;
        Loading = loading;
        Autoload = autoload;
        Launch = launch;
        Pawns = EquatableArray.From(rows);
    }

    public QuestId Quest { get; }

    public ShuttleLoading Loading { get; }

    public bool Autoload { get; }

    public bool Launch { get; }

    /// <summary>Sorted, duplicate free.</summary>
    public EquatableArray<PawnId> Pawns { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewQuestShuttleAction(ActionId id, QuestShuttle shuttle) => Create(id, ActionKind.QuestShuttle, shuttle);

    public QuestShuttle? QuestShuttle => PayloadOf<QuestShuttle>(ActionKind.QuestShuttle);
}
