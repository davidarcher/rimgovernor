using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>The monolith's observed state: each fact is unknown when native did not read it.</summary>
public sealed record MonolithFacts
{
    /// <summary>GameComponent_Anomaly's Spawned and AmbientHorror.</summary>
    public Fact<bool> Spawned { get; init; }

    public Fact<bool> AmbientHorror { get; init; }

    /// <summary>GameComponent_Anomaly's level.</summary>
    public Fact<int> Level { get; init; }

    /// <summary>The monolith thing.</summary>
    public Fact<string> Id { get; init; }

    /// <summary>The monolith's own CanActivate verdict.</summary>
    public Fact<bool> CanActivate { get; init; }

    /// <summary>The def activation would reach.</summary>
    public Fact<string> NextLevel { get; init; }

    /// <summary>The entries still undiscovered for the next level.</summary>
    public Fact<uint> CodexShortfall { get; init; }

    /// <summary>The active conditions the next level is unreachable under.</summary>
    public IReadOnlyList<string> Blocking { get; init; } = [];

    /// <summary>Whether the Gleaming monolith interaction is available.</summary>
    public Fact<bool> Gleaming { get; init; }

    /// <summary>The VoidStructures still to activate.</summary>
    public IReadOnlyList<string> PendingStructures { get; init; } = [];

    /// <summary>The VoidNode that can be touched (empty when none).</summary>
    public string NodeId { get; init; } = "";

    /// <summary>The colonists on the node's map able to touch it.</summary>
    public IReadOnlyList<string> NodePawns { get; init; } = [];

    /// <summary>
    /// The awakening quest's next interaction, in the order the quest offers them: the void node, the Gleaming
    /// monolith, then a void structure. Nothing is owed (empty target, no hold) outside the quest. A node nobody
    /// on its map can touch is a hold; an unread Gleaming fact is an issue.
    /// </summary>
    internal (string Target, IReadOnlyList<string> Performers, string Hold, IReadOnlyList<string> Issues) Endgame(string monolith)
    {
        if (NodeId != "")
        {
            if (NodePawns.Count == 0)
            {
                return ("", [], "no colonist on the void node's map can touch it", []);
            }
            return (NodeId, NodePawns, "", []);
        }
        if (!Gleaming.TryGetValue(out var gleaming))
        {
            return ("", [], "", ["whether the Gleaming monolith can be used is unread"]);
        }
        if (gleaming)
        {
            return (monolith, [], "", []);
        }
        if (PendingStructures.Count > 0)
        {
            return (PendingStructures[0], [], "", []);
        }
        return ("", [], "", []);
    }

