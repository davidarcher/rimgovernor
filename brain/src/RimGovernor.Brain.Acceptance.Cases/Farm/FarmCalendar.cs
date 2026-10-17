using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/calendar</c>: the colony facts' growing calendar is in range and its two seasonal-temperature walks agree.
/// The harvest gap the planner derives from it, and its continuity at the frost, are <c>FoodChannelPolicyTests</c>.
/// </summary>
internal static class FarmCalendar
{
    internal static TypedCase Case() => new()
    {
        Name = "farm/calendar",
        Scope = "observations_read_colony_facts' foodClimate (season, day of year, growing period, days until the crop range is "
            + "left and re-entered, the non-growing stretch) is present, inside one 60-day year, and self-consistent: exactly one "
            + "walk is zero unless the tile never leaves or never enters the range, and the non-growing stretch is the wait while "
            + "crops do not grow. Read-only; the harvest gap derived from it is FoodChannelPolicyTests.",
        Ticks = 0,
        Tier = CaseTier.Smoke,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync("colony-facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var facts = TypedReads.Observed("colony-facts", NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
        if (facts.Issues.FirstOrDefault(i => i.Field == "food_climate") is { } issue)
        {
            throw new AcceptanceException($"food_climate unavailable: {issue}");
        }

        var climate = facts.FoodClimate ?? throw new AcceptanceException("colony facts carry no foodClimate");
        s.RecordWire("food_climate", climate);
        if (climate is not { Season: { } season, DayOfYear: { } day, GrowingDays: { } growing, GrowingDaysRemaining: { } remaining, GrowingDaysUntil: { } until, NonGrowingDays: { } stretch })
        {
            throw new AcceptanceException($"foodClimate lacks a field: {climate}");
        }

        var calendar = new RimGovernor.Brain.Policy.Calendar { Season = season, DayOfYear = day, GrowingDays = growing, GrowingDaysRemaining = remaining, GrowingDaysUntil = until, NonGrowingDays = stretch };
        if (!calendar.Valid())
        {
            throw new AcceptanceException($"calendar out of range: {calendar}");
        }

        // Both walks sample the same daily seasonal temperature.
        var year = CalendarRules.YearDays;
        var walksAgree = (remaining == year && until == 0) || (remaining == 0 && until == year) || (remaining > 0 && until == 0) || (remaining == 0 && until > 0);
        if (!walksAgree)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"growing walks disagree: remaining={remaining} until={until}"));
        }

        var stretchAgrees = until > 0 ? stretch == until : remaining < year ? stretch > 0 : stretch == 0;
        if (!stretchAgrees)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"non-growing stretch {stretch} disagrees with remaining={remaining} until={until}"));
        }
    }
}
