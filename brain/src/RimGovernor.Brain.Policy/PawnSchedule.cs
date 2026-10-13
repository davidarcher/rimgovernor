using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    // TimeAssignmentDef names, as native's timetable reports them.
    public const string ScheduleWork = "Work";
    public const string ScheduleAnything = "Anything";
    public const string ScheduleJoy = "Joy";
    public const string ScheduleSleep = "Sleep";
    public const string ScheduleMeditate = "Meditate";

    // Need bands for resizing the template. Hysteresis is pure thresholds read against the pawn's current
    // timetable, in the style of EnterC/ExitC in cleanliness.go: no carried history, no minimum hold.
    //   - Sleep: rest below RestEnter (Drowsy is < 0.28) on a base-length timetable extends Sleep by
    //     SleepExtension hours at the wake end; an already-extended timetable stays extended while rest is
    //     below RestExit.
    //   - Recreation: joy below JoyEnter widens Joy to two hours (the two hours before sleep); a widened
    //     timetable stays wide while joy is below JoyExit.
    //
    // An unknown need plans the base block for that need.
    public const double RestEnter = 0.28;
    public const double RestExit = 0.60;
    public const double JoyEnter = 0.30;
    public const double JoyExit = 0.70;
    public const int SleepExtension = 2;

    /// <summary>
    /// A widened Meditate block enters when psyfocus is below its target and stays wide until psyfocus
    /// reaches target+PsyfocusExit.
    /// </summary>
    public const double PsyfocusExit = 0.10;

    /// <summary>
    /// Bounds the recreation stagger to the evening: a pawn's Joy hours move at most this many hours
    /// earlier, which keeps them clear of the Sleep block (and its wake-end extension) even on the shortest
    /// (QuickSleeper NightOwl) day.
    /// </summary>
    internal const int MaxJoyOffset = 3;
}

/// <summary>One pawn's planned timetable, hour 0 first. Matches is whether the current timetable already is this one.</summary>
public sealed record PawnSchedule(PawnId Pawn, IReadOnlyList<string> Slots, bool Matches);

/// <summary>
/// The schedule planner's output: a row per available pawn with a known timetable. Whoever wrote the current
/// timetable, Auto plans it fresh (control-loop.md, Manual control): a timetable edited under Manual is
/// evidence of an old order, not authority over planning.
/// </summary>
public sealed record ScheduleDecision(IReadOnlyList<PawnSchedule> Schedules)
{
    public ScheduleDecision()
        : this([])
    {
    }
}

/// <summary>A psycaster's psyfocus against its target.</summary>
internal sealed record PsyfocusBand(Fact<double> Focus, double Target);

public static partial class PolicyFunctions
{
    /// <summary>Pawn_TimetableTracker's constructor: Sleep 22h-5h, Anything otherwise.</summary>
    internal static string[] NativeDefaultSchedule()
    {
        var slots = new string[24];
        for (var h = 0; h < slots.Length; h++)
        {
            slots[h] = PolicyConstants.ScheduleAnything;
            if (h > 21 || h <= 5)
            {
                slots[h] = PolicyConstants.ScheduleSleep;
            }
        }
        return slots;
    }

