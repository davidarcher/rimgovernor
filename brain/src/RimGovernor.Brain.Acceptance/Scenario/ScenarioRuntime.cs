using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The minimal advance_game(rt, ...) surface an acceptance binary needs (Go <c>ScenarioRuntime</c>). It has no
/// review-task/decision-loop concurrency: every acceptance binary drives its scenario from one task.
/// </summary>
public sealed class ScenarioRuntime : IScenarioRuntime
{
    /// <summary>The generic native/MCP tool call AdvanceGame needs outside the rimgovernor/* surface <see cref="ScenarioClock.Wire"/> covers (letter dismissal), ordinarily <see cref="Harness.CallAsync"/>.</summary>
    public delegate Task<JsonObject> QueryFunc(string label, string tool, JsonObject arguments, CancellationToken cancellationToken);

    /// <summary>Removes one letter from the stack (scripts/fixtures/LetterFixture.cs); every fixture build carries it.</summary>
    public const string DismissLetterTool = "test/dismiss_letter";

    /// <summary>
    /// The letter defs AdvanceGame acknowledges by default: informational events and the choice letters whose
    /// unanswered outcome is harmless to an assertion (a joiner leaves, a quest stays offered). Threat letters are
    /// never here: they still need an expected-letters option, and the window's own safety checks (no hostiles, no
    /// downed or bleeding colonist) run for every letter regardless.
    /// </summary>
    public static readonly IReadOnlySet<string> AcknowledgedLetterDefs = new HashSet<string>(StringComparer.Ordinal)
    {
        "NeutralEvent",
        "PositiveEvent",
        "NegativeEvent",
        "NewQuest",
        "AcceptJoiner",
        "AcceptVisitors",
        "AcceptCreepJoiner",
        "RitualOutcomePositive",
        "RitualOutcomeNegative",
        "BabyBirth",
        "BabyToChild",
        "ChildToAdult",
        "ChildBirthday",
    };

    private readonly List<JsonNode?> _clockEvents = [];

