namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The closed safety scope; ordinary planning stays on its own inspection clock even when these concerns need
    /// repeated observation. Combat is judged in every pass by the military family's <c>military/combat</c> Need (#3311).
    /// </summary>
    public static bool ImmediateConcern(ConcernId id) => id == RecoverDisasterServices;

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
        var latches = previous with { Dash = DashLatch(f.Dash) };
        var c = new RoundsRun(f, previous, p)
        {
            L = previous with { },
            R = new RoundsFindings { Latches = latches, Disaster = f.Disaster },
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
                if (PlanSheltering(f).Count > 0)
                {
                    finding = Finding.Unmet;
                }
                c.R.Assessments.Add(new RoundsAssessment { Id = d.Concern, Finding = finding });
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
                if (!available.Contains(n.Id))
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
