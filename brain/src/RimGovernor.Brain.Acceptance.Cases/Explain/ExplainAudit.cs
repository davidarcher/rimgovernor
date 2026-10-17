using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What <see cref="ExplainAudit.Audit"/> found, kept in the case report.</summary>
internal sealed class ExplainResult
{
    internal int Active { get; init; }

    internal int Transitions { get; set; }

    internal int NonClear { get; set; }

    internal int Planner { get; set; }

    internal List<string> Explained { get; } = [];

    /// <summary>Active concerns with no row and no refusal or wait anywhere: a concern first seen clear writes no row by design.</summary>
    internal List<string> Silent { get; } = [];

    internal JsonObject ToJson() => new(
    [
        new KeyValuePair<string, JsonNode?>("active_concerns", Active),
        new KeyValuePair<string, JsonNode?>("transition_rows", Transitions),
        new KeyValuePair<string, JsonNode?>("non_clear_transition_rows", NonClear),
        new KeyValuePair<string, JsonNode?>("non_clear_planner_steps", Planner),
        new KeyValuePair<string, JsonNode?>("explained", new JsonArray([.. Explained.Select(x => (JsonNode?)x)])),
        new KeyValuePair<string, JsonNode?>("silent", new JsonArray([.. Silent.Select(x => (JsonNode?)x)])),
    ]);
}

/// <summary>
/// Holds a colony's rows to the explainability claim. Steps are the planner_step rows of the game's log ring and
/// transitions the session store's need_transitions; active maps every concern that had a progress record during the
/// window to the planner cause filed on it at its last non-clear poll (none when never filed).
/// <para>
/// A key is a concern id or, for a planner serving no single concern, the planner's own name (the target its transition
/// rows use). A key needs an explanation when either independent trail shows a non-clear state for it: a planner_step row
/// in the OTLP log ring with verdict refused or waiting, a closed cause, that was neither late nor an immediate review
/// (the planner ran and its verdict was filed); or, for an active concern, a planner cause on its progress record. It is
/// explained when need_transitions holds at least one row for the key with a cause: the first row of a key is always
/// non-clear, since a key first seen clear writes none. A key with neither trail is silent and passes.
/// </para>
/// <para>
/// Every transition row must also be closed typed data: a known verdict, a cause that the wording table names (not its
/// unnamed-reason fallback), a bounded printable subject and no free text. The run fails when no active concern, no planner
/// refusal or wait, or no transition row was seen at all, since nothing would have been checked.
/// </para>
/// </summary>
internal static class ExplainAudit
{
    // The verdict words a transition row with a cause carries; ClearOutcomes the two a row with no cause (the concern cleared) carries.
    private static readonly string[] CauseOutcomes = ["refused", "waiting", "disabled", "combat_orders", "hold_fallback"];
    private static readonly string[] ClearOutcomes = ["admitted", "nothing_to_do"];

