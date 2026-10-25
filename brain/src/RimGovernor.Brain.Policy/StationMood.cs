namespace RimGovernor.Brain.Policy;

/// <summary>The mean expected mood of colonists who have a mood need, or no mood-bearing colonists.</summary>
public abstract record StationMood
{
    private StationMood() { }
    public sealed record NoMood : StationMood;
    public sealed record Expected(double Level) : StationMood;

    public static StationMood Of(EquatableArray<MoodPawn> pawns)
    {
        var targets = pawns.Where(p => !p.Dead).Select(p => p.Mood).OfType<MoodLevel.Has>().Select(m => m.Target).ToArray();
        if (targets.Length == 0) return new NoMood();
        if (targets.Any(t => !double.IsFinite(t) || t < 0 || t > 1)) throw new DomainException("invalid expected pawn mood");
        return new Expected(targets.Average());
    }
}