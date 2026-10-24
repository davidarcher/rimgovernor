namespace RimGovernor.Brain.Observation;

public static class AnomalyColonyReads
{
    /// <summary>
    /// The colony's Anomaly read: Inactive without Anomaly; Active with GameComponent_Anomaly's level state and the
    /// monolith's own read (null while it is not spawned). A monolith read that disagrees with MonolithSpawned breaks
    /// the contract.
    /// </summary>
    public static AnomalyState AnomalyOf(O.AnomalySection section)
    {
        if (section is not O.AnomalySection.Active { Facts: var a })
        {
            return new AnomalyState.Inactive();
        }
        var i = a.Incidents;
        if (i.MonolithSpawned != (a.Monolith is not null))
        {
            throw new BridgeContractException($"monolith_spawned is {i.MonolithSpawned} but the monolith block is {(a.Monolith is null ? "absent" : "present")}");
        }
        return new AnomalyState.Active(new MonolithFacts
        {
            AmbientHorror = i.AmbientHorrorMode,
            Level = i.Level,
            Thing = a.Monolith is { } m
                ? new MonolithThing
                {
                    Id = m.MonolithId,
                    CanActivate = m.CanActivate,
                    NextLevel = m.NextLevelDef,
                    CodexShortfall = (uint)m.CodexShortfall,
                    Blocking = [.. m.BlockingConditions],
                    Gleaming = m.GleamingInteractionAvailable,
                    PendingStructures = [.. m.PendingVoidStructureIds],
                    NodeId = m.VoidNodeId ?? "",
                    NodePawns = [.. m.VoidNodePawnIds],
                }
                : null,
        });
    }
}
