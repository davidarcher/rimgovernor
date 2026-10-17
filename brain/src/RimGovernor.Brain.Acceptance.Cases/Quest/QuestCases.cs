namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>quest/*</c> cases.</summary>
public static class QuestCases
{
    /// <summary>Registers every <c>quest/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(QuestAccept.Case());
        registry.Register(QuestGiveItem.Case());
        registry.Register(QuestHack.Case());
        foreach (var shuttle in QuestShuttle.Cases())
        {
            registry.Register(shuttle);
        }
    }
}
