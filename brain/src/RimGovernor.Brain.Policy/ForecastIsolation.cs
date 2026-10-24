namespace RimGovernor.Brain.Policy;

// The isolation room credit and the creepjoiner downside posterior (#3844, epic #3650 P1). Confirmed with ilspycmd on
// Assembly-CSharp.dll:
// - Draw: CreepJoinerUtility.GetCreepjoinerSpecifics draws the downside with GetRandom over
//   DefDatabase<CreepJoinerDownsideDef>.AllDefsListForReading, the requires and excludes of the form and the benefit drawn
//   before it. GetRandom keeps the defs with combatPoints >= MinCombatPoints and CanOccurRandomly (only those a requires list
//   names, when one qualifies) and draws RandomElementByWeight over Weight. Its excludes loop removes `defs.RemoveAt(num)` at
//   the kept list's index num: it edits the DefDatabase list, not the kept one, so the draw itself ignores the excludes and
//   a later draw in the session sees the edited list. The posterior mirrors that draw: every drawable def by weight, excludes
//   ignored (the edited list is named not modelled).
// - Timing: Pawn_CreepJoinerTracker.Notify_ChangedFaction sets joinedTick when the pawn joins, downsideTriggersAt = joined +
//   triggersAfterDays.RandomInRange days (unless the range is zero) and canTriggerDownsideAfter = joined +
//   triggerMinDays.RandomInRange days (unless zero). TickInterval runs CheckDownsideOccurs every 15000 ticks
//   (IsHashIntervalTick), which fires DoDownside on Rand.MTBEventOccurs(triggerMtbDays, 60000, 15000) (a chance of
//   15000 / (mtb × 60000) a check) past canTriggerDownsideAfter, or once the tick reaches downsideTriggersAt.
// - Pulse: CreepJoinerWorker_PsychicAgony.DoResponse gives AgonyPulse to the creepjoiner and every spawned pawn within Range
//   (14.9) of it, its Disappears comp set to DisappearSeconds (2000~4000) × 60 ticks. The pulse's severity rises from its
//   initialSeverity at the SeverityPerSecond comp's rate (severityPerSecond + severityPerSecondRange, per second / 60 a tick)
//   through its stages' painOffset.

/// <summary>
/// One CreepJoinerDownsideDef as the posterior reads it (#3844): its def, Weight, CanOccurRandomly and MinCombatPoints (the
/// draw's), whether it adds a trait (shown from the day the pawn arrives), triggersAfterDays, triggerMtbDays and
/// triggerMinDays (zero ranges and 0 for none), and whether its worker pulses agony (CreepJoinerWorker_PsychicAgony).
/// </summary>
public sealed record CreepJoinerDownsideRow
{
    public required string Def { get; init; }

    public double Weight { get; init; }

    public bool CanOccurRandomly { get; init; }

    public double MinCombatPoints { get; init; }

    public bool AddsTrait { get; init; }

    public (double Min, double Max) TriggersAfterDays { get; init; }

    public double TriggerMtbDays { get; init; }

    public (double Min, double Max) TriggerMinDays { get; init; }

    public bool PsychicAgony { get; init; }
}

/// <summary>
/// The psychic agony pulse (#3844): the worker's Range, the AgonyPulse hediff's stages (its pain offsets), its maxSeverity,
/// its SeverityPerSecond comp's rate range a second and the ticks the worker sets on its Disappears comp.
/// </summary>
public sealed record AgonyPulse
{
    public double Range { get; init; }

    public HediffPenaltyTable Table { get; init; } = new();

    public double MaxSeverity { get; init; }

    public (double Min, double Max) SeverityPerSecond { get; init; }

    public (double Min, double Max) DisappearsTicks { get; init; }
}

/// <summary>
/// The catalog's creepjoiner downside defs (#3844); empty without Anomaly. <see cref="Agony"/> is null when no downside
/// pulses agony. <see cref="Unmirrored"/> names a form or benefit whose requires list names a downside, which would narrow
/// the draw to a hidden form's list ("" when none does).
/// </summary>
public sealed record CreepJoinerDownsideTable
{
    public EquatableArray<CreepJoinerDownsideRow> Downsides { get; init; }

