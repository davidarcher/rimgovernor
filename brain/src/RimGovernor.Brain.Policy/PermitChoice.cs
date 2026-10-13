using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// MaintainPermits spends permit points. A colonist with permit points takes the permit that is worth most to
// this colony: aid first, then trade and drop-pod access, then psycast permits (more so once a colonist is a
// psycaster). The choice is a pure ranking over the royalty read; the goal commits it as a choose_permit pawn
// setting, and the plan recorded on the goal (in the save with the goal) is the persisted intent.

public static partial class PolicyConstants
{
    public static readonly ConcernId MaintainPermits = new("MaintainPermits");

    // The worker classes of the permits that act (RoyalTitlePermitDef.workerClass, carried by the royalty
    // read): what the permit does for the colony.
    internal const string PermitWorkerCallAid = "RoyalTitlePermitWorker_CallAid";
    internal const string PermitWorkerCallLaborers = "RoyalTitlePermitWorker_CallLaborers";
    internal const string PermitWorkerCallShuttle = "RoyalTitlePermitWorker_CallShuttle";
    internal const string PermitWorkerDropResources = "RoyalTitlePermitWorker_DropResources";
    internal const string PermitWorkerOrbitalStrike = "RoyalTitlePermitWorker_OrbitalStrike";
}

/// <summary>Groups permits by what they give the colony.</summary>
public enum PermitCategory
{
    [WireName("")]
    None = 0,
    Aid,
    Trade,
    DropPod,
    Psycast,
    Other,
}

/// <summary>Names why a candidate cannot be taken now, or None when it can.</summary>
public enum PermitReason
{
    [WireName("")]
    None = 0,

    /// <summary>The permit's point cost or the holder's points or title are unread.</summary>
    Unknown,

    /// <summary>The holder's title is below the permit's minimum.</summary>
    TitleShort,

    /// <summary>The holder lacks the permit points.</summary>
    PointsShort,
}

/// <summary>
/// One candidate permit for one colonist's holding. Value is the colony's ranking score and Cost the permit
/// points it takes; Take is set when the permit is eligible and affordable now, otherwise Reason says why not.
/// </summary>
public sealed record PermitChoice
{
    public PawnId Holder { get; init; }

    public string Faction { get; init; } = "";

    public string Permit { get; init; } = "";

    public PermitCategory Category { get; init; }

    public int Value { get; init; }

    public int Cost { get; init; }

    public bool Take { get; init; }

    public PermitReason Reason { get; init; }

    /// <summary>The goal intent for a choice.</summary>
    public PermitIntent Intent() => new() { Holder = Holder, Faction = Faction, Permit = Permit };
}

/// <summary>The goal intent a chosen permit records in the save.</summary>
public sealed record PermitIntent
{
    public PawnId Holder { get; init; }

    public string Faction { get; init; } = "";

    public string Permit { get; init; } = "";

