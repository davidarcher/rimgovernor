namespace RimGovernor.Brain.Policy;

public static partial class PawnRules
{
    public const string ScheduleWork = "Work";
    public const string ScheduleAnything = "Anything";

    /// <summary>
    /// How many of the 24 hourly <paramref name="slots"/> (hour 0 first) are Work or Anything; null when they are not 24 or one
    /// holds an assignment this does not classify.
    /// </summary>
    public static int? WorkHours(IReadOnlyList<string> slots)
    {
        if (slots.Count != 24)
        {
            return null;
        }

        var n = 0;
        foreach (var s in slots)
        {
            switch (s)
            {
                case ScheduleWork:
                case ScheduleAnything:
                    n++;
                    break;
                case ScheduleSleep:
                case ScheduleJoy:
                case ScheduleMeditate:
                    break;
                default:
                    return null;
            }
        }

        return n;
    }
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
