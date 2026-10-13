using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// The startup-labor diagnosis (Go package startuplabor): the bounded, structured evidence that tells planner
// starvation apart from a missing material, a missing worker, an admitted-but-unworked action and ordinary non-work
// activity, plus the idle-pawn tick accounting.
//
// It is diagnosis only. Nothing here ranks, plans, dispatches or changes game speed or authority; every field is read
// from evidence the run already holds (the durable review record, plan progress and one bounded observations_list_pawns
// sample per observation). Missing evidence is reported as missing: a subject never infers a fact it did not observe.

/// <summary>One diagnosis outcome for a goal at a review (Go <c>startuplabor.Class</c>).</summary>
public static class LaborClass
{
    /// <summary>The open action is held on stock, a reservation or spending policy.</summary>
    public const string MaterialBlocker = "material_blocker";

    /// <summary>The goal's work has no pawn: a refusal on labor, or an open action held on an unavailable worker.</summary>
    public const string WorkerBlocker = "worker_blocker";

    /// <summary>The action was dispatched and its effect is still outstanding (admitted but unworked).</summary>
    public const string UnresolvedAction = "unresolved_action";

    /// <summary>Nothing blocks the goal; the observed pawns are asleep, eating, recreating or medically resting.</summary>
    public const string NonWorkActivity = "non_work_activity";

    /// <summary>The action is moving (prepared, dispatched with its effect resolved, or completed).</summary>
    public const string Progressing = "progressing";

    /// <summary>The evidence needed to classify was not observed. <see cref="LaborDiagnosis.Missing"/> names exactly what.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The colony and load a diagnosis was read under; a subject from another world is never compared with this one (Go <c>startuplabor.World</c>).</summary>
public readonly record struct LaborWorld(string Colony, string Load, int Map);

/// <summary>
/// The open action's view as the diagnosis reads it (Go <c>domain.ProgressView</c>): its stage, whether its dispatched
/// effect is outstanding, and the fresh hold reasons (<c>ProgressView.FreshHeldReason</c>, in the domain's own order;
/// empty when the hold evidence is stale or the action is not pending/prepared).
/// </summary>
public sealed record LaborProgress(string Stage, bool Unresolved, IReadOnlyList<string> FreshHeld);

/// <summary>
/// One goal at one review: what the ranking did with it, the open action bound to it (null when the goal has none),
/// and the labor observation that covers its work (null when no sample was taken).
/// </summary>
public sealed record LaborSubject
{
    public LaborWorld World { get; init; }

    public long ReviewTick { get; init; }

    public string Concern { get; init; } = "";

    public string Method { get; init; } = "";

    public string Action { get; init; } = "";

    /// <summary>The open action's view, null when the goal bound none.</summary>
    public LaborProgress? Progress { get; init; }

    /// <summary>What the pawns eligible for this goal's work were doing at the nearest sample; null means unobserved, not idle.</summary>
    public IReadOnlyList<string>? Activity { get; init; }

    /// <summary>Whether the shelter goal is waiting on the shelter-beds rung; null (unknown) on every other goal.</summary>
    public bool? ShelterBeds { get; init; }
}

/// <summary>The stable artifact row: the key, the class, the evidence that decided it and whatever was missing (Go <c>startuplabor.Diagnosis</c>).</summary>
public sealed record LaborDiagnosis
{
    public LaborWorld World { get; init; }

    public long ReviewTick { get; init; }

    public string Concern { get; init; } = "";

    public string Method { get; init; } = "";

    public string Action { get; init; } = "";

    public string Class { get; init; } = LaborClass.Unknown;

    /// <summary>The fresh hold reasons on the open action, in the domain's own order.</summary>
    public IReadOnlyList<string> Held { get; init; } = [];

    /// <summary>The single hold reason that decided a material or worker class.</summary>
    public string Blocker { get; init; } = "";

    /// <summary>The open action's progress stage, empty when there is none.</summary>
    public string Stage { get; init; } = "";

    /// <summary>The open action's outstanding-effect flag.</summary>
    public bool Unresolved { get; init; }

    /// <summary>True/false when known, absent from the row otherwise.</summary>
    public bool? ShelterBeds { get; init; }

    /// <summary>The observed pawn activities by kind; empty when nothing was sampled.</summary>
    public IReadOnlyDictionary<string, int> Activities { get; init; } = new Dictionary<string, int>();

