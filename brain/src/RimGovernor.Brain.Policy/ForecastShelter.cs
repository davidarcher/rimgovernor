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
/// <summary>A shelter and every heat source inside it (#3679): each source pushes its own heat while the room is below its
/// own target, its electric draw shares its network, and a fueled one's burn (#3673) is charged from the shell's completion.</summary>
public sealed record ForecastShelterPlan(ForecastShelterId Room, EquatableArray<Cell> Interior,
    EquatableArray<string> Prerequisites, Fact<double> InitialTemperature,
    Fact<double> Outdoors, Fact<double> ExchangePerTick, EquatableArray<ThermalSource> Sources, EquatableArray<string> Findings)
{
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
    /// <summary>The exchange of the room as it stands now, open roof cells at the game's no-roof rate (#3534): it holds the
    /// room until its prerequisites finish. Unknown when the ring is open or unread; the room is then open air.</summary>
    public Fact<double> StandingExchangePerTick { get; init; }
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
        // Each powered source of a room whose prerequisites are all scheduled draws on its network; the network is overloaded
        // when any such source's draw or surplus is unknown, or the draws (one per source) exceed the smallest surplus.
        var networks = scratch.Networks; var sources = scratch.Sources;
        networks.Clear(); sources.Clear();
        foreach (var room in rooms)
        {
            if (!Powered(room)) continue;
            var selected = true;
            foreach (var p in room.Prerequisites) selected &= Selected(p, allocations, completed);
            foreach (var group in room.AlternativePrerequisites)
            {
                var any = false;
                foreach (var p in group) any |= Selected(p, allocations, completed);
                selected &= any;
            }
            if (!selected) continue;
            foreach (var source in room.Sources)
            {
                if (source.PowerNetwork.Length == 0) continue;
                var network = networks.GetValueOrDefault(source.PowerNetwork, (0, double.PositiveInfinity, false));
                if (!source.AdditionalWatts.TryGetValue(out var watts) || !source.SurplusWatts.TryGetValue(out var surplus)) network.Unknown = true;
                else
                {
                    if (sources.Add((source.PowerNetwork, source.Target))) network.Watts += watts;
                    network.Surplus = Math.Min(network.Surplus, surplus);
                }
                networks[source.PowerNetwork] = network;
            }
        }
        bool Short(ThermalSource source) => source.PowerNetwork.Length != 0 && networks.TryGetValue(source.PowerNetwork, out var network)
            && (network.Unknown || network.Watts > network.Surplus);
        var outcomes = new ForecastShelterOutcome[rooms.Count];
        for (var i = 0; i < outcomes.Length; i++)
        {
            var room = rooms[i];
            var shared = false;
            foreach (var source in room.Sources) shared |= Short(source);
            // A search projects each room at a few completion ticks over thousands of schedules (#3468).
            var key = (shared, CompletedAt(room, allocations, completed), horizon);
            var byRoom = Outcomes.GetValue(room, static _ => new());
            if (!byRoom.TryGetValue(key, out var outcome))
                byRoom[key] = outcome = ShelterAt(shared ? room with
                    {
                        // A short network's sources have no known supply.
                        Sources = EquatableArray.From(room.Sources.Select(s => Short(s) ? s with { SupplyTicks = default } : s)),
                        Findings = [.. room.Findings, "shared_power_capacity_shortfall"],
                    } : room, key.Item2, horizon);
            outcomes[i] = outcome;
        }
        return EquatableArray.Wrap(outcomes);
    }

    private static bool Powered(ForecastShelterPlan room)
    {
        foreach (var source in room.Sources)
            if (source.PowerNetwork.Length != 0) return true;
        return false;
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
        var known = room.InitialTemperature.IsKnown && room.Outdoors.IsKnown && room.ExchangePerTick.IsKnown
            && room.Sources.Count != 0 && room.Interior.Count != 0;
        foreach (var source in room.Sources)
            known &= source.HeatPerTick.IsKnown && source.TargetTemperature.IsKnown && source.SupplyTicks.IsKnown;
        if (!known)
            return new(room.Room, Known(complete), default, [], [.. findings, "unknown_shelter_thermal_estimate"]);
        var outside = room.Outdoors.Value;
        var temperature = room.InitialTemperature.Value;
        var loss = room.ExchangePerTick.Value;
        // Each source's heat per interior cell and its thermostat, at most the full-efficiency bound.
        var heats = room.Sources.Select(s => (Heat: s.HeatPerTick.Value / room.Interior.Count, Target: Math.Min(20, s.TargetTemperature.Value))).ToArray();
        if (!double.IsFinite(loss) || loss <= 0 || heats.Any(h => !double.IsFinite(h.Heat) || h.Heat < 0 || !double.IsFinite(h.Target))
            || temperature > 20 || outside > 20 || Math.Abs(outside - temperature) > 100)
            return new(room.Room, Known(complete), default, [], [.. findings, "unsupported_shelter_temperature_regime"]);
        if (room.Sources.Any(s => s.SupplyTicks.Value < horizon - complete))
            return new(room.Room, Known(complete), default, [], [.. findings, "unknown_shelter_energy_duration"]);
        // Until the shell completes the room is as it stands, else open air at the observed current outdoor temperature.
        if (complete > 0) temperature = Standing(room, horizon) is { Count: > 0 } standing ? TemperatureAt(standing, complete) : outside;
        var points = Project(temperature, complete, horizon, outside, loss, heats);
        return new(room.Room, Known(complete), Known(points[^1].Temperature), EquatableArray.From(points), EquatableArray.From(findings));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastShelterPlan,
        System.Collections.Concurrent.ConcurrentDictionary<double, EquatableArray<ForecastTemperaturePoint>>> Standings = new();

    /// <summary>
    /// The room as it stands from tick 0 to <paramref name="horizon"/> (#3534): its observed temperature relaxing to
    /// outdoors at its <see cref="ForecastShelterPlan.StandingExchangePerTick"/>, heated by its complete sources. Empty when
    /// the standing exchange, a complete source's mechanics or its supply over the horizon is unknown, or outside the
    /// heater's full-efficiency regime.
    /// </summary>
    internal static EquatableArray<ForecastTemperaturePoint> Standing(ForecastShelterPlan room, double horizon) =>
        Standings.GetValue(room, static _ => new()).GetOrAdd(horizon, h =>
        {
            if (!room.InitialTemperature.TryGetValue(out var temperature) || !room.Outdoors.TryGetValue(out var outside)
                || !room.StandingExchangePerTick.TryGetValue(out var loss) || !double.IsFinite(loss) || loss <= 0 || room.Interior.Count == 0
                || temperature > 20 || outside > 20 || Math.Abs(outside - temperature) > 100) return [];
            var heats = new List<(double Heat, double Target)>();
            foreach (var s in room.Sources)
            {
                if (!s.Complete) continue;
                if (!s.HeatPerTick.TryGetValue(out var heat) || !s.TargetTemperature.TryGetValue(out var target) || !s.SupplyTicks.TryGetValue(out var supply)
                    || supply < h || !double.IsFinite(heat) || heat < 0 || !double.IsFinite(target)) return [];
                heats.Add((heat / room.Interior.Count, Math.Min(20, target)));
            }
            return EquatableArray.From(Project(temperature, 0, h, outside, loss, heats.ToArray()));
        });

    /// <summary>The trajectory's temperature at <paramref name="tick"/>, linear between its points as exposure integrates it.</summary>
    internal static double TemperatureAt(EquatableArray<ForecastTemperaturePoint> points, double tick)
    {
        for (var i = 1; i < points.Count; i++)
            if (tick <= points[i].Tick)
            {
                var a = points[i - 1]; var b = points[i];
                return a.Temperature + (b.Temperature - a.Temperature) * (tick - a.Tick) / (b.Tick - a.Tick);
            }
        return points[^1].Temperature;
    }

    // The room's mean temperature every equalization interval (120 ticks) from start to horizon.
    private static List<ForecastTemperaturePoint> Project(double temperature, double start, double horizon, double outside, double loss,
        (double Heat, double Target)[] heats)
    {
        var points = new List<ForecastTemperaturePoint> { new(start, temperature) };
        var targets = heats.Select(h => h.Target).Distinct().Order().ToArray();
        for (var time = start; time < horizon;)
        {
            var next = Math.Min(horizon, time + 120);
            temperature = Advance(temperature, next - time, outside, loss, heats, targets);
            points.Add(new(next, temperature)); time = next;
        }
        return points;
    }

    /// <summary>
    /// The room's mean temperature after <paramref name="duration"/> ticks. Each source pushes its heat while the room is
    /// below its own target (as CompHeatPusher does), so between consecutive targets the pushing set is fixed and the room
    /// relaxes exponentially toward that set's equilibrium; it stops at a target it reaches, and holds there when the set
    /// just below it heats past it and the set just above it does not. Thermostats limit heating, never cool.
    /// </summary>
    private static double Advance(double temperature, double duration, double outside, double loss,
        (double Heat, double Target)[] heats, double[] targets)
    {
        // The equilibrium of the sources whose target is above t (or at least t, when inclusive).
        double Equilibrium(double t, bool inclusive)
        {
            var heat = 0d;
            foreach (var h in heats) if (h.Target > t || inclusive && h.Target == t) heat += h.Heat;
            return outside + heat / loss;
        }
        while (duration > 0)
        {
            double equilibrium;
            if (Array.IndexOf(targets, temperature) < 0) equilibrium = Equilibrium(temperature, false);
            else
            {
                // At a target: just above it only the higher targets push, just below it the target's own sources push too.
                var above = Equilibrium(temperature, false);
                var below = Equilibrium(temperature, true);
                if (above > temperature) equilibrium = above;
                else if (below < temperature) equilibrium = below;
                else return temperature;
            }
            // The pushing set holds until the room reaches the next target in its direction of travel, if it does.
            var bound = double.NaN;
            foreach (var t in targets)
                if (equilibrium > temperature ? t > temperature && t < equilibrium && !(t >= bound)
                    : t < temperature && t > equilibrium && !(t <= bound)) bound = t;
            var crossing = double.IsNaN(bound) ? double.PositiveInfinity : Math.Log((equilibrium - temperature) / (equilibrium - bound)) / loss;
            if (crossing >= duration) return equilibrium + (temperature - equilibrium) * Math.Exp(-loss * duration);
            temperature = bound; duration -= crossing;
        }
        return temperature;
    }
}
