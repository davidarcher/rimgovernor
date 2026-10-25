namespace RimGovernor.Brain.Policy;

/// <summary>
/// The colony's rest schedule as a bed group's disturbed-sleep price reads it (#3750): per hour of the day (hour 0 first),
/// the share of the colony's workers asleep in it and the share of a day's station visits made in it (the workers on Work
/// or Anything then, each hour as many visits as workers).
/// </summary>
public sealed record RestSchedule(EquatableArray<double> Asleep, EquatableArray<double> Visits)
{
    /// <summary>The hours of a timetable (Pawn_TimetableTracker: one TimeAssignmentDef an hour of the day).</summary>
    private const int Hours = GameTime.TicksPerDay / GameTime.TicksPerHour;

    /// <summary>The share of a day's visits a sleeper sleeps through: each hour's visits times the share of the workers asleep in it.</summary>
    public double DisturbedShare()
    {
        double share = 0;
        for (var h = 0; h < Math.Min(Asleep.Count, Visits.Count); h++)
        {
            share += Visits[h] * Asleep[h];
        }
        return share;
    }

    /// <summary>
    /// The schedule of the colony's workers from their timetables (Pawn_TimetableTracker's TimeAssignmentDef names, hour
    /// 0 first). Null, an unread schedule, when a timetable is not 24 hours or a slot holds an assignment not classified
    /// as Sleep, Work, Anything, Joy or Meditate. With no worker on Work or Anything no visit is made.
    /// </summary>
    public static RestSchedule? Of(IEnumerable<IReadOnlyList<string>> timetables)
    {
        var asleep = new double[Hours];
        var working = new double[Hours];
        var workers = 0;
        foreach (var slots in timetables)
        {
            if (slots.Count != Hours)
            {
                return null;
            }
            for (var h = 0; h < slots.Count; h++)
            {
                switch (slots[h])
                {
                    case PawnRules.ScheduleSleep:
                        asleep[h]++;
                        break;
                    case PawnRules.ScheduleWork:
                    case PawnRules.ScheduleAnything:
                        working[h]++;
                        break;
                    case PawnRules.ScheduleJoy:
                    case PawnRules.ScheduleMeditate:
                        break;
                    default:
                        return null;
                }
            }
            workers++;
        }
        var visits = working.Sum();
        for (var h = 0; h < Hours; h++)
        {
            asleep[h] = workers > 0 ? asleep[h] / workers : 0;
            working[h] = visits > 0 ? working[h] / visits : 0;
        }
        return new RestSchedule(EquatableArray.From(asleep), EquatableArray.From(working));
    }
}

/// <summary>
/// A bed group hosting kitchen, dining, butchery or freezer stations (#3750) is priced, not refused: the sleepers' cost of
/// the stations is a lost effect of the group (Eval), so the seed keeps the pair apart and the regrouping merges it only
/// when the walk saved beats the cost.
/// </summary>
/// <remarks>
/// Disturbed sleep. A sleeping colonist gains the SleepDisturbed memory when a pawn moves near it
/// (Pawn.CheckForDisturbedSleep). The group's visits a day are its graph movements that start or end at one of its
/// hosted stations (a movement between two of them one visit), each unit its share of the node's; the share of them made
/// while a sleeper sleeps is the rest schedule's (RestSchedule.DisturbedShare); and the memories they keep alive
/// (MemoryThought.Mood) are the mood each sleeper loses, priced by PriceMood at the colonists' mean mood.
///
/// Cleanliness and impressiveness. Each butchery run leaves its filth (LayoutFilthRules, the units standing at once and
/// the share of the day some stands, Residual); while it stands the room's Cleanliness is those units' over its cells,
/// which lowers its Impressiveness (RoomSpaceRules, the bare room of its interior otherwise) and so, perhaps, its stage
/// (ImpressivenessLevels.Stage). The sleepers then gain their bed thought at the lower stage: the mean sleeper's
/// bedroom (or barracks) gain at each stage (BedMergeFacts.Sleepers, ForecastHousing.BedroomGain) for each sleeper,
/// times the share of the day the filth stands.
///
/// Every input is required where it is read: an unread rest schedule, SleepDisturbed def, filth model or sleeper
/// gains (a bed group whose pool the bed merge did not price) throws a RequiredFactException naming it.
/// </remarks>
internal sealed partial class Clusterer
{
    private readonly Dictionary<int, bool> _hosted = [];

    /// <summary>The impressiveness stages and each sleeper's gains at them, as ChooseBeds read them; null before the free-bed pool is priced.</summary>
    private StageGains? _stageGains;

    private sealed record StageGains(ImpressivenessLevels Levels, EquatableArray<SleeperGains> Sleepers);

