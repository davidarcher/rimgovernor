using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class ConditionalMoodCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/conditional-mood",
        Scope = "Prepared native Night Owl workers across all local hours and awake/asleep states, coherent current thought subtraction, and native seeker rise/fall/capping/sleep freeze. Unit tests cannot establish installed worker or need mechanics. Does not prove selected timing or completed sleep/wake execution.",
        Start = new FixtureStart { Op = "test/pawn_control_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_control_prepare", "test/conditional_mood_mechanics"],
        QuietWorld = true,
        Crew = new Crew { Size = 3 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            var read = await s.Harness.CallAsync("conditional-mood", "test/conditional_mood_mechanics", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Conditional native mechanics failed: " + read.ToJson());
            s.Report["observations"] = read.DeepClone();
        },
    };
}
