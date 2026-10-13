using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Ends a watch as soon as the journal shows the case cannot pass, instead of running out the wall-clock ceiling and
/// reporting the same refusal text the run notes say to read first. The default value is on with the defaults below;
/// a case where one of these shapes is an expected transient sets <see cref="Disabled"/> or raises the threshold.
/// <para>
/// Four shapes abort, each with the journal's own text as the failure: <b>no method</b> (the rounds handed the
/// watched goal a development slot and its planner committed nothing for <see cref="NoMethodReviews"/> consecutive
/// reviews while the goal stayed open/unmet with no method); <b>unsuccessful</b> (an action of one of the goal's
/// committed methods ended unsuccessful for any reason but interrupted or cancelled: a pawn's own needs or a
/// cancelled order are re-planned; a native failure, an expiry, a dead target or an unachieved outcome are not);
/// <b>refusal</b> (the service's latest scheduler step still carries the same isolated planner failure on a native
/// refusal after <see cref="RefusalSamples"/> consecutive samples); <b>emergency park</b> (the review's development
/// rows report an emergency holding goals back, the watched goal is suspended, and the live tick has not moved for
/// <see cref="ParkSamples"/> consecutive samples: for example a downed colonist with no tend/rescue family to serve
/// the emergency, the clock refusing every window as no_work, and the suspended goal waiting for a world that never
/// moves again).
/// </para>
/// </summary>
public sealed record FailFast
{
    public bool Disabled { get; init; }

    /// <summary>How many consecutive reviews may leave the goal idle with nothing committed before the watch fails (default 5). A review is one rounds_review revision; unsampled revisions in between do not count.</summary>
    public int NoMethodReviews { get; init; }

    /// <summary>
    /// How many consecutive samples the latest step may stay a native refusal before the watch fails (default 6: 30 s
    /// at the usual 5 s poll). The step event logs once per change of outcome, so an unchanged latest line means the
    /// refusal repeated every step since.
    /// </summary>
    public int RefusalSamples { get; init; }

    /// <summary>
    /// Treats a review whose row reads method_unavailable as neutral for the no-method count: neither a count nor a
    /// reset. A goal whose methods are exhausted by design (MaintainResource while the project gating its bench is
    /// unfinished) returns method_unavailable and waits for the next review, which reads like a planner that found no
    /// method, so the case that knows the design opts in.
    /// </summary>
    public bool MethodUnavailableWaits { get; init; }

    /// <summary>
    /// Reasons the unsuccessful shape skips like interrupted and cancelled: a goal serving several resources retries a
    /// shortfall on one of them (a MedicineHerbal acquisition ending outcome_not_achieved under MaintainResource)
    /// without the case failing.
    /// </summary>
    public IReadOnlyList<string> RetryableUnsuccessful { get; init; } = [];

    /// <summary>
    /// How many consecutive samples the watched goal may sit suspended under an emergency with the live tick
    /// unchanged before the watch fails (default 12: a minute at the usual 5 s poll, past the stop between windows
    /// and a checkpoint capture). A sample without a live tick does not count.
    /// </summary>
    public int ParkSamples { get; init; }

    internal const int DefaultNoMethodReviews = 5;
    internal const int DefaultRefusalSamples = 6;
    internal const int DefaultParkSamples = 12;
}

/// <summary>
/// The report's record of a fail-fast abort (<c>report["fail_fast"]</c>) and the failure the watch raises
/// (Go <c>sustainedfood.Verdict</c>).
/// </summary>
public sealed class FailFastVerdict : AcceptanceException
{
    public FailFastVerdict(string shape, string reason, JsonNode? evidence)
        : base("fail-fast (" + shape + "): " + reason)
    {
        Shape = shape;
        Reason = reason;
        Evidence = evidence;
    }

    public string Shape { get; }

    public string Reason { get; }

    /// <summary>The journal text behind the verdict: the step line, the unsuccessful action, or the review revisions counted.</summary>
    public JsonNode? Evidence { get; }

    /// <summary>The verdict in the report's JSON shape.</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["shape"] = Shape, ["reason"] = Reason };
        if (Evidence is not null)
        {
            row["evidence"] = Evidence.DeepClone();
        }

        return row;
    }
}

/// <summary>Folds the samples of one watch; <see cref="Check"/> returns a verdict the first time a shape completes (Go <c>failFastState</c>).</summary>
internal sealed class FailFastState
{
    // The causes a planner waits on rather than fails (policy.Cause.Waiting: the wait kinds and the two fight
    // outcomes); the case oracles read blocked reasons as strings, so the closed set is repeated here.
    private static readonly HashSet<string> WaitingCauses = new(StringComparer.Ordinal)
    {
        "method_already_used", "already_working_on_it", "shelter_bunks_open", "breach_held", "waiting_for_native_comfort_use",
        "existing_facility_needs_bill_or_upkeep", "hospital_bed_convert_pending", "sleeping_use_needed", "butcher_separation_pending",
        "dialog_not_interactive", "waiting_on_claim", "existing_facility_access_blocked", "waiting_for_native_temperature",
        "retry_budget_waiting", "combat_orders", "hold_fallback",
    };

    private const string HeldOptIn = "held:opt-in";
    private const string HeldUnavailable = "held:unavailable";

    private readonly FailFast _cfg;
    private readonly string _concern;
    private readonly string _logPath;
    private readonly int _noMethodReviews;
    private readonly int _refusalSamples;
    private readonly int _parkSamples;

    private ulong _lastRevision;
    private readonly List<ulong> _idleReviews = [];

    private string _lastStepLine = "";
    private int _stepSamples;

    private ulong _parkTick;
    private int _parkCount;

