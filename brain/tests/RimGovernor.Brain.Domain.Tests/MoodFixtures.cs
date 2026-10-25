using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class MoodFixtures
{
    public static MoodPawn Pawn(string id) => new() { Pawn = PawnFixtures.Row(id), Forecast = null, Thoughts = [] };

    public static MoodPawn Has(string id, double mood, double target, double threshold = .35)
    {
        var pawn = PawnFixtures.Row(id);
        return new MoodPawn
        {
            Pawn = pawn with { Needs = pawn.Needs with { Mood = new RimGovernor.Brain.GameFacts.Observations.NeedLevel.Has(mood), BreakThresholdMinor = threshold } },
            Forecast = new RimGovernor.Brain.GameFacts.Observations.PatientForecast { PawnId = id, MoodTarget = target },
            Thoughts = [],
        };
    }
}
