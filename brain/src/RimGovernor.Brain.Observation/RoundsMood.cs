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

    public Held<O.ZonesSnapshot>? Zones { get; init; }

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

public static class RoundsMoodReads
{
    // The reading's sections, every one has the frame's tick, except the room census read beside it.
    internal static RoundsSections BuildRoundsSections(RoundsFrame frame, ColonyProjection projection, O.RoomsSnapshot? rooms)
    {
        var tick = frame.Context?.Tick ?? 0;
        var output = new RoundsSections
        {
            Colony = new Held<ColonyProjection> { Value = projection, AsOf = tick, Complete = true, Source = "rimgovernor/observations_read_colony_facts" },
            PlanningCells = projection.Window,
            Zones = projection.Zones,
        };
        var emergency = frame.Emergency.Facts;
        output = output with
        {
            Emergency = new Held<EmergencyFacts> { Value = emergency, AsOf = tick, Complete = emergency.AllSeen, Source = "rimgovernor/observations_read_status" },
        };
        if (projection.WorkPawns is WorkPawnsRead.Read { Pawns: var work })
        {
            projection.Facts.MedicalPawns.TryGetValue(out var medical);
            projection.Facts.MoodPawns.TryGetValue(out var mood);
            projection.Facts.Armed.TryGetValue(out var armed);
            output = output with
            {
                Pawns = new Held<RoundsPawns>
                {
                    Value = new RoundsPawns { Work = EquatableArray.From(work), Medical = medical, Mood = mood, Armed = armed },
                    AsOf = tick,
                    Complete = true,
                    Source = "rimgovernor/observations_list_pawns",
                },
            };
        }
        if (frame.Population is { } population)
        {
            output = output with
            {
                Population = new Held<PrisonerCensus> { Value = population, AsOf = tick, Complete = true, Source = "rimgovernor/observations_read_population" },
            };
        }
        if (projection.Facts.Research is { } research)
        {
            output = output with { Research = new Held<ResearchFacts> { Value = research, AsOf = tick, Complete = true, Source = "rimgovernor/observations_read_research" } };
        }
        if (projection.Facts.Ideology is IdeologyMode.Installed { Primary: var ideology })
        {
            output = output with { Ideology = new Held<Ideoligion> { Value = ideology, AsOf = tick, Complete = true, Source = "rimgovernor/snapshot_frame_ideology" } };
        }
        if (rooms is not null)
        {
            output = output with
            {
                Rooms = new Held<RoomObservation> { Value = projection.Rooms, AsOf = rooms.Context?.Tick ?? 0, Complete = true, Source = "rimgovernor/observations_list_rooms" },
            };
        }
        return output;
    }


    internal static Fact<EquatableArray<MoodPawn>> RoundsMood(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot? snapshot)
    {
        var unknown = Unknown<EquatableArray<MoodPawn>>();
        if (!emergency.AllSeen || colony is null || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count)
        {
            return unknown;
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var p in snapshot.Pawns)
        {
            if (p is null || byId.ContainsKey((p.Pawn.Id ?? "")))
            {
                return unknown;
            }
            byId[(p.Pawn.Id ?? "")] = p;
        }
        var forecasts = new Dictionary<string, O.PatientForecast>(StringComparer.Ordinal);
        foreach (var f in colony.Forecast.Patients)
        {
            forecasts[f.PawnId] = f;
        }
        var rows = new List<MoodPawn>();
        foreach (var pawn in emergency.Colonists)
        {
            // Exact missing IDs preserve prior active history.
            if (!byId.TryGetValue(pawn.Id.Value, out var r))
            {
                continue;
            }
            if (r.Dead != pawn.Dead || r.Downed != pawn.Downed || !r.Colonist)
            {
                return unknown;
            }
            var n = r.Needs;
            rows.Add(new MoodPawn
            {
                Break = pawn.MentalState,
                Id = pawn.Id,
                Dead = r.Dead,
                Downed = r.Downed,
                Drafted = r.Drafted,
                Mental = r.MentalState is not null,
                PlayerForced = r.Job.PlayerForced,
                Mood = MoodLevelOf(pawn.Id.Value, n.Mood, forecasts),
                Threshold = n.BreakThresholdMinor,
                Food = n.Food is O.FoodNeed.Has food ? new O.NeedLevel.Has(food.Level) : new O.NeedLevel.Lacks(),
                Rest = n.Rest,
                Joy = n.Joy,
                Thoughts = MoodThoughts(r),
                HighExpectations = r.Social.HighExpectations,
            });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>
    /// A colonist's mood level with the target it is heading to: the forecast's patient row carries the target of
    /// every colonist with a mood need, so a mood need without one is a missing read, and a colonist without a
    /// mood need has neither.
    /// </summary>
    public static MoodLevel MoodLevelOf(string id, O.NeedLevel mood, IReadOnlyDictionary<string, O.PatientForecast> forecasts)
    {
        if (mood is not O.NeedLevel.Has { Level: var level })
        {
            return new MoodLevel.Lacks();
        }
        if (!forecasts.TryGetValue(id, out var f))
        {
            throw new RequiredFactException($"mood target of {id}");
        }
        return new MoodLevel.Has(level, f.MoodTarget);
    }

    /// <summary>
    /// Lifts a pawn row's grouped native thought rows (memories and the situational cache) into the thoughts the
    /// mood census keeps: one row per def with its total offset, most negative first, positive thoughts
    /// (KnowBuriedInSarcophagus) last.
    /// </summary>
    public static EquatableArray<MoodThought> MoodThoughts(O.PawnState r)
    {
        var byDef = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var rows in new[] { r.Social.Memories, r.Social.Situational })
        {
            foreach (var t in rows)
            {
                byDef[t.DefName] = byDef.GetValueOrDefault(t.DefName) + t.MoodOffsetTotal;
            }
        }
        var result = new List<MoodThought>();
        foreach (var (def, offset) in byDef)
        {
            if (offset != 0)
            {
                result.Add(new MoodThought(def, offset));
            }
        }
        result.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : string.CompareOrdinal(a.Def, b.Def));
        return EquatableArray.From(result);
    }


