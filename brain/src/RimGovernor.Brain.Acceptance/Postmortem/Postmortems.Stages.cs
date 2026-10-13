using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// Go stages.go.
public static partial class Postmortems
{
    // --- stage graph -------------------------------------------------------

    /// <summary>
    /// Reads the run's declared stages (result.json "stages") as a graph: the bundle the run opened on (staged_from),
    /// each stage's outcome (hit, captured, uncached, failed) and the stage a -through run ended on (staged_through). A
    /// run whose failure sits past a hit stage never replayed that stage's block, which is the first thing to know
    /// before reading the colony.
    /// </summary>
    private static DigestSection StageGraph(JsonObject report)
    {
        var s = new DigestSection("stage graph");
        var rows = report["stages"].AsSlice().OfType<JsonObject>().ToList();
        if (rows.Count == 0)
        {
            s.Note = "result.json has no stages (the case declares none, or the run ended before its first)";
            return s;
        }

        if (report["staged_from"] is JsonObject from)
        {
            s.Lines.Add(new DigestLine($"opened on stage {from["stage"].AsString()} (captured {from["captured_at"].AsString()}, tick {Plain(from["tick"])})", "result.json staged_from"));
        }
        else
        {
            s.Lines.Add(new DigestLine("opened fresh: every stage block ran", "result.json staged_from"));
        }

        var chain = new List<string>();
        foreach (var row in rows)
        {
            var name = row["name"].AsString();
            var outcome = row["outcome"].AsString();
            if (outcome.Length == 0)
            {
                outcome = "not reached";
            }

            var node = name + ":" + outcome;
            var ms = WallMs(row["wall_ms"]);
            if (ms > 0 && outcome != "hit")
            {
                node += string.Create(CultureInfo.InvariantCulture, $" {ms / 1000}s");
            }

            chain.Add(node);
        }

        s.Lines.Add(new DigestLine(string.Join(" -> ", chain), "result.json stages"));
        if (report["staged_through"] is JsonObject through)
        {
            s.Lines.Add(new DigestLine($"ended after stage {through["stage"].AsString()} ({through["outcome"].AsString()}): a -through run, the rest of the chain is another run's", "result.json staged_through"));
        }

        return s;
    }

    /// <summary>Reads a wall_ms value from a live report or decoded JSON; 0 when it is not a number.</summary>
    private static long WallMs(JsonNode? value) => value is JsonValue json && json.TryGetValue<double>(out var d) ? (long)d : 0;
}
