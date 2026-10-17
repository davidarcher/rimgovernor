using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The process a case got.</summary>
public static class Isolation
{
    /// <summary>The process ends with the case: nothing after it inherits native static state it touched.</summary>
    public const string Fresh = "own-process";

    /// <summary>
    /// The case ran on whatever process the previous case kept, and leaves it for the next. A map reload is not a
    /// process reset, so an assertion about initialization needs <see cref="Fresh"/>.
    /// </summary>
    public const string Shared = "shared-process";
}

/// <summary>The "what does this result prove?" block.</summary>
public static class Provenance
{
    /// <summary>The report block <see cref="Stamp"/> fills.</summary>
    public const string ProvenanceKey = "provenance";

    /// <summary>
    /// Stamps on <paramref name="report"/> the derived answer to "what does this result prove?": what the installed
    /// build had to provide for the case to start, and whether the case got its own process. Every field is derived
    /// from <paramref name="c"/> and from what the run already recorded, so the block adds no second catalog beside the
    /// registry: the claim is the case's Scope (report["scope"]) and the declared precondition is report["start"].
    /// </summary>
    public static JsonObject Stamp(AcceptanceCase c, Report report)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(report);
        var block = new JsonObject { { "isolation", IsolationOf(c) } };
        var reason = IsolationReason(c);
        if (reason.Length > 0)
        {
            block.Add("isolation_reason", reason);
        }

        var ops = c.FixtureOps();
        if (ops.Count > 0)
        {
            // The native dependency: without these ops registered by the installed package the case cannot reach its
            // precondition at all.
            block.Add("native_ops", OutputJson.Array(ops));
        }

        if (c.Serve?.Families is { } families)
        {
            // The routine families the controller was restricted to: everything outside this list was disabled for the
            // run, so the result says nothing about it.
            block.Add("routine_families", OutputJson.Array(families));
        }

        // A fresh world is not a fresh process: the runner leaves the game at the main menu for the next case unless the
        // case opted out.
        var kept = report["keep"].AsBool() == true;
        block.Add("process", kept ? "reused-after-run" : "not-kept-after-run");
        report[ProvenanceKey] = block;
        return block;
    }

    private static string IsolationOf(AcceptanceCase c) =>
        c.NoKeep || c.Expansions.Count > 0 || c.Start is OwnedStart ? Isolation.Fresh : Isolation.Shared;

    // Says why the case needs its own process, for the cases that declared it.
    private static string IsolationReason(AcceptanceCase c)
    {
        if (c.Start is OwnedStart)
        {
            return "the case drives the process lifecycle itself (Owned)";
        }

        if (c.Expansions.Count > 0)
        {
            return "the case pins a DLC profile, which a kept process cannot change";
        }

        return c.NoKeep ? "the case declares NoKeep: it restarts, faults or retires the game, or touches process-scoped static state" : "";
    }
}
