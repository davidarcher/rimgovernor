using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>One <c>goal_review</c> row: a goal's standing at a review.</summary>
internal sealed record GoalReviewRow(int Goal, string Verdict, string Reason, string Owner);

/// <summary>
/// The acceptance reader of the <see cref="TelemetryKinds.GoalReview"/> kind, and the seed of the player's goals a goals
/// case stages before it serves (there is no HTTP goals route: the case writes the blob, the brain reads it at start).
/// </summary>
internal static class GoalReviews
{
    /// <summary>The rows of goal <paramref name="goal"/> the log ring holds, oldest first.</summary>
    public static IReadOnlyList<GoalReviewRow> Of(TypedSession s, int goal) =>
        [.. s.Telemetry(TelemetryKinds.GoalReview).Select(Row).Where(r => r.Goal == goal)];

    private static GoalReviewRow Row(LogRow row)
    {
        var fields = row.Fields();
        string Text(string key) => fields.GetValueOrDefault(key) as string ?? "";
        return new GoalReviewRow(
            int.TryParse(Text("target"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var goal) ? goal : -1,
            Text("verdict"),
            Text("reason"),
            Text("owner"));
    }

    /// <summary>An empty goals record of the loaded colony.</summary>
    public static GoalsRecord Empty(TypedSession s) =>
        new() { World = new World(new ColonyId(s.Identity.ColonyId), new LoadId(s.Identity.LoadToken), new MapId(s.Identity.MapId)) };

    /// <summary>Stages <paramref name="goals"/> as the save's <c>family/goals</c> blob (colony-scoped, so unscoped on the wire).</summary>
    public static Task SeedAsync(TypedSession s, GoalsRecord goals, CancellationToken cancellationToken) =>
        TypedGovernorState.InjectAsync(s, Store.Store.GovernorGoalsKey, goals, scoped: false, cancellationToken);
}
