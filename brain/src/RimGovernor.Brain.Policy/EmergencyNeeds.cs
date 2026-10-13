namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>EmergencyNeeds</c>: uses the same census and medical/threat rules as clock safety. Incomplete or
    /// conflicting evidence cannot become a recovered routine need.
    /// </summary>
    public static (Fact<long> Hostiles, Fact<long> Patients) EmergencyNeeds(EmergencySnapshot snapshot, GenerationSnapshot current, Tick tick)
    {
        var decision = EvaluateEmergency(snapshot, current, tick);
        long threats = 0;
        foreach (var hold in decision.Holds)
        {
            switch (hold.Reason)
            {
                case EmergencyReason.UnknownFacts:
                case EmergencyReason.StaleFacts:
                    return (Unknown<long>(), Unknown<long>());
                case EmergencyReason.UnsafeThreat:
                    threats++;
                    break;
                default:
                    break;
            }
        }
        // Raiders still in their pods are a threat no census row names yet.
        if (snapshot.PodsPending)
        {
            threats++;
        }
        // Patients are counted from the census, not the holds: a colonist who only needs tending is the tend
        // planner's patient although the emergency no longer holds dispatch for them.
        return (Known(threats), Known(CountPatients(snapshot, true)));
    }

    /// <summary>
    /// Go <c>UrgentPatients</c>: counts the critical patients who are bleeding or downed with a tend outstanding:
    /// the ones whose care cannot wait for ordinary work. A living colonist who merely needs tending, or who is
    /// downed with nothing to tend, is a patient but not an urgent one. Uncertainty is unknown, exactly as
    /// <see cref="EmergencyNeeds"/> reports it.
    /// </summary>
    public static Fact<long> UrgentPatients(EmergencySnapshot snapshot, GenerationSnapshot current, Tick tick)
    {
        var (_, patients) = EmergencyNeeds(snapshot, current, tick);
        return patients.IsKnown ? Known(CountPatients(snapshot, false)) : Unknown<long>();
    }

    /// <summary>
    /// Go <c>countPatients</c>: counts the living colonists who are urgent patients and, with tending, every
    /// downed, bleeding or tend-needing colonist too. Callers have already established that every health fact is
    /// known.
    /// </summary>
    private static long CountPatients(EmergencySnapshot snapshot, bool tending)
    {
        long patients = 0;
        foreach (var pawn in snapshot.Facts.Colonists)
        {
            if (pawn.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            var downed = pawn.Downed.ValueOrDefault;
            var bleeding = pawn.Bleeding.ValueOrDefault;
            var needsTend = pawn.NeedsTend.ValueOrDefault;
            if (UrgentPatient(pawn) || (tending && (downed || bleeding || needsTend)))
            {
                patients++;
            }
        }
        return patients;
    }

    /// <summary>
    /// Go <c>AmputationNeeds</c>: adds the colonists a life-saving amputation would save to the critical and
    /// urgent patient counts, never counting a colonist either already includes. Losing an immunity race is an
    /// emergency: the amputation runs at CriticalMedical's priority. Unknown care facts add nobody; an unknown
    /// count stays unknown.
    /// </summary>
    public static (Fact<long> Patients, Fact<long> Urgent) AmputationNeeds(
        EmergencySnapshot snapshot,
        Fact<EquatableArray<CarePawn>> medical,
        Fact<long> patients,
        Fact<long> urgent
    )
    {
        if (!medical.TryGetValue(out var pawns))
        {
            return (patients, urgent);
        }
        var byId = new Dictionary<PawnId, EmergencyPawn>();
        foreach (var pawn in snapshot.Facts.Colonists)
        {
            byId[pawn.Id] = pawn;
        }
        long critical = 0;
        long pressing = 0;
        foreach (var pawn in pawns)
        {
            if (LifeSavingAmputations(pawn).Count == 0)
            {
                continue;
            }
            var found = byId.TryGetValue(pawn.Id, out var row);
            var downed = row?.Downed.ValueOrDefault ?? false;
            var bleeding = row?.Bleeding.ValueOrDefault ?? false;
            var needsTend = row?.NeedsTend.ValueOrDefault ?? false;
            if (!found || !UrgentPatient(row!))
            {
                pressing++;
            }
            if (!found || !(downed || bleeding || needsTend))
            {
                critical++;
            }
        }
        static Fact<long> Add(Fact<long> f, long n) => f.TryGetValue(out var v) ? Known(v + n) : f;
        return (Add(patients, critical), Add(urgent, pressing));
    }
}
