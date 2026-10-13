namespace RimGovernor.Brain.Observation;

/// <summary>
/// A routine reading's decoded census as the fact store holds it: one <see cref="Held{T}"/> per section the
/// bracket read, with the tick that section's reply described and the method that produced it. A section the
/// source did not offer, or whose read left the fact unknown, is null and <see cref="File"/> skips it.
/// planning_cells is filed apart from colony: an older native lists it in the colony reply, a current one serves
/// it through observations_get_cells with its own tick.
/// </summary>
public sealed record RoundsSections
{
    public Held<ColonyProjection>? Colony { get; init; }

    public Held<PlanningCells>? PlanningCells { get; init; }

    public Held<PrisonerCensus>? Population { get; init; }

    public Held<ResearchFacts>? Research { get; init; }

    public Held<RoundsPawns>? Pawns { get; init; }

    public Held<EmergencyFacts>? Emergency { get; init; }

    public Held<RoomObservation>? Rooms { get; init; }

    public Held<ZonesRead>? Zones { get; init; }

    public Held<Ideoligion>? Ideology { get; init; }

    /// <summary>Puts every section the reading read into <paramref name="store"/> under <paramref name="scope"/>.</summary>
    public void File(FactStore? store, Scope scope)
    {
        if (store is null)
        {
            return;
        }
        FileSection(store, scope, Section.Colony, Colony);
        FileSection(store, scope, Section.PlanningCells, PlanningCells);
        FileSection(store, scope, Section.Population, Population);
        FileSection(store, scope, Section.Research, Research);
        FileSection(store, scope, Section.Pawns, Pawns);
        FileSection(store, scope, Section.Emergency, Emergency);
        FileSection(store, scope, Section.Rooms, Rooms);
        FileSection(store, scope, Section.Zones, Zones);
        FileSection(store, scope, Section.Ideology, Ideology);
    }

    private static void FileSection<T>(FactStore store, Scope scope, Section section, Held<T>? held)
    {
        if (held is null || held.Source == "")
        {
            return;
        }
        FactStore.Put(store, scope, section, held);
    }
}

/// <summary>The colony facts' planning window: the observed region and the cells read inside it.</summary>
public sealed record PlanningCells
{
    public Rectangle Region { get; init; }

    public EquatableArray<SiteCell> Cells { get; init; }
}

/// <summary>
/// The colonists' routine pawn detail projected against the colony and emergency census: the work, medical and
/// mood views and the armed count.
/// </summary>
public sealed record RoundsPawns
{
    public EquatableArray<WorkPawn> Work { get; init; }

    public EquatableArray<CarePawn> Medical { get; init; }

    public EquatableArray<MoodPawn> Mood { get; init; }

    public long Armed { get; init; }
}

public static partial class ObservationPackage
{
    // rounds_sections.go: the reading's sections, every one has the frame's tick, except the room census read beside it.
    internal static RoundsSections BuildRoundsSections(RoundsFrame frame, ColonyProjection projection, O.RoomsSnapshot? rooms)
    {
        var tick = frame.Context.Tick;
        var output = new RoundsSections
        {
            Colony = new Held<ColonyProjection> { Value = projection, AsOf = tick, Complete = true, Source = "rimgovernor/observations_read_colony_facts" },
            PlanningCells = projection.Window,
            Zones = projection.Zones,
        };
        var emergency = frame.Emergency.Facts;
        var known = emergency.ColonistsComplete.TryGetValue(out var complete);
        output = output with
        {
            Emergency = new Held<EmergencyFacts> { Value = emergency, AsOf = tick, Complete = known && complete, Source = "rimgovernor/observations_read_status" },
        };
        if (projection.WorkPawns.TryGetValue(out var work))
        {
            projection.Facts.MedicalPawns.TryGetValue(out var medical);
            projection.Facts.MoodPawns.TryGetValue(out var mood);
            projection.Facts.Armed.TryGetValue(out var armed);
            output = output with
            {
                Pawns = new Held<RoundsPawns>
                {
                    Value = new RoundsPawns { Work = EquatableArray.From(work), Medical = EquatableArray.From(medical ?? []), Mood = EquatableArray.From(mood ?? []), Armed = armed },
                    AsOf = tick,
                    Complete = true,
                    Source = "rimgovernor/observations_list_pawns",
                },
            };
        }
        if (frame.Population is { } population)
        {
            var prisoners = population.Prisoners.IsKnown;
            var custody = population.Custody.IsKnown;
            output = output with
            {
                Population = new Held<PrisonerCensus> { Value = population, AsOf = tick, Complete = prisoners && custody, Source = "rimgovernor/observations_read_population" },
            };
        }
        if (projection.Facts.Research.TryGetValue(out var research))
        {
            output = output with { Research = new Held<ResearchFacts> { Value = research, AsOf = tick, Complete = true, Source = "rimgovernor/observations_read_research" } };
        }
        if (projection.Facts.Ideology.TryGetValue(out var ideology))
        {
            output = output with { Ideology = new Held<Ideoligion> { Value = ideology, AsOf = tick, Complete = true, Source = "rimgovernor/snapshot_frame_ideology" } };
        }
        if (projection.Rooms.TryGetValue(out var census) && rooms is not null)
        {
            output = output with
            {
                Rooms = new Held<RoomObservation> { Value = census, AsOf = rooms.Context?.Tick ?? 0, Complete = true, Source = "rimgovernor/observations_list_rooms" },
            };
        }
        return output;
    }

