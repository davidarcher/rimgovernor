namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>EmergencyNeed</c>: classifies an unrecovered priority-0 or priority-1 assessment with an available
    /// response method. Dialogs and squad hunts do not qualify.
    /// </summary>
    public static bool EmergencyNeed(RoundsAssessment a) =>
        a.Priority < 2 && a.Id != AnswerDialog && a.Finding != Finding.Met && !a.MethodUnavailable && a.Hunt.Count == 0;
}
