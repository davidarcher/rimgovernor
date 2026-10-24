using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Where a test pawn sleeps (#3840, <see cref="NeedsForecastPawn.Sleep"/>) and the planned bed it moves into, for the tests
/// of the room thoughts: a wood bed at the temperate <see cref="Temperate"/> inside the human comfortable range, so the bed's
/// own terms (ground, comfort, rest, temperature) are 0 and only the room thoughts move the credit.
/// </summary>
internal static class SleepFixtures
{
    /// <summary>The case's ambient temperature, inside the human ComfyTemperatureMin/Max (16 to 26 °C).</summary>
    public const double Temperate = 21;

    /// <summary>A pawn's rest gained a tick in a bed of rest effectiveness 1.</summary>
    public const double Gain = .00005;

    /// <summary>Lying in <paramref name="bed"/> (a wood bed: costed, Comfort 0.75), its room psychologically outdoors or not.</summary>
    public static NeedSleep InBed(string bed, bool outside = false) => new(bed, outside, false, Gain, .75, Temperate);

    /// <summary>On the ground at an indoor cell (no bed): no room thought.</summary>
    public static NeedSleep Ground(bool outside = false) => new("", outside, true, Gain * .8, 0, Temperate);

    /// <summary>The planned wood bed (#3840 SleepingBedFacts): costed, rest effectiveness 1, Comfort 0.75.</summary>
    public static readonly SleepingBedFacts WoodBed = new("Bed", "WoodLog", false, 1, .75);

    /// <summary><paramref name="pawn"/> sleeping at <paramref name="sleep"/>, its sleep range the human one and its rest need's gain per effectiveness <see cref="Gain"/>.</summary>
    public static NeedsForecastPawn Sleeping(NeedsForecastPawn pawn, NeedSleep sleep) => pawn with
    {
        Sleep = sleep, SleepComfortableMin = 16, SleepComfortableMax = 26,
        Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "rest" ? n with { GainPerTickPerEffectiveness = Gain } : n)),
    };

    /// <summary>The map at <see cref="Temperate"/> outdoors, no cells or sources.</summary>
    public static CoupledThermalRead Outdoors() => new()
    {
        Cells = [], Sources = [], Workers = [], RoofRoutes = [], Outdoors = Temperate,
        ThinRoofExchangePerTick = 0, NoRoofExchangePerTick = 0, WallExchangePerTick = 0,
    };

    /// <summary><paramref name="beds"/> wood beds planned in each of <paramref name="rooms"/>.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<SleepingBedFacts?>> Beds(int beds, params int[] rooms) =>
        rooms.ToDictionary(r => r, _ => (IReadOnlyList<SleepingBedFacts?>)[.. Enumerable.Repeat(WoodBed, beds)]);
}