    /// <summary>Every fact the classification wanted and did not have, sorted.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>
    /// Renders the diagnosis for the report artifact. Absent facts are absent keys, never zero values standing in for
    /// evidence.
    /// </summary>
    public JsonObject Row()
    {
        var row = new JsonObject
        {
            ["colony"] = World.Colony,
            ["load"] = World.Load,
            ["map"] = World.Map,
            ["review_tick"] = ReviewTick,
            ["concern"] = Concern,
            ["class"] = Class,
        };
        if (Method.Length > 0)
        {
            row["method"] = Method;
        }

        if (Action.Length > 0)
        {
            row["action"] = Action;
        }

        if (Stage.Length > 0)
        {
            row["stage"] = Stage;
        }

        if (Unresolved)
        {
            row["unresolved"] = true;
        }

        if (Held.Count > 0)
        {
            row["held"] = new JsonArray([.. Held.Select(h => (JsonNode?)JsonValue.Create(h))]);
        }

        if (Blocker.Length > 0)
        {
            row["blocker"] = Blocker;
        }

        if (ShelterBeds is { } beds)
        {
            row["shelter_beds"] = beds;
        }

        if (Activities.Count > 0)
        {
            var activities = new JsonObject();
            foreach (var (activity, count) in Activities)
            {
                activities[activity] = count;
            }

            row["activities"] = activities;
        }

        if (Missing.Count > 0)
        {
            row["missing"] = new JsonArray([.. Missing.Select(m => (JsonNode?)JsonValue.Create(m))]);
        }

        return row;
    }
}

public static partial class StartupLabor
{
    // The hold reasons that mean "the stuff is not there or not ours to take".
    private static readonly HashSet<string> MaterialHolds = new(StringComparer.Ordinal)
    {
        "explicit_material_required", "insufficient_stock", "spending_policy", "already_reserved", "held_reservation_unverifiable", "missing_storage",
    };

    // The hold reasons that mean "no pawn can take this".
    private static readonly HashSet<string> WorkerHolds = new(StringComparer.Ordinal) { "urgent_competing_work" };

    /// <summary>
    /// Classifies one subject. It reads only what the subject carries: an absent fact produces
    /// <see cref="LaborClass.Unknown"/> with the fact named in Missing, and never a guess.
    /// </summary>
    public static LaborDiagnosis Diagnose(LaborSubject s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var activities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in s.Activity ?? [])
        {
            activities[a] = activities.GetValueOrDefault(a) + 1;
        }

        IReadOnlyList<string> held = s.Progress?.FreshHeld ?? [];
        var missing = new List<string>();
        var blocker = "";
        var cls = Classify(s, held, missing, ref blocker);
        missing.Sort(StringComparer.Ordinal);
        return new LaborDiagnosis
        {
            World = s.World,
            ReviewTick = s.ReviewTick,
            Concern = s.Concern,
            Method = s.Method,
            Action = s.Action,
            Class = cls,
            Held = held,
            Blocker = blocker,
            Stage = s.Progress?.Stage ?? "",
            Unresolved = s.Progress?.Unresolved ?? false,
            ShelterBeds = s.ShelterBeds,
            Activities = activities,
            Missing = missing,
        };
    }

    private static string Classify(LaborSubject s, IReadOnlyList<string> held, List<string> missing, ref string blocker)
    {
        if (s.Progress is null)
        {
            missing.Add("progress");
            return LaborClass.Unknown;
        }

        // An outstanding dispatch outranks every other reading: the action was admitted and its effect has not come back.
        if (s.Progress.Unresolved)
        {
            return LaborClass.UnresolvedAction;
        }

        foreach (var r in held)
        {
            if (MaterialHolds.Contains(r))
            {
                blocker = r;
                return LaborClass.MaterialBlocker;
            }
        }

        foreach (var r in held)
        {
            if (WorkerHolds.Contains(r))
            {
                blocker = r;
                return LaborClass.WorkerBlocker;
            }
        }

        if (held.Count > 0)
        {
            // A held action with a reason in neither set is evidence we hold but do not classify; say so rather than
            // pick a side.
            blocker = held[0];
            missing.Add("hold_reason_class");
            return LaborClass.Unknown;
        }

        switch (s.Progress.Stage)
        {
            case "pending":
                // Pending with no hold: fall through to the ranking and the pawn sample below.
                break;
            case "":
                missing.Add("progress_stage");
                return LaborClass.Unknown;
            default:
                return LaborClass.Progressing;
        }

        if (s.Activity is null)
        {
            missing.Add("pawn_activity");
            return LaborClass.Unknown;
        }

        foreach (var a in s.Activity)
        {
            switch (a)
            {
                case LaborActivity.Unknown:
                    missing.Add("pawn_activity");
                    return LaborClass.Unknown;
                case LaborActivity.Work:
                    return LaborClass.Progressing;
                case LaborActivity.Idle:
                    // An idle pawn beside a selected, unheld goal is the starvation this epic is after: no blocker names itself.
                    missing.Add("blocker");
                    return LaborClass.Unknown;
            }
        }

        return LaborClass.NonWorkActivity;
    }
}
