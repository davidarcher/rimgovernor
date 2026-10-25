namespace RimGovernor.Brain.Policy;

// Construction helpers: a pawn under the Construction floor (4) is not a constructor, but while suitable
// construction is ready and unmet and the pawn has been idle across two reviews, the roster planner enables
// Construction for it at the lowest rank (4 under manual priorities) beside the skilled owners, who stay first.
//
// Enforcement boundary: a work-type priority enables every native construction job, not one chosen wall.
// Native RimWorld still refuses a frame whose constructionSkillPrerequisite the pawn lacks. Quality sites with
// an observed finishing minimum also enforce that minimum natively. Helper safety is derived from observed
// facts per definition, with no name list: a definition is helper work when its quality sensitivity is
// observed false and its skill prerequisite is observed (the helper must reach it), and a quality-sensitive
// definition is protected when its sites carry an observed finishing minimum, which native enforces per target
// (helpers then neither count as demand nor constrain the floor). A candidate without a placed site, or a quality site without a finishing policy, withholds helpers. A new
// unconfigured site between reviews still follows vanilla rules until the brain adopts it on a later review.

/// <summary>The planner's helper input (WorkDemand.Help).</summary>
public sealed record ConstructionHelp
{
    public Tick Tick { get; init; }

    /// <summary>The highest observed skill prerequisite among the quality-free definitions in play; a helper must reach it.</summary>
    public int Floor { get; init; }

    /// <summary>The runnable helper-eligible construction parallelism from the complete census.</summary>
    public int Ready { get; init; }

    /// <summary>The candidates without a placed building site and the sites without a finishing policy.</summary>
    public EquatableArray<string> Withheld { get; init; }

    /// <summary>The highest-ranked fact among the withheld definitions.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The last review's record in the same world, empty when none.</summary>
    public ConstructionHelpRecord Previous { get; init; } = new();
}

/// <summary>
/// What the planner decided and why; the roster report carries it so the next review's hysteresis and the
/// dossier read the same row.
/// </summary>
public sealed record ConstructionHelpRecord
{
    public Tick Tick { get; init; }

    /// <summary>The pawns seen idle this review (spare capacity).</summary>
    public EquatableArray<PawnId> Idle { get; init; }

    /// <summary>The pawns enabled for Construction below its floor.</summary>
    public EquatableArray<PawnId> Helpers { get; init; }

    /// <summary>The last tick unmet suitable demand held.</summary>
    public Tick DemandTick { get; init; }

    public int Ready { get; init; }

    public int Unmet { get; init; }

    public string Reason { get; init; } = "";

    public EquatableArray<string> Withheld { get; init; }
}

public static class ConstructionHelpersRules
{
    /// <summary>Helpers stay one game hour after suitable demand disappears, so a review between two walls does not rewrite work settings.</summary>
    public const long ConstructionHelpHoldTicks = GameTime.TicksPerHour;

    // Helper reasons recorded on ConstructionHelpRecord.Reason.
    public const string HelpAssigned = "helping";
    public const string HelpHeld = "held_after_demand";
    public const string HelpNoDemand = "no_unmet_suitable_construction";
    public const string HelpNoSpare = "no_sustained_idle_pawn";

    // Withholding reasons name the stage or native site state.
    public const string HelpStageNotBuilding = "construction_stage_not_building";
    public const string HelpSiteNotPlaced = "construction_site_not_placed";
    public const string HelpQualityUnprotected = "construction_quality_unprotected";

    /// <summary>Orders withholding reasons when several hold.</summary>
    private static int HelpWithholdRank(string reason) => reason switch
    {
        HelpStageNotBuilding => 1,
        HelpSiteNotPlaced => 2,
        HelpQualityUnprotected => 3,
        _ => 0,
    };

    private enum HelpQuality
    {
        Free,
        Protected,
        Unprotected,
    }

