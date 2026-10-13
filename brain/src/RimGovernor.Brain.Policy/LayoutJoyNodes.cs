namespace RimGovernor.Brain.Policy;

// Joy outings in the station graph (#2997, child of #2949). A colonist's joy need falls while they are awake
// and rises by the sessions they take; the ones taken outdoors cross the base's exterior doors, so they are
// trips to the outside node like the day cycle's. The model, from the game (ilspycmd, RimWorld.Need_Joy,
// JoyUtility.JoyTickCheckEnd, JoyGiver and the JoyGiverDef, JobDef and NeedDef rows of the catalog):
//
//   - Need_Joy falls FallPerInterval a need interval (150 ticks) while the pawn is not gaining joy, by the band
//     of its level: 0.0015 from 0.3 to 0.85, the band a colony that keeps its people Satisfied holds them in.
//     The NeedDef freezes the need asleep (freezeWhileSleeping), so the day's fall is the awake hours of the
//     schedule's template of the day.
//   - A session gains joyGainRate * 0.36 / 2500 a tick for joyDuration ticks (JoyUtility.JoyTickCheckEnd).
//   - A colonist picks a giver by its chance (JoyGiver.GetChance: baseChance), and only the pctPawnsEverDo
//     share of colonists ever do a giver.
//   - A giver is outdoors when its def says so (unroofedOnly, requiresEnjoyOutdoors) or, for the walk, when its
//     class does (the table).
//
// The sessions a day a colonist needs are the fall over the mean gain of a session; the outdoor ones are the
// chance-weighted share of the outdoor givers; each outdoor session is one trip out. One session is one
// outing: no judgment constant stands between them.
//
// Not modelled, so the outings are a lower bound: joy tolerance, the fall-rate stat, weather and night, and
// the givers that need a building or an item. An unread NeedDef, giver or job adds no flow: it is unknown,
// never zero.
public static partial class PolicyConstants
{
    /// <summary>Why a bed has joy trips to the outside node. The edge merges with the bed's day-cycle edge, which keeps its own reason.</summary>
    public const string ReasonFlowJoyOuting = "flow_joy_outing";

    /// <summary>Why the graph holds the recreation node: the joy the colonists need a day.</summary>
    public const string ReasonRecreation = "recreation_joy_need";

    private static readonly JoyTable JoyGameValue = new()
    {
        Need = "Joy",
        IntervalTicks = 150,
        FallPerInterval = 0.0015,
        GainPerTick = 0.36 / 2500,
        OutdoorGivers = ["GoForWalk"],
    };

    internal static JoyTable JoyJudgment => JoyGameValue;
}

/// <summary>
/// The joy model's game numbers the defs do not carry, registered in DefTables so a renamed def fails the
/// dangling-reference gate. IntervalTicks is Pawn_NeedsTracker's need interval, FallPerInterval Need_Joy's
/// Satisfied-band fall, GainPerTick JoyUtility.JoyTickCheckEnd's 0.36 / 2500. OutdoorGivers are the
/// JoyGiverDefs whose class is outdoors although no flag of the def says so.
/// </summary>
public sealed record JoyTable
{
    public string Need { get; init; } = string.Empty;

    public double IntervalTicks { get; init; }

    public double FallPerInterval { get; init; }

    public double GainPerTick { get; init; }

    public IReadOnlyList<string> OutdoorGivers { get; init; } = [];
}

/// <summary>One joy giver a colonist may use without a building: its chance, the joy a session of its job gives before the game's per-tick scale, and whether it is outdoors.</summary>
public sealed record JoySource
{
    public string Giver { get; init; } = string.Empty;

    public double Weight { get; init; }

    public double Session { get; init; }

    public bool Outdoor { get; init; }
}

/// <summary>The catalog's joy givers. The zero value is unknown and leaves joy unsized.</summary>
public sealed record JoyModel
{
    public IReadOnlyList<JoySource> Sources { get; init; } = [];

    /// <summary>The joy demand the model implies; unknown without a source that gives joy.</summary>
    public Fact<RecreationNode> RecreationDemand()
    {
        double weight = 0, gain = 0, outdoor = 0;
        foreach (var s in Sources)
        {
            if (!(s.Weight > 0) || !(s.Session > 0) || double.IsInfinity(s.Weight) || double.IsInfinity(s.Session))
            {
                continue;
            }
            weight += s.Weight;
            gain += s.Weight * s.Session * PolicyConstants.JoyJudgment.GainPerTick;
            if (s.Outdoor)
            {
                outdoor += s.Weight;
            }
        }
        if (weight <= 0 || gain <= 0)
        {
            return Unknown<RecreationNode>();
        }
        var game = PolicyConstants.JoyJudgment;
        var need = game.FallPerInterval * GameTime.TicksPerDay / game.IntervalTicks * PolicyFunctions.AwakeShare();
        var session = gain / weight;
        var sessions = need / session;
        var share = outdoor / weight;
        return Known(new RecreationNode
        {
            NeedPerDay = need,
            Session = session,
            Sessions = sessions,
            OutdoorShare = share,
            Outdoor = sessions * share,
            Indoor = sessions * (1 - share),
            Reason = PolicyConstants.ReasonRecreation,
        });
    }
}

/// <summary>
/// The colony's joy demand: what a colonist needs of recreation a day and how it splits between the outdoors
/// and the indoors. It is a layout fact, not a station; clustering and the embedder ignore it.
/// </summary>
public sealed record RecreationNode
{
    /// <summary>The joy a colonist loses awake in a day.</summary>
    public double NeedPerDay { get; init; }

    /// <summary>The mean joy a session gives.</summary>
    public double Session { get; init; }

    /// <summary>The sessions a colonist takes a day.</summary>
    public double Sessions { get; init; }

    /// <summary>The chance-weighted share of sessions taken outdoors.</summary>
    public double OutdoorShare { get; init; }

    public double Outdoor { get; init; }

    public double Indoor { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public static partial class PolicyFunctions
{
    /// <summary>The table the bridge reads the joy givers with.</summary>
    public static JoyTable JoyGame() => PolicyConstants.JoyJudgment;

    /// <summary>The share of the day a colonist is not on the Sleep block of the schedule template.</summary>
    internal static double AwakeShare()
    {
        var slots = ScheduleTemplate(new TraitEffects());
        var awake = slots.Count(s => s != PolicyConstants.ScheduleSleep);
        return (double)awake / slots.Length;
    }
}

internal sealed partial class GraphBuilder
{
    /// <summary>Adds the joy outings: each healthy sleeper's outdoor sessions a day, trips from their bed to the outside node, and returns the recreation node (unknown without the model).</summary>
    public Fact<RecreationNode> Recreation()
    {
        var demand = In.Catalog.Joy.RecreationDemand();
        if (!demand.TryGetValue(out var node) || In.Population.Colonists <= 0)
        {
            return demand;
        }
        foreach (var n in Nodes.Values)
        {
            if (n.Purpose == StationPurpose.Sleep)
            {
                AddEdge(n.Id, PolicyConstants.OutsideNode, node.Outdoor * HealthySleepers(n), false, PolicyConstants.ReasonFlowJoyOuting);
            }
        }
        return demand;
    }
}
