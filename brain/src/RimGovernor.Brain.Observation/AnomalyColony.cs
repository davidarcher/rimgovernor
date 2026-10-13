namespace RimGovernor.Brain.Observation;

/// <summary>
/// The Anomaly colony section: knowledge and codex progress, the entities held on the colony map and the state
/// that gates and times Anomaly threats. Per-thing facts are the pawn and building rows' (PawnAnomaly,
/// BuildingAnomaly). An absent scalar is unknown, never zero; the whole section is unknown without Anomaly or
/// when the read failed.
/// </summary>
public sealed record AnomalyColony
{
    public IReadOnlyList<KnowledgeProgress> Knowledge { get; init; } = [];

    public IReadOnlyList<CodexProgress> Codex { get; init; } = [];

    /// <summary>The codex entry names the colony has discovered.</summary>
    public IReadOnlyList<string> DiscoveredEntries { get; init; } = [];

    /// <summary>Join the pawn and building rows by id.</summary>
    public IReadOnlyList<HeldEntity> HeldEntities { get; init; } = [];

    /// <summary>The game's own check that a holding platform can take an entity on the colony map.</summary>
    public Fact<bool> HoldingPlatformAvailable { get; init; }

    public Fact<AnomalyIncidents> Incidents { get; init; }

    /// <summary>Unknown unless the monolith is spawned.</summary>
    public Fact<MonolithState> Monolith { get; init; }
}

/// <summary>
/// The void monolith and the endgame it gates, every verdict the game's own. CanActivate is
/// Building_VoidMonolith.CanActivate. The next level's requirement is the codex category and count its def
/// lists (unknown when it lists none, or no level follows); CodexShortfall is the entries still undiscovered
/// there. BlockingConditions are the active game conditions the next level lists as unreachable
/// (UnnaturalDarkness blocks VoidAwakened). VoidAwakeningStage is unknown while the quest is not running.
/// </summary>
public sealed record MonolithState
{
    public Fact<bool> CanActivate { get; init; }

    public Fact<string> NextLevelDef { get; init; }

    public Fact<string> NextLevelCodexCategory { get; init; }

    public Fact<uint> NextLevelCodexRequired { get; init; }

    public Fact<uint> CodexShortfall { get; init; }

    public IReadOnlyList<string> BlockingConditions { get; init; } = [];

    public Fact<bool> GleamingInteractionAvailable { get; init; }

    public Fact<uint> VoidStructures { get; init; }

    public Fact<uint> VoidStructuresActivated { get; init; }

    public Fact<bool> VoidNodeExists { get; init; }

    public Fact<int> VoidAwakeningStage { get; init; }

    /// <summary>The thing a give-job targets.</summary>
    public Fact<string> MonolithID { get; init; }

    /// <summary>The VoidStructures that can still be interacted with.</summary>
    public IReadOnlyList<string> PendingVoidStructureIDs { get; init; } = [];

    /// <summary>The VoidNode that can be touched ("" when none). VoidNodeExists is true for a node on any loaded map.</summary>
    public string VoidNodeID { get; init; } = "";

    /// <summary>The colonists on the void node's map able to touch it.</summary>
    public IReadOnlyList<string> VoidNodePawnIDs { get; init; } = [];
}

/// <summary>One knowledge category: the project the research manager funds from it (unknown when none is set), the knowledge stored for that project and whether any project of the category can still be researched.</summary>
public sealed record KnowledgeProgress
{
    public string Category { get; init; } = "";

    public Fact<string> CurrentProject { get; init; }

    public Fact<double> Knowledge { get; init; }

    public Fact<bool> ProjectsAvailable { get; init; }
}

/// <summary>Counts one entity category's codex entries and the discovered ones.</summary>
public sealed record CodexProgress(string Category, uint Entries, uint Discovered);

/// <summary>A pawn held by a holding platform.</summary>
public sealed record HeldEntity(string PawnID, string PlatformID);

