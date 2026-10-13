namespace RimGovernor.Brain.Domain;

public enum StandardStatus
{
    Open,
    Settled,
    Voided,
}

/// <summary>
/// An Inspection's measured result for a Standard Concern: Met, Unmet, or Unclear when the facts do not say.
/// Unclear is the default value.
/// </summary>
public enum Finding
{
    Unclear,
    Unmet,
    Met,
}

/// <summary>
/// An Inspection's measured result for an Incident Concern: Active, Clear, or Unclear. It is a distinct type
/// so an Incident cannot be read as Met or Unmet; both reduce to the same tri-state fact. Members mirror
/// <see cref="Finding"/> one for one (same order and wire names).
/// </summary>
public enum Situation
{
    Unclear,

    [WireName("unmet")]
    Active,

    [WireName("met")]
    Clear,
}

public static class FindingExtensions
{
    /// <summary>The Incident reading of <paramref name="f"/>: Unmet is Active, Met is Clear.</summary>
    public static Situation Situation(this Finding f) => (Situation)(int)f;

    /// <summary>The tri-state fact under <paramref name="s"/>.</summary>
    public static Finding Finding(this Situation s) => (Finding)(int)s;
}

public static partial class DomainConstants
{
    /// <summary>
    /// The bot's own colony size target. The colony grows toward it only as fast as policy.JoinerCapacity's bed
    /// and food gates allow, so in practice the target rises with the colony's means.
    /// </summary>
    public const int PopulationTarget = 100;

    /// <summary>Go <c>MaxStandardRecord</c>: bounds <see cref="Standard.Record"/> in UTF-8 bytes.</summary>
    public const int MaxStandardRecord = 4096;
}

/// <summary>
/// Records a maintained outcome. Executable methods reference ordinary shared plans; their receipts,
/// progress and uncertainty stay in those plans.
/// </summary>
public sealed partial record Standard
{
    public StandardId Id { get; init; }

    /// <summary>The Concern the Standard serves.</summary>
    public ConcernId Kind { get; init; }

