using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Rot prevented by served freezers, allocated once per stock stack in room-id order (#3799).</summary>
public static class ForecastFreezer
{
    internal static IReadOnlyList<RoomGain> Credit(ForecastRoomService room, ForecastCoupledOutcome outcome,
        IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (context.FreezerStock.Count == 0) return [];
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var served in outcome.ServedRooms.Where(r => PlannedRoomCredits.Serves(context, r.Room, PlannedRole.Freezer))
            .Where(r => RingClosure.CreditDays(context.InvestmentHorizonDays, r.ServedAt) > 0).OrderBy(r => r.Room))
        {
            var layout = context.LayoutRooms.TryGetValue(served.Room, out var planned) ? planned
                : throw new RequiredFactException("freezer room:" + served.Room);
            var shelter = context.Shelters.FirstOrDefault(s => s.PlannedRoom == served.Room
                || s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == served.Room.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ?? throw new RequiredFactException("freezer shelter:" + served.Room);
            // No vented wall or no researched cooler/generator is a real state, earning no cooling credit.
            var coolers = shelter.Sources.Where(s => s.HeatPerTick < 0).ToArray();
            if (coolers.Length == 0) continue;
            var generator = context.RoomPrerequisites.TryGetValue(served.Room, out var prerequisites)
                ? prerequisites.FirstOrDefault(p => p.Definition == PowerRules.WindTurbineDefinition || p.Definition == LayoutUtilityRules.SolarDefinition || p.Definition == PowerRules.GeothermalDefinition) : null;
            if (coolers.Any(s => !HeatSupplies.Supplied(s.Supply, out _)) && generator is null) continue;
            if (generator is not null && coolers.Any(s => !double.IsFinite(s.PowerWatts) || s.PowerWatts <= 0))
                throw new RequiredFactException("cooler power draw:" + served.Room);
            if (shelter.Thermal is not ForecastShelterThermal.Estimated) throw new RequiredFactException("freezer thermal:" + served.Room);
            // Its service day already owes its sources. Reuse the shared shelter model, with all owed work complete.
            var closed = shelter with { Prerequisites = [], AlternativePrerequisites = [],
                Sources = EquatableArray.From(shelter.Sources.Select(s => s.HeatPerTick < 0 && !HeatSupplies.Supplied(s.Supply, out _) && generator is not null
                    ? s with { Supply = new HeatSupply.Powered(generator.Id, s.PowerWatts, s.PowerWatts, GameTime.TicksPerDay) } : s)) };
            var rate = RotRate(context.RotRates, ForecastShelterPolicy.SteadyTemperature(closed));
            var gains = context.FreezerStock.Where(s => !taken.Contains(s.Id)).Select(s => new RoomGain(s.Id, "", Gain(s, rate, context)))
                .Where(g => g.PerDay > 0).OrderByDescending(g => g.PerDay).ThenBy(g => g.Subject, StringComparer.Ordinal)
                .Take(layout.Region.Cells().Count).ToArray();
            taken.UnionWith(gains.Select(g => g.Subject));
            if (served.Room == room.Room) return gains;
        }
        return [];
    }

    /// <summary>Linear interpolation of native whole-degree samples, never a copied rot curve.</summary>
    public static double RotRate(IReadOnlyDictionary<int, double> samples, double temperature)
    {
        if (!double.IsFinite(temperature)) throw new RequiredFactException("freezer steady temperature");
        var lower = checked((int)Math.Floor(temperature));
        var upper = checked((int)Math.Ceiling(temperature));
        if (!samples.TryGetValue(lower, out var a) || !samples.TryGetValue(upper, out var b)
            || !double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b < 0)
            throw new RequiredFactException("native rot samples:" + temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return lower == upper ? a : a + (b - a) * (temperature - lower);
    }

    private static double Gain(FoodStock stock, double freezerRate, ForecastCoupledContext context) =>
        SpoilageAvoided(stock, stock.RotRate, freezerRate, context.MaterialValues);

    /// <summary>
    /// The value a day that cooling a stock from <paramref name="warmRate"/> to <paramref name="coolRate"/> (native rot-rate
    /// multipliers) keeps from rotting: its count at the material value over its rot life at the warm rate. Zero for a stock
    /// that does not rot, a cooling that slows nothing, or an unpriced material. The power comparison prices a cooler's
    /// interruption with it: the stock warms to the outdoor rate.
    /// </summary>
    public static double SpoilageAvoided(FoodStock stock, double warmRate, double coolRate, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values)
    {
        if (!double.IsFinite(warmRate) || warmRate < 0 || !double.IsFinite(coolRate) || coolRate < 0) throw new RequiredFactException("stock rot rate:" + stock.Id);
        if (stock.Rot is not FoodRot.Rots { Ticks: > 0 } rot || warmRate == 0 || warmRate <= coolRate) return 0;
        var resource = new Resource(stock.DefName);
        if (!values.TryGetValue(resource, out var value)) throw new RequiredFactException("material value " + stock.DefName);
        return value.Basis == MaterialValueBasis.Unpriced ? 0
            : stock.Count * value.PerUnit * (warmRate - coolRate) / (warmRate * rot.Ticks / GameTime.TicksPerDay);
    }
}


