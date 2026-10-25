using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

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
    EquatableArray<string> Prerequisites, ForecastShelterThermal Thermal, EquatableArray<ThermalSource> Sources, EquatableArray<string> Findings)
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
}
/// <summary>A shelter's heat balance inputs: estimated from its read cells, ring and the map's outdoors, or unestimated
/// (its geometry or a source is unsupported, its cells or the shelter read are absent; the findings name why).</summary>
public abstract record ForecastShelterThermal
{
    private protected ForecastShelterThermal() { }
    /// <summary>The room's mean read temperature, the map's outdoors and the completed shell's exchange per cell per tick.</summary>
    public sealed record Estimated(double InitialTemperature, double Outdoors, double ExchangePerTick, ForecastShelterStanding Standing)
        : ForecastShelterThermal;
    public sealed record Unestimated : ForecastShelterThermal;
}
/// <summary>The room as it stands now (#3534), which holds it until its prerequisites finish: enclosed behind a closed ring
/// at its exchange (open roof cells at the game's no-roof rate), or open air.</summary>
public abstract record ForecastShelterStanding
{
    private protected ForecastShelterStanding() { }
    public sealed record Enclosed(double ExchangePerTick) : ForecastShelterStanding;
    public sealed record Open : ForecastShelterStanding;
}
/// <summary>A shelter's projection in a schedule: incomplete (a prerequisite is not scheduled), unprojected (its shell completes
/// at a tick but no temperature is projected; the findings name why, beyond the horizon among them), or projected.</summary>
public abstract record ForecastShelterOutcome(ForecastShelterId Room, EquatableArray<string> Findings)
{
    public sealed record Incomplete(ForecastShelterId Room, EquatableArray<string> Findings) : ForecastShelterOutcome(Room, Findings);
    public abstract record Scheduled(ForecastShelterId Room, double CompleteAt, EquatableArray<string> Findings) : ForecastShelterOutcome(Room, Findings);
    public sealed record Unprojected(ForecastShelterId Room, double CompleteAt, EquatableArray<string> Findings)
        : Scheduled(Room, CompleteAt, Findings);
    public sealed record Projected(ForecastShelterId Room, double CompleteAt, double TemperatureAtHorizon,
        EquatableArray<ForecastTemperaturePoint> Trajectory, EquatableArray<string> Findings) : Scheduled(Room, CompleteAt, Findings);
}
public sealed record ForecastTemperaturePoint(double Tick, double Temperature);

public static class ForecastShelterPolicy
{
    public static EquatableArray<ForecastShelterOutcome> ForecastShelters(IEnumerable<ForecastShelterPlan> rooms,
        ForecastActivityPlan schedule, double horizon)
        => ForecastShelters(rooms.ToArray(), schedule.Allocations.ToDictionary(a => a.Activity, StringComparer.Ordinal), null, horizon, new());

    /// <summary>A search's scratch for <see cref="ForecastShelters(IReadOnlyList{ForecastShelterPlan}, IReadOnlyDictionary{string, ForecastActivityAllocation}, IReadOnlyDictionary{string, Fact{bool}}?, double, ShelterScratch)"/>, reused by every projection.</summary>
    internal sealed class ShelterScratch
    {
        public readonly Dictionary<string, (double Watts, double Surplus)> Networks = new(StringComparer.Ordinal);
        public readonly HashSet<(string Network, string Source)> Sources = [];
    }