    // rounds_mood.go

    private static Fact<IReadOnlyList<MoodPawn>> RoundsMood(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot? snapshot)
    {
        var unknown = Unknown<IReadOnlyList<MoodPawn>>();
        if (!emergency.ColonistsComplete.TryGetValue(out var complete) || !complete || colony is null || !colony.HasColonistCount || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count)
        {
            return unknown;
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var p in snapshot.Pawns)
        {
            if (p is null || p.Pawn is null || byId.ContainsKey(p.Pawn.Id))
            {
                return unknown;
            }
            byId[p.Pawn.Id] = p;
        }
        var forecasts = new Dictionary<string, O.PatientForecast>(StringComparer.Ordinal);
        if (colony.Forecast?.Observed is { } observed)
        {
            foreach (var f in observed.Patients)
            {
                forecasts[f.PawnId] = f;
            }
        }
        var rows = new List<MoodPawn>();
        foreach (var pawn in emergency.Colonists)
        {
            // Exact missing IDs preserve prior active history.
            if (!byId.TryGetValue(pawn.ID.Value, out var r))
            {
                continue;
            }
            var dk = pawn.Dead.TryGetValue(out var dead);
            var nk = pawn.Downed.TryGetValue(out var downed);
            if (!dk || !nk || !r.HasDead || !r.HasDowned || r.Dead != dead || r.Downed != downed || !r.HasColonist || !r.Colonist)
            {
                return unknown;
            }
            var p = new MoodPawn
            {
                Break = pawn.MentalState,
                ID = pawn.ID,
                Dead = Optional(r.HasDead, r.Dead),
                Downed = Optional(r.HasDowned, r.Downed),
                Drafted = Optional(r.HasDrafted, r.Drafted),
                Mental = NativePresence(r.MentalState, r.Issues, "mental_state"),
            };
            if (r.Job is { } job && !HasIssue(r.Issues, "job"))
            {
                p = p with { PlayerForced = Optional(job.HasPlayerForced, job.PlayerForced) };
            }
            if (r.Needs is { } n && !HasIssue(r.Issues, "needs"))
            {
                p = p with
                {
                    Mood = Optional(n.HasMood, n.Mood),
                    Threshold = Optional(n.HasBreakThresholdMinor, n.BreakThresholdMinor),
                    Food = Optional(n.HasFood, n.Food),
                    Rest = Optional(n.HasRest, n.Rest),
                    Joy = Optional(n.HasJoy, n.Joy),
                };
            }
            if (forecasts.TryGetValue(pawn.ID.Value, out var f) && !HasIssue(f.Issues, "mood_target"))
            {
                p = p with { Target = Optional(f.HasMoodTarget, f.MoodTarget) };
            }
            p = p with { Thoughts = MoodThoughts(r) };
            if (r.Social is { } social && !HasIssue(r.Issues, "social"))
            {
                p = p with { HighExpectations = Optional(social.HasHighExpectations, social.HighExpectations) };
            }
            rows.Add(p);
        }
        return Known<IReadOnlyList<MoodPawn>>(rows);
    }