/// <summary>GameComponent_Anomaly's state: the monolith level and the fraction of threat points the game gives Anomaly incidents now.</summary>
public sealed record AnomalyIncidents
{
    public Fact<bool> MonolithSpawned { get; init; }

    public Fact<bool> QuestlineEnded { get; init; }

    public Fact<bool> AmbientHorrorMode { get; init; }

    public Fact<bool> AnomalyStudyEnabled { get; init; }

    public Fact<bool> VoidAwakeningActive { get; init; }

    public Fact<bool> AwokenCorpseActive { get; init; }

    public Fact<bool> MetalhorrorImplantPossible { get; init; }

    public Fact<string> LevelDef { get; init; }

    public Fact<int> Level { get; init; }

    public Fact<int> HighestLevelReached { get; init; }

    public Fact<int> TicksSinceLevelChange { get; init; }

    public Fact<double> ThreatFractionNow { get; init; }
}

public static partial class ObservationPackage
{
    /// <summary>The monolith rule's input: the level state of GameComponent_Anomaly and the monolith's own read; unknown without the Anomaly section.</summary>
    internal static Fact<MonolithFacts> MonolithFacts(Fact<AnomalyColony> section)
    {
        if (!section.TryGetValue(out var a))
        {
            return default;
        }
        var output = new MonolithFacts
        {
            Spawned = Unknown<bool>(),
            AmbientHorror = Unknown<bool>(),
            Level = Unknown<int>(),
            Id = Unknown<string>(),
            CanActivate = Unknown<bool>(),
            NextLevel = Unknown<string>(),
            CodexShortfall = Unknown<uint>(),
            Gleaming = Unknown<bool>(),
        };
        if (a.Incidents.TryGetValue(out var i))
        {
            output = output with { Spawned = i.MonolithSpawned, AmbientHorror = i.AmbientHorrorMode, Level = i.Level };
        }
        if (a.Monolith.TryGetValue(out var m))
        {
            output = output with
            {
                Id = m.MonolithID,
                CanActivate = m.CanActivate,
                NextLevel = m.NextLevelDef,
                CodexShortfall = m.CodexShortfall,
                Blocking = m.BlockingConditions,
                Gleaming = m.GleamingInteractionAvailable,
                PendingStructures = m.PendingVoidStructureIDs,
                NodeId = m.VoidNodeID,
                NodePawns = m.VoidNodePawnIDs,
            };
        }
        return Known(output);
    }