    public AgonyPulse? Agony { get; init; }

    public string Unmirrored { get; init; } = "";
}

/// <summary>A creepjoiner the colony holds apart (#3844): its pawn and the days since it joined ((now − joinedTick) / 60000).</summary>
public sealed record HeldCreepJoiner(string Pawn, double DaysJoined);

/// <summary>
/// What the isolation credit reads of the colony's creepjoiners (#3844): the ones it holds apart (downside not shown, no
/// finished inspection; <see cref="CreepJoinerDownsides.Isolated"/>) and the catalog's downside table. <see cref="Unread"/>
/// says why the held ones are unread, which the credit raises as a required fact; a forecast nobody set is unread.
/// </summary>
public sealed record CreepJoinerForecast
{
    public EquatableArray<HeldCreepJoiner> Held { get; init; }

    /// <summary>Why the creepjoiners held apart are unread; "" when they were read.</summary>
    public string Unread { get; init; } = "not read";

    public CreepJoinerDownsideTable Table { get; init; } = new();
}

/// <summary>
/// The IsolationRoom credit (#3844 D10/D11): the agony pulse the room keeps from the colony. A creepjoiner held in a room
/// that stands out of a pulse's reach of every other room (<see cref="IsolationRoomRules.ApartFromIsolation"/>) pulses only
/// itself, where unheld it pulses every other colonist. The room holds it until its downside shows
/// (<see cref="CreepJoinerDownsides.IsolationMoves"/> releases it then), so it keeps off the first pulse only: per held
/// creepjoiner, once, P(its downside pulses agony and first fires between the room's service day and the horizon | not shown
/// by now) × Σ over the other colonists of one pulse's loss (<see cref="PulseLoss"/>). A colony holding no creepjoiner apart,
/// or whose downsides pulse no agony, earns nothing, named.
/// </summary>
public static class ForecastIsolation
{
    /// <summary>Pawn_CreepJoinerTracker.TickInterval's check interval, IsHashIntervalTick(15000): a code literal no def carries, a quarter day.</summary>
    public const double CheckTicks = GameTime.TicksPerDay / 4.0;

    /// <summary>The ticks in a game second: HediffComp_SeverityPerSecond adds its rate / 60 a tick, a code literal.</summary>
    public const double TicksPerSecond = 60;

    /// <summary>What the credit leaves out, named on every served isolation room (<see cref="Findings"/>).</summary>
    public static readonly IReadOnlyList<string> NotModelled =
    [
        // A colonist walking past the room, or working outdoors near it, is pulsed with it held (D10).
        "isolation_passers_by_unmodelled",
        // The admin Need releases a held creepjoiner once its inspection is done, or while it is hungry; it is held to the horizon.
        "isolation_release_unmodelled",
        // Only colonists are pulsed in the forecast: the colony's animals, prisoners and guests in reach are not counted.
        "isolation_nonColonists_unmodelled",
        // The pulse's pain is priced through consciousness; its mood thoughts and a pawn's own pain beside it are not.
        "isolation_pulse_pain_mood_unmodelled",
        // A downside does not fire while its creepjoiner is downed, unconscious or unspawned (CheckDownsideOccurs).
        "isolation_trigger_conditions_unmodelled",
        // GetRandom's excludes edit the DefDatabase list a later draw reads; the posterior reads the catalog's list.
        "creepjoiner_downside_list_edit_unmodelled",
    ];

    /// <summary>The named finding of a colony holding no creepjoiner apart: the room earns nothing.</summary>
    public const string NoneHeld = "isolation_no_creepjoiner_held";

    /// <summary>The named finding of a colony whose creepjoiner downsides pulse no agony: the room earns nothing.</summary>
    public const string NoAgony = "isolation_no_agony_downside";

