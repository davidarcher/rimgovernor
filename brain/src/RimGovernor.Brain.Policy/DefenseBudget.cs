namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // Threat demand: the base budget plus TurretStep more for every TurretStepPoints of observed raid points.
    // Unknown threat keeps the base budget. There is no ceiling; power, stock and the layout's geometry decide
    // how many of the demanded turrets are placed, and each gate that stops placement is named on the tier.
    internal const int TurretBaseBudget = 2;
    internal const int TurretStep = 2;

    /// <summary>The raid-point span that adds TurretStep to the demand.</summary>
    internal const int TurretStepPoints = 300;

    /// <summary>The raid-point reading at which the mortar tier opens (sieges are then a threat worth the shells).</summary>
    internal const int TurretHighPoints = 800;

    /// <summary>
    /// The number of turrets the observed raid points demand: unknown, negative or NaN readings keep the base
    /// budget, and each full TurretStepPoints adds TurretStep.
    /// </summary>
    public static int TurretBudget(Fact<double> raidPoints)
    {
        if (!raidPoints.TryGetValue(out var points) || double.IsNaN(points) || double.IsInfinity(points) || points < TurretStepPoints)
        {
            return TurretBaseBudget;
        }
        return TurretBaseBudget + (TurretStep * (int)(points / TurretStepPoints));
    }
}
