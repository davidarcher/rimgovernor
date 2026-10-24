namespace RimGovernor.Brain.Policy;

public sealed record ConstructionSkillChoice(ConstructionSite Site, int Minimum);

public static class ConstructionSkillRules
{
    /// <summary>
    /// Adopts each unconfigured quality site once. Current jobs, timetable, priorities and sleep do not reduce
    /// construction capability.
    /// </summary>
    public static IReadOnlyList<ConstructionSkillChoice> ConstructionSkillChoices(CurrentConstruction? census, Fact<EquatableArray<WorkPawn>> pawns)
    {
        if (census is null || !pawns.TryGetValue(out var workers))
        {
            return [];
        }
        var best = 0;
        var found = false;
        foreach (var pawn in workers)
        {
            if (!pawn.ConstructionAble || pawn.Work is not PawnWorkTable.Works { Rows: var work })
            {
                continue;
            }
            var skills = pawn.Skills;
            var profile = PawnRules.BuildProfile(pawn);
            if (profile.Forbidden(WorkType.Construction) || profile.Incapable.GetValueOrDefault(WorkType.Construction))
            {
                continue;
            }
            var able = work.Any(w => w.Work == WorkType.Construction && !w.Disabled);
            if (!able)
            {
                continue;
            }
            foreach (var skill in skills)
            {
                if (skill.Name == "Construction" && !skill.Disabled)
                {
                    best = Math.Max(best, skill.Level);
                    found = true;
                }
            }
        }
        if (!found)
        {
            return [];
        }
        var result = new List<ConstructionSkillChoice>();
        foreach (var site in census.Sites)
        {
            if (site.Id.Length == 0 || !site.QualitySensitive || site.Minimum is not null || best < site.NativeFinishingSkill)
            {
                continue;
            }
            result.Add(new ConstructionSkillChoice(site, best));
        }
        return result.OrderBy(c => c.Site.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Augments action progress with native site facts. A placed blueprint is not a finished building, but a
    /// filled frame is work vanilla can offer now. Unknown/material-blocked sites never invent readiness.
    /// </summary>
    public static ReadyWorkReport? ConstructionHelperView(ReadyWorkReport? previous, CurrentConstruction? census, GenerationSnapshot current)
    {
        if (census is null)
        {
            return previous;
        }
        var report = new ReadyWorkReport { Colony = current.Colony, Map = current.Map, Load = current.Load };
        var candidates = new List<ReadyWork>();
        if (previous is not null && previous.Current(current))
        {
            report = previous;
            candidates.AddRange(previous.Candidates);
        }
        foreach (var site in census.Sites)
        {
            var cell = site.Building!.Cell;
            var claim = ReadyWorkRules.CellClaim(cell);
            var row = new ReadyWork
            {
                Stage = "building:" + site.Building.Definition,
                Work = EquatableArray.From([WorkType.Construction]),
                State = ReadyState.Runnable,
                Parallelism = 1,
                Claims = EquatableArray.From([claim]),
                Reason = "frame_materials_complete",
            };
            if (site.Stage != "frame" || !site.ResourcesComplete)
            {
                row = row with { State = ReadyState.Awaiting, Parallelism = 0, Reason = "construction_materials_pending" };
            }
            var found = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Stage != row.Stage)
                {
                    continue;
                }
                if (c.Claims.Contains(claim))
                {
                    candidates[i] = row;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                candidates.Add(row);
            }
        }
        return report with { Candidates = EquatableArray.From(candidates) };
    }
}
