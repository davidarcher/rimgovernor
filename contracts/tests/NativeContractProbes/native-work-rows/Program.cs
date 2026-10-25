using System;
using System.Linq;
using RimGovernor.Native;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

// A work settings write skips a row the pawn can no longer do instead of refusing the request (#3678): a same-step
// ideology role (ProductionSpecialist: ManualDumb, Cooking, PlantWork, Mining...) disabled types the review's request
// still asked for, and native refused the pawn's whole work request.
internal static class NativeWorkRowsProbe
{
    internal static void Invoke()
    {
        Func<string, bool> disabled = name => name is "Cooking" or "Hauling";
        RimGovernor.Brain.GameOrders.Operations.WorkPriority Row(string def, int priority) => new() { WorkTypeDef = def, Priority = priority };

        Require(DisabledWorkRows.Skipped(Row("Cooking", 2), disabled), "a nonzero priority on a disabled type is skipped");
        Require(!DisabledWorkRows.Skipped(Row("Cooking", 0), disabled), "a zero on a disabled type is still written");
        Require(!DisabledWorkRows.Skipped(Row("Warden", 1), disabled), "an enabled type is written");

        var request = new[] { Row("Warden", 1), Row("Cooking", 2), Row("Construction", 3), Row("Hauling", 4), Row("Mining", 0) };
        var skipped = DisabledWorkRows.SkippedNames(request, disabled);
        Require(skipped.SequenceEqual(new[] { "Cooking", "Hauling" }), "the skipped rows are named in request order");
        Require(DisabledWorkRows.SkippedNames(request, _ => false).Count == 0, "nothing is skipped for a pawn that can do every type");

        var effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect { SkippedWork = [.. skipped] };
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Receipts.SettingsEffect>(OrdersJson.Serialize(effect));
        Require(back.SkippedWork.SequenceEqual(new[] { "Cooking", "Hauling" }), "the skipped rows survive the wire");
        Require(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Receipts.SettingsEffect>("{}").SkippedWork.Count == 0, "a receipt without skips names none");
        Console.WriteLine("native-work-rows: disabled-row skipping and the receipt field passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
