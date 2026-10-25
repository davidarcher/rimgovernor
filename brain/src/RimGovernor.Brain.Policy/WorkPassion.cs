using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class WorkRules
{
    /// <summary>The vanilla skill passion name used by profile and advice readers.</summary>
    public static string PassionName(Passion passion)
    {
        switch (passion)
        {
            case Passion.None: return nameof(Passion.None);
            case Passion.Minor: return nameof(Passion.Minor);
            case Passion.Major: return nameof(Passion.Major);
        }
        throw new DomainException($"Invalid passion {passion}.");
    }
}
