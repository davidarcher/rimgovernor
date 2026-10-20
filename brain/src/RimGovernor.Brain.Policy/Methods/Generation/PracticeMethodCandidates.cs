using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record PracticeMethodCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastCommitment[] Roots,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings,
    EquatableArray<ForecastPracticeTask> Practice, EquatableArray<ForecastMaintenanceRequirement> Requirements,
    EquatableArray<string> Findings)
{
    /// <summary>The practice-surgery task ids (#3637): valued only by the Investment term, never by a maintenance requirement.</summary>
    public EquatableArray<string> Surgeries { get; init; }

    internal static PracticeMethodCandidates Generate(RoundsFacts facts, PracticeActivityRead read, double horizon, EquatableArray<AssignedCapability> assigned, bool admitted,
        IReadOnlyList<PracticeSurgeryWorker> surgeries)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var roots = new List<ForecastCommitment>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>(); var practice = new List<ForecastPracticeTask>();
        var requirements = new List<ForecastMaintenanceRequirement>(); var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        if (!read.Known) findings.Add("practice_observation_unknown");
        if (!facts.Ideology.IsKnown) findings.Add("assigned_capability_floor_unknown");
        var pawns = facts.ForecastObserved.WorkPawns.TryGetValue(out var rows) ? rows : [];
        foreach (var pawn in pawns.Where(p => assigned.All(a => a.Pawn != p.Id)))
            findings.Add("unsupported_defensive_improvement_without_assignment:" + pawn.Id.Value);
        if (facts.Ideology.TryGetValue(out var ideology))
            foreach (var role in ideology.Facts.Roles.Where(r => r.Active && r.Pawns.Count == 0))
                findings.Add("acquisition_requires_assignment:" + role.Id);
        foreach (var requirement in assigned)
        {
            var pawn = pawns.FirstOrDefault(p => p.Id == requirement.Pawn);
            if (pawn is null || !pawn.Skills.TryGetValue(out var skills)) { findings.Add("maintenance_skills_unknown:" + requirement.Id); continue; }
            requirements.Add(new(requirement, EquatableArray.From(skills)));
        }
        if (!admitted) findings.Add("practice_held_essential_emergency_or_unknown");
        var eligible = requirements.Where(_ => admitted).SelectMany(r => r.Requirement.Alternatives.Where(a => a.Skill is "Shooting" or "Melee")
            .SelectMany(a => read.Workers.Where(w => w.Pawn == r.Requirement.Pawn.Value && w.Skill == a.Skill)
                .Select(w => (Requirement: r.Requirement, Worker: w)))).Where(p =>
                    !facts.WorkView!.Claims.Pawns.Contains(p.Worker.Pawn) && !facts.WorkView.Claims.Targets.Contains(p.Worker.Target)
                    && p.Worker.DurationTicks.IsKnown && p.Worker.DurationTicks.Value > 0
                    && p.Worker.ApproachTicks.IsKnown && p.Worker.CallbackIntervalTicks.IsKnown && p.Worker.RawXpPerCallback.IsKnown
                    && p.Worker.Learning.PracticeEligible == Known(true)).Take(32).ToArray();
        foreach (var row in eligible.Where(p => p.Worker.Skill == "Shooting"))
        {
            var id = "maintenance/" + row.Requirement.Id + "/" + row.Worker.Target;
            Add(id, row.Requirement, row.Worker, null, 0);
            roots.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "practice/" + row.Worker.Pawn });
        }
        foreach (var first in eligible.Where(p => p.Worker.Skill == "Melee"))
        foreach (var second in read.Workers.Where(w => w.Skill == "Melee" && w.Pawn != first.Worker.Pawn
            && w.Target != first.Worker.Target && w.ApproachTicks.IsKnown && w.DurationTicks.IsKnown
            && w.CallbackIntervalTicks.IsKnown && w.RawXpPerCallback.IsKnown && w.Learning.PracticeEligible == Known(true)
            && !facts.WorkView!.Claims.Pawns.Contains(w.Pawn) && !facts.WorkView.Claims.Targets.Contains(w.Target)))
        {
            if (tasks.Count >= 64) { findings.Add("practice_generation_truncated"); break; }
            var id = "maintenance/pair/" + first.Requirement.Id + "/" + second.Pawn + "/" + first.Worker.Target + "/" + second.Target;
            var start = Math.Max(first.Worker.ApproachTicks.Value, second.ApproachTicks.Value);
            Add(id + "/a", first.Requirement, first.Worker, second, start);
            Add(id + "/b", first.Requirement, second, first.Worker, start);
            roots.Add(new(id, []) { ActivityCompletions = [id + "/a", id + "/b"] });
        }
        var surgeryIds = new List<string>();
        foreach (var row in surgeries)
        {
            var id = "practice/surgery/" + row.Doctor + "/" + row.Patient + "/" + row.Recipe + "/"
                + row.Part.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + row.Medicine;
            var location = ForecastLocations.Of(row.Location);
            // Medicine is consumed and charged through its medicine/<kind> key; the material part comes back on removal (#3344), so it is not.
            tasks.Add(new(id, location, location, []) { ChoiceIdentity = "practice/" + row.Doctor, Inputs = [new(new(row.Medicine), Known((double)row.MedicineCount))] });
            options.Add(new(id, row.Doctor, row.WorkTicks, Known(0d)) { SkillWork = row.SkillWork });
            routes.Add(new(row.Doctor, "start", location, row.ApproachTicks));
            var duration = (int)Math.Clamp(Math.Ceiling(row.ApproachTicks.Value + row.WorkTicks.Value), 1, GameTime.TicksPerDay);
            bindings[(id, row.Doctor)] = new(id, row.Doctor, [], PawnActivityKind.Work, row.Patient, row.WorkGiver, "DoBill", 0,
                checked(facts.Tick.Value + (long)horizon), row.Location)
            { Practice = new("Medicine", "", 0, "", "", duration) { Surgery = new(row.Recipe, row.Part, row.Medicine) } };
            // One practice surgery at a time, as the in-flight rule holds every other prisoner surgery.
            roots.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "practice/surgery" });
            surgeryIds.Add(id);
        }
        return new(tasks.ToArray(), options.ToArray(), routes.ToArray(), roots.ToArray(), bindings,
            EquatableArray.From(practice), EquatableArray.From(requirements), EquatableArray.From(findings)) { Surgeries = EquatableArray.From(surgeryIds) };

        void Add(string id, AssignedCapability requirement, PracticeWorker worker, PracticeWorker? partner, double start)
        {
            var location = ForecastLocations.Of(worker.Location);
            tasks.Add(new(id, location, location, []) { ChoiceIdentity = "practice/" + worker.Pawn,
                NotBefore = start, LatestStart = partner is null ? null : start });
            options.Add(new(id, worker.Pawn, worker.DurationTicks, Known(0d)) { Equipment = [worker.Target, .. worker.Equipment] });
            routes.Add(new(worker.Pawn, "start", location, worker.ApproachTicks));
            var index = int.Parse(requirement.Target.Predicate.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Target,
                worker.WorkGiver, worker.Job, 0, checked(facts.Tick.Value + (long)horizon), worker.Location)
            { Practice = new(worker.Skill, requirement.Target.Subject, index, partner?.Pawn ?? "", partner?.Target ?? "", (int)Math.Ceiling(worker.DurationTicks.Value)) { Beneficiary = requirement.Pawn.Value } };
            practice.Add(new(id, worker, requirement.Id));
            findings.UnionWith(worker.Findings);
            findings.Add("practice_injury_recovery_unassessed");
        }
    }
}
