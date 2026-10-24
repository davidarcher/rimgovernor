namespace RimGovernor.Brain.Observation;

/// <summary>
/// The DLC colony sections gate their families: Royalty, Anomaly and Biotech each run only while the colony read
/// carries the section as active, so an inactive section derives no Needs from that expansion.
/// </summary>
public static class DlcSectionReads
{
    /// <summary>The installed flags of the review's facts, read from the colony snapshot's DLC sections.</summary>
    public static RoundsFacts Installed(RoundsFacts facts, O.ColonyFactsSnapshot colony) => facts with
    {
        RoyaltyInstalled = colony.Royalty is O.RoyaltySection.Active,
        AnomalyInstalled = colony.Anomaly is O.AnomalySection.Active,
        BiotechInstalled = colony.Biotech is O.BiotechSection.Active,
    };
}
