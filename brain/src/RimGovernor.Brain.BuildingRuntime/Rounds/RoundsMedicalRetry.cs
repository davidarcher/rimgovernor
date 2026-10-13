namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal const long MedicalWaitTicks = StockWaitTicks;

    /// <summary>The first unused method id with the prefix and a numeric wave suffix.</summary>
    internal static MethodId NextWaveMethod(StandardState goal, string prefix)
    {
        var bound = new HashSet<MethodId>();
        foreach (var m in goal.History)
        {
            bound.Add(m.Method);
        }

        foreach (var m in goal.Methods)
        {
            bound.Add(m.Method);
        }

        for (var i = 0; ; i++)
        {
            var method = new MethodId($"{prefix}{i}");
            if (!bound.Contains(method))
            {
                return method;
            }
        }
    }
}
