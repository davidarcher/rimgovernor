namespace RimGovernor.Brain.Observation;

public static class AnomalyColonyReads
{
    /// <summary>
    /// The monolith rule's input: GameComponent_Anomaly's level state and the monolith's own read (null while it is
    /// not spawned). Unknown without Anomaly; a monolith read that disagrees with MonolithSpawned breaks the contract.
    /// </summary>
    internal static Fact<MonolithFacts> MonolithFacts(O.AnomalySection section)
    {
        if (section is not O.AnomalySection.Active { Facts: var a })
        {
            return Unknown<MonolithFacts>();
        }
        var i = a.Incidents;
        if (i.MonolithSpawned != (a.Monolith is not null))
        {
            throw new BridgeContractException($"monolith_spawned is {i.MonolithSpawned} but the monolith block is {(a.Monolith is null ? "absent" : "present")}");
        }
        return Known(new MonolithFacts
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
