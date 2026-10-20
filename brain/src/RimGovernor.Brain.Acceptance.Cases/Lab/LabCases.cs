namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>lab/*</c> acceptance cases: lab contract cases that open on the blank Soil lab, spawn what
/// they need with <see cref="DebugGame.LabSpawnAsync"/> and make one op or read assertion in 10-30 s.
/// <see cref="Register"/> registers them.
/// </summary>
public static class LabCases
{
    /// <summary>Registers every <c>lab/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LabGridThings.Case());
        registry.Register(LabSpawn.Case());
        registry.Register(LabStage.Case());
        registry.Register(LabSpoilagePick.Case());
        registry.Register(LabTraining.Case());
        registry.Register(LabSkillLearning.Case());
        registry.Register(LabSkillStatCurve.Case());
        registry.Register(LabCaptureProfile.Case());
        registry.Register(LabFoodStorageRoutes.Case());
        foreach (var c in LabTrainingRange.Cases().Concat(LabSparring.Cases()).Concat(LabSparringWatch.Cases()))
        {
            registry.Register(c);
        }
    }
}
