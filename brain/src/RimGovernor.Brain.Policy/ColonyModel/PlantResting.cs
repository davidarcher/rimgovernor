namespace RimGovernor.Brain.Policy;

/// <summary>
/// When the game's plants rest. <c>Plant.Resting</c> is a method-body literal (no def carries it): a plant does not grow while
/// the local day fraction is under <see cref="RestEndsAt"/> or over <see cref="RestStartsAt"/> (<c>GenLocalDate.DayPercent</c> in
/// <c>Plant.GrowthPerTick</c>). Mirrored here and in kept constants because the tier-2 colony model integrates crop growth per
/// twelfth and needs the growing fraction of a day.
/// </summary>
public static class PlantResting
{
    /// <summary>The day fraction before which a plant rests.</summary>
    public const double RestEndsAt = 0.25;

    /// <summary>The day fraction after which a plant rests.</summary>
    public const double RestStartsAt = 0.8;

    /// <summary>The fraction of a day a plant rests: the part before the first bound and the part after the second.</summary>
    public const double RestingDayFraction = RestEndsAt + (1 - RestStartsAt);

    /// <summary>The fraction of a day a plant grows, between the two bounds: 22.5 grow days take about 41 calendar days.</summary>
    public const double GrowingDayFraction = 1 - RestingDayFraction;
}
