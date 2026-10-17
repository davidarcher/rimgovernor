namespace RimGovernor.Brain.Policy;

/// <summary>A finished building a ritual may be held at: its ThingDef.</summary>
public sealed record RitualSite
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";
}

/// <summary>The pawns that fill one role slot of a ritual's behavior, by the catalog's slot id, in priority order.</summary>
public sealed record RitualSlotFill
{
    public EquatableArray<PawnId> Pawns { get; init; }
}

/// <summary>
/// One ritual precept ready to begin, at a finished required building. Organizer leads
/// the first filled slot; Slots fill the behavior's role slots and Spectators attend without a role.
/// </summary>
public sealed record RitualPlan
{
    public PawnId Organizer { get; init; }

    public EquatableArray<RitualSlotFill> Slots { get; init; }

    public EquatableArray<PawnId> Spectators { get; init; }

    /// <summary>Every pawn the ritual gathers: the organizer, the slot fillers and the spectators.</summary>
    public IReadOnlyList<PawnId> Attendees()
    {
        var output = new List<PawnId> { Organizer };
        foreach (var slot in Slots)
        {
            foreach (var pawn in slot.Pawns)
            {
                if (pawn != Organizer)
                {
                    output.Add(pawn);
                }
            }
        }
        output.AddRange(Spectators);
        return output;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Whether the colony is calm enough to hold a ritual: known no hostile threat and no critical patient. Unknown stays unknown.</summary>
    public static Fact<bool> RitualCalm(Fact<long> hostiles, Fact<long> patients)
    {
        if (!hostiles.TryGetValue(out var h) || !patients.TryGetValue(out var p))
        {
            return Unknown<bool>();
        }
        return Known(h == 0 && p == 0);
    }

    /// <summary>
    /// Whether the held ritual is due at tick. A running ritual is not. An active obligation (the game's own,
    /// raised by the pattern's triggers) is due. Otherwise the pattern must allow a free start, the repeat
    /// penalty must be off and the pattern's own cooldown (the minimum of its free start interval, in days)
    /// must have passed since the raw lastFinishedTick.
    /// </summary>
    internal static bool RitualDue(RitualDef def, HeldRitual held, Tick tick)
    {
        if (held.Running)
        {
            return false;
        }
        if (held.ActiveObligations > 0)
        {
            return true;
        }
        if ((!def.CanStartAnytime && !def.AlwaysStartAnytime) || held.RepeatPenaltyActive)
        {
            return false;
        }
        return (double)(tick.Value - held.LastFinishedTick) >= def.IntervalDaysMin * GameTime.TicksPerDay;
    }

    /// <summary>
    /// The rituals to begin now: each due held ritual (RitualDue) that has a finished building site and pawns
    /// to fill its required slots, sorted by precept id. Nothing is planned while the colony is not known calm
    /// (RitualCalm). A pawn attends one ritual per call: a pawn a ritual takes is not offered to a later one.
    /// Attendees are available believers of the ideoligion: a slot bound to a role precept takes that role's
    /// holders (up to the slot's maximum), a required slot with no role precept takes the most certain
    /// unassigned believer, optional unbound slots stay empty and every other believer attends as a
    /// spectator. Whether the game accepts a pawn in a slot is its own check when the begin applies. Unknown
    /// without the ideoligion, the pawn rows, the sites or the calm reading. A held ritual whose pattern the
    /// catalog lacks, or that names no required building, is not planned: nothing says where to hold it.
    /// </summary>
    public static Fact<EquatableArray<RitualPlan>> PlanRituals(
        Fact<Ideoligion> ideology, Fact<EquatableArray<WorkPawn>> pawns, Fact<EquatableArray<RitualSite>> sites, Tick tick, Fact<bool> calm)
    {
        var ik = ideology.TryGetValue(out var ideo);
        var pk = pawns.TryGetValue(out var rows);
        var sk = sites.TryGetValue(out var known);
        var ck = calm.TryGetValue(out var quiet);
        if (!ik || !pk || !sk || !ck)
        {
            return Unknown<EquatableArray<RitualPlan>>();
        }
        var output = new List<RitualPlan>();
        if (!quiet)
        {
            return Known(EquatableArray.From(output));
        }
        var pool = new List<(PawnId Id, double Certainty)>();
        foreach (var row in rows)
        {
            var ak = row.Available.TryGetValue(out var available);
            var nk = row.PolicyInputs.TryGetValue(out var inputs);
            if (ak && available && nk && inputs!.Ideo == ideo!.Facts.IdeoId)
            {
                inputs.IdeoCertainty.TryGetValue(out var certainty);
                pool.Add((row.Id, certainty));
            }
        }
        pool = pool.OrderByDescending(b => b.Certainty).ThenBy(b => b.Id).ToList();
        var rituals = ideo!.Facts.Rituals.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var taken = new HashSet<PawnId>();
        foreach (var held in rituals)
        {
            if (!ideo.Defs.Rituals.TryGetValue(held.Pattern, out var def) || def.RequiredBuildings.Count == 0 || !RitualDue(def, held, tick))
            {
                continue;
            }
            var at = known.Where(site => def.RequiredBuildings.Contains(site.Def)).OrderBy(site => site.Id, StringComparer.Ordinal).ToList();
            if (at.Count == 0)
            {
                continue;
            }
            var used = new HashSet<PawnId>();
            var fillable = true;
            var slots = new List<RitualSlotFill>();
            foreach (var slot in def.Roles)
            {
                var fill = new List<PawnId>();
                if (slot.Precept.Length != 0)
                {
                    foreach (var role in ideo.Facts.Roles)
                    {
                        if (role.Def != slot.Precept || !role.Active)
                        {
                            continue;
                        }
                        foreach (var holder in role.Pawns)
                        {
                            var pawn = holder;
                            if (fill.Count < slot.MaxCount && !taken.Contains(pawn) && !used.Contains(pawn) && pool.Any(b => b.Id == pawn))
                            {
                                fill.Add(pawn);
                                used.Add(pawn);
                            }
                        }
                    }
                }
                else if (slot.Required)
                {
                    foreach (var b in pool)
                    {
                        if (!taken.Contains(b.Id) && !used.Contains(b.Id))
                        {
                            fill.Add(b.Id);
                            used.Add(b.Id);
                            break;
                        }
                    }
                }
                if (fill.Count == 0)
                {
                    if (slot.Required)
                    {
                        fillable = false;
                        break;
                    }
                    continue;
                }
                slots.Add(new RitualSlotFill { Pawns = EquatableArray.From(fill) });
            }
            if (!fillable)
            {
                continue;
            }
            var spectators = new List<PawnId>();
            foreach (var b in pool)
            {
                if (!taken.Contains(b.Id) && !used.Contains(b.Id))
                {
                    spectators.Add(b.Id);
                }
            }
            PawnId organizer;
            if (slots.Count > 0)
            {
                organizer = slots[0].Pawns[0];
            }
            else if (spectators.Count > 0)
            {
                organizer = spectators[0];
                spectators.RemoveAt(0);
            }
            else
            {
                continue;
            }
            var plan = new RitualPlan
            {
                Organizer = organizer,
                Slots = EquatableArray.From(slots),
                Spectators = EquatableArray.From(spectators),
            };
            foreach (var pawn in plan.Attendees())
            {
                taken.Add(pawn);
            }
            output.Add(plan);
        }
        return Known(EquatableArray.From(output));
    }

}
