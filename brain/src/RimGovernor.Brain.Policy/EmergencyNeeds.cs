namespace RimGovernor.Brain.Policy;

public static class EmergencyNeedsRules
{
    /// <summary>
    /// Uses the same census and medical/threat rules as clock safety. Incomplete or
    /// conflicting evidence cannot become a recovered routine need.
    /// </summary>
    public static (HostileCensus Hostiles, long Patients) EmergencyNeeds(EmergencySnapshot snapshot, GenerationSnapshot current, Tick tick)
    {
        var decision = EmergencyRules.EvaluateEmergency(snapshot, current, tick);
        // Patients are counted from the census, not the holds: ordinary tending also owns a patient.
        return (HostilesOf(decision, snapshot.PodsPending), CountPatients(snapshot.Facts, true));
    }

    /// <summary>The named threat state of an assessed emergency, including raiders still inside pending pods.</summary>
    public static HostileCensus HostilesOf(EmergencyDecision decision, bool podsPending)
    {
        long threats = 0;
        foreach (var hold in decision.Holds)
        {
            switch (hold.Reason)
            {
                case EmergencyReason.StaleFacts:
                    throw new RequiredFactException("current emergency census");
                case EmergencyReason.UnsafeThreat:
                    threats++;
                    break;
                case EmergencyReason.CriticalMedical:
                    break;
            }
        }
        // Raiders still in their pods are a threat no census row names yet.
        if (podsPending)
        {
            threats++;
        }
        return HostileCensus.Of(threats);
    }

    /// <summary>
    /// Counts the critical patients who are bleeding or downed with a tend outstanding:
    /// the ones whose care cannot wait for ordinary work. A living colonist who merely needs tending, or who is
    /// downed with nothing to tend, is a patient but not an urgent one. A stale census fails the read, exactly as
    /// <see cref="EmergencyNeeds"/> reports it.
    /// </summary>
    public static long UrgentPatients(EmergencySnapshot snapshot, GenerationSnapshot current, Tick tick)
    {
        var (_, patients) = EmergencyNeeds(snapshot, current, tick);
        return CountPatients(snapshot.Facts, false);
    }

    /// <summary>
    /// Counts the living colonists who are urgent patients and, with tending, every
    /// downed, bleeding or tend-needing colonist too.
    /// </summary>
    private static long CountPatients(EmergencyFacts facts, bool tending)
    {
        long patients = 0;
        foreach (var pawn in EmergencyPatientsRules.EmergencyPatients(facts))
        {
            if (pawn.Dead)
            {
                continue;
            }
            if (EmergencyRules.UrgentPatient(pawn) || (tending && (pawn.Downed || pawn.Bleeding || pawn.NeedsTend)))
            {
                patients++;
            }
        }
        return patients;
    }

    /// <summary>
    /// The critical patients by id (#3258): the living colonists and dependents <see cref="EmergencyNeeds"/> counts
    /// (urgent, downed, bleeding or needing a tend) and the colonists a life-saving amputation would save
    /// (<see cref="PatientCounts"/>), sorted. A stale census raises a boundary failure.
    /// </summary>
    public static EquatableArray<PawnId> CriticalPatientIds(EmergencySnapshot snapshot, GenerationSnapshot current, Tick tick, EquatableArray<CarePawn> medical)
    {
        var (_, patients) = EmergencyNeeds(snapshot, current, tick);

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pawn in EmergencyPatientsRules.EmergencyPatients(snapshot.Facts))
        {
            if (pawn.Dead)
            {
                continue;
            }
            if (EmergencyRules.UrgentPatient(pawn) || pawn.Downed || pawn.Bleeding || pawn.NeedsTend)
            {
                ids.Add(pawn.Id.Value);
            }
        }
        {
            var pawns = medical;
            foreach (var pawn in pawns.Where(p => EmergencyRules.LifeSavingAmputations(p).Count > 0))
            {
                ids.Add(pawn.Id.Value);
            }
        }
        return EquatableArray.From(ids.Select(id => new PawnId(id)));
    }

    /// <summary>The same-frame critical and urgent census, including patients whose life-saving amputation is owed.</summary>
    public static (long Patients, long Urgent) PatientCounts(EmergencyFacts facts, EquatableArray<CarePawn> medical)
    {
        if (facts.Unseen.Count != 0) throw new RequiredFactException("current emergency census");
        var patients = CountPatients(facts, true);
        var urgent = CountPatients(facts, false);
        var pawns = medical;
        var byId = new Dictionary<PawnId, EmergencyPawn>();
        foreach (var pawn in EmergencyPatientsRules.EmergencyPatients(facts))
        {
            byId[pawn.Id] = pawn;
        }
        long critical = 0;
        long pressing = 0;
        foreach (var pawn in pawns)
        {
            if (EmergencyRules.LifeSavingAmputations(pawn).Count == 0)
            {
                continue;
            }
            var found = byId.TryGetValue(pawn.Id, out var row);
            if (!found || !EmergencyRules.UrgentPatient(row!))
            {
                pressing++;
            }
            if (!found || !(row!.Downed || row.Bleeding || row.NeedsTend))
            {
                critical++;
            }
        }

        return (patients + critical, urgent + pressing);
    }
}
