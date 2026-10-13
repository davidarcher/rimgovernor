using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// How much of a case a result covers (Go <c>cases.Execution</c>). A full run drove the case from its declared
/// precondition; the others opened on something an earlier run left behind and therefore prove less.
/// </summary>
public static class Execution
{
    /// <summary>Ran the declared Start and every stage itself.</summary>
    public const string Full = "full";

    /// <summary>Opened on a stage bundle: the staging block's own behaviour was not exercised, only the behaviour after it.</summary>
    public const string CachedPrecondition = "cached-precondition";

    /// <summary>Opened on a checkpoint from a failed run: only behaviour past the checkpoint's offset was exercised.</summary>
    public const string ResumedSuffix = "resumed-suffix";

    /// <summary>An <c>acceptance dev</c> iteration over a pinned bundle: an edit loop, never evidence.</summary>
    public const string Dev = "dev-iteration";

    /// <summary>Reloaded a failed bundle and ran only the case's Postmortem reads: it proves the assertion, not the scenario.</summary>
    public const string PostmortemOnly = "postmortem-only";
}

/// <summary>The process a case got (Go <c>cases.Isolation</c>).</summary>
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

/// <summary>The "what does this result prove?" block (Go <c>cases/provenance.go</c>).</summary>
public static class Provenance
{
    /// <summary>The report block <see cref="Stamp"/> fills.</summary>
    public const string ProvenanceKey = "provenance";

    /// <summary>
    /// Stamps on <paramref name="report"/> the derived answer to "what does this result prove?": which segment of the
    /// case ran, what the installed build had to provide for it to start, and whether the case got its own process.
    /// Every field is derived from <paramref name="c"/> and from what the run already recorded, so the block adds no
    /// second catalog to maintain beside the registry. It deliberately does not restate the registry: the claim is the
    /// case's Scope (already report["scope"]), the declared precondition is report["start"], and the bundle a reused run
    /// opened on is report["resumed_from"]/["staged_from"]; what is derived here is the judgement those fields support.
    /// </summary>
    public static JsonObject Stamp(AcceptanceCase c, Report report)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(report);
        var (execution, proves) = Classify(c, report);
        var block = new JsonObject
        {
            ["execution"] = execution,
            ["proves"] = proves,
            ["isolation"] = IsolationOf(c),
        };
        var reason = IsolationReason(c);
        if (reason.Length > 0)
        {
            block["isolation_reason"] = reason;
        }

        var ops = c.FixtureOps();
        if (ops.Count > 0)
        {
            // The native dependency: without these ops registered by the installed package the case cannot reach its
            // precondition at all.
            block["native_ops"] = ToArray(ops);
        }

        if (c.Serve?.Families is { } families)
        {
            // The routine families the controller was restricted to: everything outside this list was disabled for the
            // run, so the result says nothing about it.
            block["routine_families"] = ToArray(families);
        }

        block["world"] = report.Has("resumed_from") || report.Has("staged_from") ? "restored-save" : "fresh";

        // A fresh world is not a fresh process: the runner leaves the game at the main menu for the next case unless the
        // case opted out.
        var kept = report["keep"].AsBool() == true;
        block["process"] = kept ? "reused-after-run" : "not-kept-after-run";
        report[ProvenanceKey] = block;
        return block;
    }

    // Decides the run's Execution and the one-line statement of what segment it covers. The order matters: a
    // postmortem-only run over a resumed bundle is a postmortem-only run.
    private static (string Execution, string Proves) Classify(AcceptanceCase c, Report report)
    {
        if (report["postmortem_only"].AsBool() == true)
        {
            return (Execution.PostmortemOnly, "the case's Postmortem reads over a bundle an earlier run left; the scenario that produced it was not rerun");
        }

        if (report["dev"].AsBool() == true)
        {
            return (Execution.Dev, "an edit iteration over a pinned bundle; not evidence for any segment");
        }

        if (report["resumed_from"].AsMap() is { } resumed)
        {
            return (Execution.ResumedSuffix, $"behaviour past {OffsetOf(resumed)} only; everything before it came from the checkpoint");
        }

        if (report["staged_from"].AsMap() is { } staged)
        {
            var stage = staged["stage"].AsString();
            if (stage.Length == 0)
            {
                stage = "the cached stage";
            }

            return (Execution.CachedPrecondition, $"behaviour after stage \"{stage}\" only; the staging blocks that produced the precondition were not rerun ({string.Join(" -> ", StagesUpTo(c, stage))})");
        }

        return (Execution.Full, "the whole case from its declared precondition");
    }

    // Describes where a resumed bundle picked up, by game tick when the checkpoint recorded one and by run offset otherwise.
    private static string OffsetOf(JsonObject from)
    {
        if (from["tick"] is JsonValue && from["tick"].AsNumber() > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"tick {(long)from["tick"].AsNumber()}");
        }

        var label = from["label"].AsString();
        if (label.Length > 0)
        {
            return label;
        }

        if (from["offset_ms"] is JsonValue)
        {
            return string.Create(CultureInfo.InvariantCulture, $"t+{from["offset_ms"].AsNumber() / 1000:0}s");
        }

        return "the checkpoint";
    }

    // Names the case's stages the bundle stands in for, in declared order through stage.
    private static List<string> StagesUpTo(AcceptanceCase c, string stage)
    {
        var skipped = new List<string>();
        foreach (var name in c.Stages)
        {
            skipped.Add(name);
            if (name == stage)
            {
                break;
            }
        }

        return skipped.Count == 0 ? ["no stage of that name is declared"] : skipped;
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

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
