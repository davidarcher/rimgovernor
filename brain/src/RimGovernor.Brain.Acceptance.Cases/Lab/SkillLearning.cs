using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/skill-learning</c> and the learning-callback verifier the sparring and cooking cases share. test/skill_learning_probe
/// is an equality check: it runs the brain's learning projection and native SkillRecord learning on detached records and
/// replies the samples that disagree. test/skill_learning_verify is a recorder over the native XP callbacks.
/// </summary>
internal static class LabSkillLearning
{
    private const string ProbeTool = "test/skill_learning_probe";
    internal const string VerifyTool = "test/skill_learning_verify";

    // Cooking, Melee and Shooting x three passions x saturated or not.
    private const int Samples = 18;

    private static readonly FixtureOp<NoArgs, Fx.SkillLearningProbeRead> Probe = ServedOps.Read<Fx.SkillLearningProbeRead>(ProbeTool);

    internal static readonly FixtureOp<string, Fx.SkillLearningVerifyRead> Verify =
        new(VerifyTool, action => ServedOps.Args(("action", action)), ServedOps.Reply<Fx.SkillLearningVerifyRead>(VerifyTool));

    internal static TypedCase Case() => new()
    {
        Name = "lab/skill-learning",
        Scope = "Native read/projection differential: the brain's learning projection agrees with native SkillRecord learning "
            + "(ordinary learning, direct range learning and decay) across passions and daily saturation, and the native read "
            + "leaves the pawn's skills unchanged. A unit test cannot run the game's SkillRecord; the cooking, range and "
            + "sparring cases own the physical work.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Ops = [ProbeTool],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            var result = await s.FixtureAsync("learning-probe", Probe, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("learning", ServedOps.Json(result));
            if (result.Samples != Samples || result.Failures.Count != 0)
            {
                throw new AcceptanceException($"native learning differential: {result.Samples} samples (want {Samples}), disagreeing: {string.Join(", ", result.Failures)}");
            }
        },
    };

    /// <summary>Stops the verifier and requires at least one checked <paramref name="kind"/> callback (recipe or sparring) and no failure.</summary>
    internal static async Task VerifyCallbacksAsync(ITypedSession s, string kind, CancellationToken ct)
    {
        var result = await s.FixtureAsync("learning-stop", Verify, "stop", ct).ConfigureAwait(false);
        s.Record("learning-callbacks", ServedOps.Json(result));
        var seen = kind == "recipe" ? result.Recipe : result.Sparring;
        if (seen < 1 || result.Failures.Count != 0)
        {
            throw new AcceptanceException($"native {kind} learning callbacks: {seen} checked, failures: {string.Join(", ", result.Failures)}");
        }
    }
}
