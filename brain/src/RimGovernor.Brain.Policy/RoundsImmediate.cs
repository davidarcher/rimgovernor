namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The closed safety scope; ordinary planning stays on its own inspection clock even when these concerns need repeated observation.</summary>
    public static bool ImmediateConcern(ConcernId id) =>
        id == ActiveCombat || id == CriticalMedicine || id == RestoreWorkers || id == MaintainFireSafety || id == MaintainShelter || id == RecoverDisasterServices;

    /// <summary>
    /// Shared by ordinary and immediate reviews. Other upkeep facts and latches cannot make a fresh fire
    /// observation fail or age. Throws DomainException on an invalid fire row.
    /// </summary>
    public static UpkeepNeed ReviewFireSafety(Fact<EquatableArray<UpkeepFire>> fires, bool previous, bool issued)
    {
        var priority = 1;
        var active = previous || issued;
        var unsafeFire = false;
        var targets = Unknown<EquatableArray<string>>();
        var metric = Unknown<double>();
        if (fires.TryGetValue(out var rows))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<string>();
            var total = 0.0;
            var measured = true;
            foreach (var row in rows)
            {
                var known = row.Size.TryGetValue(out var size);
                if (!FoodId(row.Id) || !seen.Add(row.Id) || (known && (!FoodNumber(size) || size < 0)))
                {
                    throw new DomainException("invalid upkeep fire");
                }
                if (!row.Home)
                {
                    continue;
                }
                selected.Add(row.Id);
                total += size;
                measured = measured && known;
                unsafeFire = unsafeFire || !known || size > 1;
            }
            selected.Sort(StringComparer.Ordinal);
            targets = Known(EquatableArray.From(selected));
            active = selected.Count > 0 || issued;
            unsafeFire = unsafeFire || selected.Count > 3;
            if (measured)
            {
                metric = Known(total);
            }
        }
        else if (!active)
        {
            priority = 4;
        }
        return new UpkeepNeed { Concern = MaintainFireSafety, Priority = priority, Active = active, Unsafe = unsafeFire, Targets = targets, Metric = metric };
    }

    /// <summary>The immediate (safety-scope) review. Throws DomainException on invalid facts.</summary>
    public static RoundsFindings InspectImmediateRounds(RoundsFacts f, RoundsLatches previous, RoundsPolicy p)
    {
        p.Validate();
        foreach (var count in new[] { f.Hostiles, f.CriticalPatients, f.UrgentPatients })
        {
            if (count.TryGetValue(out var n) && (n < 0 || n > long.MaxValue / 10))
            {
                throw new DomainException("invalid immediate count");
            }
        }
        var fire = ReviewFireSafety(f.Upkeep.Fires, previous.Upkeep.Fire, f.IsIssued(MaintainFireSafety));
        var latches = previous with { Upkeep = previous.Upkeep with { Fire = fire.Active }, Dash = DashLatch(f.Dash) };
        var c = new RoundsRun(f, previous, p)
        {
            L = previous with { },
            R = new RoundsFindings { Latches = latches, Disaster = f.Disaster },
            Upkeep = new UpkeepReview { Needs = [fire] },
        };
        foreach (var d in InspectionRegistry.Inspections)
        {
            if (!ImmediateConcern(d.Concern))
            {
                continue;
            }
            int goals = c.R.Concerns.Count, assessments = c.R.Assessments.Count;
            if (d.Concern == RecoverDisasterServices)
            {
                // Area protection has a fresh native input; service restoration does not. Absence of an
                // area change never clears an ongoing recovery.
                var finding = Finding.Unclear;
                var priority = 3;
                if (PlanSheltering(f).Count > 0)
                {
                    finding = Finding.Unmet;
                    var (trigger, _) = ShelterTriggerOf(f);
                    priority = ShelterPriority(trigger);
                }
                c.R.Assessments.Add(new RoundsAssessment { Id = d.Concern, Priority = priority, Finding = finding });
            }
            else
            {
                d.Inspect(c);
            }
            if (NoOpOf(d.Concern, c.R.Concerns.Skip(goals).ToList(), c.R.Assessments.Skip(assessments).ToList()) is { } noOpRecord)
            {
                c.R.NoOps.Add(noOpRecord);
            }
        }
        if (f.AvailableMethods.TryGetValue(out var methods))
        {
            var available = new HashSet<ConcernId>();
            foreach (var id in methods)
            {
                var (_, recognized) = InspectionFor(id);
                if (!recognized || available.Contains(id))
                {
                    throw new DomainException("invalid routine method capability " + id.Value);
                }
                available.Add(id);
            }
            foreach (var n in c.R.Concerns)
            {
                if (n.Priority >= 3 && !available.Contains(n.Id))
                {
                    n.MethodUnavailable = true;
                }
            }
            foreach (var n in c.R.Assessments)
            {
                if (n.Id == MaintainFireSafety && n.Priority < 2 && !available.Contains(n.Id))
                {
                    n.MethodUnavailable = true;
                }
            }
        }
        var all = c.R.Assessments;
        c.R.Assessments = [];
        foreach (var n in all)
        {
            if (IsIncidentKind(n.Id))
            {
                c.R.Incidents.Add(n);
            }
            else
            {
                c.R.Assessments.Add(n);
            }
        }
        return c.R;
    }
}
