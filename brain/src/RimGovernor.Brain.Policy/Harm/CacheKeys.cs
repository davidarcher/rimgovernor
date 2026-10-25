// Value tuples are invocation-local cache identities, not additional forecast records.
global using HarmFightKey = (string Pawn, string Role,
    RimGovernor.Brain.Domain.EquatableArray<RimGovernor.Brain.Policy.GearOption> Outfit,
    RimGovernor.Brain.Policy.ForecastSupportConsequences Support,
    RimGovernor.Brain.Policy.HitRange Hits,
    RimGovernor.Brain.Domain.EquatableArray<RimGovernor.Brain.Policy.WeaponBin> Bins,
    double EventDay, double Horizon, RimGovernor.Brain.Policy.StockArrival Stock);
global using IllnessHarmKey = (string Pawn, RimGovernor.Brain.Policy.ForecastSupportConsequences? Support,
    RimGovernor.Brain.Policy.StockArrival Stock, double Horizon);
