using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The healthy-clock fixture precondition (Go <c>clock.go</c>).</summary>
public static class ClockFixture
{
    private static readonly string[] HealthFields = ["dead", "downed"];

    /// <summary>
    /// Asserts a fresh observations_list_pawns reply (filter colonist=true) is a complete, fully-matched,
    /// fully-readable census with none dead, downed, bleeding, or needing tend: a missing health fact is never
    /// treated as healthy, only an explicit false is. Pawns the colonist filter dropped (animals, prisoners) are
    /// expected, not a defect.
    /// </summary>
    public static void RequireHealthyColonists(JsonObject reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var observed = Harness.Outcome(reply, "observed").Value;
        var pawns = observed["pawns"].AsSlice();
        if (pawns.Count == 0)
        {
            throw new AcceptanceException("no colonists returned");
        }

        foreach (var raw in pawns)
        {
            var pawn = raw.AsMap();
            var health = pawn?["health"].AsMap();
            var blocked = new List<string>();
            foreach (var name in HealthFields)
            {
                if (!ExplicitFalse(pawn?[name]))
                {
                    blocked.Add(name);
                }
            }

            if (!ExplicitFalse(health?["bleeding"]))
            {
                blocked.Add("bleeding");
            }

            if (!ExplicitFalse(health?["needsTend"]))
            {
                blocked.Add("needsTend");
            }

            if (blocked.Count > 0)
            {
                var id = pawn?["pawn"].AsMap()?["id"].AsString() ?? "";
                throw new AcceptanceException($"healthy-clock fixture prerequisite failed for {id}: [{string.Join(' ', blocked)}]");
            }
        }
    }

    private static bool ExplicitFalse(JsonNode? value) => value.AsBool() is false;
}
