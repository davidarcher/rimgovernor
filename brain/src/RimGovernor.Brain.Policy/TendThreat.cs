namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// How close (Chebyshev cells) a live hostile pawn must stand to a patient for the tend to wait: a
    /// manhunter closes that gap in seconds, before a doctor arrives, and the doctor would meet it alone.
    /// </summary>
    public const int TendThreatCells = 8;

    /// <summary>
    /// The colonists with a holding hostile pawn (ThreatHolds, never a building) known within TendThreatCells.
    /// A patient or threat with an unknown cell is not named: the veto needs a measured gap, and a far or
    /// unplaced threat is the combat family's alone.
    /// </summary>
    public static HashSet<PawnId> ThreatenedPatients(EmergencyFacts facts)
    {
        var threats = new List<Cell>();
        foreach (var t in facts.Threats)
        {
            if (t.Building || !t.Holds())
            {
                continue;
            }
            if (t.Position.TryGetValue(out var at))
            {
                threats.Add(at);
            }
        }
        var output = new HashSet<PawnId>();
        foreach (var p in facts.Colonists)
        {
            if (!p.Position.TryGetValue(out var at))
            {
                continue;
            }
            foreach (var t in threats)
            {
                if (Chebyshev(at, t) <= TendThreatCells)
                {
                    output.Add(p.Id);
                    break;
                }
            }
        }
        return output;
    }
}
