using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class LabSkillLearning
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/skill-learning", Start = new LabStart { Colonists = 3 }, QuietWorld = true,
        Budget = AcceptanceCase.LabBudget, Crew = new Crew { Size = 3 },
        RequiredOps = ["test/skill_learning_probe"],
        Scope = "Prepared native read/projection differential: ordinary SkillRecord learning, direct range learning and decay "
            + "across passions and saturation. Detached records leave pawn skills unchanged. This proves native mechanics access, "
            + "not recipe completion or sparring cadence; existing cooking/range/sparring cases own physical work and safety.",
        Run = async (s, ct) =>
        {
            var result = await s.Harness.CallAsync("learning-probe", "test/skill_learning_probe", new JsonObject(), ct).ConfigureAwait(false);
            s.Report["learning"] = result;
            if (result["success"].AsBool() != true || result["samples"].AsNumber() != 18)
                throw new AcceptanceException($"native learning differential: {result.ToJson()}");
        },
    };
    internal static async Task VerifyCallbacksAsync(ISession s, string kind, CancellationToken ct)
    {
        var result = await s.Harness.CallAsync("learning-stop", "test/skill_learning_verify", new() { ["action"] = "stop" }, ct).ConfigureAwait(false);
        s.Report["learning-callbacks"] = result;
        if (result[kind].AsNumber() < 1 || result["failures"].AsSlice().Count != 0)
            throw new AcceptanceException($"Native {kind} learning callbacks: {result.ToJson()}");
    }
}