    public int Priority { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public ulong Episode { get; init; }

    public StandardStatus Status { get; init; }

    public Finding Finding { get; init; }

    public bool RecoveryObserved { get; init; }

    /// <summary>
    /// Marks a build-once outcome (cooking, basic power, defensive layout, a trade mission): once settled it
    /// stays settled, an unknown measurement does not reopen it, and only a measured deficit with no work open
    /// starts a new Episode.
    /// </summary>
    public bool Once { get; init; }

    /// <summary>
    /// The goal's own durable intent (Go's JSON <c>Record</c> text, here typed): what the world cannot show.
    /// Only a trade-mission Standard carries one; null otherwise.
    /// </summary>
    public TradeMission? Mission { get; init; }

    /// <summary>
    /// The goal's own durable intent as JSON text (Go <c>Standard.Record</c>), written and read by the goal's planner:
    /// what the world cannot show (a creep-joiner, ideoligion-reform or trade-request record). Null or empty for most
    /// goals; a trade-mission Standard uses the typed <see cref="Mission"/> instead. Bounded by
    /// <see cref="DomainConstants.MaxStandardRecord"/> bytes.
    /// </summary>
    public string? Record { get; init; }

    /// <summary>A new open, unclear Standard (Go <c>NewStandard</c>); throws when invalid.</summary>
    public static Standard NewStandard(StandardId id, ConcernId kind, int priority, GenerationSnapshot snapshot)
    {
        var g = new Standard
        {
            Id = id,
            Kind = kind,
            Priority = priority,
            Snapshot = snapshot,
            Status = StandardStatus.Open,
            Finding = Finding.Unclear,
        };
        g.Validate();
        return g;
    }

    public void Validate()
    {
        DomainException.Require(Id.IsValid && Kind.IsValid && Priority >= 0 && Priority <= DomainConstants.MaxPriority && Snapshot.IsValid, "invalid standard");
        DomainException.Require(Record is null || System.Text.Encoding.UTF8.GetByteCount(Record) <= DomainConstants.MaxStandardRecord, "standard record exceeds bound");
        if (Kind == Concerns.TradeMission)
        {
            DomainException.Require(Mission is not null && Once && Mission.HomeColony == Snapshot.Colony && Mission.HomeMap == Snapshot.Map, "invalid trade mission standard record");
            Mission.Validate();
        }
        DomainException.Require(Wire.IsDefined(Status), "invalid standard status");
        DomainException.Require(Wire.IsDefined(Finding), "invalid standard need");
        DomainException.Require(!(Status == StandardStatus.Settled && (Finding != Finding.Met || !RecoveryObserved)), "settled standard needs observed recovery");
    }

    /// <summary>
    /// Invalidates captured work on world, direction or tick replacement. <paramref name="openWork"/> is derived from
    /// linked shared plans, including cancelled actions whose effects are still unresolved. A deficit measured
    /// after a recovery starts a new Episode once no work is open: either the recovery was observed as
    /// satisfaction, or the previous review measured it while a plan's effects were still unresolved and the
    /// world has regressed since (a lamp removed behind a lit bench), so the settled episode's methods may be
    /// proposed again. Priority orders work only: a manual pause vetoes proposals through the policy Safeguards.
    /// A Once Standard that settled stays settled: only a measured deficit with no work open (it regressed)
    /// starts a new Episode, and a regression it measured while its effects were unresolved is not one.
    /// <para>
    /// A goal carries no review tick: a review that changes nothing returns a value equal to its input, so the
    /// store neither bumps the revision nor rewrites the goal's governor state each round. A tick rewind is
    /// ReviewRounds' to catch.
    /// </para>
    /// </summary>
    public static Standard Review(Standard g, GenerationSnapshot current, Finding need, bool openWork)
    {
        var original = g;
        g.Validate();
        DomainException.Require(current.IsValid, "invalid standard review scope");
        DomainException.Require(Wire.IsDefined(need), "invalid observed need");
        if (g.Status == StandardStatus.Voided)
        {
            return g;
        }
        if (!g.Snapshot.SameColonyMap(current))
        {
            return g with { Status = StandardStatus.Voided };
        }
        // Review revisions may advance without changing the player's direction.
        g = g with { Snapshot = current };
        if (g.Once && g.Status == StandardStatus.Settled && (need != Finding.Unmet || openWork))
        {
            return g;
        }
        if (need == Finding.Unclear)
        {
            return g with { Status = g.Status == StandardStatus.Settled ? StandardStatus.Open : g.Status, Finding = need };
        }
        if (need == Finding.Unmet && (g.RecoveryObserved || (!g.Once && g.Finding == Finding.Met)) && !openWork)
        {
            DomainException.Require(g.Episode != ulong.MaxValue, "standard episode exhausted");
            g = g with { Episode = g.Episode + 1, RecoveryObserved = false };
        }
        g = g with { Finding = need };
        return need == Finding.Met && !openWork
            ? g with { Status = StandardStatus.Settled, RecoveryObserved = true }
            : g with { Status = StandardStatus.Open };
    }

    /// <summary>Whether any of the progress values is still open work.</summary>
    public static bool WorkOpen(IEnumerable<Progress> progress)
    {
        foreach (var p in progress)
        {
            var v = p.View;
            if (v.Unresolved || v.Stage is Stage.Pending or Stage.Prepared or Stage.Dispatched or Stage.AwaitingObservation)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Binds a selected method to its original Episode and executable plan. Renewed deficits get a new Episode;
/// the old plan remains available for readback.
/// </summary>
public sealed record Method(StandardId Owner, ulong Episode, MethodId MethodId, PlanId Plan)
{
    public void Validate() => DomainException.Require(Owner.IsValid && MethodId.IsValid && Plan.IsValid, "invalid method");
}
