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
    private const string MedicalTool = "test/medical_learning";

    // Cooking, Melee, Shooting, Intellectual and Plants x three passions x saturated or not.
    private const int Samples = 30;

    private static readonly FixtureOp<NoArgs, Fx.SkillLearningProbeRead> Probe = ServedOps.Read<Fx.SkillLearningProbeRead>(ProbeTool);

    internal static readonly FixtureOp<string, Fx.SkillLearningVerifyRead> Verify =
        new(VerifyTool, action => ServedOps.Args(("action", action)), ServedOps.Reply<Fx.SkillLearningVerifyRead>(VerifyTool));

    private static readonly string[] MedicalKinds = ["tend", "surgery"];

    private static readonly FixtureOp<string, Fx.MedicalLearningRead> Medical =
        new(MedicalTool, action => ServedOps.Args(("action", action)), ServedOps.Reply<Fx.MedicalLearningRead>(MedicalTool));

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

    /// <summary>
    /// <c>lab/skill-learning-tend</c> and <c>lab/skill-learning-surgery</c> (#3632): a doctor's completed tend and peg-leg
    /// surgery pay Medicine XP through the native callbacks exactly as the brain's tend and surgery learning project it.
    /// </summary>
    internal static IEnumerable<TypedCase> MedicalCases() => MedicalKinds.Select(kind => new TypedCase
    {
        Name = "lab/skill-learning-" + kind,
        Scope = $"A doctor's completed {kind} (#3632) pays Medicine XP through the native callback "
            + (kind == "tend" ? "(Toils_Tend.FinalizeTend) " : "(the recipe callback, before the surgery outcome) ")
            + "and the verifier finds it equal to the brain's projected learning. A unit test cannot run the game's job or SkillRecord.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Stage = new Fx.StageSpec { Paused = true },
        Ops = [VerifyTool, MedicalTool],
        QuietWorld = true,
        Run = (s, ct) => RunMedicalAsync(s, kind, ct),
    });

    private static async Task RunMedicalAsync(TypedSession s, string kind, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("learning-start", Verify, "start", cancellationToken).ConfigureAwait(false);
        var staged = await s.FixtureAsync("medical-" + kind, Medical, kind, cancellationToken).ConfigureAwait(false);
        s.Record("medical", ServedOps.Json(staged));
        _ = await s.RunUntilAsync("medical-" + kind, s.TicksLeft, async ct =>
        {
            var at = await s.FixtureAsync("medical-poll", Medical, "poll", ct).ConfigureAwait(false);
            var got = await s.FixtureAsync("learning-read", Verify, "read", ct).ConfigureAwait(false);
            var seen = kind == "tend" ? got.Tend : got.Surgery;
            return (Wait.Signature(at.InBed, at.Started, seen, got.Failures.Count), seen >= 1 || got.Failures.Count != 0);
        }, cancellationToken).ConfigureAwait(false);
        await VerifyCallbacksAsync(s, kind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the verifier and requires at least one checked <paramref name="kind"/> callback (recipe, sparring, surgery or tend) and no failure.</summary>
    internal static async Task VerifyCallbacksAsync(TypedSession s, string kind, CancellationToken ct)
    {
        var result = await s.FixtureAsync("learning-stop", Verify, "stop", ct).ConfigureAwait(false);
        s.Record("learning-callbacks", ServedOps.Json(result));
        var seen = kind switch
        {
            "recipe" => result.Recipe,
            "surgery" => result.Surgery,
            "tend" => result.Tend,
            _ => result.Sparring,
        };
        if (seen < 1 || result.Failures.Count != 0)
        {
            throw new AcceptanceException($"native {kind} learning callbacks: {seen} checked, failures: {string.Join(", ", result.Failures)}");
        }
    }
}
