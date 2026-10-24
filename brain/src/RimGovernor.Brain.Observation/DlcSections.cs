namespace RimGovernor.Brain.Observation;

/// <summary>
/// The DLC colony sections gate their families: Royalty, Anomaly and Biotech each run only while the colony read
/// carries the section as active, so an inactive section derives no Needs from that expansion.
/// </summary>
public static class DlcSectionReads
{
    /// <summary>The review's DLC sections, read from the colony snapshot.</summary>
    public static RoundsFacts Installed(RoundsFacts facts, O.ColonyFactsSnapshot colony) => facts with
    {
        Anomaly = AnomalyColonyReads.AnomalyOf(colony.Anomaly),
        Biotech = colony.Biotech,
    };
}
