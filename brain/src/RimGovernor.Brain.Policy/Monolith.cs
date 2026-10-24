using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The colony's Anomaly read, by the colony section's Anomaly gate: Inactive without the expansion (the anomaly
/// family derives no Needs), Active with the monolith's state.
/// </summary>
public abstract record AnomalyState
{
    private protected AnomalyState() { }

    /// <summary>The Anomaly expansion is not active.</summary>
    public sealed record Inactive : AnomalyState;

    /// <summary>The Anomaly expansion is active: GameComponent_Anomaly's monolith state.</summary>
    public sealed record Active(MonolithFacts Monolith) : AnomalyState;
}

/// <summary>
/// The monolith's observed state: GameComponent_Anomaly's AmbientHorror and level, and the monolith thing's own
/// read (null unless it is spawned).
/// </summary>
public sealed record MonolithFacts
{
    public required bool AmbientHorror { get; init; }

    public required int Level { get; init; }

    public required MonolithThing? Thing { get; init; }

    /// <summary>GameComponent_Anomaly's MonolithSpawned.</summary>
    public bool Spawned => Thing is not null;
}

/// <summary>The spawned monolith and the endgame it gates.</summary>
public sealed record MonolithThing
{
    /// <summary>The monolith thing.</summary>
    public required string Id { get; init; }

    /// <summary>The monolith's own CanActivate verdict.</summary>
    public required bool CanActivate { get; init; }

    /// <summary>The def activation would reach; null when the level does not advance through activation.</summary>
    public required string? NextLevel { get; init; }

    /// <summary>The entries still undiscovered for the next level.</summary>
    public required uint CodexShortfall { get; init; }

    /// <summary>The active conditions the next level is unreachable under.</summary>
    public required IReadOnlyList<string> Blocking { get; init; }

    /// <summary>Whether the Gleaming monolith interaction is available.</summary>
    public required bool Gleaming { get; init; }

    /// <summary>The VoidStructures still to activate.</summary>
    public required IReadOnlyList<string> PendingStructures { get; init; }

    /// <summary>The VoidNode that can be touched (empty when none).</summary>
    public required string NodeId { get; init; }

    /// <summary>The colonists on the node's map able to touch it.</summary>
    public required IReadOnlyList<string> NodePawns { get; init; }

    /// <summary>
    /// The awakening quest's next interaction, in the order the quest offers them: the void node, the Gleaming
    /// monolith, then a void structure. Nothing is owed (no interaction, no hold) outside the quest. A node nobody
    /// on its map can touch is a hold.
    /// </summary>
    internal (bool Interact, string Hold) Endgame()
    {
        if (NodeId != "")
        {
            if (NodePawns.Count == 0)
            {
                return (false, "no colonist on the void node's map can touch it");
            }
            return (true, "");
        }
        if (Gleaming)
        {
            return (Id != "", "");
        }
        if (PendingStructures.Count > 0)
        {
            return (PendingStructures[0] != "", "");
        }
        return (false, "");
    }

    internal string CannotActivate()
    {
        if (CodexShortfall is var shortfall and > 0)
        {
            return $"the codex is short {shortfall} entries for the next level";
        }
        if (Blocking.Count > 0)
        {
            return "the monolith is unreachable during " + string.Join(", ", Blocking);
        }
        return "the game does not allow activation now";
    }
}

/// <summary>The job the monolith advance sends a colonist on.</summary>
public enum MonolithOrder
{
    [WireName("")]
    None = 0,
    Investigate,
    Activate,

    /// <summary>The awakening quest's interaction with Target.</summary>
    Interact,
}

/// <summary>What the monolith advance needs the colony to be: for the awakening defended against the waves, with containment stable and no threat standing.</summary>
public sealed record AwakenGate
{
    /// <summary>The observed defense capacity.</summary>
    public Fact<double> Capacity { get; init; }

    /// <summary>The points a default threat draws, in raid-point units.</summary>
    public Fact<double> RaidPoints { get; init; }

    public Fact<long> Hostiles { get; init; }
    public ContainmentPlanning Containment { get; init; } = new();
}

/// <summary>
/// The rule's decision. Order is None when nothing is owed; Hold says why an advance the game allows is withheld
/// (or the game does not allow it yet), and Issues name the unread facts that kept the rule from deciding.
/// </summary>
public sealed record MonolithAdvance
{
    public MonolithOrder Order { get; init; }
    public string Hold { get; init; } = "";
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>
/// The colony advances the void monolith through its activation levels. The rules are
/// the game's, from the decompile: the Inactive monolith (level 0) is investigated; every later level is
/// ActivateMonolith directly; both run only while the game's own CanActivate holds. Activating into VoidAwakened
/// is the awakening: it cannot be undone and starts the EndGame_VoidAwakening quest. Ambient Horror mode has no
/// monolith questline; the rule is inert there. No level is advanced before the colony has reached the Stable
/// stage (#630); the awakening is further ordered only when the colony is strong enough for those waves,
/// containment is stable and no threat stands. An unread colony fact is a loud issue and holds the order, never a guess.
/// The awakening quest already under way is walked at any stage.
/// </summary>
public static partial class MonolithRules
{
    /// <summary>MonolithLevelDefOf.VoidAwakened, the level whose activation is the awakening.</summary>
    public const string MonolithLevelVoidAwakened = "VoidAwakened";

    /// <summary>The largest points factor of an EndGame_VoidAwakening wave: the colony's defense capacity must reach this multiple of the raid points the waves are drawn from.</summary>
    public const double AwakenStrengthFactor = 1.25;

    /// <summary>Decides the monolith's next step.</summary>
    public static MonolithAdvance MonolithAdvanceOwed(MonolithFacts f, AwakenGate gate)
    {
        var none = new MonolithAdvance();
        if (f.Thing is not { } thing || f.AmbientHorror)
        {
            return none;
        }
        var outcome = none;
        var (interact, hold) = thing.Endgame();
        if (interact || hold != "")
        {
            return outcome with { Hold = hold, Order = interact ? MonolithOrder.Interact : MonolithOrder.None };
        }
        if (!thing.CanActivate)
        {
            return outcome with { Hold = thing.CannotActivate() };
        }
        if (f.Level == 0)
        {
            return outcome with { Order = MonolithOrder.Investigate };
        }
        if (thing.NextLevel is not { } next)
        {
            return outcome with { Hold = "no level follows the monolith's through activation" };
        }
        outcome = outcome with { Order = MonolithOrder.Activate };
        if (next != MonolithLevelVoidAwakened)
        {
            return outcome;
        }
        var (awakenHold, awakenIssues) = AwakenHold(gate);
        outcome = outcome with { Hold = awakenHold, Issues = awakenIssues };
        if (outcome.Hold != "" || outcome.Issues.Count > 0)
        {
            outcome = outcome with { Order = MonolithOrder.None };
        }
        return outcome;
    }

    /// <summary>Why the awakening waits: the first standing reason, or the unread facts that keep it from deciding.</summary>
    private static (string Hold, List<string> Issues) AwakenHold(AwakenGate g)
    {
        var hold = "";
        var issues = new List<string>();
        void Note(string reason)
        {
            if (hold == "")
            {
                hold = reason;
            }
        }

        var ck = g.Capacity.TryGetValue(out var capacity);
        var pk = g.RaidPoints.TryGetValue(out var points);
        if (!ck)
        {
            issues.Add("the defense capacity is unread");
        }
        else if (!pk)
        {
            issues.Add("the raid points are unread");
        }
        else if (capacity < AwakenStrengthFactor * points)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"defense capacity {capacity:F0} is below {AwakenStrengthFactor:F2} times the {points:F0} raid points of the awakening waves"));
        }
        if (!g.Hostiles.TryGetValue(out var hostiles))
        {
            issues.Add("whether a threat stands is unread");
        }
        else if (hostiles > 0)
        {
            Note($"{hostiles} threats stand");
        }
        var (stable, reason, unread) = ContainmentStable(g.Containment);
        issues.AddRange(unread);
        if (!stable && reason != "")
        {
            Note(reason);
        }
        return (hold, issues);
    }

    /// <summary>Whether every living entity is held, none is escaping and no cell door stands open or breached. Reason names the first thing that is not; Unread lists the facts that kept it from deciding.</summary>
    private static (bool Stable, string Reason, List<string> Unread) ContainmentStable(ContainmentPlanning p)
    {
        var unread = new List<string>();
        var reason = "";
        var upkeep = EntityUpkeepRules.ContainmentDoorUpkeep(p);
        foreach (var issue in upkeep.Issues)
        {
            unread.Add(issue.Reason);
        }
        if (upkeep.CloseDoors.Count > 0)
        {
            reason = "a cell door stands open";
        }
        foreach (var e in p.Entities)
        {
            if (e.Dead || reason != "")
            {
                // A dead entity needs no containment.
                continue;
            }
            if (!e.Held)
            {
                reason = $"entity {e.Pawn} is free";
            }
            else if (e.Target is { Escaping: true })
            {
                reason = $"entity {e.Pawn} is escaping";
            }
        }
        return (reason == "" && unread.Count == 0, reason, unread);
    }

    /// <summary>Whether the monolith is spawned and its questline runs (not Ambient Horror mode): the only time the rule reads anything further.</summary>
    public static bool MonolithInPlay(MonolithFacts f) => f.Spawned && !f.AmbientHorror;

    /// <summary>The advance gate the facts give.</summary>
    public static AwakenGate MonolithGate(RoundsFacts f) =>
        new()
        {
            Capacity = f.DefenseCapacity,
            RaidPoints = f.RaidPoints,
            Hostiles = f.Hostiles,
            Containment = f.Containment,
        };
}
