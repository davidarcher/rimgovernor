using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// The practice sessions the planner may schedule (#3341, #3813): a range session for each pawn the practice eligibility rule
/// admits for Shooting and a sparring bout for each pair admitted for Melee, at an unclaimed target, whatever role they hold
/// (colony defense is every capable pawn's). A session competes with other work on value: the skill XP it applies, and the
/// maintenance_xp tiebreak of an Ideology role's holder. The role's skill requirement itself is not modelled (a named finding).
/// </summary>
internal sealed record PracticeMethodCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastCommitment[] Roots,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings,
    EquatableArray<ForecastPracticeTask> Practice, EquatableArray<ForecastMaintenanceRequirement> Requirements,
    EquatableArray<string> Findings, SparringTrainees? Trainees)
{
    internal static PracticeMethodCandidates Generate(RoundsFacts facts, PracticeActivityRead read, double horizon, EquatableArray<AssignedCapability> assigned, bool admitted)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var roots = new List<ForecastCommitment>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>(); var practice = new List<ForecastPracticeTask>();
        var requirements = new List<ForecastMaintenanceRequirement>(); var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        if (!read.Known) findings.Add("practice_observation_unknown");
        if (!facts.Ideology.TryGetValue(out var ideology)) findings.Add("assigned_capability_floor_unknown");
        else
            foreach (var role in ideology.Facts.Roles.Where(r => r.Active && r.Pawns.Count == 0))
                findings.Add("acquisition_requires_assignment:" + role.Id);
        var pawns = facts.ForecastObserved.WorkPawns.TryGetValue(out var rows) ? rows : [];
        foreach (var requirement in assigned)
        {
            findings.Add("ideology_role_skill_requirement_not_modelled:" + requirement.Id);
            var pawn = pawns.FirstOrDefault(p => p.Id == requirement.Pawn);
            if (pawn is null || !pawn.Skills.TryGetValue(out var skills)) { findings.Add("maintenance_skills_unknown:" + requirement.Id); continue; }
            requirements.Add(new(requirement, EquatableArray.From(skills)));
        }
        if (!admitted) findings.Add("practice_held_essential_emergency_or_unknown");
        var eligible = read.Workers.Where(w => admitted && w.Skill is "Shooting" or "Melee"
            && !facts.WorkView!.Claims.Pawns.Contains(w.Pawn) && !facts.WorkView.Claims.Targets.Contains(w.Target)
            && w.DurationTicks.TryGetValue(out var duration) && duration > 0
            && w.ApproachTicks.IsKnown && w.CallbackIntervalTicks.IsKnown && w.RawXpPerCallback.IsKnown
            && w.Learning.PracticeEligible == Known(true)).Take(32).ToArray();
        foreach (var worker in eligible.Where(w => w.Skill == "Shooting"))
        {
            var id = "practice/" + worker.Pawn + "/" + worker.Target;
            Add(id, worker, null, 0);
            roots.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "practice/" + worker.Pawn });
        }
        var melee = eligible.Where(w => w.Skill == "Melee").ToArray();
        foreach (var first in melee)
        foreach (var second in melee.Where(w => string.CompareOrdinal(first.Pawn, w.Pawn) < 0 && w.Target != first.Target))
        {
            if (tasks.Count >= 64) { findings.Add("practice_generation_truncated"); break; }
            var id = "practice/pair/" + first.Pawn + "/" + second.Pawn + "/" + first.Target + "/" + second.Target;
            var start = Math.Max(first.ApproachTicks.Value, second.ApproachTicks.Value);
            Add(id + "/a", first, second, start);
            Add(id + "/b", second, first, start);
            roots.Add(new(id, []) { ActivityCompletions = [id + "/a", id + "/b"] });
        }
        var trainees = TraineesOf(facts, read, findings);
        return new(tasks.ToArray(), options.ToArray(), routes.ToArray(), roots.ToArray(), bindings,
            EquatableArray.From(practice), EquatableArray.From(requirements), EquatableArray.From(findings), trainees);

        void Add(string id, PracticeWorker worker, PracticeWorker? partner, double start)
        {
            var location = ForecastLocations.Of(worker.Location);
            tasks.Add(new(id, location, location, []) { ChoiceIdentity = "practice/" + worker.Pawn,
                NotBefore = start, LatestStart = partner is null ? null : start });
            options.Add(new(id, worker.Pawn, worker.DurationTicks, Known(0d)) { Equipment = [worker.Target, .. worker.Equipment] });
            routes.Add(new(worker.Pawn, "start", location, worker.ApproachTicks));
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Target,
                worker.WorkGiver, worker.Job, 0, checked(facts.Tick.Value + (long)horizon), worker.Location)
            { Practice = new(worker.Skill, partner?.Pawn ?? "", partner?.Target ?? "", (int)Math.Ceiling(worker.DurationTicks.Value)) };
            practice.Add(new(id, worker));
            findings.UnionWith(worker.Findings);
            findings.Add("practice_injury_recovery_unassessed");
        }
    }

    /// <summary>
    /// The pawns a sparring ring trains (#3813): each work pawn whose Melee the one practice eligibility rule admits
    /// (<see cref="SkillLearning.PracticeCapable"/>), with its learning, the sparring ceiling, its MeleeDPS at each stored Melee
    /// level and its health; the standing Melee practice targets. A trainee without a MeleeDPS curve (a pipeline the curve
    /// does not qualify) is a named not-modelled finding and earns nothing; one whose skills, health or ceiling were not
    /// read is listed unread. Null when the work pawns were not read.
    /// </summary>
    internal static SparringTrainees? TraineesOf(RoundsFacts facts, PracticeActivityRead read, SortedSet<string> findings)
    {
        if (!facts.ForecastObserved.WorkPawns.TryGetValue(out var pawns)) return null;
        var trainees = new List<SparringTrainee>(); var unread = new List<string>();
        foreach (var pawn in pawns.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            if (!pawn.Skills.TryGetValue(out var skills)) { unread.Add(pawn.Id.Value); continue; }
            if (skills.FirstOrDefault(s => s.Name == "Melee")?.Learning is not { PracticeCapable: true } learning) continue;
            if (!pawn.Health.TryGetValue(out var health) || !learning.PracticeCeiling.TryGetValue(out var ceiling)) { unread.Add(pawn.Id.Value); continue; }
            if (pawn.SkillStatCurves.FirstOrDefault(c => c.Stat == "MeleeDPS" && c.Skill == "Melee") is not { } curve)
            { findings.Add("sparring_ring_melee_not_modelled:" + pawn.Id.Value); continue; }
            trainees.Add(new(pawn.Id.Value, learning, ceiling, EquatableArray.From(curve.Levels.OrderBy(l => l.StoredLevel).Select(l => (double)l.Value)), health));
        }
        var markers = read.Workers.Where(w => w.Skill == "Melee").Select(w => w.Target).Distinct(StringComparer.Ordinal).Count();
        return new(EquatableArray.From(trainees), EquatableArray.From(unread), markers);
    }
}
