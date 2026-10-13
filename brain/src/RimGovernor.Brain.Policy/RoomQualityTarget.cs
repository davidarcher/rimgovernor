namespace RimGovernor.Brain.Policy;

/// <summary>
/// The minimum scores of the impressiveness stages the planners aim at: the scoreStages minScore of the
/// game's Impressiveness RoomStatDef, read from the catalog (DefinitionCatalog.ImpressivenessLevels). The
/// stages run awful, dull, mediocre, decent, slightly impressive, and on up; which stage a tier or trait asks
/// for is the planner's choice.
/// </summary>
public sealed record ImpressivenessLevels
{
    public double Dull { get; init; }

    public double Mediocre { get; init; }

    public double Decent { get; init; }

    public double SlightlyImpressive { get; init; }

    /// <summary>Every score stage's minimum score in the game's order (awful first); Stage indexes into them. Empty reads as the four named levels.</summary>
    public EquatableArray<double> Stages { get; init; }

    /// <summary>
    /// The impressiveness stage a score reaches: the index of the highest stage whose minimum score it meets
    /// (0 below the first named stage). Stages beyond the four named levels count only when the catalog listed them.
    /// </summary>
    public int Stage(double score)
    {
        var stages = Stages;
        if (stages.Count == 0)
        {
            stages = [0, Dull, Mediocre, Decent, SlightlyImpressive];
        }
        var stage = 0;
        for (var i = 1; i < stages.Count; i++)
        {
            if (score >= stages[i])
            {
                stage = i;
            }
        }
        return stage;
    }

    /// <summary>The colony-wide floor by tech tier, one native stage per tier: Camp asks nothing, Masonry dull, Powered mediocre, Industrial decent, Spacer slightly impressive.</summary>
    public double Baseline(TechTier tier)
    {
        if (tier >= TechTier.Spacer)
        {
            return SlightlyImpressive;
        }
        if (tier >= TechTier.Industrial)
        {
            return Decent;
        }
        if (tier >= TechTier.Powered)
        {
            return Mediocre;
        }
        if (tier >= TechTier.Masonry)
        {
            return Dull;
        }
        return 0;
    }
}

/// <summary>
/// The impressiveness one owned bedroom should reach. Consumers (room ranking, the gap closer) compare it
/// against the room's observed RoomQuality.Impressiveness.
/// </summary>
public sealed record RoomTarget
{
    public string Room { get; init; } = "";

    public EquatableArray<PawnId> Owners { get; init; }

    /// <summary>The impressiveness the room should reach; zero asks nothing.</summary>
    public double Min { get; init; }

    /// <summary>When positive, the exclusive ceiling the room should stay under (ascetic owners); zero means no ceiling.</summary>
    public double Max { get; init; }

    /// <summary>Marks a room no upgrade should touch: every owner is ascetic and none demands a floor.</summary>
    public bool NeverUpgrade { get; init; }