    /// <summary>Projects a validated section; an absent or unavailable one is an unknown fact.</summary>
    internal static Fact<AnomalyColony> ColonyAnomaly(O.AnomalySection? section)
    {
        var f = section?.Outcome?.Observed;
        if (f is null)
        {
            return default;
        }
        var knowledge = new List<KnowledgeProgress>();
        foreach (var x in f.Knowledge)
        {
            knowledge.Add(new KnowledgeProgress
            {
                Category = x.Category ?? "",
                CurrentProject = Optional(x.CurrentProject is not null, x.CurrentProject ?? ""),
                Knowledge = Optional(x.Knowledge is not null, x.Knowledge.GetValueOrDefault()),
                ProjectsAvailable = Optional(x.ProjectsAvailable is not null, x.ProjectsAvailable.GetValueOrDefault()),
            });
        }
        var codex = new List<CodexProgress>();
        foreach (var x in f.Codex)
        {
            codex.Add(new CodexProgress(x.Category ?? "", x.Entries.GetValueOrDefault(), x.Discovered.GetValueOrDefault()));
        }
        var held = new List<HeldEntity>();
        foreach (var x in f.HeldEntities)
        {
            held.Add(new HeldEntity(x.PawnId ?? "", x.PlatformId ?? ""));
        }
        var r = new AnomalyColony
        {
            DiscoveredEntries = [.. f.DiscoveredEntries],
            HoldingPlatformAvailable = Optional(f.HoldingPlatformAvailable is not null, f.HoldingPlatformAvailable.GetValueOrDefault()),
            Knowledge = knowledge,
            Codex = codex,
            HeldEntities = held,
        };
        if (f.Incidents is { } i)
        {
            r = r with
            {
                Incidents = Known(new AnomalyIncidents
                {
                    MonolithSpawned = Optional(i.MonolithSpawned is not null, i.MonolithSpawned.GetValueOrDefault()),
                    QuestlineEnded = Optional(i.QuestlineEnded is not null, i.QuestlineEnded.GetValueOrDefault()),
                    AmbientHorrorMode = Optional(i.AmbientHorrorMode is not null, i.AmbientHorrorMode.GetValueOrDefault()),
                    AnomalyStudyEnabled = Optional(i.AnomalyStudyEnabled is not null, i.AnomalyStudyEnabled.GetValueOrDefault()),
                    VoidAwakeningActive = Optional(i.VoidAwakeningActive is not null, i.VoidAwakeningActive.GetValueOrDefault()),
                    AwokenCorpseActive = Optional(i.AwokenCorpseActive is not null, i.AwokenCorpseActive.GetValueOrDefault()),
                    MetalhorrorImplantPossible = Optional(i.MetalhorrorImplantPossible is not null, i.MetalhorrorImplantPossible.GetValueOrDefault()),
                    LevelDef = Optional(i.LevelDef is not null, i.LevelDef ?? ""),
                    Level = Optional(i.Level is not null, i.Level.GetValueOrDefault()),
                    HighestLevelReached = Optional(i.HighestLevelReached is not null, i.HighestLevelReached.GetValueOrDefault()),
                    TicksSinceLevelChange = Optional(i.TicksSinceLevelChange is not null, i.TicksSinceLevelChange.GetValueOrDefault()),
                    ThreatFractionNow = Optional(i.AnomalyThreatFractionNow is not null, i.AnomalyThreatFractionNow.GetValueOrDefault()),
                }),
            };
        }
        if (f.Monolith is { } m)
        {
            r = r with
            {
                Monolith = Known(new MonolithState
                {
                    CanActivate = Optional(m.CanActivate is not null, m.CanActivate.GetValueOrDefault()),
                    NextLevelDef = Optional(m.NextLevelDef is not null, m.NextLevelDef ?? ""),
                    NextLevelCodexCategory = Optional(m.NextLevelCodexCategory is not null, m.NextLevelCodexCategory ?? ""),
                    NextLevelCodexRequired = Optional(m.NextLevelCodexRequired is not null, m.NextLevelCodexRequired.GetValueOrDefault()),
                    CodexShortfall = Optional(m.CodexShortfall is not null, m.CodexShortfall.GetValueOrDefault()),
                    BlockingConditions = [.. m.BlockingConditions],
                    GleamingInteractionAvailable = Optional(m.GleamingInteractionAvailable is not null, m.GleamingInteractionAvailable.GetValueOrDefault()),
                    VoidStructures = Optional(m.VoidStructures is not null, m.VoidStructures.GetValueOrDefault()),
                    VoidStructuresActivated = Optional(m.VoidStructuresActivated is not null, m.VoidStructuresActivated.GetValueOrDefault()),
                    VoidNodeExists = Optional(m.VoidNodeExists is not null, m.VoidNodeExists.GetValueOrDefault()),
                    VoidAwakeningStage = Optional(m.VoidAwakeningStage is not null, m.VoidAwakeningStage.GetValueOrDefault()),
                    MonolithID = Optional(m.MonolithId is not null, m.MonolithId ?? ""),
                    PendingVoidStructureIDs = [.. m.PendingVoidStructureIds],
                    VoidNodeID = m.VoidNodeId ?? "",
                    VoidNodePawnIDs = [.. m.VoidNodePawnIds],
                }),
            };
        }
        return Known(r);
    }
}
