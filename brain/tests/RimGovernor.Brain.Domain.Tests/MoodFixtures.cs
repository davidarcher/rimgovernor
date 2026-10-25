using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

internal static class MoodFixtures
{
    public static MoodPawn Pawn(string id) => new() { Pawn = PawnFixtures.Row(id), Forecast = null, Thoughts = [] };

    public static MoodPawn Has(string id, double mood, double target, double threshold = .35)
    {
        var pawn = PawnFixtures.Row(id);
        return new MoodPawn
        {
            Pawn = pawn with { Needs = pawn.Needs with { Mood = new O.NeedLevel.Has(mood), BreakThresholdMinor = threshold } },
            Forecast = new O.PatientForecast { PawnId = id, MoodTarget = target },
            Thoughts = [],
        };
    }
}
