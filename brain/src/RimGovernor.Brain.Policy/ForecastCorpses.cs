using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Morgue and Tomb credits (#3806, epic #3650 P1): the observed-corpse moods a planned room removes by taking the corpses
/// that lie in sight (<see cref="NeedsForecastRead.Corpses"/>). A tomb takes the colonist corpses; a morgue takes the
/// strangers', and the colonists' too while the plan holds no priced tomb. With several rooms of a role the lowest id takes
/// them. Per colonist, the corpses cost the mood <see cref="CorpseMood"/> replays from its own sighting record; the room's
/// credit is what that mood costs a day, through <see cref="ForecastHousing.GainPerDay"/>.
/// </summary>
internal static class ForecastCorpses
{
    /// <summary>Per role with a planned priced room, per colonist its <see cref="CorpseMood"/> of the corpses the role takes.</summary>
    private sealed record Moods(IReadOnlyDictionary<PlannedRole, IReadOnlyDictionary<string, double>> ByRole, IReadOnlyDictionary<string, double[]> Shifts);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Moods> Cache = new();

    /// <summary>The morgue's credit (<see cref="RoomCredit"/>).</summary>
    internal static IReadOnlyList<RoomGain> MorgueCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
        => Credit(PlannedRole.Morgue, room, outcome, context, horizon);

    /// <summary>The tomb's credit (<see cref="RoomCredit"/>).</summary>
    internal static IReadOnlyList<RoomGain> TombCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
        => Credit(PlannedRole.Tomb, room, outcome, context, horizon);

    /// <summary>
    /// Per colonist whose corpses cost it mood, the capability-days a day the room gains it. The forecast's mood timeline renews
    /// no observation memory (an observed one only runs out), so it already holds the colony with the corpses out of sight; the
    /// colony without the room is that timeline shifted by the corpses' mood, and the room's gain a day is minus
    /// <see cref="ForecastHousing.GainPerDay"/> of that shift. None for a room that takes no corpse; throws when a colonist's
    /// mood is unread (<see cref="PlannedRoomCredits.MoodGain"/>, #3835).
    /// </summary>
    private static List<RoomGain> Credit(PlannedRole role, ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (Taker(context, role) != room.Room) return [];
        if (!Cache.GetValue(context, Of).ByRole.TryGetValue(role, out var moods)) return [];
        return [.. moods.Where(m => m.Value != 0).OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new RoomGain(m.Key, m.Key, -PlannedRoomCredits.MoodGain(m.Value, m.Key, outcome, context, horizon)))];
    }

    /// <summary>Per pawn, the distinct nonzero shifts (mood / 100) its timeline integrates breaks at for the corpse credits.</summary>
    internal static IReadOnlyDictionary<string, double[]> BreakShifts(ForecastCoupledContext context) => Cache.GetValue(context, Of).Shifts;

    /// <summary>The planned room of <paramref name="role"/> that takes its corpses: the lowest id; 0 for none.</summary>
    private static int Taker(ForecastCoupledContext context, PlannedRole role) =>
        context.PlannedRoomRoles.Where(r => r.Value == role).Select(r => r.Key).DefaultIfEmpty(0).Min();

    private static Moods Of(ForecastCoupledContext context)
    {
        var byRole = new Dictionary<PlannedRole, IReadOnlyDictionary<string, double>>();
        var tomb = Taker(context, PlannedRole.Tomb) != 0 && context.RoomCredits.ContainsKey(PlannedRole.Tomb);
        foreach (var role in new[] { PlannedRole.Morgue, PlannedRole.Tomb })
        {
            if (Taker(context, role) == 0 || !context.RoomCredits.ContainsKey(role)) continue;
            CorpseSightingRead[] taken = [.. context.Needs.Corpses.Where(c => role == PlannedRole.Tomb ? c.Colonist : !c.Colonist || !tomb)];
            byRole[role] = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).Select(g => g.First())
                .ToDictionary(p => p.Pawn, p => CorpseMood(p, taken), StringComparer.Ordinal);
        }
        var shifts = byRole.Values.SelectMany(m => m).Where(m => m.Value != 0)
            .GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(m => m.Value / 100).Distinct().Order().ToArray(), StringComparer.Ordinal);
        return new(byRole, shifts);
    }

    /// <summary>
    /// The mean mood <paramref name="corpses"/> cost <paramref name="pawn"/> while they lie where they do (#3806), replayed from
    /// its own observer passes. PawnObserver sees a corpse within 5 cells in line of sight while awake; the sighting gives each
    /// ideo memory the corpse's event raises (<see cref="NeedsForecastPawn.CorpseThoughts"/>), and Thought_MemoryObservation
    /// renews that corpse's memory instead of adding one, so a corpse's memory is active from a sighting for its DurationTicks
    /// or until the next sighting renews it. The pawn's routine is taken to repeat: the whole days its record covers
    /// (<see cref="NeedsForecastPawn.SightingTicks"/>) are one cycle, a memory running past the cycle's end wrapping to its
    /// start; a record shorter than a day (a new colonist's, or one begun since a save without it) is its own cycle, the ticks it
    /// covers. At each moment the n memories of a thought then active count as the game's group does
    /// (ThoughtHandler.MoodOffsetOfGroup with MemoryThoughtHandler's stackLimit): offset × Σ_{i&lt;min(n, limit)} multiplier^i;
    /// the result is that offset's mean over the cycle, summed over the thoughts. Zero with no corpse or no thought, and for an
    /// empty record (it holds no sighting, #3835).
    /// </summary>
    internal static double CorpseMood(NeedsForecastPawn pawn, IReadOnlyList<CorpseSightingRead> corpses)
    {
        if (corpses.Count == 0 || pawn.CorpseThoughts.All(t => t.Offset == 0) || pawn.SightingTicks <= 0) return 0;
        var days = Math.Floor(pawn.SightingTicks / (double)GameTime.TicksPerDay);
        var cycle = days >= 1 ? days * GameTime.TicksPerDay : pawn.SightingTicks;
        var mood = 0.0;
        foreach (var thought in pawn.CorpseThoughts.Where(t => t.Offset != 0).DistinctBy(t => (t.Rotting, t.Thought)))
        {
            var edges = new List<(double At, int Step)>();
            void Active(double from, double to)
            {
                if (to <= from) return;
                edges.Add((from, 1));
                edges.Add((to, -1));
            }
            foreach (var corpse in corpses.Where(c => c.Rotting == thought.Rotting))
            {
                if (corpse.Sightings.FirstOrDefault(s => s.Pawn == pawn.Pawn) is not { } seen) continue;
                double[] at = [.. seen.TicksAgo.Where(a => a < cycle).Select(a => cycle - (double)a).Order()];
                for (var i = 0; i < at.Length; i++)
                {
                    var next = i + 1 < at.Length ? at[i + 1] : at[0] + cycle;
                    var end = at[i] + Math.Min(thought.DurationTicks, next - at[i]);
                    Active(at[i], Math.Min(end, cycle));
                    Active(0, end - cycle);
                }
            }
            var active = 0;
            var last = 0.0;
            foreach (var (at, step) in edges.OrderBy(e => e.At))
            {
                mood += Group(thought, active) * (at - last) / cycle;
                active += step;
                last = at;
            }
        }
        return mood;
    }

    /// <summary>ThoughtHandler.MoodOffsetOfGroup of <paramref name="count"/> memories of one thought, capped at its stackLimit (negative: none).</summary>
    private static double Group(CorpseThoughtRead thought, int count)
    {
        var n = thought.StackLimit < 0 ? count : Math.Min(count, thought.StackLimit);
        double sum = 0, weight = 1;
        for (var i = 0; i < n; i++) { sum += weight; weight *= thought.StackedMultiplier; }
        return thought.Offset * sum;
    }
}
