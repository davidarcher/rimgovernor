namespace RimGovernor.Brain.Policy;

/// <summary>What the Safeguards read: the rounds's control state and the loose things the last safety census reported unsafe to haul (fire, trap, hostile line of sight).</summary>
public sealed record SafeguardContext
{
    public bool Enabled { get; init; }

    public EquatableArray<string> Unsafe { get; init; }
}

/// <summary>The routine goal a proposal would serve: its need and its current priority.</summary>
public sealed record SafeguardProposal
{
    public ConcernId Need { get; init; }

    public int Priority { get; init; }
}

/// <summary>What a Safeguard is asked to admit: a whole-goal proposal or one action at dispatch. Exactly one is set.</summary>
public sealed record Admission
{
    public SafeguardProposal? Proposal { get; init; }

    public DomainAction? Action { get; init; }
}

/// <summary>A veto: the Safeguard that refused and why.</summary>
public sealed record SafeguardRefusal
{
    public string Safeguard { get; init; } = "";

    public string Reason { get; init; } = "";
}

/// <summary>
/// An admission veto, not a goal row. Priority orders work only; suspending other work is a Safeguard's job.
/// The planner runner asks the Safeguards before planning for a need, method admission asks them again as a
/// backstop, and dispatch asks them before any prepared plan writes. A Safeguard returns "" for an admission
/// kind it does not govern.
/// </summary>
public interface ISafeguard
{
    /// <summary>Identifies the Safeguard in a refusal.</summary>
    string Name { get; }

    /// <summary>The reason the admission is refused, or "" to admit it.</summary>
    string Veto(SafeguardContext c, Admission a);
}

/// <summary>Vetoes every routine proposal while control is paused: native work already issued keeps progressing and the resumed goal adopts it.</summary>
public sealed class PauseSafeguard : ISafeguard
{
    public string Name => "PauseSafeguard";

    public string Veto(SafeguardContext c, Admission a) =>
        a.Proposal is null || c.Enabled ? "" : "control paused";
}

/// <summary>
/// Refuses allowing an item the safety census reported unsafe. Forbidding stays open: that is
/// ManageSupplySafety's Standard work. A veto refuses only that action, and the rest of its plan dispatches.
/// </summary>
public sealed class UnsafeLootSafeguard : ISafeguard
{
    public string Name => "UnsafeLootSafeguard";

    public string Veto(SafeguardContext c, Admission a)
    {
        if (a.Action is null)
        {
            return "";
        }
        var s = a.Action.SupplyAllow;
        if (s is null || s.Forbidden || !c.Unsafe.Contains(s.Thing))
        {
            return "";
        }
        return "unsafe item " + s.Thing;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The veto registry: every admission Safeguard, asked in order.</summary>
    public static readonly IReadOnlyList<ISafeguard> Safeguards = [new PauseSafeguard(), new UnsafeLootSafeguard()];

    /// <summary>Asks each Safeguard in turn and returns the first refusal, or false when every Safeguard admits.</summary>
    public static (SafeguardRefusal Refusal, bool Refused) Refuse(SafeguardContext c, Admission a)
    {
        foreach (var r in Safeguards)
        {
            var reason = r.Veto(c, a);
            if (reason != "")
            {
                return (new SafeguardRefusal { Safeguard = r.Name, Reason = reason }, true);
            }
        }
        return (new SafeguardRefusal(), false);
    }

    /// <summary>Refuse for a goal-level proposal.</summary>
    public static (SafeguardRefusal Refusal, bool Refused) RefuseProposal(SafeguardContext c, SafeguardProposal p) =>
        Refuse(c, new Admission { Proposal = p });

    /// <summary>Refuse for one action at dispatch.</summary>
    public static (SafeguardRefusal Refusal, bool Refused) RefuseAction(SafeguardContext c, DomainAction a) =>
        Refuse(c, new Admission { Action = a });

    /// <summary>The first veto's reason, or "" when every Safeguard admits the proposal.</summary>
    public static string VetoProposal(SafeguardContext c, SafeguardProposal p) => RefuseProposal(c, p).Refusal.Reason;
}
