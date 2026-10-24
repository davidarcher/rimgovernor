namespace RimGovernor.Brain.Policy;

// Replan hysteresis for unbuilt rooms (#3740, D1 of #3738). A replan's embedding is searched afresh, so an unbuilt
// room of the held plan would move whenever the search lands anywhere else, however little it gains, and its
// reconcile history (keyed by room id) would be orphaned. The held arrangement is therefore re-evaluated over the same
// survey and facts: the standing rooms (and the best run's relocation, if any) stay, each group of a held unbuilt
// room's match key and shape stands on that room's site where it still fits, and the groups no held room carries (a
// new role, a changed count or size) are placed and searched around them. The new embedding replaces it only when it
// leaves less undone, or as much and scores PlanWeights.ReplanGain better. Built and started rooms move only at the
// switching price (SwitchThreshold, LayoutBuilt.cs).

/// <summary>
/// The new embedding against the held plan's unbuilt rooms (#3740). Verdict is ReplanKept (the held arrangement
/// stands) or ReplanReplaced; Reason says why. Held and Tiled are the two arrangements' objectives (cost, lower is
/// better, the relocation price included); Rooms counts the groups the held arrangement stood on a held room's site.
/// </summary>
public sealed record EmbedReplan
{
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public double Held { get; init; }
    public double Tiled { get; init; }
    public int Rooms { get; init; }
}

public static partial class LayoutEmbedRules
{
    /// <summary>Verdict: the held arrangement of the unbuilt rooms stands.</summary>
    public const string ReplanKept = "kept";

    /// <summary>Verdict: the new embedding replaces the held arrangement.</summary>
    public const string ReplanReplaced = "replanned";

    /// <summary>The new embedding gains less than ReplanGain over the held arrangement.</summary>
    public const string ReasonReplanGainBelow = "replan_gain_below";

    /// <summary>The held arrangement leaves fewer rooms or required links undone than the new embedding.</summary>
    public const string ReasonReplanHeldFailsFewer = "replan_held_fails_fewer";

    /// <summary>The new embedding gains at least ReplanGain over the held arrangement.</summary>
    public const string ReasonReplanGainAbove = "replan_gain_above";

    /// <summary>The held arrangement leaves more rooms or required links undone than the new embedding.</summary>
    public const string ReasonReplanHeldFailsMore = "replan_held_fails_more";
}

internal sealed partial class Embedder
{
    /// <summary>
    /// The held plan's unbuilt room (of prior) whose interior group i's block r is, by the group's match key, none of
    /// taken (which it joins); 0 for none.
    /// </summary>
    internal int PriorAt(int i, Rectangle r, IReadOnlyList<BuiltRegion> prior, HashSet<int> taken)
    {
        if (i >= _nRooms || prior.Count == 0)
        {
            return 0;
        }
        var key = LayoutStationPlanRules.MatchKeyOfGroup(_groups[i]);
        foreach (var p in prior)
        {
            if (!taken.Contains(p.Room) && p.Region.IsRect() && p.Region.Bounds() == r && p.MatchKey() == key)
            {
                taken.Add(p.Room);
                return p.Room;
            }
        }
        return 0;
    }

    /// <summary>
    /// Stands each free group on a held room's site, in group order: the lowest id of prior with the group's match key
    /// and one of its shapes that still fits beside what is placed. blocks and done are changed in place; the count of
    /// groups stood is returned.
    /// </summary>
    internal int StandPrior(Rectangle[] blocks, bool[] done, IReadOnlyList<BuiltRegion> prior)
    {
        var taken = new HashSet<int>();
        var stood = 0;
        var ordered = prior.OrderBy(p => p.Room).ToList();
        for (var i = 0; i < _nRooms; i++)
        {
            if (done[i])
            {
                continue;
            }
            var key = LayoutStationPlanRules.MatchKeyOfGroup(_groups[i]);
            foreach (var p in ordered)
            {
                if (taken.Contains(p.Room) || !p.Region.IsRect() || p.MatchKey() != key)
                {
                    continue;
                }
                var r = p.Region.Bounds();
                if (!_shapes[i].Contains((r.Width, r.Height)) || !Fits(i, r, blocks, _sky[i]))
                {
                    continue;
                }
                blocks[i] = r;
                done[i] = true;
                taken.Add(p.Room);
                stood++;
                break;
            }
        }
        return stood;
    }
}

internal sealed partial class EmbedRun
{
    /// <summary>
    /// The held arrangement of prior, the held plan's unbuilt rooms, against this run, the new embedding (#3740): at
    /// this run's level and seed, the standing rooms and this run's relocation fixed, every group a held room fits
    /// stood on it (StandPrior) and the rest placed and searched around them. The held arrangement is kept unless this
    /// run leaves less undone, or as much and its objective is ReplanGain lower. Returns this run and null when no group
    /// stands on a held room.
    /// </summary>
    internal (EmbedRun Run, EmbedReplan? Replan) HoldPrior(IReadOnlyList<BuiltRegion> prior)
    {
        if (prior.Count == 0 || E.Core.Count == 0)
        {
            return (this, null);
        }
        var e = E;
        var (blocks, done) = e.HomeStart();
        if (Built.Moved >= 0)
        {
            blocks[Built.Moved] = Blocks[Built.Moved];
        }
        var stood = e.StandPrior(blocks, done, prior);
        if (stood == 0)
        {
            return (this, null);
        }
        e.Greedy(blocks, done, Seed);
        e.Search(blocks, Seed, LayoutSiteGroundRules.SiteSearchIters, done);
        var held = new EmbedRun { E = e, Level = Level, Seed = Seed, Blocks = blocks, Built = Built };
        for (var k = 0; k < e.NRooms; k++)
        {
            if (blocks[k].Width == 0)
            {
                held.Unplaced++;
            }
        }
        held.Unmet = e.UnmetLinks(blocks);
        held.Obj = e.Objective(blocks) + Built.Price;
        var replan = new EmbedReplan { Held = held.Obj, Tiled = Obj, Rooms = stood };
        if (held.Failed() != Failed())
        {
            return held.Failed() < Failed()
                ? (held, replan with { Verdict = LayoutEmbedRules.ReplanKept, Reason = LayoutEmbedRules.ReasonReplanHeldFailsFewer })
                : (this, replan with { Verdict = LayoutEmbedRules.ReplanReplaced, Reason = LayoutEmbedRules.ReasonReplanHeldFailsMore });
        }
        return held.Obj - Obj < PlanWeights.ReplanGain
            ? (held, replan with { Verdict = LayoutEmbedRules.ReplanKept, Reason = LayoutEmbedRules.ReasonReplanGainBelow })
            : (this, replan with { Verdict = LayoutEmbedRules.ReplanReplaced, Reason = LayoutEmbedRules.ReasonReplanGainAbove });
    }
}
