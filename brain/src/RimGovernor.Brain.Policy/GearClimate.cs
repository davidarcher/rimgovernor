namespace RimGovernor.Brain.Policy;

/// <summary>Rules over the gear census's seasonal climate (the Contracts <see cref="GearClimate"/>).</summary>
public static class GearClimateExtensions
{
    /// <summary>Throws <see cref="DomainException"/>.</summary>
    public static void Validate(this GearClimate c)
    {
        DomainException.Require(
            c.Temperatures.Count == 12 && c.CurrentTwelfth >= 0 && c.CurrentTwelfth < 12 && c.TicksToNextTwelfth > 0 && c.TicksToNextTwelfth <= GearClimateRules.GearTwelfthTicks,
            "invalid gear seasonal curve"
        );
        foreach (var n in c.Temperatures)
        {
            DomainException.Require(double.IsFinite(n), "invalid gear seasonal temperature");
        }
        DomainException.Require(
            c.DaylightHours.Count == 12 && c.DaylightHours.All(h => double.IsFinite(h) && h >= 0 && h <= 24),
            "invalid gear daylight hours"
        );
    }

    /// <summary>The hours of daylight on the given twelfth's sampled day.</summary>
    public static double DaylightHoursIn(this GearClimate c, int twelfth) => c.DaylightHours[twelfth];
}

public static class GearClimateRules
{
    internal const long GearTwelfthTicks = 300000;
}