    /// <summary>
    /// Lifts a pawn row's grouped native thought rows (memories and the situational cache) into the thoughts the
    /// mood census keeps: one row per def with its total offset, most negative first, positive thoughts
    /// (KnowBuriedInSarcophagus) last. A skipped or unreadable social block, or a missing memories/situational
    /// list, leaves the thoughts unknown rather than empty.
    /// </summary>
    public static Fact<IReadOnlyList<MoodThought>> MoodThoughts(O.PawnState r)
    {
        var unknown = Unknown<IReadOnlyList<MoodThought>>();
        var social = r.Social;
        if (social is null || HasIssue(r.Issues, "social") || HasIssue(social.Issues, "memories") || HasIssue(social.Issues, "situational"))
        {
            return unknown;
        }
        var byDef = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var rows in new[] { social.Memories, social.Situational })
        {
            foreach (var t in rows)
            {
                if (t is null || !t.HasMoodOffsetTotal)
                {
                    return unknown;
                }
                byDef[t.DefName] = byDef.GetValueOrDefault(t.DefName) + t.MoodOffsetTotal;
            }
        }
        var result = new List<MoodThought>();
        foreach (var (def, offset) in byDef)
        {
            if (offset != 0)
            {
                result.Add(new MoodThought { Def = def, Offset = offset });
            }
        }
        result.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : string.CompareOrdinal(a.Def, b.Def));
        return Known<IReadOnlyList<MoodThought>>(result);
    }

    // rounds_mood_ledger.go

    /// <summary>
    /// Builds the colony mood ledger from the review's mood census, each pawn's traits and precepts, and the
    /// catalog's ThoughtFacts. The ledger is unknown when the census is. A pawn attribute the snapshot lacks (an
    /// unread biography, policy or social block) stays unknown, so MinExpectation thoughts of such a pawn stay
    /// unverified.
    /// </summary>
    private static Fact<MoodLedger> RoundsMoodLedger(Fact<IReadOnlyList<MoodPawn>> census, O.PawnSnapshot? snapshot, IReadOnlyDictionary<string, ThoughtFacts> facts)
    {
        if (!census.TryGetValue(out var moods))
        {
            return Unknown<MoodLedger>();
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        if (snapshot is not null)
        {
            foreach (var p in snapshot.Pawns)
            {
                byId[p.Pawn?.Id ?? ""] = p;
            }
        }
        var pawns = new List<MoodLedgerPawn>(moods.Count);
        foreach (var m in moods)
        {
            var pawn = new MoodLedgerPawn
            {
                ID = m.ID,
                Thoughts = m.Thoughts,
                Traits = Unknown<IReadOnlyList<string>>(),
                Precepts = Unknown<IReadOnlyList<string>>(),
                Expectation = Unknown<string>(),
            };
            if (byId.TryGetValue(m.ID.Value, out var r))
            {
                if (r.Biography is { } b && !HasIssue(b.Issues, "traits"))
                {
                    pawn = pawn with { Traits = Known<IReadOnlyList<string>>([.. b.Traits.Select(t => t.DefName)]) };
                }
                if (r.Social is { } s && !HasIssue(r.Issues, "social") && s.HasExpectationDefName)
                {
                    pawn = pawn with { Expectation = Known(s.ExpectationDefName) };
                }
                if (r.Settings is { } settings && !HasIssue(settings.Issues, "policy_inputs") && PawnPolicyInputs(settings.PolicyInputs).TryGetValue(out var inputs))
                {
                    pawn = pawn with { Precepts = Known<IReadOnlyList<string>>([.. inputs.Precepts]) };
                }
            }
            pawns.Add(pawn);
        }
        return Known(PolicyFunctions.BuildMoodLedger(pawns, facts));
    }

    // rounds_pawns.go

    /// <summary>
    /// Compares independent same-tick censuses before deriving available armed capacity. Missing or contradictory
    /// pawn details must not recover a defense deficit. The second count is the living, conscious colonists able
    /// to fight who hold no weapon: EnsureBasicDefense stays owed while any remain, so a loose starting club is
    /// equipped (or a weapon crafted) for every one of them, not only the first two. A row without its biography
    /// leaves it unknown.
    /// </summary>
    private static (Fact<long> Armed, Fact<long> Unarmed) RoundsArmed(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot pawns, CreepJoinerDownsides downsides)
    {
        var unknown = Unknown<long>();
        if (colony is null || !colony.HasColonistCount || colony.ColonistCount != emergency.Colonists.Count || pawns.Pawns.Count != emergency.Colonists.Count)
        {
            return (unknown, unknown);
        }
        var byId = new Dictionary<string, O.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            byId[row.Pawn?.Id ?? ""] = row;
        }
        long armed = 0, unarmed = 0;
        var unarmedKnown = true;
        foreach (var pawn in emergency.Colonists)
        {
            byId.TryGetValue(pawn.ID.Value, out var row);
            var dk = pawn.Dead.TryGetValue(out var dead);
            var nk = pawn.Downed.TryGetValue(out var downed);
            if (row is null || !dk || !nk || !row.HasDead || !row.HasDowned || row.Dead != dead || row.Downed != downed || !row.HasColonist || !row.Colonist)
            {
                return (unknown, unknown);
            }
            if (dead || downed)
            {
                continue;
            }
            if (row.Equipment is null || !row.Equipment.HasArmed)
            {
                return (unknown, unknown);
            }
            if (row.Equipment.Armed)
            {
                armed++;
                continue;
            }
            // A colonist held back from arms (a creepjoiner whose downside has not shown) is owed no weapon:
            // counting it would hold EnsureBasicDefense open for good.
            if (downsides.ArmsHold(CreepJoinerPawn(row)) != "")
            {
                continue;
            }
            if (row.Biography is null || HasIssue(row.Biography.Issues, "disabled_work_tags"))
            {
                unarmedKnown = false;
                continue;
            }
            var violent = true;
            foreach (var tag in row.Biography.DisabledWorkTags)
            {
                violent = violent && tag != "Violent";
            }
            if (violent)
            {
                unarmed++;
            }
        }
        return (Known(armed), unarmedKnown ? Known(unarmed) : unknown);
    }

    /// <summary>
    /// Each census colonist's defense capacity facts from the same pawn rows <see cref="RoundsArmed"/> compares:
    /// dead and downed from the emergency census, violence from the biography, melee power as the observed
    /// MeleeDPS times summary health and the observed ranged DPS. A row the census does not match leaves the
    /// roster unknown; a missing stat leaves that fact unknown.
    /// </summary>
    private static Fact<IReadOnlyList<SquadDefenderFacts>> RoundsDefenders(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot pawns)
    {
        var unknown = Unknown<IReadOnlyList<SquadDefenderFacts>>();
        if (colony is null || !colony.HasColonistCount || colony.ColonistCount != emergency.Colonists.Count || pawns.Pawns.Count != emergency.Colonists.Count)
        {
            return unknown;
        }
        var byId = new Dictionary<string, O.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            byId[row.Pawn?.Id ?? ""] = row;
        }
        var output = new List<SquadDefenderFacts>(emergency.Colonists.Count);
        foreach (var pawn in emergency.Colonists)
        {
            if (!byId.TryGetValue(pawn.ID.Value, out var row))
            {
                return unknown;
            }
            var d = new SquadDefenderFacts { ID = new RimGovernor.Brain.Domain.PawnId(pawn.ID.Value), Dead = pawn.Dead, Downed = pawn.Downed };
            if (PawnBiotech(row.Biotech).TryGetValue(out var bt))
            {
                d = d with { Deathresting = bt.IsDeathresting() };
            }
            if (row.Biography is { } bio && !HasIssue(bio.Issues, "disabled_work_tags"))
            {
                var violent = true;
                foreach (var tag in bio.DisabledWorkTags)
                {
                    violent = violent && tag != "Violent";
                }
                d = d with { ViolenceCapable = Known(violent) };
            }
            if (row.Equipment is { } e)
            {
                d = d with { RangedDPS = Optional(e.HasRangedDps, e.RangedDps) };
                if (e.HasMeleeDps && row.Health is { } health && health.HasSummaryFraction)
                {
                    d = d with { MeleePower = Known(e.MeleeDps * health.SummaryFraction) };
                }
            }
            output.Add(d);
        }
        return Known<IReadOnlyList<SquadDefenderFacts>>(output);
    }
}
