namespace RimGovernor.Brain.Policy;

/// <summary>What the gathering review reads. A fact left unknown never starts a party.</summary>
public sealed record GatheringInput
{
    public Fact<MoodLedger> Ledger { get; init; }

    /// <summary>The mood census: the guests' state and the AttendedParty memory.</summary>
    public Fact<EquatableArray<MoodPawn>> Pawns { get; init; }

    /// <summary>The AttendedParty mood effect from the catalog; a party whose memory is not positive repays nothing and is never held.</summary>
    public required double Benefit { get; init; }

    /// <summary>RitualCalm: no hostile threat and no critical patient.</summary>
    public Fact<bool> Calm { get; init; }

    /// <summary>Holds the built PartySpot.</summary>
    public CurrentConstruction? Census { get; init; }
}

/// <summary>One party ready to start: the organizer to name.</summary>
public sealed record GatheringPlan
{
    public PawnId Organizer { get; init; }
}

public static class GatheringRules
{
    // The rooms/gatherings Need holds a vanilla party when the colony as a whole is losing mood a party would repay. It is a
    // mood source with a cost (colonist time), not a repair: the PartySpot is kept by rooms/party-spot
    // (PartySpotDefinition), and this Need only decides when to start the party and who organizes it. Every
    // number below is a vanilla fact, not a tuning choice:
    //
    //   - the def is Core's Party, the GatheringDef whose gatherSpotDefs name the PartySpot;
    //   - a party gives each attendee the AttendedParty memory (ThoughtDef baseMoodEffect), read from the
    //     catalog, so a pawn is worth a party when its ledger loss is at least that effect;
    //   - the game itself refuses under four colonists and sizes the guest list to 0.65 of them
    //     (GatheringsUtility.AcceptableGameConditionsToStartGathering), so the party is worth holding when at
    //     least that share of the colony is under pressure;
    //   - the cooldown is the AttendedParty memory: while any colonist still carries it (ThoughtDef
    //     durationDays) the last party is still paying out, so nothing is stored;
    //   - a party's lord job runs 5000 to 15000 ticks (LordJob_Joinable_Party.durationTicks) and, while it
    //     runs, the game refuses a new gathering.

    /// <summary>The GatheringDef whose gatherSpotDefs is the PartySpot.</summary>
    public const string PartyGatheringDef = "Party";

    /// <summary>The memory each attendee gains when the party ends.</summary>
    public const string PartyThought = "AttendedParty";

    /// <summary>The game's own gate (see rooms/gatherings).</summary>
    public const int GatheringMinColonists = 4;

    /// <summary>The game's own guest fraction (see rooms/gatherings).</summary>
    public const double GatheringGuestShare = 0.65;

    /// <summary>
    /// Returns the party to start now, or the zero plan (Organizer empty) when none is
    /// due. Unknown while any gate's input is unknown and no known gate already says no.
    /// </summary>
    public static Fact<GatheringPlan> PlanGathering(GatheringInput input)
    {
        var none = Known(new GatheringPlan());
        var pk = input.Pawns.TryGetValue(out var pawns);
        var lk = input.Ledger.TryGetValue(out var ledger);
        var benefit = input.Benefit;
        var ck = input.Calm.TryGetValue(out var calm);
        var census = input.Census;
        // Known gates that refuse, whatever else is unread.
        if (benefit <= 0 || (ck && !calm))
        {
            return none;
        }
        if (pk)
        {
            var alive = 0;
            foreach (var p in pawns!)
            {
                if (p.Dead)
                {
                    continue;
                }
                alive++;
                if (p.Mental)
                {
                    return none;
                }
                if (HasPartyMemory(p))
                {
                    return none;
                }
            }
            if (alive < GatheringMinColonists)
            {
                return none;
            }
        }
        if (census is not null && !HasPartySpot(census))
        {
            return none;
        }
        if (!pk || !lk || !ck || census is null)
        {
            return Unknown<GatheringPlan>();
        }
        if (!GatheringPressure(ledger!, benefit))
        {
            return none;
        }
        var (organizer, ok) = GatheringOrganizer(pawns!);
        if (!ok)
        {
            return none;
        }
        return Known(new GatheringPlan { Organizer = organizer });
    }

    /// <summary>Reports whether the pawn carries the AttendedParty memory.</summary>
    private static bool HasPartyMemory(MoodPawn p) => p.Thoughts.Any(t => t.Def == PartyThought && t.Offset > 0);

    private static bool HasPartySpot(CurrentConstruction c) => c.Buildings.Any(b => b.Building!.Definition == PartySpotRules.PartySpotDefinition && b.Cells.Count > 0);

    /// <summary>Whether at least GatheringGuestShare of the ledger's colonists lose at least the party's own mood effect.</summary>
    private static bool GatheringPressure(MoodLedger l, double benefit)
    {
        if (l.Pawns.Count == 0)
        {
            return false;
        }
        var pressed = 0;
        foreach (var p in l.Pawns)
        {
            var lost = 0.0;
            foreach (var t in p.Lost)
            {
                lost -= t.Offset;
            }
            if (lost >= benefit)
            {
                pressed++;
            }
        }
        return pressed >= GatheringGuestShare * l.Pawns.Count;
    }

    /// <summary>
    /// The colonist best placed to organize: known alive, undowned, undrafted and out of any mental state, the
    /// highest mood first, then the lowest id. The game's PawnCanStartOrContinueGathering decides the rest and
    /// native refuses otherwise.
    /// </summary>
    private static (PawnId Organizer, bool Ok) GatheringOrganizer(IReadOnlyList<MoodPawn> pawns)
    {
        var able = new List<(MoodPawn Pawn, double Mood)>();
        foreach (var p in pawns)
        {
            if (p.Mood is MoodLevel.Has { Level: var mood } && !p.Dead && !p.Downed && !p.Drafted && !p.Mental)
            {
                able.Add((p, mood));
            }
        }
        if (able.Count == 0)
        {
            return (default, false);
        }
        var best = able.OrderByDescending(a => a.Mood).ThenBy(a => a.Pawn.Id).First().Pawn;
        return (best.Id, true);
    }

    /// <summary>Measures rooms/gatherings: known false when no party is due now, unknown while its inputs are.</summary>
    public static Fact<bool> GatheringOwed(Fact<GatheringPlan> plan) => plan.TryGetValue(out var p) ? Known(!p.Organizer.IsEmpty) : Unknown<bool>();

}
