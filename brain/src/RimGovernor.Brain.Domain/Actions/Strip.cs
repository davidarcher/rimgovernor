namespace RimGovernor.Brain.Domain;

/// <summary>
/// Places vanilla's Strip designation on one exact spawned pawn or corpse; colonists strip it through ordinary
/// Hauling work. Who to strip is the caller's decision; native validates the target live.
/// </summary>
public sealed record Strip : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public Strip(string target)
    {
        DomainException.Require(IdText.IsValidText(target), "strip requires a valid target identity");
        Target = target;
    }

    public string Target { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewStripAction(ActionId id, Strip strip) => Create(id, ActionKind.Strip, strip);

    public Strip? Strip => PayloadOf<Strip>(ActionKind.Strip);
}
