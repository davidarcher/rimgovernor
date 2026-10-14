namespace RimGovernor.Brain.Policy;

public enum ProductionNeedPriority { Maintenance = 1, Required = 2, Safety = 3 }
public sealed record ForecastRequirementRelief(string Need, ProductionNeedPriority Priority, double Fraction);
public sealed record ForecastRequirementAssessment(EquatableArray<ForecastRequirementRelief> Relief,
    Fact<double> MaterialCost, Fact<double> Work);
public sealed record ProductionRequirementVariant(EquatableArray<string> Ingredients, ForecastRequirementAssessment Assessment);
public sealed record ProductionRequirements(EquatableArray<ProductionRequirementVariant> Variants);
public sealed record ForecastRequirementPriority(double Safety = 0, double Required = 0, double Maintenance = 0,
    double MaterialCost = 0, double Work = 0)
{
    public bool BetterThan(ForecastRequirementPriority other, double value, double otherValue)
    {
        if (Safety != other.Safety) return Safety > other.Safety;
        if (Required != other.Required) return Required > other.Required;
        if (Maintenance != other.Maintenance) return Maintenance > other.Maintenance;
        if (value != otherValue) return value > otherValue;
        if (MaterialCost != other.MaterialCost) return MaterialCost < other.MaterialCost;
        return Work < other.Work;
    }
}

public static partial class PolicyFunctions
{
    private static ForecastRequirementPriority? RequirementPriorityOf(IEnumerable<ForecastMaterialOption> options, SortedSet<string> findings)
    {
        var relief = new Dictionary<string, (ProductionNeedPriority Priority, double Fraction)>(StringComparer.Ordinal);
        double material = 0, work = 0;
        foreach (var option in options)
        {
            if (option.Requirements is not { } assessment) continue;
            if (!assessment.MaterialCost.TryGetValue(out var cost) || !assessment.Work.TryGetValue(out var effort))
            { findings.Add("unknown_requirement_cost:" + option.Id); return null; }
            if (!double.IsFinite(cost) || cost < 0 || !double.IsFinite(effort) || effort < 0)
                throw new DomainException("invalid requirement cost");
            material += cost; work += effort;
            foreach (var effect in assessment.Relief)
            {
                if (string.IsNullOrWhiteSpace(effect.Need) || !double.IsFinite(effect.Fraction) || effect.Fraction < 0 || effect.Fraction > 1
                    || effect.Priority is < ProductionNeedPriority.Maintenance or > ProductionNeedPriority.Safety)
                    throw new DomainException("invalid requirement relief");
                if (relief.TryGetValue(effect.Need, out var prior) && prior.Priority != effect.Priority)
                    throw new DomainException("inconsistent requirement priority");
                relief[effect.Need] = (effect.Priority, Math.Min(1, prior.Fraction + effect.Fraction));
            }
        }
        if (!double.IsFinite(material) || !double.IsFinite(work)) throw new DomainException("requirement cost overflow");
        double Sum(ProductionNeedPriority p) => relief.Where(r => r.Value.Priority == p).Sum(r => r.Value.Fraction);
        return new(Sum(ProductionNeedPriority.Safety), Sum(ProductionNeedPriority.Required), Sum(ProductionNeedPriority.Maintenance), material, work);
    }
}
