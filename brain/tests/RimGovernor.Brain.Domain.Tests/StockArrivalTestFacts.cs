using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

internal static class StockArrivalTestFacts
{
    internal static O.BillStackRow Bench(string definition, bool usable, double workSpeed) =>
        ProductionBenchFixtures.Observed(definition + "-bench", definition, "stock-bench", usable) with { WorkSpeed = workSpeed };

    internal static O.PawnState Crafter(string id, int crafting, double speed, WorkType work = WorkType.Crafting)
    {
        var pawn = PawnFixtures.Row(id);
        return pawn with
        {
            Settings = pawn.Settings with
            {
                Schedule = EquatableArray.From(Enumerable.Range(0, 24).Select(hour => new O.TimetableSlot { Hour = (uint)hour, AssignmentDefName = hour < 8 ? PawnRules.ScheduleWork : PawnRules.ScheduleSleep })),
                Work = new O.PawnWork.Works(true, [new O.WorkSetting { DefName = Wire.Name(work), Priority = 1, Disabled = false }]),
            },
            Biography = pawn.Biography with
            {
                Skills = [new O.Skill { DefName = "Crafting", Level = crafting, StoredLevel = crafting, Passion = O.Passion.None, Disabled = false }],
                WorkSpeeds = [new O.WorkSpeedRead("GeneralLaborSpeed", speed)],
            },
        };
    }
}