    /// <summary>
    /// Builds the colony mood ledger from the review's mood census, each pawn's traits and precepts, and the
    /// catalog's ThoughtFacts. The ledger is unknown when the census is; a census pawn the snapshot lacks is a
    /// missing read.
    /// </summary>
    internal static Fact<MoodLedger> RoundsMoodLedger(Fact<EquatableArray<MoodPawn>> census, O.PawnSnapshot snapshot, IReadOnlyDictionary<string, ThoughtFacts> facts)
    {
        if (!census.TryGetValue(out var moods))
        {
            return Unknown<MoodLedger>();
        }
        var byId = new Dictionary<string, O.PawnState>(StringComparer.Ordinal);
        foreach (var p in snapshot.Pawns)
        {
            byId[p.Pawn.Id ?? ""] = p;
        }
        var pawns = new List<MoodLedgerPawn>(moods.Count);
        foreach (var m in moods)
        {
            if (!byId.TryGetValue(m.Id.Value, out var r))
            {
                throw new RequiredFactException($"pawn row of {m.Id.Value}");
            }
            pawns.Add(new MoodLedgerPawn
            {
                Thoughts = m.Thoughts,
                Traits = [.. r.Biography.Traits.Select(t => t.DefName)],
                Expectation = r.Social.ExpectationDefName,
                Precepts = r.Settings.PolicyInputs.Ideo is O.PawnIdeo.Member ideo ? [.. ideo.Precepts] : [],
            });
        }
        return Known(MoodRules.BuildMoodLedger(pawns, facts));
    }


    /// <summary>
    /// Compares independent same-tick censuses before deriving available armed capacity. Missing or contradictory
    /// pawn details must not recover a defense deficit. The second count is the living, conscious colonists able
    /// to fight who hold no weapon: military/defense stays unmet while any remain, so a loose starting club is
    /// equipped (or a weapon crafted) for every one of them, not only the first two.
    /// </summary>
    internal static (Fact<long> Armed, Fact<long> Unarmed) RoundsArmed(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot pawns, CreepJoinerDownsides downsides)
    {
        var unknown = Unknown<long>();
        if (colony is null || pawns is null || colony.ColonistCount != emergency.Colonists.Count || pawns.Pawns.Count != emergency.Colonists.Count)
        {
            return (unknown, unknown);
        }
        var byId = new Dictionary<string, O.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            byId[row.Pawn.Id ?? ""] = row;
        }
        long armed = 0, unarmed = 0;
        foreach (var pawn in emergency.Colonists)
        {
            byId.TryGetValue(pawn.Id.Value, out var row);
            if (row is null || row.Dead != pawn.Dead || row.Downed != pawn.Downed || !row.Colonist)
            {
                return (unknown, unknown);
            }
            if (pawn.Dead || pawn.Downed)
            {
                continue;
            }
            if (row.Equipment.Armed)
            {
                armed++;
                continue;
            }
            // A colonist held back from arms (a creepjoiner whose downside has not shown) is owed no weapon:
            // counting it would hold military/defense unmet for good.
            if (downsides.ArmsHold(CreepJoinerFacts.CreepJoinerPawn(row)) != "")
            {
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
        return (Known(armed), Known(unarmed));
    }

    /// <summary>
    /// Each census colonist's defense capacity facts from the same pawn rows <see cref="RoundsArmed"/> compares:
    /// dead and downed from the emergency census, violence from the biography, melee power as the observed
    /// MeleeDPS times summary health and the observed ranged DPS at the pawn's hit factor. A row the census does not
    /// match, or a frame without the pawn rows, leaves the roster unknown.
    /// </summary>
    internal static Fact<EquatableArray<DefenderStrength>> RoundsDefenders(O.ColonyFactsSnapshot? colony, EmergencyFacts emergency, O.PawnSnapshot? pawns)
    {
        var unknown = Unknown<EquatableArray<DefenderStrength>>();
        if (colony is null || pawns is null || colony.ColonistCount != emergency.Colonists.Count || pawns.Pawns.Count != emergency.Colonists.Count)
        {
            return unknown;
        }
        var byId = new Dictionary<string, O.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            byId[row.Pawn.Id ?? ""] = row;
        }
        var output = new List<DefenderStrength>(emergency.Colonists.Count);
        foreach (var pawn in emergency.Colonists)
        {
            if (!byId.TryGetValue(pawn.Id.Value, out var row))
            {
                return unknown;
            }
            var e = row.Equipment;
            // Ranged DPS counts at the pawn's hit chance over the reference gunner's (#3845).
            output.Add(new DefenderStrength
            {
                Id = pawn.Id,
                Dead = pawn.Dead,
                Downed = pawn.Downed,
                ViolenceCapable = !row.Biography.DisabledWorkTags.Contains("Violent"),
                MeleePower = e.MeleeDps * row.Health.SummaryFraction,
                RangedDps = e.RangedDps * e.RangedHitFactor,
            });
        }
        return Known(EquatableArray.From(output));
    }
}
