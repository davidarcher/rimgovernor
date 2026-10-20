using RimGovernor.Brain.Domain;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/practice-surgery-binding</c> (#3637): the practice-surgery step the coupled selector binds (PracticeSurgeryTests
/// prove the selection) applied as the selected order. test/medical_learning "practice" stages a held prisoner missing a leg in
/// a prison bed with a peg leg and herbal medicine beside it and no bill; the controlled doctor queues the bill restricted to
/// itself and completes the native surgery, whose Medicine callback the learning verifier checks.
/// </summary>
internal static class MedicalPracticeSurgery
{
    private const string TaskId = "practice/surgery/case";
    private const string Recipe = "InstallPegLeg";
    private const string Medicine = "MedicineHerbal";

    // A hang guard: the walk, the two fetches and the peg-leg install take a few thousand ticks.
    private const int Budget = 20000;

    internal static TypedCase Case() => new()
    {
        Name = "medical/practice-surgery-binding",
        Scope = "An applied practice-surgery step (#3637: InstallPegLeg on a held prisoner's missing leg with herbal medicine) "
            + "queues the native bill restricted to the step's doctor and no other medical bill, and the doctor's native DoBill "
            + "completes it with a Medicine XP callback equal to the projection. A unit test cannot run the game's bill, job or SkillRecord.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = new RimGovernor.Fixtures.Contracts.StageSpec { Paused = true },
        Ops = [LabSkillLearning.VerifyTool, LabSkillLearning.MedicalTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("learning-start", LabSkillLearning.Verify, "start", cancellationToken).ConfigureAwait(false);
        var staged = await s.FixtureAsync("practice-stage", LabSkillLearning.Medical, "practice", cancellationToken).ConfigureAwait(false);
        s.Record("staged", ServedOps.Json(staged));
        if (staged.Doctor.Length == 0 || staged.Patient.Length == 0 || staged.Part < 0 || staged.WorkGiver.Length == 0)
        {
            throw new AcceptanceException($"practice-stage: {ServedOps.Json(staged)}");
        }

        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        var step = new PawnActivityStep(PawnActivityKind.Work, staged.Patient, staged.WorkGiver, "DoBill", 0, int.MaxValue)
        {
            Practice = new PawnActivityPractice("Medicine", "", 0, "", "", Budget) { Surgery = new PawnActivitySurgery(Recipe, staged.Part, Medicine) },
        };
        var batch = new PawnActivities(generation, [new PawnActivityOrder(staged.Doctor, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, staged.Doctor, [], step, new Cell(0, 0), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        var at = staged;
        _ = await s.RunUntilAsync("practice-surgery", Budget, async ct =>
        {
            at = await s.FixtureAsync("practice-poll", LabSkillLearning.Medical, "poll", ct).ConfigureAwait(false);
            var got = await s.FixtureAsync("learning-read", LabSkillLearning.Verify, "read", ct).ConfigureAwait(false);
            return (Wait.Signature(at.InBed, at.Restricted, at.Other, at.Started, got.Surgery, got.Failures.Count),
                at.Other || got.Surgery >= 1 || got.Failures.Count != 0);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("poll", ServedOps.Json(at));
        if (!at.Restricted || at.Other || !at.Started)
        {
            throw new AcceptanceException($"want only a bill restricted to {staged.Doctor} and its DoBill: {ServedOps.Json(at)}");
        }

        await LabSkillLearning.VerifyCallbacksAsync(s, "surgery", cancellationToken).ConfigureAwait(false);
    }
}
