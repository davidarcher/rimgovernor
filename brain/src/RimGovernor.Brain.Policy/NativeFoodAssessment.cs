namespace RimGovernor.Brain.Policy;

public enum NativeFoodRisk { NoDemonstratedShortfall, SupplyBelowMinimum, RecoveryGap }

/// <summary>Current stock and reserve evidence, with recovery explicitly conditional. No exact eating or production trajectory is inferred.</summary>
public abstract record NativeFoodAssessment
{
    private NativeFoodAssessment() { }
    public sealed record Unassessed(string Reason) : NativeFoodAssessment;
    public sealed record NoConsumers : NativeFoodAssessment;
    public sealed record Reviewed(double RunwayDays, double MinimumDays, FoodDeliveries Recovery,
        FoodReserveState Reserve, NativeFoodRisk Risk) : NativeFoodAssessment;

    /// <summary>A reason to change available native work, not authority for an exact pawn order.</summary>
    public bool NeedsResponse => this is Reviewed { Risk: NativeFoodRisk.SupplyBelowMinimum or NativeFoodRisk.RecoveryGap };

    public static NativeFoodAssessment Of(FoodRunway? runway, FoodPlan? plan, FoodReserveState reserve)
    {
        if (runway is FoodRunway.NoEaters) return new NoConsumers();
        if (runway is not FoodRunway.Days(var days)) return new Unassessed("food_runway_unobserved");
        if (plan is null) return new Unassessed("food_plan_unobserved");
        if (plan.MinimumDays is ForecastTerm.Unassessed(var reason)) return new Unassessed(reason);
        if (plan.MinimumDays is not ForecastTerm.Assessed(var minimum) || !FieldCapacityRules.FieldPositive(minimum)) throw new FoodFactsException();
        if (!FoodForecastRules.FoodNumber(days)) throw new FoodFactsException();
        var recovery = FoodReserveRules.FoodDeliveryDays(plan);
        var timing = FoodReserveRules.RecoveryAtRunway(days, recovery);
        var risk = days < minimum || reserve is FoodReserveState.Reviewed { Review.IsShort: true }
            ? NativeFoodRisk.SupplyBelowMinimum
            : timing == FoodRecoveryTiming.AtOrAfterRunway || reserve is FoodReserveState.Reviewed { Review.Emergency: true }
                ? NativeFoodRisk.RecoveryGap : NativeFoodRisk.NoDemonstratedShortfall;
        return new Reviewed(days, minimum, recovery, reserve, risk);
    }
}