    /// <summary>The credit (<see cref="RoomCredit"/>): one once gain per held creepjoiner, under the creepjoiner.</summary>
    public static IReadOnlyList<RoomGain> IsolationCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        var held = HeldOf(context.CreepJoiners);
        var table = context.CreepJoiners.Table;
        if (held.Count == 0 || !table.Downsides.Any(d => d.PsychicAgony)) return [];
        var agony = AgonyOf(table);
        var from = room.ServedAt / GameTime.TicksPerDay;
        var to = context.InvestmentHorizonDays;
        if (to <= from) return [];
        var apart = held.Select(h => h.Pawn).ToHashSet(StringComparer.Ordinal);
        var loss = context.Needs.Pawns.Where(p => !apart.Contains(p.Pawn)).DistinctBy(p => p.Pawn).Sum(p => PulseLoss(p, agony));
        var gains = new List<RoomGain>();
        foreach (var h in held.OrderBy(h => h.Pawn, StringComparer.Ordinal))
        {
            var chance = FirstPulseChance(table, h.DaysJoined + from, h.DaysJoined + to, h.DaysJoined);
            if (chance * loss > 0) gains.Add(new(h.Pawn, "", 0) { Once = chance * loss });
        }
        return gains;
    }

    /// <summary>
    /// The served isolation room's named findings (<see cref="PlannedRoomCredits.Unmodelled"/>): <see cref="NoneHeld"/> or
    /// <see cref="NoAgony"/> for a room that earns nothing, else <see cref="NotModelled"/>.
    /// </summary>
    public static IEnumerable<string> Findings(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (HeldOf(context.CreepJoiners).Count == 0) return [NoneHeld];
        return context.CreepJoiners.Table.Downsides.Any(d => d.PsychicAgony) ? NotModelled : [NoAgony];
    }

    /// <summary>The creepjoiners held apart, raised as a required fact while unread.</summary>
    internal static EquatableArray<HeldCreepJoiner> HeldOf(CreepJoinerForecast forecast) =>
        forecast.Unread.Length == 0 ? forecast.Held : throw new RequiredFactException("creepjoiner isolation: the creepjoiners held apart: " + forecast.Unread);

    /// <summary>The pulse, raised when a downside pulses and the catalog read none, or its reach outgrows the layout's (<see cref="IsolationRoomRules.AgonyRange"/>).</summary>
    internal static AgonyPulse AgonyOf(CreepJoinerDownsideTable table)
    {
        var agony = table.Agony ?? throw new RequiredFactException("creepjoiner isolation: the agony pulse");
        if (agony.Range > IsolationRoomRules.AgonyRange)
            throw new DomainException(FormattableString.Invariant($"the agony pulse reaches {agony.Range} cells, past the {IsolationRoomRules.AgonyRange} the layout keeps rooms apart by"));
        if (!agony.Table.Valid() || !(agony.SeverityPerSecond.Min > 0) || agony.SeverityPerSecond.Max < agony.SeverityPerSecond.Min
            || !(agony.DisappearsTicks.Min >= 0) || agony.DisappearsTicks.Max < agony.DisappearsTicks.Min)
            throw new DomainException("the agony pulse's hediff table, severity rate or duration is not a course");
        return agony;
    }

    /// <summary>
    /// The chance a held creepjoiner's downside pulses agony and first fires between <paramref name="fromDays"/> and
    /// <paramref name="toDays"/> since it joined, given it has not shown by <paramref name="nowDays"/> (D11): over the
    /// drawable downsides that add no trait (a trait shows from arrival), w_agony × (S(from) − S(to)) / Σ w × S(now), S each
    /// downside's chance it has not fired yet (<see cref="Survival"/>). A drawable def gated on combat points raises a required
    /// fact (the points at the draw are kept nowhere), as does a form or benefit that requires a downside.
    /// </summary>
    public static double FirstPulseChance(CreepJoinerDownsideTable table, double fromDays, double toDays, double nowDays)
    {
        if (table.Unmirrored.Length != 0) throw new RequiredFactException("creepjoiner downside draw: " + table.Unmirrored);
        double hidden = 0, pulse = 0;
        foreach (var d in table.Downsides.Where(d => d.CanOccurRandomly))
        {
            if (d.MinCombatPoints > 0)
                throw new RequiredFactException(FormattableString.Invariant($"creepjoiner downside draw: the combat points {d.Def} needs ({d.MinCombatPoints}) at the draw"));
            if (d.AddsTrait || !(d.Weight > 0)) continue;
            hidden += d.Weight * Survival(d, nowDays);
            if (d.PsychicAgony) pulse += d.Weight * Math.Max(0, Survival(d, fromDays) - Survival(d, toDays));
        }
        return hidden > 0 ? pulse / hidden : 0;
    }

    /// <summary>
    /// The chance <paramref name="d"/> has not fired <paramref name="days"/> after joining: its timer's, P(T + lag > days) with
    /// T uniform on triggersAfterDays and the check's lag uniform on one interval, times its MTB's, E over the gate m uniform on
    /// triggerMinDays of (1 − p)^(checks past m), p = <see cref="CheckTicks"/> / (triggerMtbDays × a day) (the checks taken as
    /// continuous). A downside with neither never fires.
    /// </summary>
    public static double Survival(CreepJoinerDownsideRow d, double days)
    {
        var check = CheckTicks / GameTime.TicksPerDay;
        var s = 1.0;
        if (d.TriggersAfterDays is not (0, 0) and var (a, b))
        {
            // P(lag > x) for lag uniform on [0, check), and Φ(x) = ∫ from x to ∞ of it.
            double Late(double x) => x < 0 ? 1 : x < check ? 1 - x / check : 0;
            double Phi(double x) => x >= check ? 0 : x >= 0 ? (check - x) * (check - x) / (2 * check) : check / 2 - x;
            s *= b > a ? (Phi(days - b) - Phi(days - a)) / (b - a) : Late(days - a);
        }
        if (d.TriggerMtbDays > 0)
        {
            var p = check / d.TriggerMtbDays;
            var q = p >= 1 ? double.NegativeInfinity : Math.Log(1 - p) / check;
            double Left(double m) => m >= days ? 1 : double.IsNegativeInfinity(q) ? 0 : Math.Exp(q * (days - m));
            var (m0, m1) = d.TriggerMinDays;
            if (m1 > m0)
            {
                // ∫ of exp(q (days − m)) over the gates before days, plus the gates at or past it, which have not opened.
                var hi = Math.Min(m1, days);
                var open = hi > m0 ? double.IsNegativeInfinity(q) ? 0 : (Math.Exp(q * (days - m0)) - Math.Exp(q * (days - hi))) / q : 0;
                s *= (open + Math.Max(0, m1 - Math.Max(m0, days))) / (m1 - m0);
            }
            else
            {
                s *= Left(m0);
            }
        }
        return Math.Clamp(s, 0, 1);
    }

    /// <summary>
    /// The capability-days one pulse costs <paramref name="pawn"/>: the pulse's severity rises from its initial severity at the
    /// mean of the rate's inverse (E[1/r] over r uniform) through its stages to its maxSeverity, and lasts the mean of its
    /// Disappears ticks; each stretch costs (1 − the share its pain leaves, <see cref="HarmConsequences.PainFactor"/>) of its
    /// length.
    /// </summary>
    public static double PulseLoss(RimGovernor.Contracts.Wire.Observations.NeedsForecastPawn pawn, AgonyPulse agony)
    {
        var (consciousness, shock) = (pawn.Consciousness, pawn.PainShockThreshold);
        var (lo, hi) = agony.SeverityPerSecond;
        var ticksPerSeverity = (hi > lo ? Math.Log(hi / lo) / (hi - lo) : 1 / lo) * TicksPerSecond;
        var length = (agony.DisappearsTicks.Min + agony.DisappearsTicks.Max) / 2;
        var stages = agony.Table.Stages;
        double severity = agony.Table.InitialSeverity, at = 0, lost = 0;
        // Each stretch: the severity from where it is to the next boundary it reaches (a stage's minimum within maxSeverity),
        // in the stage it is in (none below the first, which costs nothing), cut at the pulse's end.
        while (at < length)
        {
            var next = stages.Select(s => s.MinSeverity).Where(m => m > severity && m <= agony.MaxSeverity).DefaultIfEmpty(double.PositiveInfinity).Min();
            var stretch = Math.Min(double.IsPositiveInfinity(next) ? double.PositiveInfinity : (next - severity) * ticksPerSeverity, length - at);
            if (agony.Table.StageAt(severity) is { } stage)
                lost += stretch * (1 - HarmConsequences.PainFactor(consciousness, shock, stage.PainOffset, out _));
            at += stretch;
            severity = next;
        }
        return lost / GameTime.TicksPerDay;
    }
}