    /// <summary>
    /// Reads the ready-work report and the native construction census for helper demand. Each building
    /// definition in play (an observed site, an open plan or a construction candidate) is classified from
    /// observed facts: quality-free definitions with an observed prerequisite are helper work, their runnable
    /// candidates count their parallelism and their highest prerequisite becomes the helper floor; protected
    /// quality definitions are left to native per-target enforcement; anything else is withheld under the
    /// reason naming the missing fact. A previous report from another world contributes no actions;
    /// the current census remains authoritative.
    /// </summary>
    public static ConstructionHelp ConstructionHelpDemand(
        ReadyWorkReport? report,
        GenerationSnapshot current,
        Tick tick,
        ConstructionHelpRecord? previous,
        CurrentConstruction? census)
    {
        var help = new ConstructionHelp { Tick = tick };
        report = ConstructionSkillRules.ConstructionHelperView(report, census, current);
        if (previous is not null && previous.Tick <= tick)
        {
            help = help with { Previous = previous };
        }
        var withheld = new Dictionary<string, string>(StringComparer.Ordinal);
        void Withhold(string name, string reason)
        {
            var rank = HelpWithholdRank(withheld.GetValueOrDefault(name, ""));
            if (rank == 0 || HelpWithholdRank(reason) < rank)
            {
                withheld[name] = reason;
            }
        }
        // Quality per definition: a definition with no observed site has no native quality evidence.
        var quality = new Dictionary<string, HelpQuality>(StringComparer.Ordinal);
        var siteSkill = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in census!.Sites)
        {
            var name = site.Building!.Definition;
            var q = !site.QualitySensitive ? HelpQuality.Free
                : site.Minimum is not null ? HelpQuality.Protected
                : HelpQuality.Unprotected;
            quality[name] = (HelpQuality)Math.Max((int)quality.GetValueOrDefault(name), (int)q);
            siteSkill[name] = Math.Max(siteSkill.GetValueOrDefault(name), site.NativeFinishingSkill);
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in quality.Keys)
        {
            names.Add(name);
        }
        var candidateDefs = new Dictionary<string, string>(StringComparer.Ordinal); // stage -> definition
        foreach (var c in report.Candidates)
        {
            if (!c.Work.Contains(WorkType.Construction))
            {
                continue;
            }
            const string Prefix = "building:";
            if (!c.Stage.StartsWith(Prefix, StringComparison.Ordinal))
            {
                Withhold(c.Stage, HelpStageNotBuilding);
                continue;
            }
            var def = c.Stage[Prefix.Length..];
            candidateDefs[c.Stage] = def;
            names.Add(def);
        }
        var helperWork = new HashSet<string>(StringComparer.Ordinal);
        var floor = 0;
        foreach (var name in names)
        {
            if (withheld.GetValueOrDefault(name, "").Length != 0)
            {
                continue;
            }
            if (!quality.TryGetValue(name, out var q))
            {
                Withhold(name, HelpSiteNotPlaced);
            }
            else if (q == HelpQuality.Unprotected)
            {
                Withhold(name, HelpQualityUnprotected);
            }
            else if (q == HelpQuality.Free)
            {
                var skill = siteSkill[name];
                helperWork.Add(name);
                floor = Math.Max(floor, skill);
            }
        }
        help = help with { Floor = floor };
        var ready = 0;
        foreach (var c in report.Candidates)
        {
            if (candidateDefs.TryGetValue(c.Stage, out var def) && helperWork.Contains(def) && c.State == ReadyState.Runnable)
            {
                ready += c.Parallelism;
            }
        }
        help = help with { Ready = ready };
        var reason = "";
        foreach (var (_, why) in withheld)
        {
            if (reason.Length == 0 || HelpWithholdRank(why) < HelpWithholdRank(reason))
            {
                reason = why;
            }
        }
        var withheldNames = withheld.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        return help with { Withheld = EquatableArray.From(withheldNames), Reason = reason };
    }

    /// <summary>
    /// Picks helpers among sub-floor pawns: able at the native floor (requirement minimum, else 0), not an
    /// owner, no player override, idle now and idle (or helping) at the previous review. Unmet demand is ready
    /// work beyond the owners not held by other work; previous helpers are kept first and through the hold
    /// after demand clears.
    /// </summary>
    internal static ConstructionHelpRecord PlanConstructionHelp(
        ConstructionHelp h,
        IReadOnlyList<WorkWorker> workers,
        IReadOnlyList<WorkWorker> owners,
        Func<WorkWorker, bool> eligible)
    {
        var rec = new ConstructionHelpRecord { Tick = h.Tick, Withheld = h.Withheld };
        // An owner a giver of another type holds builds nothing this review.
        var free = 0;
        foreach (var o in owners)
        {
            var job = o.Pawn.Job;
            if (IdleJob(job) || job.Work == WorkType.None || job.Work == WorkType.Construction)
            {
                free++;
            }
        }
        var idle = new HashSet<PawnId>();
        var idleList = new List<PawnId>();
        foreach (var w in workers)
        {
            if (IdleJob(w.Pawn.Job))
            {
                idle.Add(w.Pawn.Id);
                idleList.Add(w.Pawn.Id);
            }
        }
        rec = rec with { Idle = EquatableArray.From(idleList) };
        var ready = h.Ready;
        if (h.Withheld.Count > 0)
        {
            return rec with { Reason = h.Reason };
        }
        var unmet = Math.Max(ready - free, 0);
        rec = rec with { Ready = ready, Unmet = unmet };
        var was = new HashSet<PawnId>(h.Previous.Helpers);
        var wasIdle = new HashSet<PawnId>(h.Previous.Idle);
        var want = unmet;
        string reason;
        if (want > 0)
        {
            reason = HelpAssigned;
            rec = rec with { DemandTick = h.Tick };
        }
        else if (h.Previous.Helpers.Count > 0 && h.Previous.DemandTick.Value > 0 && h.Tick.Value - h.Previous.DemandTick.Value < ConstructionHelpHoldTicks)
        {
            reason = HelpHeld;
            rec = rec with { DemandTick = h.Previous.DemandTick };
            want = h.Previous.Helpers.Count;
        }
        else
        {
            return rec with { Reason = HelpNoDemand };
        }
        var pool = new List<WorkWorker>();
        foreach (var w in workers)
        {
            if (!eligible(w))
            {
                continue;
            }
            // A helper keeps helping while eligible; a new one needs spare capacity now and at the previous review.
            var id = w.Pawn.Id;
            if ((was.Contains(id) && (reason == HelpHeld || idle.Contains(id) || BusyBuilding(w)))
                || (idle.Contains(id) && wasIdle.Contains(id) && reason == HelpAssigned))
            {
                pool.Add(w);
            }
        }
        static int Level(WorkWorker w) => w.Profile.SkillFor(WorkType.Construction).Level;
        var ordered = pool
            .OrderByDescending(w => was.Contains(w.Pawn.Id))
            .ThenByDescending(Level)
            .ThenBy(w => w.Pawn.Id)
            .Take(want)
            .Select(w => w.Pawn.Id)
            .Order()
            .ToList();
        if (ordered.Count == 0)
        {
            reason = HelpNoSpare;
        }
        return rec with { Helpers = EquatableArray.From(ordered), Reason = reason };
    }

    internal static bool BusyBuilding(WorkWorker w) => w.Pawn.Job.Work == WorkType.Construction;

    /// <summary>Reports a pawn job that holds the pawn from no work: none, waiting or wandering, or a preemptible
    /// vanilla job any coupled order may replace (#3670).</summary>
    internal static bool IdleJob(PawnJob job) =>
        job.Preemptible || job.Def is "" or "Wait" or "Wait_Wander" or "GotoWander" or "Wait_MaintainPosture";
}