    /// <summary>Each room's projection when its prerequisites are the allocations <paramref name="completed"/> marks done
    /// (all of <paramref name="allocations"/> when null). A search projects thousands of schedules against one set of
    /// rooms, so this reads the search's own rows and allocates only the result (#3468).</summary>
    internal static EquatableArray<ForecastShelterOutcome> ForecastShelters(IReadOnlyList<ForecastShelterPlan> rooms,
        IReadOnlyDictionary<string, ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, bool>? completed, double horizon, ShelterScratch scratch)
    {
        // Each networked source of a room whose prerequisites are all scheduled draws on its network; the network is
        // overloaded when the draws (one per source) exceed the smallest surplus.
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
                if (!HeatSupplies.OnNetwork(source.Supply, out var id, out var watts, out var surplus)) continue;
                var network = networks.GetValueOrDefault(id, (0, double.PositiveInfinity));
                if (sources.Add((id, source.Target))) network.Watts += watts;
                network.Surplus = Math.Min(network.Surplus, surplus);
                networks[id] = network;
            }
        }
        bool Short(ThermalSource source) => HeatSupplies.OnNetwork(source.Supply, out var id, out _, out _)
            && networks.TryGetValue(id, out var network) && network.Watts > network.Surplus;
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
                        // A short network's sources are short of power.
                        Sources = EquatableArray.From(room.Sources.Select(s => Short(s) ? s with { Supply = HeatSupplies.Overloaded(s.Supply) } : s)),
                        Findings = [.. room.Findings, "shared_power_capacity_shortfall"],
                    } : room, key.Item2, horizon);
            outcomes[i] = outcome;
        }
        return EquatableArray.Wrap(outcomes);
    }

    private static bool Powered(ForecastShelterPlan room)
    {
        foreach (var source in room.Sources)
            if (HeatSupplies.OnNetwork(source.Supply, out _, out _, out _)) return true;
        return false;
    }

    private static bool Selected(string activity, IReadOnlyDictionary<string, ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, bool>? completed)
        => allocations.ContainsKey(activity) && (completed is null || completed.GetValueOrDefault(activity));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastShelterPlan,
        System.Collections.Concurrent.ConcurrentDictionary<(bool Shared, double Complete, double Horizon), ForecastShelterOutcome>> Outcomes = new();

    /// <summary>When the room's shell completes in the schedule, or NaN when a prerequisite is not scheduled.</summary>
    private static double CompletedAt(ForecastShelterPlan room, IReadOnlyDictionary<string, ForecastActivityAllocation> allocations, IReadOnlyDictionary<string, bool>? completed)
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
            return new ForecastShelterOutcome.Incomplete(room.Room, [.. findings, "incomplete_shelter_prerequisites"]);
        if (complete > horizon) return new ForecastShelterOutcome.Unprojected(room.Room, complete, [.. findings, "shelter_beyond_horizon"]);
        if (room.Thermal is not ForecastShelterThermal.Estimated { InitialTemperature: var temperature, Outdoors: var outside, ExchangePerTick: var loss }
            || room.Sources.Count == 0 || room.Interior.Count == 0)
            return new ForecastShelterOutcome.Unprojected(room.Room, complete, [.. findings, "unknown_shelter_thermal_estimate"]);
        // A source short of power, unconnected or burning unsteadily heats nothing the forecast can count on.
        if (room.Sources.Any(s => !HeatSupplies.Supplied(s.Supply, out _)))
            return new ForecastShelterOutcome.Unprojected(room.Room, complete, [.. findings, "unsupplied_shelter_heat_source"]);
        // Each source's heat per interior cell and its thermostat, at most the full-efficiency bound.
        var heats = room.Sources.Select(s => (Heat: s.HeatPerTick / room.Interior.Count, Target: Math.Min(20, s.TargetTemperature))).ToArray();
        if (!double.IsFinite(loss) || loss <= 0 || heats.Any(h => !double.IsFinite(h.Heat) || h.Heat < 0 || !double.IsFinite(h.Target))
            || temperature > 20 || outside > 20 || Math.Abs(outside - temperature) > 100)
            return new ForecastShelterOutcome.Unprojected(room.Room, complete, [.. findings, "unsupported_shelter_temperature_regime"]);
        if (room.Sources.Any(s => HeatSupplies.Supplied(s.Supply, out var ticks) && ticks < horizon - complete))
            return new ForecastShelterOutcome.Unprojected(room.Room, complete, [.. findings, "shelter_energy_runs_out"]);
        // Until the shell completes the room is as it stands, else open air at the observed current outdoor temperature.
        if (complete > 0) temperature = Standing(room, horizon) is { Count: > 0 } standing ? TemperatureAt(standing, complete) : outside;
        var points = Project(temperature, complete, horizon, outside, loss, heats);
        return new ForecastShelterOutcome.Projected(room.Room, complete, points[^1].Temperature, EquatableArray.From(points), EquatableArray.From(findings));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastShelterPlan,
        System.Collections.Concurrent.ConcurrentDictionary<double, EquatableArray<ForecastTemperaturePoint>>> Standings = new();

    /// <summary>
    /// The room as it stands from tick 0 to <paramref name="horizon"/> (#3534): its observed temperature relaxing to
    /// outdoors at its <see cref="ForecastShelterStanding.Enclosed"/> exchange, heated by its complete sources. Empty when
    /// the room is unestimated or open air, a complete source is not supplied over the horizon, or outside the heater's
    /// full-efficiency regime.
    /// </summary>
    internal static EquatableArray<ForecastTemperaturePoint> Standing(ForecastShelterPlan room, double horizon) =>
        Standings.GetValue(room, static _ => new()).GetOrAdd(horizon, h =>
        {
            if (room.Thermal is not ForecastShelterThermal.Estimated { InitialTemperature: var temperature, Outdoors: var outside,
                    Standing: ForecastShelterStanding.Enclosed { ExchangePerTick: var loss } }
                || !double.IsFinite(loss) || loss <= 0 || room.Interior.Count == 0
                || temperature > 20 || outside > 20 || Math.Abs(outside - temperature) > 100) return [];
            var heats = new List<(double Heat, double Target)>();
            foreach (var s in room.Sources)
            {
                if (!s.Complete) continue;
                if (!HeatSupplies.Supplied(s.Supply, out var supply) || supply < h || !double.IsFinite(s.HeatPerTick) || s.HeatPerTick < 0
                    || !double.IsFinite(s.TargetTemperature)) return [];
                heats.Add((s.HeatPerTick / room.Interior.Count, Math.Min(20, s.TargetTemperature)));
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

/// <summary>Policy reads of a heat source's <see cref="HeatSupply"/>.</summary>
public static class HeatSupplies
{
    /// <summary>Whether the source draws on a power network: its id, its additional draw and the network's current surplus.</summary>
    public static bool OnNetwork(HeatSupply supply, out string network, out double watts, out double surplus)
    {
        switch (supply)
        {
            case HeatSupply.Powered p:
                (network, watts, surplus) = (p.Network, p.AdditionalWatts, p.SurplusWatts);
                return true;
            case HeatSupply.ShortOfPower s:
                (network, watts, surplus) = (s.Network, s.AdditionalWatts, s.SurplusWatts);
                return true;
            case HeatSupply.Unconnected or HeatSupply.Burning or HeatSupply.UnsteadyBurn:
                (network, watts, surplus) = ("", 0, 0);
                return false;
        }
        throw new ArgumentOutOfRangeException(nameof(supply), supply, "unhandled heat supply");
    }

    /// <summary>
    /// Whether the source is supplied, and for how many ticks: a powered heater's <see cref="HeatSupply.Powered.Ticks"/>, a
    /// steady burn's unlimited. Short of power, unconnected and an unsteady burn are not supplied.
    /// </summary>
    public static bool Supplied(HeatSupply supply, out double ticks)
    {
        switch (supply)
        {
            case HeatSupply.Powered p:
                ticks = p.Ticks;
                return true;
            case HeatSupply.Burning:
                ticks = double.PositiveInfinity;
                return true;
            case HeatSupply.ShortOfPower or HeatSupply.Unconnected or HeatSupply.UnsteadyBurn:
                ticks = 0;
                return false;
        }
        throw new ArgumentOutOfRangeException(nameof(supply), supply, "unhandled heat supply");
    }

    /// <summary>A powered source whose network the schedule overloads is short of power.</summary>
    public static HeatSupply Overloaded(HeatSupply supply) =>
        supply is HeatSupply.Powered p ? new HeatSupply.ShortOfPower(p.Network, p.AdditionalWatts, p.SurplusWatts) : supply;

    /// <summary>The supply <paramref name="elapsed"/> ticks on: a powered heater's remaining ticks, every other supply as it is.</summary>
    public static HeatSupply After(HeatSupply supply, double elapsed) =>
        supply is HeatSupply.Powered p ? p with { Ticks = Math.Max(0, p.Ticks - elapsed) } : supply;
}