    /// <summary>Names the constraints that set Min or Max, sorted: "tier", "greedy", "jealous", "title", "ascetic".</summary>
    public EquatableArray<string> Reasons { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// A RoomTarget per dining and rec room, keyed by room id; empty while the room census is unknown. Every
    /// colonist eats and relaxes there, so no one's traits apply: Min is the plain tier baseline, reason
    /// "common". A room holding colonist beds is left to RoomQualityTargets.
    /// </summary>
    public static Dictionary<string, RoomTarget> CommonRoomTargets(SleepingObservation obs, TechTier tier, ImpressivenessLevels levels)
    {
        var targets = new Dictionary<string, RoomTarget>(StringComparer.Ordinal);
        if (!obs.Rooms.TryGetValue(out var rooms))
        {
            return targets;
        }
        var floor = levels.Baseline(tier);
        foreach (var room in rooms)
        {
            if (room.Beds.Count > 0 || (room.Role != Wire.Name(RoomRole.DiningRoom) && room.Role != Wire.Name(RoomRole.RecRoom)))
            {
                continue;
            }
            var t = new RoomTarget { Room = room.Id, Min = floor };
            if (floor > 0)
            {
                t = t with { Reasons = ["common"] };
            }
            targets[room.Id] = t;
        }
        return targets;
    }

    /// <summary>
    /// A RoomTarget per owned bedroom, keyed by room id; empty while the room census is unknown. A bedroom is
    /// a room holding a humanlike, non-medical, non-prisoner bed with owners. traits carries each pawn's
    /// TraitEffects; a missing entry reads as no relevant trait.
    /// <para>
    /// Min is the ceiling the room climbs to, not a spend it demands: the in-place upgrade planners place one
    /// piece at a time while the room is below it, and RoomGate refuses a step the owners' remaining personal
    /// share does not pay for (a necessity, Baseline(Camp) = 0, is never charged).
    /// </para>
    /// <para>
    /// Per owner, the floor is the max of: the tier baseline (ImpressivenessLevels.Baseline), unless the owner
    /// is ascetic; Greedy: slightly impressive, the first stage ThoughtWorker_Greedy leaves null in Core;
    /// Jealous: the highest impressiveness among the colony's other owned bedrooms
    /// (ThoughtWorker_BedroomJealous fires on any better one); a royal title's BedroomMinImpressiveness.
    /// </para>
    /// <para>
    /// An Ascetic owner caps the room below decent: ThoughtWorker_Ascetic's mood bonus covers only awful, dull
    /// and mediocre. Several owners (a couple) combine as: Min is the largest owner floor; the ascetic ceiling
    /// applies only when every owner is ascetic and Min stays below it. A shared room cannot please both a
    /// greedy and an ascetic partner, and the demanded floor wins because a missed demand costs more mood than
    /// the lost ascetic bonus. NeverUpgrade holds only when the ceiling holds and Min is zero.
    /// </para>
    /// </summary>
    public static Dictionary<string, RoomTarget> RoomQualityTargets(
        SleepingObservation obs, IReadOnlyDictionary<PawnId, TraitEffects> traits, TechTier tier, ImpressivenessLevels levels)
    {
        var targets = new Dictionary<string, RoomTarget>(StringComparer.Ordinal);
        if (!obs.Rooms.TryGetValue(out var rooms))
        {
            return targets;
        }
        var impressiveness = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            if (room.Quality.TryGetValue(out var q))
            {
                impressiveness[room.Id] = q.Impressiveness;
            }
        }
        var titles = new Dictionary<PawnId, RoyalTitle?>();
        foreach (var p in obs.People)
        {
            titles[p.Id] = p.Title;
        }
        var owners = new Dictionary<string, List<PawnId>>(StringComparer.Ordinal);
        foreach (var bed in obs.Beds)
        {
            var ok = bed.Room.TryGetValue(out var room);
            bed.Humanlike.TryGetValue(out var humanlike);
            bed.Medical.TryGetValue(out var medical);
            bed.Prisoners.TryGetValue(out var prisoners);
            if (!ok || string.IsNullOrEmpty(room) || !humanlike || medical || prisoners)
            {
                continue;
            }
            foreach (var o in bed.Owners)
            {
                if (!owners.TryGetValue(room, out var list))
                {
                    owners[room] = list = [];
                }
                if (!list.Contains(o))
                {
                    list.Add(o);
                }
            }
        }
        foreach (var (room, pawns) in owners)
        {
            pawns.Sort();
            double min = 0, max = 0;
            var neverUpgrade = false;
            var reasons = new HashSet<string>(StringComparer.Ordinal);

            void Raise(double v, string why)
            {
                if (v > min)
                {
                    min = v;
                }
                // A trait or title names itself only when it asks above the tier baseline, so a suite claim
                // can tell a raised target from a tier-only one.
                if (v > 0 && (why == "tier" || v > levels.Baseline(tier)))
                {
                    reasons.Add(why);
                }
            }

            var allAscetic = true;
            foreach (var pawn in pawns)
            {
                var e = traits.TryGetValue(pawn, out var found) ? found : new TraitEffects();
                if (!e.Ascetic)
                {
                    allAscetic = false;
                    Raise(levels.Baseline(tier), "tier");
                }
                if (e.Greedy)
                {
                    Raise(levels.SlightlyImpressive, "greedy");
                }
                if (e.Jealous)
                {
                    var best = 0.0;
                    foreach (var other in owners.Keys)
                    {
                        if (other != room && impressiveness.TryGetValue(other, out var v) && v > best)
                        {
                            best = v;
                        }
                    }
                    Raise(best, "jealous");
                }
                var title = titles.GetValueOrDefault(pawn);
                if (title is not null)
                {
                    Raise(title.BedroomMinImpressiveness, "title");
                }
            }
            if (allAscetic && min < levels.Decent)
            {
                max = levels.Decent;
                reasons.Add("ascetic");
                neverUpgrade = min == 0;
            }
            targets[room] = new RoomTarget
            {
                Room = room,
                Owners = EquatableArray.From(pawns),
                Min = min,
                Max = max,
                NeverUpgrade = neverUpgrade,
                Reasons = EquatableArray.From(reasons.Order(StringComparer.Ordinal)),
            };
        }
        return targets;
    }
}
