namespace RimGovernor.Brain.Policy;

/// <summary>One pawn taking one role precept. Role is the role precept's id (the thing Assign targets); Def its def.</summary>
public sealed record RoleAssignment(PawnId Pawn, string Role, string Def);

/// <summary>What the journal commits one pawn to outside its live work rows.</summary>
public abstract record PawnCommitment
{
    private protected PawnCommitment()
    {
    }

    /// <summary>The work types of the pawn's live orders and pending priority writes.</summary>
    public sealed record Work(EquatableArray<WorkType> Types) : PawnCommitment;

    /// <summary>A step of the pawn's orders names a work giver the catalog lacks, so its work is not listed: no role that disables work is given to the pawn.</summary>
    public sealed record GiverMissing : PawnCommitment;
}

/// <summary>What the journal commits the pawns to: each pawn's work, or that the action census was cut off.</summary>
public abstract record WorkCommitments
{
    private protected WorkCommitments()
    {
    }

    /// <summary>The census of live actions is complete: each pawn it lists, by pawn id; a pawn it does not list has no commitment.</summary>
    public sealed record Listed(IReadOnlyDictionary<PawnId, PawnCommitment> ByPawn) : WorkCommitments;

    /// <summary>The census was cut off, so no pawn's commitments are listed: no role that disables work is given to anyone.</summary>
    public sealed record Truncated : WorkCommitments;
}

public static class IdeologyRolesRules
{
    /// <summary>
    /// Scores pawn for def: the sum, over the def's skill requirements, of the best level among the skills
    /// of that requirement. Ok is false when the pawn fails a skill requirement (no listed skill is enabled
    /// and at the minimum). Requirements without skills are the game's to judge when the assignment applies.
    /// </summary>
    internal static (int Score, bool Ok) RoleFit(RoleDef def, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Skill> skills)
    {
        var score = 0;
        foreach (var requirement in def.Requirements)
        {
            if (requirement.Skills.Count == 0)
            {
                continue;
            }
            int best = 0;
            var met = false;
            foreach (var need in requirement.Skills)
            {
                foreach (var s in skills)
                {
                    if (s.DefName == need.Skill && !s.Disabled && s.Level >= need.MinLevel)
                    {
                        met = true;
                        best = Math.Max(best, s.Level);
                    }
                }
            }
            if (!met)
            {
                return (0, false);
            }
            score += best;
        }
        return (score, true);
    }

    /// <summary>
    /// The work each pawn is committed to outside its live work rows (#3644): the work types of its live coupled orders
    /// (a step's work giver's type, Hauling for a delivery, a supply or an output delivery) and of the nonzero rows of a
    /// pending work-priority write. <paramref name="giverWork"/> gives a work giver's work type (None for a giver with
    /// none), null when the catalog lacks the giver: the pawn is then <see cref="PawnCommitment.GiverMissing"/>. A released
    /// order and an absent action (<see cref="WorkRules.ActionAbsent"/>) own nothing; a truncated census is
    /// <see cref="WorkCommitments.Truncated"/>.
    /// </summary>
    public static WorkCommitments SelectedWork(WorkActionCensus activities,
        IEnumerable<WorkAssignment> pending, Func<string, WorkType?> giverWork)
    {
        if (activities.Truncated)
        {
            return new WorkCommitments.Truncated();
        }
        var work = new Dictionary<PawnId, SortedSet<WorkType>>();
        var unknown = new HashSet<PawnId>();
        SortedSet<WorkType> Of(PawnId pawn) => work.TryGetValue(pawn, out var set) ? set : work[pawn] = [];
        void Step(PawnId pawn, PawnActivityStep step)
        {
            if (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply)
            {
                Of(pawn).Add(WorkType.Hauling);
            }
            if (step.WorkGiver.Length == 0)
            {
                return;
            }
            if (giverWork(step.WorkGiver) is { } type)
            {
                if (type != WorkType.None)
                {
                    Of(pawn).Add(type);
                }
            }
            else
            {
                unknown.Add(pawn);
            }
        }
        foreach (var row in activities.Actions)
        {
            if (row.Action.PawnActivities is not { } batch || WorkRules.ActionAbsent(row.Progress))
            {
                continue;
            }
            foreach (var order in batch.Orders.Where(o => !o.Release))
            {
                var pawn = new PawnId(order.Pawn);
                foreach (var step in order.Steps)
                {
                    Step(pawn, step);
                }
                if (order.OutputDelivery is not null)
                {
                    Of(pawn).Add(WorkType.Hauling);
                }
            }
            foreach (var task in batch.Tasks)
            {
                var pawn = new PawnId(task.Pawn);
                foreach (var step in task.PriorSteps.Append(task.Step))
                {
                    Step(pawn, step);
                }
            }
        }
        foreach (var assignment in pending)
        {
            foreach (var setting in assignment.Settings.Where(s => s.Priority > 0))
            {
                Of(assignment.Pawn).Add(Wire.TryParse<WorkType>(setting.Definition, out var type) ? type : WorkType.Unrecognized);
            }
        }
        return new WorkCommitments.Listed(work.Keys.Concat(unknown).Distinct().ToDictionary(pawn => pawn,
            pawn => unknown.Contains(pawn) ? (PawnCommitment)new PawnCommitment.GiverMissing() : new PawnCommitment.Work(EquatableArray.From(work[pawn].ToArray()))));
    }

