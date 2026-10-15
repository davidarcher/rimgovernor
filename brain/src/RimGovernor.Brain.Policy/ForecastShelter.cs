using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public enum ForecastShelterSource { Planned, Observed }
public readonly record struct ForecastShelterId(ForecastShelterSource Source, string Value)
{
    public override string ToString() => (Source == ForecastShelterSource.Observed ? "observed/" : "planned/") + Value;
}
internal sealed record ForecastShelterGeometry(ForecastShelterId Id, EquatableArray<Cell> Interior, Rectangle Bounds, Cell Door, bool Outdoor);
public sealed record ForecastShelterPlan(ForecastShelterId Room, EquatableArray<Cell> Interior,
    EquatableArray<string> Prerequisites, Fact<double> InitialTemperature,
    Fact<double> Outdoors, Fact<double> ExchangePerTick, Fact<double> HeatPerTick,
    Fact<double> TargetTemperature, Fact<double> SupplyTicks, EquatableArray<string> Findings)
{
    public string PowerNetwork { get; init; } = "";
    public string Source { get; init; } = "";
    public Fact<double> AdditionalWatts { get; init; }
    public Fact<double> SurplusWatts { get; init; }
    /// <summary>Each physical prerequisite needs one selected realization; distinct sites remain additive.</summary>
    public EquatableArray<EquatableArray<string>> AlternativePrerequisites { get; init; }
}
public sealed record ForecastShelterOutcome(ForecastShelterId Room, Fact<double> CompleteAt,
    Fact<double> TemperatureAtHorizon, EquatableArray<ForecastTemperaturePoint> Trajectory,
    EquatableArray<string> Findings);
public sealed record ForecastTemperaturePoint(double Tick, double Temperature);

public static partial class PolicyFunctions
{
    public static EquatableArray<ForecastShelterOutcome> ForecastShelters(IEnumerable<ForecastShelterPlan> rooms,
        ForecastActivityPlan schedule, double horizon)
    {
        var selected = schedule.Allocations.Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        var plans = rooms.ToArray();
        var overloaded = plans.Where(r => r.PowerNetwork.Length != 0 && r.Prerequisites.All(selected.Contains)
                && r.AlternativePrerequisites.All(group => group.Any(selected.Contains)))
            .GroupBy(r => r.PowerNetwork).Where(g => g.Any(r => !r.AdditionalWatts.IsKnown || !r.SurplusWatts.IsKnown)
                || g.GroupBy(r => r.Source).Sum(s => s.First().AdditionalWatts.Value) > g.Min(r => r.SurplusWatts.Value))
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return EquatableArray.From(plans.Select(r => ForecastShelter(overloaded.Contains(r.PowerNetwork)
            ? r with { SupplyTicks = default, Findings = [.. r.Findings, "shared_power_capacity_shortfall"] } : r, schedule, horizon)));
    }

    /// <summary>Conditional current-weather heat balance, bounded to the native heater's full-efficiency regime.
    /// Enclosure, roof and source completion are prerequisites; presence alone has no thermal credit.</summary>
    public static ForecastShelterOutcome ForecastShelter(ForecastShelterPlan room,
        ForecastActivityPlan schedule, double horizon)
    {
        var findings = new SortedSet<string>(room.Findings, StringComparer.Ordinal)
        { "current_weather_and_power_constant", "closed_doors_thin_roof", "spatial_mean_exchange_estimate", "heater_full_efficiency_below_20C" };
        var allocations = schedule.Allocations.ToDictionary(a => a.Activity, StringComparer.Ordinal);
        var missing = room.Prerequisites.Where(p => !allocations.ContainsKey(p)).ToArray();
        if (missing.Length != 0 || room.AlternativePrerequisites.Any(group => !group.Any(allocations.ContainsKey)))
            return new(room.Room, default, default, [], [.. findings, "incomplete_shelter_prerequisites"]);
        var complete = room.Prerequisites.Select(p => allocations[p].End).Concat(room.AlternativePrerequisites
            .Select(group => group.Where(allocations.ContainsKey).Min(p => allocations[p].End))).DefaultIfEmpty(0).Max();
        if (complete > horizon) return new(room.Room, Known(complete), default, [], [.. findings, "shelter_beyond_horizon"]);
        if (!room.InitialTemperature.IsKnown || !room.Outdoors.IsKnown || !room.ExchangePerTick.IsKnown
            || !room.HeatPerTick.IsKnown || !room.TargetTemperature.IsKnown || !room.SupplyTicks.IsKnown
            || room.Interior.Count == 0)
            return new(room.Room, Known(complete), default, [], [.. findings, "unknown_shelter_thermal_estimate"]);
        var outside = room.Outdoors.Value;
        var temperature = room.InitialTemperature.Value;
        var target = Math.Min(20, room.TargetTemperature.Value);
        var loss = room.ExchangePerTick.Value;
        var heat = room.HeatPerTick.Value / room.Interior.Count;
        if (!double.IsFinite(loss) || loss <= 0 || !double.IsFinite(heat) || heat < 0
            || temperature > 20 || outside > 20 || Math.Abs(outside - temperature) > 100)
            return new(room.Room, Known(complete), default, [], [.. findings, "unsupported_shelter_temperature_regime"]);
        if (room.SupplyTicks.Value < horizon - complete)
            return new(room.Room, Known(complete), default, [], [.. findings, "unknown_shelter_energy_duration"]);
        // Until the shell completes, open-air equilibrium is the observed current outdoor temperature.
        if (complete > 0) temperature = outside;
        var points = new List<ForecastTemperaturePoint> { new(complete, temperature) };
        var equilibrium = outside + heat / loss;
        // The native thermostat limits heating, not cooling; the analytical mean model captures both.
        for (var time = complete; time < horizon;)
        {
            var next = Math.Min(horizon, time + 120);
            var duration = next - time;
            if (temperature >= target)
            {
                var passive = outside + (temperature - outside) * Math.Exp(-loss * duration);
                if (outside >= target || passive >= target) temperature = passive;
                else
                {
                    var crossing = Math.Log((temperature - outside) / (target - outside)) / loss;
                    temperature = Math.Min(target, equilibrium + (target - equilibrium) * Math.Exp(-loss * (duration - crossing)));
                }
            }
            else
            {
                var heated = equilibrium + (temperature - equilibrium) * Math.Exp(-loss * duration);
                if (heated <= target) temperature = heated;
                else
                {
                    var crossing = Math.Log((equilibrium - temperature) / (equilibrium - target)) / loss;
                    temperature = outside > target ? outside + (target - outside) * Math.Exp(-loss * (duration - crossing)) : target;
                }
            }
            points.Add(new(next, temperature)); time = next;
        }
        return new(room.Room, Known(complete), Known(temperature), EquatableArray.From(points), EquatableArray.From(findings));
    }
}