    private static void Fill(string[] slots, int from, int to, string def)
    {
        for (var h = from; ; h = (h + 1) % 24)
        {
            slots[h] = def;
            if (h == to)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The role-based timetable for a profile: the native day sleeps 22h-5h; a NightOwl sleeps 10h-17h (the
    /// hours the trait penalises being awake) and is free overnight; a QuickSleeper needs half the rest, so
    /// its Sleep block loses two hours at the start (and, for a NightOwl, one at each end). Joy is the hour
    /// right before sleep and every other hour is Anything. The planner never writes Work: that would
    /// suppress rest and recreation and wake sleeping pawns.
    /// </summary>
    internal static string[] ScheduleTemplate(TraitEffects effects)
    {
        var slots = new string[24];
        Array.Fill(slots, PolicyConstants.ScheduleAnything);
        var (sleepFrom, sleepTo) = SleepBlock(effects);
        Fill(slots, sleepFrom, sleepTo, PolicyConstants.ScheduleSleep);
        slots[(sleepFrom + 23) % 24] = PolicyConstants.ScheduleJoy;
        return slots;
    }

    /// <summary>The template's Sleep block, first and last hour inclusive.</summary>
    internal static (int From, int To) SleepBlock(TraitEffects effects)
    {
        int sleepFrom = 22, sleepTo = 5;
        if (effects.NightShift)
        {
            (sleepFrom, sleepTo) = (10, 17);
            if (effects.QuickSleeper)
            {
                (sleepFrom, sleepTo) = (11, 16);
            }
        }
        else if (effects.QuickSleeper)
        {
            sleepFrom = 0;
        }
        return (sleepFrom, sleepTo);
    }

    /// <summary>Answers whether a resize holds: enter below enter, stay below exit.</summary>
    private static bool NeedBand(Fact<double> level, bool active, double enter, double exit)
    {
        if (!level.TryGetValue(out var v))
        {
            return false;
        }
        return active ? v < exit : v < enter;
    }

    /// <summary>
    /// The template resized from the pawn's needs against its current timetable. It only ever adds Sleep,
    /// Joy or Meditate hours over Anything.
    /// </summary>
    private static string[] PlannedSchedule(
        TraitEffects effects, Fact<double> rest, Fact<double> joy, IReadOnlyList<string> current, int joyOffset, PsyfocusBand? meditate)
    {
        var slots = ScheduleTemplate(effects);
        var (from, to) = SleepBlock(effects);
        string At(int h) => current.Count != 24 ? "" : current[(((h % 24) + 24) % 24)];
        var extended = At(to + 1) == PolicyConstants.ScheduleSleep;
        if (NeedBand(rest, extended, PolicyConstants.RestEnter, PolicyConstants.RestExit))
        {
            for (var i = 1; i <= PolicyConstants.SleepExtension; i++)
            {
                slots[(to + i) % 24] = PolicyConstants.ScheduleSleep;
            }
        }
        // A staggered pawn takes its Joy hour, and the widened hour before it, joyOffset hours earlier; Sleep
        // never moves.
        slots[(from + 23) % 24] = PolicyConstants.ScheduleAnything;
        // A psycaster meditates in the recreation block instead: meditation also fills recreation, and low
        // psyfocus widens it too.
        var block = meditate is not null ? PolicyConstants.ScheduleMeditate : PolicyConstants.ScheduleJoy;
        slots[(from + 23 - joyOffset) % 24] = block;
        var prev = At(from - 2 - joyOffset);
        var widened = prev == PolicyConstants.ScheduleJoy || prev == PolicyConstants.ScheduleMeditate;
        var wide = NeedBand(joy, widened, PolicyConstants.JoyEnter, PolicyConstants.JoyExit);
        if (meditate is not null && NeedBand(meditate.Focus, widened, meditate.Target, meditate.Target + PolicyConstants.PsyfocusExit))
        {
            wide = true;
        }
        if (wide)
        {
            slots[(from + 22 - joyOffset) % 24] = block;
        }
        return slots;
    }

    /// <summary>
    /// Staggers recreation when the colony has fewer recreation places than people: scheduled pawns, sorted
    /// by ID, fill the places hour by hour back from sleep (wrapping past MaxJoyOffset). Unknown counts, no
    /// places at all, or enough places: no stagger.
    /// </summary>
    private static Dictionary<PawnId, int> JoyOffsets(List<PawnId> ids, Fact<ComfortObservation> comfort)
    {
        var known = comfort.TryGetValue(out var v);
        var places = v?.Recreation.Count ?? 0;
        if (!known || places == 0 || places >= v!.People.Count)
        {
            return new Dictionary<PawnId, int>();
        }
        ids.Sort();
        var offsets = new Dictionary<PawnId, int>();
        for (var i = 0; i < ids.Count; i++)
        {
            offsets[ids[i]] = (i / places) % (PolicyConstants.MaxJoyOffset + 1);
        }
        return offsets;
    }

    /// <summary>The pawn's psyfocus band when it should meditate: the Meditate def exists and the pawn has a known psylink.</summary>
    private static PsyfocusBand? Meditation(WorkPawn pawn, bool meditateAvailable)
    {
        var lk = pawn.PsylinkLevel.TryGetValue(out var level);
        var tk = pawn.PsyfocusTarget.TryGetValue(out var target);
        if (!meditateAvailable || !lk || level < 1 || !tk)
        {
            return null;
        }
        return new PsyfocusBand(pawn.Psyfocus, target);
    }

    private static (IReadOnlyList<string> Current, bool Ok) Schedulable(WorkPawn pawn)
    {
        var ak = pawn.Available.TryGetValue(out var available);
        var pk = pawn.Applies.TryGetValue(out var applies);
        var ck = pawn.Schedule.TryGetValue(out var current);
        return (current, ak && pk && ck && available && applies);
    }

    private static bool SameSchedule(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>
    /// Chooses a timetable per available pawn from its profile and needs (PlannedSchedule), staggering Joy
    /// hours against the recreation census (JoyOffsets); a pawn whose timetable is unknown is skipped.
    /// meditateAvailable is whether the Meditate TimeAssignmentDef exists (unknown reads as false):
    /// psycasters then meditate in their recreation block.
    /// </summary>
    public static ScheduleDecision PlanSchedules(IReadOnlyList<WorkPawn> pawns, Fact<ComfortObservation> comfort, bool meditateAvailable) =>
        PlanSchedulesHeld(pawns, comfort, meditateAvailable, null);

    /// <summary>
    /// PlanSchedules with the pawns in hold kept off Sleep (every Sleep hour planned Anything): a pending
    /// bestowing ceremony (CeremonyHold) needs its colonist and attendees awake to join the ritual. Rest
    /// below the sleep band still sends them to bed on their own.
    /// </summary>
    public static ScheduleDecision PlanSchedulesHeld(
        IReadOnlyList<WorkPawn> pawns, Fact<ComfortObservation> comfort, bool meditateAvailable, IReadOnlyDictionary<PawnId, bool>? hold)
    {
        var schedules = new List<PawnSchedule>();
        var ids = new List<PawnId>();
        foreach (var pawn in pawns)
        {
            if (Schedulable(pawn).Ok)
            {
                ids.Add(pawn.Id);
            }
        }
        var offsets = JoyOffsets(ids, comfort);
        foreach (var pawn in pawns)
        {
            var (current, ok) = Schedulable(pawn);
            if (!ok)
            {
                continue;
            }
            var profile = BuildProfile(pawn);
            var want = PlannedSchedule(profile.Effects, pawn.Rest, pawn.Joy, current, offsets.GetValueOrDefault(pawn.Id), Meditation(pawn, meditateAvailable));
            // A need an active gene removes needs no block for it.
            for (var h = 0; h < want.Length; h++)
            {
                var slot = want[h];
                if ((slot == PolicyConstants.ScheduleSleep && profile.Genes.NeedDisabled("Rest"))
                    || (slot == PolicyConstants.ScheduleJoy && profile.Genes.NeedDisabled("Joy")))
                {
                    want[h] = PolicyConstants.ScheduleAnything;
                }
            }
            if (hold?.GetValueOrDefault(pawn.Id) ?? false)
            {
                for (var h = 0; h < want.Length; h++)
                {
                    if (want[h] == PolicyConstants.ScheduleSleep)
                    {
                        want[h] = PolicyConstants.ScheduleAnything;
                    }
                }
            }
            schedules.Add(new PawnSchedule(pawn.Id, want, SameSchedule(current, want)));
        }
        return new ScheduleDecision([.. schedules.OrderBy(s => s.Pawn)]);
    }
}