    /// <summary>The write that carries the intent: the colonist chooses the permit with the faction. Throws when the intent is invalid.</summary>
    public PawnSettings Setting() => PawnSettings.NewChoosePermitSetting(new Domain.PawnId(Holder.Value), Faction, Permit);
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Classifies a permit by its worker class. A permit with no worker of its own (the passive trade
    /// permits) or a read that carries none falls back to its def name.
    /// </summary>
    internal static PermitCategory PermitCategoryOf(RoyalPermit p)
    {
        switch (p.Worker)
        {
            case PolicyConstants.PermitWorkerCallAid:
            case PolicyConstants.PermitWorkerCallLaborers:
                return PermitCategory.Aid;
            case PolicyConstants.PermitWorkerCallShuttle:
            case PolicyConstants.PermitWorkerDropResources:
                return PermitCategory.DropPod;
            case PolicyConstants.PermitWorkerOrbitalStrike:
                return PermitCategory.Other;
        }
        var name = p.Name;
        if (name.Contains("Psy", StringComparison.Ordinal))
        {
            return PermitCategory.Psycast;
        }
        if (name.Contains("Aid", StringComparison.Ordinal) || name.Contains("Laborer", StringComparison.Ordinal))
        {
            return PermitCategory.Aid;
        }
        if (name.Contains("Trade", StringComparison.Ordinal))
        {
            return PermitCategory.Trade;
        }
        if (name.Contains("Drop", StringComparison.Ordinal) || name.Contains("Shuttle", StringComparison.Ordinal)
            || name.Contains("Transport", StringComparison.Ordinal))
        {
            return PermitCategory.DropPod;
        }
        return PermitCategory.Other;
    }

    /// <summary>Scores a category for this colony: psycast permits outrank trade once any colonist is a psycaster.</summary>
    internal static int PermitValue(PermitCategory category, bool psycaster) => category switch
    {
        PermitCategory.Aid => 40,
        PermitCategory.Trade => 30,
        PermitCategory.DropPod => 20,
        PermitCategory.Psycast => psycaster ? 35 : 10,
        _ => 0,
    };

    private static (int Seniority, bool Known) TitleSeniority(IReadOnlyList<RoyalRung> ladder, string title)
    {
        foreach (var rung in ladder)
        {
            if (rung.Title == title)
            {
                var known = rung.Seniority.TryGetValue(out var value);
                return (value, known);
            }
        }
        return (0, false);
    }

    /// <summary>
    /// Lists every untaken permit for every titled colonist holding, best first: takeable before blocked,
    /// then by value, then cheaper, then by holder and name. A holding without a title cannot take permits.
    /// </summary>
    public static IReadOnlyList<PermitChoice> RankPermits(RoyaltyFacts f)
    {
        var psycaster = f.Psycasts.Values.Any(casts => casts.Count > 0);
        var names = f.Permits.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var result = new List<PermitChoice>();
        foreach (var holder in SortedHolders(f.Holders))
        {
            foreach (var h in f.Holders[holder])
            {
                if (h.Title.Length == 0)
                {
                    continue;
                }
                var held = new HashSet<string>(h.Permits, StringComparer.Ordinal);
                var (rank, rankKnown) = TitleSeniority(f.Ladder, h.Title);
                var pointsKnown = h.PermitPoints.TryGetValue(out var points);
                foreach (var name in names)
                {
                    if (held.Contains(name))
                    {
                        continue;
                    }
                    var permit = f.Permits[name];
                    var category = PermitCategoryOf(permit);
                    var costKnown = permit.PermitPoints.TryGetValue(out var cost);
                    var (need, needKnown) = (0, true);
                    if (permit.MinTitle.TryGetValue(out var min) && min.Length != 0)
                    {
                        (need, needKnown) = TitleSeniority(f.Ladder, min);
                    }
                    var take = false;
                    var reason = PermitReason.None;
                    if (!costKnown || !pointsKnown || !rankKnown || !needKnown)
                    {
                        reason = PermitReason.Unknown;
                    }
                    else if (rank < need)
                    {
                        reason = PermitReason.TitleShort;
                    }
                    else if (points < cost)
                    {
                        reason = PermitReason.PointsShort;
                    }
                    else
                    {
                        take = true;
                    }
                    result.Add(new PermitChoice
                    {
                        Holder = holder,
                        Faction = h.FactionDef,
                        Permit = name,
                        Category = category,
                        Value = PermitValue(category, psycaster),
                        Cost = cost,
                        Take = take,
                        Reason = reason,
                    });
                }
            }
        }
        return [.. result
            .OrderByDescending(c => c.Take)
            .ThenByDescending(c => c.Value)
            .ThenBy(c => c.Cost)
            .ThenBy(c => c.Holder)
            .ThenBy(c => c.Permit, StringComparer.Ordinal)];
    }

    /// <summary>The best permit to take now, null when none is takeable or the best takeable one is worth nothing to the colony.</summary>
    public static PermitChoice? NextPermit(RoyaltyFacts f)
    {
        var ranked = RankPermits(f);
        if (ranked.Count == 0 || !ranked[0].Take || ranked[0].Value <= 0)
        {
            return null;
        }
        return ranked[0];
    }

    /// <summary>NextPermit of a royalty read that may be unknown.</summary>
    public static PermitChoice? NextPermitOf(Fact<RoyaltyFacts> royalty) =>
        royalty.TryGetValue(out var f) ? NextPermit(f) : null;

    /// <summary>Measures MaintainPermits: true once no worthwhile permit is takeable now, unknown without the royalty read (nothing is raised then).</summary>
    public static Fact<bool> PermitsSpent(Fact<RoyaltyFacts> royalty)
    {
        if (!royalty.IsKnown)
        {
            return Unknown<bool>();
        }
        return Known(NextPermitOf(royalty) is null);
    }
}
