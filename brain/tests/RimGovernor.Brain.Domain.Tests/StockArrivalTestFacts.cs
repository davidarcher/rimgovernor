using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class StockArrivalTestFacts
{
    internal static RimGovernor.Brain.GameFacts.Observations.BillStackRow Bench(string definition, bool usable, double workSpeed) =>
        ProductionBenchFixtures.Observed(definition + "-bench", definition, "stock-bench", usable) with { WorkSpeed = workSpeed };

    internal static RimGovernor.Brain.GameFacts.Observations.PawnState Crafter(string id, int crafting, double speed, WorkType work = WorkType.Crafting)
    {
        var pawn = PawnFixtures.Row(id);
        return pawn with
        {
            Settings = pawn.Settings with
            {
                Schedule = EquatableArray.From(Enumerable.Range(0, 24).Select(hour => new RimGovernor.Brain.GameFacts.Observations.TimetableSlot { Hour = (uint)hour, AssignmentDefName = hour < 8 ? PawnRules.ScheduleWork : PawnRules.ScheduleSleep })),
                Work = new RimGovernor.Brain.GameFacts.Observations.PawnWork.Works(true, [new RimGovernor.Brain.GameFacts.Observations.WorkSetting { DefName = Wire.Name(work), Priority = 1, Disabled = false }]),
            },
            Biography = pawn.Biography with
            {
                Skills = [new RimGovernor.Brain.GameFacts.Observations.Skill { DefName = "Crafting", Level = crafting, StoredLevel = crafting, Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, Disabled = false }],
                WorkSpeeds = [new RimGovernor.Brain.GameFacts.Observations.WorkSpeedRead("GeneralLaborSpeed", speed)],
            },
        };
    }
}