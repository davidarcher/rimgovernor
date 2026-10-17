namespace RimGovernor.Brain.Policy;

public static partial class PawnRules
{
    public const string ScheduleWork = "Work";
    public const string ScheduleAnything = "Anything";
    public const string ScheduleJoy = "Joy";
    public const string ScheduleSleep = "Sleep";
    public const string ScheduleMeditate = "Meditate";

    /// <summary>Fixed occupancy assumption for the independent layout comfort estimate only.
    /// This is neither a pawn schedule nor a selected needs timeline.</summary>
    internal static string[] EnvironmentOccupancyBaseline()
    {
        var slots = new string[24];
        Array.Fill(slots, PawnRules.ScheduleAnything);
        for (var hour = 0; hour < slots.Length; hour++)
            if (hour >= 22 || hour <= 5) slots[hour] = PawnRules.ScheduleSleep;
        slots[21] = PawnRules.ScheduleJoy;
        return slots;
    }
}
