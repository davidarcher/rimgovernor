using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Actual recipe access at a standing bench, or research readiness before its future host exists.</summary>
public abstract record RecipeAccess
{
    private protected RecipeAccess() { }
    public abstract string Definition { get; }
    public abstract bool ResearchReady { get; }
    public abstract bool HostReady { get; }

    /// <summary>Food is present only for a cooking capability whose native product gates were read.</summary>
    public sealed record OnBench(O.RecipeState Observed, O.FoodProduction? Food = null) : RecipeAccess
    {
        public override string Definition => Observed.Recipe;
        public override bool ResearchReady => Observed.AvailableNow && (Food is null || Food.Available);
        public override bool HostReady => Observed.AvailableOnBench;
    }

    /// <summary>The catalog's recipe identity and actual research verdict; no native bench exists for this option.</summary>
    public sealed record Catalog(string Name, bool Available) : RecipeAccess
    {
        public override string Definition => Name;
        public override bool ResearchReady => Available;
        public override bool HostReady => true;
    }
}