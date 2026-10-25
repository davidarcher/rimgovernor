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
/// A bed group hosting other stations (#3750, #3817) is priced, not refused: the sleepers' cost of
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
    /// <summary>The impressiveness stages and each sleeper's gains at them, as ChooseBeds read them; null before the free-bed pool is priced.</summary>
    private StageGains? _stageGains;

    private sealed record StageGains(ImpressivenessLevels Levels, EquatableArray<SleeperGains> Sleepers);

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
            else if (n.Count > 0 && n.Purpose is not (StationPurpose.SleepMed or StationPurpose.SleepPrison or StationPurpose.SleepBaby) && !n.Purpose.ClusterAdded())
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
        var points = PlannedRoomCredits.DisturbedMood(_in.Graph, _in.Catalog.Mood.SleepDisturbed, _in.Rest, share);
        if (points > 0)
        {
            e.Effects.Add(new LostRoleEffect(RoleEffect.SleepDisturbed, e.Role) { Points = points });
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
            e.Effects.Add(new LostRoleEffect(RoleEffect.Impressiveness, e.Role));
            e.Lost.Add(Wire.Name(RoleEffect.Impressiveness));
        }
    }
}
