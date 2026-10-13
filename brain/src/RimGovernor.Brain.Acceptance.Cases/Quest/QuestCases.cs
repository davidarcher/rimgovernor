namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>quest/*</c> cases (Go <c>cases/quest</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class QuestCases
{
    /// <summary>Registers every <c>quest/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(QuestAccept.Case());
        registry.Register(QuestGiveItem.Case());
        registry.Register(QuestHack.Case());
        registry.Register(QuestShuttle.Case());
    }
}