    internal static ExplainResult Audit(IReadOnlyList<LogRow> steps, IReadOnlyList<NeedTransition> transitions, IReadOnlyDictionary<string, Cause> active)
    {
        var problems = new List<string>();
        var result = new ExplainResult { Active = active.Count };

        var need = new Dictionary<string, string>(StringComparer.Ordinal); // key -> where the non-clear state showed
        foreach (var r in steps)
        {
            var verdict = r.Payload.GetValueOrDefault("verdict") as string ?? "";
            var reason = r.Payload.GetValueOrDefault("reason") as string ?? "";
            var attrs = r.Payload.GetValueOrDefault("attrs") as IReadOnlyDictionary<string, object?>;
            if ((verdict != "refused" && verdict != "waiting") || !Named(new Cause(reason), out _, validOnly: true))
            {
                continue;
            }

            if (attrs?.GetValueOrDefault("late") is true)
            {
                continue;
            }

            if (attrs?.GetValueOrDefault("scope") as string == "immediate")
            {
                continue;
            }

            var key = attrs?.GetValueOrDefault("concern") as string ?? "";
            if (key.Length == 0)
            {
                key = r.Payload.GetValueOrDefault("target") as string ?? "";
            }

            if (key.Length == 0)
            {
                continue;
            }

            result.Planner++;
            _ = need.TryAdd(key, $"planner_step {verdict} {reason}");
        }

        foreach (var (concern, cause) in active)
        {
            if (cause.Value.Length == 0)
            {
                continue;
            }

            _ = need.TryAdd(concern, "progress record planner cause " + cause.Value);
        }

        var explained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in transitions)
        {
            result.Transitions++;
            var problem = CheckRow(r, out var target, out var cause);
            if (problem is not null)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"transition row seq {r.Sequence}: {problem}"));
                continue;
            }

            if (cause.Length != 0)
            {
                result.NonClear++;
                _ = explained.Add(target);
            }
        }

        var missing = new List<string>();
        foreach (var (key, where) in need)
        {
            if (explained.Contains(key))
            {
                result.Explained.Add(key);
            }
            else
            {
                missing.Add(key + " (" + where + ")");
            }
        }

        foreach (var (concern, cause) in active)
        {
            if (!need.ContainsKey(concern) && cause.Value.Length == 0)
            {
                result.Silent.Add(concern);
            }
        }

        result.Explained.Sort(StringComparer.Ordinal);
        result.Silent.Sort(StringComparer.Ordinal);
        missing.Sort(StringComparer.Ordinal);

        if (missing.Count > 0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{missing.Count} key(s) refused or waited with no need_transitions row naming a cause (a planner stopped emitting): [{string.Join(' ', missing)}]"));
        }

        if (active.Count == 0)
        {
            problems.Add("no active concern: the review held no progress record, nothing was checked");
        }
        else if (result.Planner == 0)
        {
            problems.Add("no planner refused or waited in the window: nothing was checked");
        }
        else if (result.NonClear == 0)
        {
            problems.Add("need_transitions holds no row with a cause");
        }

        return problems.Count == 0
            ? result
            : throw new ExplainAuditException(result, string.Join('\n', problems));
    }

    /// <summary>Holds one transition row to the closed shape; returns the problem, null when the row is closed typed data.</summary>
    private static string? CheckRow(NeedTransition r, out string target, out string cause)
    {
        target = r.Need;
        cause = r.Cause;
        var verdict = r.Verdict;
        if (target.Length == 0)
        {
            return "no target";
        }

        if (r.Cause.Length == 0)
        {
            if (!ClearOutcomes.Contains(verdict))
            {
                return $"{target}: clear row with verdict \"{verdict}\"";
            }
        }
        else
        {
            if (!CauseOutcomes.Contains(verdict))
            {
                return $"{target}: verdict \"{verdict}\" with cause \"{r.Cause}\"";
            }

            if (!Named(new Cause(r.Cause), out var why))
            {
                return $"{target}: {why}";
            }
        }

        if (r.Previous.Length != 0 && !Named(new Cause(r.Previous), out var previous))
        {
            return $"{target} previous cause: {previous}";
        }

        if (r.Subject != CauseRules.BoundSubject(r.Subject))
        {
            return $"{target}: subject \"{r.Subject}\" is not a bounded printable subject";
        }

        return !IdLike(r.Method) ? $"{target}: method \"{r.Method}\" is not an identifier" : null;
    }

    /// <summary>
    /// Whether the cause is in the closed set (<paramref name="validOnly"/>) and, otherwise, also named by the wording table
    /// rather than served the generic fallback sentence.
    /// </summary>
    private static bool Named(Cause c, out string why, bool validOnly = false)
    {
        why = "";
        if (!Cause.All.Contains(c))
        {
            why = $"unknown cause \"{c.Value}\"";
            return false;
        }

        if (validOnly)
        {
            return true;
        }

        if (CauseRules.Wording(c, "") == CauseRules.Wording(Cause.None, ""))
        {
            why = $"cause \"{c.Value}\" has no plain-language wording";
            return false;
        }

        return true;
    }

    /// <summary>An identifier-shaped method: printable ASCII with no space, so a sentence cannot ride in the field.</summary>
    private static bool IdLike(string s) => s.Length <= 80 && s.All(c => c > ' ' && c <= '~');
}

/// <summary>The audit failed; carries the result so the case report keeps what was found.</summary>
internal sealed class ExplainAuditException(ExplainResult result, string message) : AcceptanceException(message)
{
    internal ExplainResult Result { get; } = result;
}