    internal FailFastState(FailFast cfg, string goal, string logPath)
    {
        _cfg = cfg;
        _concern = goal;
        _logPath = logPath;
        _noMethodReviews = cfg.NoMethodReviews > 0 ? cfg.NoMethodReviews : FailFast.DefaultNoMethodReviews;
        _refusalSamples = cfg.RefusalSamples > 0 ? cfg.RefusalSamples : FailFast.DefaultRefusalSamples;
        _parkSamples = cfg.ParkSamples > 0 ? cfg.ParkSamples : FailFast.DefaultParkSamples;
    }

    /// <summary>Folds one sample (a standard sample without an error) and the service's latest scheduler step.</summary>
    internal FailFastVerdict? Check(JsonObject sample)
    {
        if (_cfg.Disabled)
        {
            return null;
        }

        if (Unsuccessful(sample) is { } unsuccessful)
        {
            return unsuccessful;
        }

        if (NoMethod(sample) is { } noMethod)
        {
            return noMethod;
        }

        if (EmergencyPark(sample) is { } park)
        {
            return park;
        }

        if (_logPath.Length > 0 && StepEvents.LastSchedulerStepFile(_logPath) is { } step)
        {
            return Refusal(step);
        }

        return null;
    }

    private FailFastVerdict? Unsuccessful(JsonObject sample)
    {
        foreach (var plan in sample["plans"].AsSlice().OfType<JsonObject>())
        {
            foreach (var row in plan["unsuccessful"].AsSlice().OfType<JsonObject>())
            {
                var reason = row["reason"].AsString();
                if (reason is "interrupted" or "cancelled" || _cfg.RetryableUnsuccessful.Contains(reason, StringComparer.Ordinal))
                {
                    continue;
                }

                return new FailFastVerdict(
                    "unsuccessful",
                    $"standard {_concern} plan {plan["plan"].AsString()} action {row["action"].AsString()} ended unsuccessful ({reason})",
                    row.DeepClone());
            }
        }

        return null;
    }

    private FailFastVerdict? NoMethod(JsonObject sample)
    {
        var revision = SampleJson.Tick(sample["review_revision"]) ?? 0;
        if (revision == 0 || revision == _lastRevision)
        {
            return null;
        }

        _lastRevision = revision;
        var methodCount = SampleJson.Count(sample, "method_count");
        var deficit = sample["need"].AsString() == "unmet" && sample["status"].AsString() == "open";
        var blocked = sample["blocked"].AsString();
        var vetoed = sample["vetoed"].AsBool() == true;
        if (!deficit || methodCount > 0)
        {
            _idleReviews.Clear();
            return null;
        }

        if (vetoed || WaitingCauses.Contains(blocked) || blocked == HeldOptIn || (_cfg.MethodUnavailableWaits && blocked == HeldUnavailable))
        {
            return null;
        }

        _idleReviews.Add(revision);
        if (_idleReviews.Count < _noMethodReviews)
        {
            return null;
        }

        var revisions = new JsonArray();
        foreach (var r in _idleReviews)
        {
            revisions.Add(r);
        }

        return new FailFastVerdict(
            "no_method",
            string.Create(CultureInfo.InvariantCulture, $"standard {_concern} stayed open/unmet with no method through {_idleReviews.Count} reviews (revisions {_idleReviews[0]}..{revision})"),
            new JsonObject { ["revisions"] = revisions, ["blocked"] = blocked });
    }

    private FailFastVerdict? Refusal(SchedulerStep step)
    {
        if (!step.Refused)
        {
            _lastStepLine = "";
            _stepSamples = 0;
            return null;
        }

        if (step.Line != _lastStepLine)
        {
            _lastStepLine = step.Line;
            _stepSamples = 0;
        }

        _stepSamples++;
        if (_stepSamples < _refusalSamples)
        {
            return null;
        }

        return new FailFastVerdict(
            "refusal",
            string.Create(CultureInfo.InvariantCulture, $"the latest scheduler step has repeated the same native refusal for {_stepSamples} samples: {step.PlannerFailures}"),
            JsonValue.Create(step.Line));
    }

    // Counts consecutive samples in which the Safeguards veto the watched goal, the review names emergency needs
    // (sample["emergency"]), and the live tick is where the previous sample left it. Any of the three changing resets
    // the count: a moving tick means something is serving the emergency, and a goal no longer vetoed or a review with
    // no emergency means it was served.
    private FailFastVerdict? EmergencyPark(JsonObject sample)
    {
        var tickValue = SampleJson.Tick(sample["tick"]);
        var emergency = SampleJson.Strings(sample["emergency"]);
        var vetoed = sample["vetoed"].AsBool() == true;
        var tick = tickValue ?? 0;
        if (tickValue is null || !vetoed || emergency.Count == 0 || tick != _parkTick)
        {
            _parkTick = tick;
            _parkCount = 0;
            if (tickValue is null || !vetoed || emergency.Count == 0)
            {
                return null;
            }
        }

        _parkCount++;
        if (_parkCount < _parkSamples)
        {
            return null;
        }

        return new FailFastVerdict(
            "emergency_park",
            string.Create(CultureInfo.InvariantCulture, $"standard {_concern} stayed vetoed while emergency [{string.Join(' ', emergency)}] with the live tick parked at {tick} for {_parkCount} samples: nothing serves the emergency and the clock admits no work"),
            new JsonObject
            {
                ["tick"] = tick,
                ["emergency"] = sample["emergency"]?.DeepClone(),
                ["review_revision"] = sample["review_revision"]?.DeepClone(),
                ["development"] = sample["development"]?.DeepClone(),
            });
    }
}
