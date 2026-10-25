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
    public sealed record Active(RimGovernor.Brain.GameFacts.Observations.AnomalyColonyFacts Monolith) : AnomalyState;
}

/// <summary>Policy for the spawned native monolith and its endgame interactions.</summary>
public static partial class MonolithRules
{
    /// <summary>
    /// The awakening quest's next interaction, in the order the quest offers them: the void node, the Gleaming
    /// monolith, then a void structure. Nothing is owed (no interaction, no hold) outside the quest. A node nobody
    /// on its map can touch is a hold.
    /// </summary>
    internal static (bool Interact, string Hold) Endgame(this RimGovernor.Brain.GameFacts.Observations.MonolithState thing)
    {
        if (thing.VoidNodeId is not null)
        {
            if (thing.VoidNodePawnIds.Count == 0)
            {
                return (false, "no colonist on the void node's map can touch it");
            }
            return (true, "");
        }
        if (thing.GleamingInteractionAvailable)
        {
            return (thing.MonolithId != "", "");
        }
        if (thing.PendingVoidStructureIds.Count > 0)
        {
            return (thing.PendingVoidStructureIds[0] != "", "");
        }
        return (false, "");
    }

    internal static string CannotActivate(this RimGovernor.Brain.GameFacts.Observations.MonolithState thing)
    {
        if (thing.CodexShortfall is var shortfall and > 0)
        {
            return $"the codex is short {shortfall} entries for the next level";
        }
        if (thing.BlockingConditions.Count > 0)
        {
            return "the monolith is unreachable during " + string.Join(", ", thing.BlockingConditions);
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
    public double Capacity { get; init; }

    /// <summary>The native threat census used to price the awakening waves.</summary>
    public required RimGovernor.Brain.GameFacts.Observations.ThreatFacts Threat { get; init; }

    public HostileCensus Hostiles { get; init; } = new HostileCensus.AwaitingEmergency();
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
    public static MonolithAdvance MonolithAdvanceOwed(RimGovernor.Brain.GameFacts.Observations.AnomalyColonyFacts f, AwakenGate gate)
    {
        if (f.Monolith is { CodexShortfall: < 0 })
        {
            throw new DomainException("negative monolith codex shortfall");
        }
        var none = new MonolithAdvance();
        if (f.Monolith is not { } thing || f.Incidents.AmbientHorrorMode)
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
        if (f.Incidents.Level == 0)
        {
            return outcome with { Order = MonolithOrder.Investigate };
        }
        if (thing.NextLevelDef is not { } next)
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

        var capacity = g.Capacity;
        var points = g.Threat.RaidPoints;
        if (capacity < AwakenStrengthFactor * points)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"defense capacity {capacity:F0} is below {AwakenStrengthFactor:F2} times the {points:F0} raid points of the awakening waves"));
        }
        if (g.Hostiles is HostileCensus.AwaitingEmergency)
        {
            issues.Add("whether a threat stands is unread");
        }
        else if (g.Hostiles.ThreatCount is > 0 and var hostiles)
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
    public static bool MonolithInPlay(RimGovernor.Brain.GameFacts.Observations.AnomalyColonyFacts f) => f.Incidents.MonolithSpawned && !f.Incidents.AmbientHorrorMode;

    /// <summary>The advance gate the facts give.</summary>
    public static AwakenGate MonolithGate(ReviewFacts f) =>
        new()
        {
            Capacity = f.DefenseCapacity,
            Threat = f.Census.Threat.Observed,
            Hostiles = f.Hostiles,
            Containment = f.Containment,
        };
}