    /// <summary>Whether node n's stations are a kitchen, dining, butchery or freezer station: a dining or cold-store use, or a work station the game alone reads as a kitchen or dining room (a stove, a butcher table).</summary>
    private bool Hosted(int n)
    {
        if (_hosted.TryGetValue(n, out var hosted))
        {
            return hosted;
        }
        var node = _in.Graph.Nodes[n];
        hosted = node.Purpose is StationPurpose.Dine or StationPurpose.StoreCold;
        if (!hosted && node.Purpose == StationPurpose.Work)
        {
            var piece = new RoomPiece { Def = node.Def, State = LayoutClusterRules.PieceState(node.Purpose, _in.Catalog.Stations[node.Def]) };
            hosted = RoomRoleRules.PredictRole(_in.Catalog, [piece]).Role is RoomRole.Kitchen or RoomRole.DiningRoom;
        }
        _hosted[n] = hosted;
        return hosted;
    }

    /// <summary>Adds the sleepers' cost of the hosted stations to e, the evaluation of the units us: disturbed sleep and the filth's impressiveness stage, each a lost effect when it costs anything.</summary>
    private void BedStationEffects(IReadOnlyList<ClusterUnit> us, GroupEval e)
    {
        var sleepers = 0;
        var share = new Dictionary<string, double>(StringComparer.Ordinal); // the share of each hosted node's stations the group holds
        foreach (var u in us)
        {
            var n = Node(u);
            if (n.Purpose == StationPurpose.Sleep)
            {
                var cp = _in.Catalog.Stations[n.Def].Capacity;
                sleepers += cp.Kind == CapacityKind.Sleepers ? cp.Slots * u.Count : 0;
            }
            else if (n.Count > 0 && Hosted(u.Node))
            {
                share[n.Id] = share.GetValueOrDefault(n.Id) + ((double)u.Count / n.Count);
            }
        }
        if (sleepers == 0 || share.Count == 0)
        {
            return;
        }
        DisturbedSleep(e, sleepers, share);
        if (_in.BedMerge?.Facts is BedMergeFacts.NoStuffObtainable) return;
        FilthStage(e, sleepers);
    }

    private void DisturbedSleep(GroupEval e, int sleepers, Dictionary<string, double> share)
    {
        var visits = 0.0;
        foreach (var edge in _in.Graph.Edges)
        {
            visits += edge.Weight * Math.Min(1, share.GetValueOrDefault(edge.A) + share.GetValueOrDefault(edge.B));
        }
        if (!(visits > 0))
        {
            return;
        }
        var rest = _in.Rest ?? throw new RequiredFactException("rest_schedule");
        var perDay = visits * rest.DisturbedShare();
        if (!(perDay > 0))
        {
            return;
        }
        var thought = _in.Catalog.Mood.SleepDisturbed ?? throw new RequiredFactException("sleep_disturbed_thought");
        var points = -thought.Mood(perDay);
        if (points > 0)
        {
            e.Effects.Add(new LostRoleEffect(RoleEffect.SleepDisturbed, e.Role, e.Role, sleepers) { Points = points });
            e.Lost.Add(Wire.Name(RoleEffect.SleepDisturbed));
        }
    }

    private void FilthStage(GroupEval e, int sleepers)
    {
        if (e.Runs.Count == 0 || e.Runs.Values.All(r => !(r.Runs > 0)))
        {
            return;
        }
        var filth = _in.Catalog.Filth;
        if (filth.Def == string.Empty || !QuestCapacityForecast.FinitePositive(filth.StandTicks))
        {
            throw new RequiredFactException("filth_model");
        }
        // The stages and the sleepers' gains at each are the bed merge's, read when it chose the free-bed pool's rooms.
        var (levels, gains) = _stageGains ?? throw new RequiredFactException("sleeper_mood");
        var (units, exposure) = LayoutFilthRules.Standing(e.Runs.Values, filth.StandTicks);
        var space = RoomSpaceRules.Space(e.Area);
        var clean = levels.Stage(RoomSpaceRules.Impressiveness(space, 0, 0, 0));
        var dirty = levels.Stage(RoomSpaceRules.Impressiveness(space, 0, units * filth.Cleanliness / e.Area, 0));
        if (dirty >= clean || gains.Count == 0)
        {
            return;
        }
        var barracks = e.Role == RoomRole.Barracks;
        double lost = 0;
        foreach (var s in gains)
        {
            var stages = barracks ? s.Barracks : s.Bedroom;
            lost += stages[clean] - stages[dirty];
        }
        var days = lost / gains.Count * sleepers * exposure;
        if (days > 0)
        {
            e.Effects.Add(new LostRoleEffect(RoleEffect.Impressiveness, e.Role, e.Role, sleepers) { Days = days });
            e.Lost.Add(Wire.Name(RoleEffect.Impressiveness));
        }
    }
}
