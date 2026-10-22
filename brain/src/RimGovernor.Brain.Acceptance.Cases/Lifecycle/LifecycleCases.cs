namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>lifecycle/*</c> acceptance cases: the process lifecycle (save, load, new colony, unload, fault, reuse) driven
/// through the native tools.
/// </summary>
public static class LifecycleCases
{
    /// <summary>Registers every <c>lifecycle/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LifecycleCheckpoint.Case());
        registry.Register(LifecycleIdeoligionDesign.Case());
        registry.Register(LifecycleIdeoligionShared.Case());
        registry.Register(LifecycleThemeColony.Case());
        registry.Register(LifecycleShutdown.Case());
        registry.Register(LifecycleShutdownCleanup.Case());
        registry.Register(LifecycleRuntimeFault.Case());
        registry.Register(LifecycleLoad.Case());
        registry.Register(LifecycleNewColony.Case());
        registry.Register(LifecycleNewColonyParts.Case());
        registry.Register(LifecycleQuietStoryteller.Case());
        registry.Register(LifecycleScenarioStartParts.Case());
        registry.Register(LifecycleReuse.Case());
    }
}