    /// <summary>
    /// The assignments the ideoligion owes now: for each active role with fewer holders than its def's
    /// MaxCount, the best-fitting available believer who holds no role, by fit, then certainty, then id.
    /// The pawn row must show the ideoligion's id and no role; its skills must be read. A role that disables
    /// work (<see cref="RoleDef.DisabledWork"/>) skips a pawn whose enabled priority-1 rows or
    /// <paramref name="selected"/> work (<see cref="SelectedWork"/>) it would disable, or whose work is not listed
    /// (<see cref="PawnCommitment.GiverMissing"/>, <see cref="WorkCommitments.Truncated"/>; #3644).
    /// A role whose def is missing from the catalog fails the
    /// decode, so none reaches here. At most one pawn is chosen per role and per call, so a role with several
    /// places fills over successive calls as the section shows each holder. Sorted by role id.
    /// </summary>
    public static EquatableArray<RoleAssignment> RoleAssignments(Ideoligion ideo, EquatableArray<WorkPawn> rows, WorkCommitments selected)
    {
        var pool = new List<(PawnId Id, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Skill> Skills, double Certainty, HashSet<WorkType>? Work)>();
        foreach (var row in rows)
        {
            if (row.Available && row.PolicyInputs.Ideo is PawnIdeo.Member { Role: null } member && member.IdeoId == ideo.Facts.IdeoId)
            {
                var certainty = member.Certainty;
                HashSet<WorkType>? work = null;
                var commitment = selected is WorkCommitments.Listed listed
                    ? listed.ByPawn.TryGetValue(row.Id, out var found) ? found : new PawnCommitment.Work([])
                    : null;
                if (commitment is PawnCommitment.Work committed)
                {
                    work = [.. row.Work.Priorities().Where(p => p.Priority == 1 && !p.Disabled).Select(p => p.Work), .. committed.Types];
                }
                pool.Add((row.Id, row.Skills, certainty, work));
            }
        }
        var roles = ideo.Facts.Roles.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var output = new List<RoleAssignment>();
        var taken = new HashSet<PawnId>();
        foreach (var role in roles)
        {
            if (!ideo.Defs.Roles.TryGetValue(role.DefName, out var def) || !role.Active || role.Pawns.Count >= def.MaxCount)
            {
                continue;
            }
            (PawnId Id, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Skill> Skills, double Certainty, HashSet<WorkType>? Work) best = default;
            var bestScore = 0;
            var found = false;
            foreach (var c in pool)
            {
                if (taken.Contains(c.Id) || def.DisabledWork.Count > 0 && (c.Work is null || c.Work.Overlaps(def.DisabledWork)))
                {
                    continue;
                }
                var (score, fits) = RoleFit(def, c.Skills);
                if (!fits)
                {
                    continue;
                }
                if (!found || score > bestScore || (score == bestScore && (c.Certainty > best.Certainty || (c.Certainty == best.Certainty && c.Id < best.Id))))
                {
                    best = c;
                    bestScore = score;
                    found = true;
                }
            }
            if (found)
            {
                taken.Add(best.Id);
                output.Add(new RoleAssignment(best.Id, role.Id, role.DefName));
            }
        }
        return EquatableArray.From(output);
    }
}
