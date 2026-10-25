using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameOrders.Operations;

internal static class WorkSettingsProof
{
    internal static void Run(Assembly bridge, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        // The game-dependent handler is loaded from the privately built native assembly.
        var handler = bridge.GetType("RimGovernor.Native.WorkSettingsActionHandler", true)!;
        var validate = handler.GetMethod("Valid", flags)!;
        bool Valid(WorkSettingsIntent? request) => (bool)validate.Invoke(null, new object?[] { request })!;
        WorkPriority Row(string name, int priority) => new() { WorkTypeDef = name, Priority = priority };
        var request = new WorkSettingsIntent { PawnId = "Pawn1", Work = [Row("Cooking", 1)] };
        check(Valid(request), "exact pawn work priorities accepted");
        check(Valid(request with { Work = [], AllowedArea = new Assignment { Value = new AssignmentValue.ClearCase() } }), "area clear is an explicit settings write");
        check(Valid(request with { Work = [], Schedule = new Schedule { AssignmentDefs = [.. Enumerable.Repeat("Anything", 24)] } }), "complete timetable accepted");
        check(!Valid(null), "absent work request refused");
        check(!Valid(request with { PawnId = null }), "missing pawn refused");
        check(!Valid(request with { PawnId = "" }), "empty pawn refused");
        check(!Valid(request with { Work = [] }), "empty settings write refused");
        foreach (var priority in new[] { -1, 5 })
            check(!Valid(request with { Work = [Row("Cooking", priority)] }), "out of range work priority refused");
        check(!Valid(request with { Work = [Row("Cooking", 1), Row("Cooking", 2)] }), "duplicate work type refused");
        check(!Valid(request with { Work = [Row("", 1)] }), "empty work type refused");
        check(!Valid(request with { Schedule = new Schedule { AssignmentDefs = ["Anything"] } }), "incomplete timetable refused");
        check(!Valid(request with { AllowedArea = new Assignment() }), "area write requires an explicit target or clear");
        foreach (var field in new[] { "medicalCare", "selfTend", "followDrafted", "followFieldwork", "slaughter", "releaseToWild", "master" })
        {
            try
            {
                OrdersJson.Deserialize<WorkSettingsIntent>("{\"pawnId\":\"Pawn1\",\"" + field + "\":false}");
                throw new InvalidOperationException("Unrelated setting accepted: " + field);
            }
            catch (JsonException) { check(true, "work settings cannot widen to " + field); }
        }
        // Settings no longer publish a work-specific CAS token. Disabled-row writes and the
        // receipt field are covered by native-work-rows.
    }
}
