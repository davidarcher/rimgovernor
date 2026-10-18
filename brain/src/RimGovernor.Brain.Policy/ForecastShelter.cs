using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public enum ForecastShelterSource { Planned, Observed }
public readonly record struct ForecastShelterId(ForecastShelterSource Source, string Value)
{
    public override string ToString() => (Source == ForecastShelterSource.Observed ? "observed/" : "planned/") + Value;
}
internal sealed record ForecastShelterGeometry(ForecastShelterId Id, EquatableArray<Cell> Interior, Rectangle Bounds, Cell Door, bool Outdoor)
{
    /// <summary>The planned room an observed room realizes (same interior), or 0.</summary>
    public int PlannedRoom { get; init; }
}
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
    /// <summary>The planned room whose roof this shelter's roof cells complete: a planned room generated as planned sites, or
    /// the planned room a standing observed room's interior matches; 0 for none.</summary>
    public int PlannedRoom { get; init; }
    /// <summary>The per-cell roof work of the planned room (<see cref="PlannedRoom"/>), each cell's realizations (a subset of
    /// AlternativePrerequisites). Together the cells are the room's one roof development unit (#3334): a planned room's
    /// designated roof is finished even where the room already holds its heat, since thermal relief misses the open cells'
    /// heat loss and margin. A planned room still raised by placed construction is not requested development.</summary>
    public EquatableArray<EquatableArray<string>> RoofCells { get; init; }
}
public sealed record ForecastShelterOutcome(ForecastShelterId Room, Fact<double> CompleteAt,
    Fact<double> TemperatureAtHorizon, EquatableArray<ForecastTemperaturePoint> Trajectory,
    EquatableArray<string> Findings);
public sealed record ForecastTemperaturePoint(double Tick, double Temperature);

public static class ForecastShelterPolicy
{
    public static EquatableArray<ForecastShelterOutcome> ForecastShelters(IEnumerable<ForecastShelterPlan> rooms,
        ForecastActivityPlan schedule, double horizon)
        => ForecastShelters(rooms.ToArray(), schedule.Allocations.ToDictionary(a => a.Activity, StringComparer.Ordinal), null, horizon, new());

    /// <summary>A search's scratch for <see cref="ForecastShelters(IReadOnlyList{ForecastShelterPlan}, IReadOnlyDictionary{string, ForecastActivityAllocation}, IReadOnlyDictionary{string, Fact{bool}}?, double, ShelterScratch)"/>, reused by every projection.</summary>
    internal sealed class ShelterScratch
    {
        public readonly Dictionary<string, (double Watts, double Surplus, bool Unknown)> Networks = new(StringComparer.Ordinal);
        public readonly HashSet<(string Network, string Source)> Sources = [];
    }

    /// <summary>Each room's projection when its prerequisites are the allocations <paramref name="completed"/> marks done
    /// (all of <paramref name="allocations"/> when null). A search projects thousands of schedules against one set of
    /// rooms, so this reads the search's own rows and allocates only the result (#3468).</summary>
    internal static EquatableArray<ForecastShelterOutcome> ForecastShelters(IReadOnlyList<ForecastShelterPlan> rooms,
        IReadOnlyDictionary<string, ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, Fact<bool>>? completed, double horizon, ShelterScratch scratch)
    {
        // A powered room whose prerequisites are all scheduled draws on its network; the network is overloaded when any such
        // room's draw or surplus is unknown, or the draws (one per source) exceed the smallest surplus.
        var networks = scratch.Networks; var sources = scratch.Sources;
        networks.Clear(); sources.Clear();
        foreach (var room in rooms)
        {
            if (room.PowerNetwork.Length == 0) continue;
            var selected = true;
            foreach (var p in room.Prerequisites) selected &= Selected(p, allocations, completed);
            foreach (var group in room.AlternativePrerequisites)
            {
                var any = false;
                foreach (var p in group) any |= Selected(p, allocations, completed);
                selected &= any;
            }
            if (!selected) continue;
            var network = networks.GetValueOrDefault(room.PowerNetwork, (0, double.PositiveInfinity, false));
            if (!room.AdditionalWatts.TryGetValue(out var watts) || !room.SurplusWatts.TryGetValue(out var surplus)) network.Unknown = true;
            else
            {
                if (sources.Add((room.PowerNetwork, room.Source))) network.Watts += watts;
                network.Surplus = Math.Min(network.Surplus, surplus);
            }
            networks[room.PowerNetwork] = network;
        }
        var outcomes = new ForecastShelterOutcome[rooms.Count];
        for (var i = 0; i < outcomes.Length; i++)
        {
            var room = rooms[i];
            var shared = networks.TryGetValue(room.PowerNetwork, out var network) && (network.Unknown || network.Watts > network.Surplus);
            // A search projects each room at a few completion ticks over thousands of schedules (#3468).
            var key = (shared, CompletedAt(room, allocations, completed), horizon);
            var byRoom = Outcomes.GetValue(room, static _ => new());
            if (!byRoom.TryGetValue(key, out var outcome))
                byRoom[key] = outcome = ShelterAt(shared ? room with { SupplyTicks = default, Findings = [.. room.Findings, "shared_power_capacity_shortfall"] } : room,
                    key.Item2, horizon);
            outcomes[i] = outcome;
        }
        return EquatableArray.Wrap(outcomes);
    }

    private static bool Selected(string activity, IReadOnlyDictionary<string, ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, Fact<bool>>? completed)
        => allocations.ContainsKey(activity) && (completed is null || completed.GetValueOrDefault(activity) == Known(true));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastShelterPlan,
        System.Collections.Concurrent.ConcurrentDictionary<(bool Shared, double Complete, double Horizon), ForecastShelterOutcome>> Outcomes = new();

    /// <summary>When the room's shell completes in the schedule, or NaN when a prerequisite is not scheduled.</summary>
    private static double CompletedAt(ForecastShelterPlan room, IReadOnlyDictionary<string, ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, Fact<bool>>? completed)
    {
        double complete = 0;
        foreach (var p in room.Prerequisites)
        {
            if (!Selected(p, allocations, completed)) return double.NaN;
            complete = Math.Max(complete, allocations[p].End);
        }
        foreach (var group in room.AlternativePrerequisites)
        {
            var first = double.PositiveInfinity;
            foreach (var p in group)
                if (Selected(p, allocations, completed)) first = Math.Min(first, allocations[p].End);
            if (double.IsPositiveInfinity(first)) return double.NaN;
            complete = Math.Max(complete, first);
        }
        return complete;
    }

    /// <summary>Conditional current-weather heat balance, bounded to the native heater's full-efficiency regime.
    /// Enclosure, roof and source completion are prerequisites; presence alone has no thermal credit.</summary>
    public static ForecastShelterOutcome ForecastShelter(ForecastShelterPlan room,
        ForecastActivityPlan schedule, double horizon) =>
        ShelterAt(room, CompletedAt(room, schedule.Allocations.ToDictionary(a => a.Activity, StringComparer.Ordinal), null), horizon);

    /// <summary>The room's projection when its shell completes at <paramref name="complete"/> (NaN: never scheduled).</summary>
    private static ForecastShelterOutcome ShelterAt(ForecastShelterPlan room, double complete, double horizon)
    {
        var findings = new SortedSet<string>(room.Findings, StringComparer.Ordinal)
        { "current_weather_and_power_constant", "closed_doors_thin_roof", "spatial_mean_exchange_estimate", "heater_full_efficiency_below_20C" };
        if (double.IsNaN(complete))
            return new(room.Room, default, default, [], [.. findings, "incomplete_shelter_prerequisites"]);
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
