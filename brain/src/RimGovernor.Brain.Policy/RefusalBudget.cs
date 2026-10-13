namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // Budget verdict codes. A planner journals the Code as the stable reason of its decision and the Reason beside it.

    /// <summary>BudgetSpent: native refused permanently; the subject is given up on.</summary>
    public const string BudgetSpent = "retry_budget_spent";

    /// <summary>BudgetWaiting: native refused transiently and the world has not changed since.</summary>
    public const string BudgetWaiting = "retry_budget_waiting";

    /// <summary>BudgetUnknown: native could not classify the refusal. The subject stays allowed; the refusal stays visible.</summary>
    public const string BudgetUnknown = "refusal_unknown";
}

/// <summary>One ledger row: the newest refusal of one subject for one reason, with the world it was met in.</summary>
public sealed record RefusalEntry
{
    public string Subject { get; init; } = "";

    public string Reason { get; init; } = "";

    public RefusalClass Class { get; init; }

    public GenerationSnapshot World { get; init; }
}

/// <summary>Says why Allowed answered as it did. It is the default value when the subject has no refusal on record.</summary>
public sealed record BudgetVerdict
{
    public string Code { get; init; } = "";

    public string Reason { get; init; } = "";

    public RefusalClass Class { get; init; }
}

/// <summary>
/// The one reason-aware retry budget every planner shares. A permanent refusal gives its subject up under its
/// real reason, a transient one waits for the world to change, and an unclassified one never bans anything.
/// The ledger is a plain value; the store that owns the subject persists Entries.
/// </summary>
public sealed record RefusalBudget
{
    public EquatableArray<RefusalEntry> Entries { get; init; }

    /// <summary>
    /// Returns the budget with the refusal noted. A second refusal of the same subject for the same reason
    /// replaces the first. A class outside the three is unknown.
    /// </summary>
    public RefusalBudget Record(string subject, string reason, RefusalClass @class, GenerationSnapshot world)
    {
        if (!@class.IsValid())
        {
            @class = RefusalClass.Unknown;
        }
        var entries = new List<RefusalEntry>(Entries.Count + 1);
        foreach (var e in Entries)
        {
            if (e.Subject != subject || e.Reason != reason)
            {
                entries.Add(e);
            }
        }
        entries.Add(new RefusalEntry { Subject = subject, Reason = reason, Class = @class, World = world });
        return new RefusalBudget { Entries = EquatableArray.From(entries) };
    }

    /// <summary>
    /// Reports whether subject may be tried in world. A permanent refusal on record blocks for good and names
    /// its real reason; a transient one blocks only while world is the world it was met in; an unknown one
    /// never blocks but is returned so the caller journals it.
    /// </summary>
    public (bool Allowed, BudgetVerdict Verdict) Allowed(string subject, GenerationSnapshot world)
    {
        var unknown = new BudgetVerdict();
        var waiting = new BudgetVerdict();
        foreach (var e in Entries)
        {
            if (e.Subject != subject)
            {
                continue;
            }
            switch (e.Class)
            {
                case RefusalClass.Permanent:
                    return (false, new BudgetVerdict { Code = BudgetSpent, Reason = e.Reason, Class = e.Class });
                case RefusalClass.Transient:
                    if (e.World == world && waiting.Code.Length == 0)
                    {
                        waiting = new BudgetVerdict { Code = BudgetWaiting, Reason = e.Reason, Class = e.Class };
                    }
                    break;
                default:
                    unknown = new BudgetVerdict { Code = BudgetUnknown, Reason = e.Reason, Class = RefusalClass.Unknown };
                    break;
            }
        }
        if (waiting.Code.Length != 0)
        {
            return (false, waiting);
        }
        return (true, unknown);
    }
}
