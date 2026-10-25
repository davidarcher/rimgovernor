using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
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
    EquatableArray<string> Findings, PracticeTrainees? Sparring, PracticeTrainees? Range)
{
    internal static PracticeMethodCandidates Generate(ReviewFacts facts, PracticeActivityRead read, double horizon, EquatableArray<AssignedCapability> assigned, bool admitted)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var roots = new List<ForecastCommitment>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>(); var practice = new List<ForecastPracticeTask>();
        var requirements = new List<ForecastMaintenanceRequirement>(); var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (facts.Ideology is IdeologyMode.Installed { Primary: var ideology })
            foreach (var role in ideology.Facts.Roles.Where(r => r.Active && r.Pawns.Count == 0))
                findings.Add("acquisition_requires_assignment:" + role.Id);
        var pawns = facts.ForecastObserved.WorkPawns.Pawns;
        foreach (var requirement in assigned)
        {
            findings.Add("ideology_role_skill_requirement_not_modelled:" + requirement.Id);
            var pawn = pawns.FirstOrDefault(p => p.Id == requirement.Pawn);
            if (pawn is null) { findings.Add("maintenance_skills_unknown:" + requirement.Id); continue; }
            requirements.Add(new(requirement, pawn.Skills));
        }
        if (!admitted) findings.Add("practice_held_essential_emergency_or_unknown");
        var eligible = read.Workers.Where(w => admitted && w.Skill is "Shooting" or "Melee"
            && !facts.WorkView!.Claims.Pawns.Contains(w.Pawn) && !facts.WorkView.Claims.Targets.Contains(w.Target)
            && w.DurationTicks > 0
            && w.Learning.Practice is SkillPractice.Drill { Eligible: true }).Take(32).ToArray();
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
            var start = Math.Max(first.ApproachTicks, second.ApproachTicks);
            Add(id + "/a", first, second, start);
            Add(id + "/b", second, first, start);
            roots.Add(new(id, []) { ActivityCompletions = [id + "/a", id + "/b"] });
        }
        var sparring = TraineesOf(facts, read, findings, "Melee"); var range = TraineesOf(facts, read, findings, "Shooting");
        return new(tasks.ToArray(), options.ToArray(), routes.ToArray(), roots.ToArray(), bindings,
            EquatableArray.From(practice), EquatableArray.From(requirements), EquatableArray.From(findings), sparring, range);

        void Add(string id, PracticeWorker worker, PracticeWorker? partner, double start)
        {
            var location = ForecastLocations.Of(worker.Location);
            tasks.Add(new(id, location, location, []) { ChoiceIdentity = "practice/" + worker.Pawn,
                NotBefore = start, LatestStart = partner is null ? double.PositiveInfinity : start });
            options.Add(new(id, worker.Pawn, worker.DurationTicks, 0) { Equipment = [worker.Target, .. worker.Equipment] });
            routes.Add(new(worker.Pawn, "start", location, new RouteTicks.Reachable(worker.ApproachTicks)));
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Target,
                worker.WorkGiver, worker.Job, 0, checked(facts.Tick.Value + (long)horizon), worker.Location)
            { Practice = new(worker.Skill, partner?.Pawn ?? "", partner?.Target ?? "", (int)Math.Ceiling(worker.DurationTicks)) };
            practice.Add(new(id, worker));
            findings.UnionWith(worker.Findings);
            findings.Add("practice_injury_recovery_unassessed");
        }
    }

    /// <summary>
    /// The pawns a practice room trains (#3813, #3846): each work pawn whose <paramref name="skill"/> (Melee for the sparring
    /// ring, Shooting for the training range) the one practice eligibility rule admits (<see cref="SkillPractice.Drill.Capable"/>),
    /// with its learning, the skill's practice ceiling and the defense power it fights with at each stored level: MeleeDPS
    /// (its biography's curve) x health for Melee, as the defense capacity counts melee power; its ranged DPS at its hit factor
    /// (the biography's <see cref="SkillStatCurve.RangedDefenseDps"/> curve) for Shooting, as the capacity counts ranged
    /// damage. The standing practice targets of the skill. A trainee without its curve (a pipeline the curve does not
    /// qualify) is a named not-modelled finding and earns nothing. Null when the work pawns were not read.
    /// </summary>
    internal static PracticeTrainees? TraineesOf(ReviewFacts facts, PracticeActivityRead read, SortedSet<string> findings, string skill)
    {
        var pawns = facts.ForecastObserved.WorkPawns.Pawns;
        var melee = skill == "Melee";
        var (stat, finding) = melee ? ("MeleeDPS", "sparring_ring_melee_not_modelled:") : (SkillStatCurve.RangedDefenseDps, "training_range_shooting_not_modelled:");
        var trainees = new List<PracticeTrainee>();
        foreach (var pawn in pawns.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            if (pawn.Skills.FirstOrDefault(s => s.DefName == skill)?.Learning is not { Practice: SkillPractice.Drill { Capable: true, Ceiling: var ceiling } } learning) continue;
            // Ranged damage counts at full health in the defense capacity; melee power scales with it.
            var health = melee ? pawn.Health : 1d;
            if (pawn.SkillStatCurves.FirstOrDefault(c => c.Stat == stat && c.Skill == skill) is not { } curve)
            { findings.Add(finding + pawn.Id.Value); continue; }
            trainees.Add(new(pawn.Id.Value, learning, ceiling, EquatableArray.From(curve.Levels.OrderBy(l => l.StoredLevel).Select(l => health * l.Value))));
        }
        var targets = read.Workers.Where(w => w.Skill == skill).Select(w => w.Target).Distinct(StringComparer.Ordinal).Count();
        return new(EquatableArray.From(trainees), targets);
    }
}