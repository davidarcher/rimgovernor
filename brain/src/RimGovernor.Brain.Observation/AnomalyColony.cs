namespace RimGovernor.Brain.Observation;

public static class AnomalyColonyReads
{
    /// <summary>
    /// The monolith rule's input: GameComponent_Anomaly's level state and the monolith's own read. Unknown without
    /// Anomaly; the monolith's facts are unknown while it is not spawned, and the next level is unknown when none
    /// follows through activation.
    /// </summary>
    internal static Fact<MonolithFacts> MonolithFacts(O.AnomalySection section)
    {
        if (section is not O.AnomalySection.Active { Facts: var a })
        {
            return Unknown<MonolithFacts>();
        }
        var i = a.Incidents;
        var output = new MonolithFacts
        {
            Spawned = Known(i.MonolithSpawned),
            AmbientHorror = Known(i.AmbientHorrorMode),
            Level = Known(i.Level),
            Id = Unknown<string>(),
            CanActivate = Unknown<bool>(),
            NextLevel = Unknown<string>(),
            CodexShortfall = Unknown<uint>(),
            Gleaming = Unknown<bool>(),
        };
        if (a.Monolith is { } m)
        {
            output = output with
            {
                Id = Known(m.MonolithId),
                CanActivate = Known(m.CanActivate),
                NextLevel = m.NextLevelDef is { } next ? Known(next) : Unknown<string>(),
                CodexShortfall = Known((uint)m.CodexShortfall),
                Blocking = [.. m.BlockingConditions],
                Gleaming = Known(m.GleamingInteractionAvailable),
                PendingStructures = [.. m.PendingVoidStructureIds],
                NodeId = m.VoidNodeId ?? "",
                NodePawns = [.. m.VoidNodePawnIds],
            };
        }
        return Known(output);
    }
}