    internal string CannotActivate()
    {
        if (CodexShortfall.TryGetValue(out var shortfall) && shortfall > 0)
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

/// <summary>What the monolith advance needs the colony to be: at least Stable, and for the awakening defended against the waves, with containment stable and no threat standing.</summary>
public sealed record AwakenGate
{
    /// <summary>The colony stage of the review; the default is Foothold.</summary>
    public ColonyStage Stage { get; init; }

    /// <summary>The observed defense capacity.</summary>
    public Fact<double> Capacity { get; init; }

    /// <summary>The points a default threat draws, in raid-point units.</summary>
    public Fact<double> RaidPoints { get; init; }

    public Fact<long> Hostiles { get; init; }
    public ContainmentPlanning Containment { get; init; } = new();
}

/// <summary>
/// The rule's decision. Order is None when nothing is owed; Hold says why an advance the game allows is withheld
/// (or the game does not allow it yet), and Issues name the unread facts that kept the rule from deciding. Target
/// is the thing a MonolithInteract order is for; Performers, when set, are the only pawns that can carry it out
/// (the void node's, on the pocket map), otherwise any able colonist on the colony map.
/// </summary>
public sealed record MonolithAdvance
{
    public MonolithOrder Order { get; init; }
    public string Monolith { get; init; } = "";
    public int Level { get; init; }
    public bool Awaken { get; init; }
    public string Target { get; init; } = "";
    public IReadOnlyList<string> Performers { get; init; } = [];
    public string Hold { get; init; } = "";
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>
/// Ports <c>monolith.go</c>. The colony advances the void monolith through its activation levels. The rules are
/// the game's, from the decompile: the Inactive monolith (level 0) is investigated; every later level is
/// ActivateMonolith directly; both run only while the game's own CanActivate holds. Activating into VoidAwakened
/// is the awakening: it cannot be undone and starts the EndGame_VoidAwakening quest. Ambient Horror mode has no
/// monolith questline; the rule is inert there. No level is advanced before the colony has reached the Stable
/// stage (#630); the awakening is further ordered only when the colony is strong enough for those waves,
/// containment is stable and no threat stands. An unread fact is a loud issue and holds the order, never a guess.
/// The awakening quest already under way is walked at any stage.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>MonolithLevelDefOf.VoidAwakened, the level whose activation is the awakening.</summary>
    public const string MonolithLevelVoidAwakened = "VoidAwakened";

    /// <summary>The largest points factor of an EndGame_VoidAwakening wave: the colony's defense capacity must reach this multiple of the raid points the waves are drawn from.</summary>
    public const double AwakenStrengthFactor = 1.25;

    /// <summary>Decides the monolith's next step.</summary>
    public static MonolithAdvance MonolithAdvanceOwed(Fact<MonolithFacts> m, AwakenGate gate)
    {
        var none = new MonolithAdvance();
        if (!m.TryGetValue(out var f))
        {
            return none;
        }
        if (!f.Spawned.TryGetValue(out var spawned) || !spawned)
        {
            return none;
        }
        if (!f.AmbientHorror.TryGetValue(out var horror))
        {
            return none with { Issues = ["whether Ambient Horror mode is on is unread"] };
        }
        if (horror)
        {
            return none;
        }
        var lk = f.Level.TryGetValue(out var level);
        var ik = f.Id.TryGetValue(out var id);
        var ck = f.CanActivate.TryGetValue(out var can);
        var missing = new List<string>();
        foreach (var (isKnown, what) in new[] { (lk, "the monolith level"), (ik, "the monolith thing"), (ck, "whether the monolith can be activated") })
        {
            if (!isKnown)
            {
                missing.Add(what + " is unread");
            }
        }
        if (missing.Count > 0)
        {
            return none with { Issues = missing };
        }
        var outcome = none with { Monolith = id!, Level = level };
        var (target, performers, hold, issues) = f.Endgame(id!);
        if (target != "" || hold != "" || issues.Count > 0)
        {
            outcome = outcome with { Hold = hold, Issues = issues };
            if (target != "")
            {
                outcome = outcome with { Order = MonolithOrder.Interact, Target = target, Performers = performers };
            }
            return outcome;
        }
        if (gate.Stage < ColonyStage.Stable)
        {
            return outcome with { Hold = $"colony stage {Wire.Name(gate.Stage)} has not reached {Wire.Name(ColonyStage.Stable)}" };
        }
        if (!can)
        {
            return outcome with { Hold = f.CannotActivate() };
        }
        if (level == 0)
        {
            return outcome with { Order = MonolithOrder.Investigate };
        }
        if (!f.NextLevel.TryGetValue(out var next))
        {
            return outcome with { Issues = ["the level the monolith would reach is unread"] };
        }
        outcome = outcome with { Order = MonolithOrder.Activate };
        if (next != MonolithLevelVoidAwakened)
        {
            return outcome;
        }
        var (awakenHold, awakenIssues) = AwakenHold(gate);
        outcome = outcome with { Awaken = true, Hold = awakenHold, Issues = awakenIssues };
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
        if (!p.Entities.TryGetValue(out var entities))
        {
            return (false, "", ["the entities are unread"]);
        }
        if (!p.Holders.IsKnown)
        {
            return (false, "", ["the holding platforms are unread"]);
        }
        var reason = "";
        var upkeep = ContainmentDoorUpkeep(p);
        foreach (var issue in upkeep.Issues)
        {
            unread.Add(issue.Reason);
        }
        if (upkeep.CloseDoors.Count > 0)
        {
            reason = "a cell door stands open";
        }
        foreach (var e in entities)
        {
            var dk = e.Dead.TryGetValue(out var dead);
            var hk = e.Held.TryGetValue(out var held);
            var ek = e.Escaping.TryGetValue(out var escaping);
            if (!dk)
            {
                unread.Add($"whether entity {e.Pawn} is dead is unread");
            }
            else if (dead)
            {
                // A dead entity needs no containment.
            }
            else if (!hk)
            {
                unread.Add($"whether a platform holds entity {e.Pawn} is unread");
            }
            else if (!held && reason == "")
            {
                reason = $"entity {e.Pawn} is free";
            }
            else if (held && !ek)
            {
                unread.Add($"whether entity {e.Pawn} is escaping is unread");
            }
            else if (held && escaping && reason == "")
            {
                reason = $"entity {e.Pawn} is escaping";
            }
        }
        return (reason == "" && unread.Count == 0, reason, unread);
    }

    /// <summary>Whether the monolith is spawned and its questline runs (not Ambient Horror mode): the only time the rule reads anything further.</summary>
    public static bool MonolithInPlay(Fact<MonolithFacts> m)
    {
        if (!m.TryGetValue(out var f))
        {
            return false;
        }
        var sk = f.Spawned.TryGetValue(out var spawned);
        var hk = f.AmbientHorror.TryGetValue(out var horror);
        return sk && spawned && (!hk || !horror);
    }

    /// <summary>Whether the monolith is to be advanced now: a standing work for MaintainPopulation, whose custody step carries it.</summary>
    internal static bool MonolithAdvanceOwedNow(RoundsFacts f, ColonyStage stage) => MonolithAdvanceOwed(f.Monolith, MonolithGate(f, stage)).Order != MonolithOrder.None;

    /// <summary>The advance gate the facts and the review's stage give.</summary>
    public static AwakenGate MonolithGate(RoundsFacts f, ColonyStage stage) =>
        new()
        {
            Stage = stage,
            Capacity = f.DefenseCapacity,
            RaidPoints = f.RaidPoints,
            Hostiles = f.Hostiles,
            Containment = f.Containment,
        };
}