    public ScenarioRuntime(QueryFunc query, ScenarioClock clock, Report report, IReadOnlyList<string>? tools)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(report);
        Query = query;
        Clock = clock;
        Report = report;
        Tools = tools ?? [];
    }

    public QueryFunc Query { get; }

    public ScenarioClock Clock { get; }

    public Report Report { get; }

    /// <summary>
    /// Mirrors rt.current_plan.control["combat"]["targets"]: the committed defense targets AdvanceGame's
    /// combat-target option must match. Empty outside combat scenarios.
    /// </summary>
    public IReadOnlyList<string> CombatTargets { get; set; } = [];

    /// <summary>The discovered tool names; when they include <see cref="DismissLetterTool"/> AdvanceGame takes each acknowledged letter off the stack. Empty leaves letters on the stack.</summary>
    public IReadOnlyList<string> Tools { get; }

    internal void AddClockEvents(IEnumerable<JsonNode?> events) => _clockEvents.AddRange(events);

    internal void ReceiveClockEvents()
    {
        var delivered = ReportArray("delivered_events");
        foreach (var row in _clockEvents)
        {
            delivered.Add(row?.DeepClone());
        }

        _clockEvents.Clear();
    }

    internal void Note(string kind, string message, JsonObject details)
    {
        var entry = new JsonObject { ["kind"] = kind, ["message"] = message };
        foreach (var (key, value) in details)
        {
            entry[key] = value?.DeepClone();
        }

        ReportArray("notes").Add(entry);
    }

    /// <summary>The report's array field <paramref name="key"/>, created when absent.</summary>
    internal JsonArray ReportArray(string key)
    {
        if (Report.Data[key] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        Report.Data[key] = created;
        return created;
    }

    internal async Task<JsonObject> IdentityNowAsync(CancellationToken cancellationToken)
    {
        var reply = await Clock.Wire("scenario-identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        JsonObject loaded;
        try
        {
            (_, loaded) = Harness.Outcome(reply, "loaded");
        }
        catch (AcceptanceException)
        {
            throw new ScenarioInterruptedException("Native identity unavailable");
        }

        var identity = loaded["context"].AsMap()?["identity"].AsMap();
        if (identity is null || identity["colonyId"].AsString().Length == 0 || identity["loadToken"].AsString().Length == 0)
        {
            throw new ScenarioInterruptedException("Native identity unavailable");
        }

        return (JsonObject)identity.DeepClone();
    }

    /// <summary>
    /// observations_read_status with colonists and threats on the clock's identity, its colonist references joined to
    /// their pawn rows, with health, under colonistRows; Complete is false when a read is not observed, carries an
    /// issue or misses a colonist's row (Go <c>readSafetyStatus</c>).
    /// </summary>
    internal static async Task<(JsonObject? Observed, bool Complete)> ReadSafetyStatusAsync(ScenarioClock clock, string label, CancellationToken cancellationToken)
    {
        var reply = await clock.Wire(
            label,
            "observations_read_status",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = clock.Identity.DeepClone() },
                ["colonists"] = true,
                ["threats"] = true,
            },
            cancellationToken).ConfigureAwait(false);
        JsonObject observed;
        try
        {
            (_, observed) = Harness.Outcome(reply, "observed");
        }
        catch (AcceptanceException)
        {
            return (null, false);
        }

        if (observed["issues"].AsSlice().Count > 0)
        {
            return (observed, false);
        }

        var ids = new JsonArray();
        foreach (var raw in observed["colonists"].AsSlice())
        {
            ids.Add(raw.AsMap()?["id"].AsString() ?? "");
        }

        if (ids.Count == 0)
        {
            return (observed, true);
        }

        reply = await clock.Wire(
            label + "-pawns",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = clock.Identity.DeepClone() },
                ["filter"] = new JsonObject { ["ids"] = ids },
                ["details"] = new JsonObject
                {
                    ["needs"] = false,
                    ["health"] = true,
                    ["equipment"] = false,
                    ["biography"] = false,
                    ["settings"] = false,
                    ["social"] = false,
                    ["animals"] = false,
                },
            },
            cancellationToken).ConfigureAwait(false);
        var pawnsOk = true;
        JsonObject? pawns = null;
        try
        {
            (_, pawns) = Harness.Outcome(reply, "observed");
        }
        catch (AcceptanceException)
        {
            pawnsOk = false;
        }

        var rows = new JsonArray();
        foreach (var row in pawns?["pawns"].AsSlice() ?? [])
        {
            rows.Add(row?.DeepClone());
        }

        observed["colonistRows"] = rows;
        return (observed, pawnsOk && rows.Count == ids.Count);
    }

    /// <summary>
    /// Whether status's threat fact rows classify a hostile or a hunting predator (Go <c>activeThreat</c> over
    /// <c>bridge.ClassifyThreat</c>); unreadable rows count as a threat. The classification rule is the Host's
    /// ClassifyThreat, restated over the JSON rows because that one is internal and typed.
    /// </summary>
    internal static bool ActiveThreat(JsonObject status)
    {
        if (status["threats"] is not JsonObject threats)
        {
            return true;
        }

        var pawns = threats["pawns"];
        if (pawns is not null and not JsonArray)
        {
            return true;
        }

        foreach (var raw in pawns.AsSlice())
        {
            if (raw is not JsonObject row)
            {
                return true;
            }

            if (row["mentalState"].AsString().Contains("manhunter", StringComparison.OrdinalIgnoreCase)
                || row["factionHostile"].AsBool() == true
                || row["prisonBreak"].AsBool() == true)
            {
                return true;
            }

            if (row["predatorHunt"].AsBool() == true)
            {
                var ignored = row["ours"].AsBool() == true || (row["prey"] is not null && row["preyIsOurs"].AsBool() != true);
                if (!ignored)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies one letter for an advance window: expected letters (label and def listed) are inspected fixture
    /// warnings and stay on the stack; acknowledged defs are informational and are dismissed; anything else
    /// interrupts. Strict (an expected-letters option) disables acknowledgement.
    /// </summary>
    internal static (bool Approved, bool Acknowledged) LetterApproval(string label, string def, IReadOnlyList<(string Label, string LetterDef)> expected, bool strict)
    {
        foreach (var (expectedLabel, expectedDef) in expected)
        {
            if (expectedLabel == label && expectedDef == def)
            {
                return (true, false);
            }
        }

        return !strict && AcknowledgedLetterDefs.Contains(def) ? (true, true) : (false, false);
    }
}
