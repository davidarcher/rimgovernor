using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
namespace RimGovernor.Brain.Bridge;

public static class MoodReads
{
    // The reading's sections, every one has the frame's tick, except the room census read beside it.
    internal static Sections BuildReviewSections(ReviewFrame frame, ColonyReading projection, RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot? rooms)
    {
        var tick = frame.Context?.Tick ?? 0;
        var output = new Sections
        {
            Colony = new Held<ColonyReading> { Value = projection, AsOf = tick, Complete = true, Source = "observations_read_colony_facts" },
            PlanningCells = projection.Window,
            Zones = projection.Zones,
        };
        var emergency = frame.Emergency.Facts;
        output = output with
        {
            Emergency = new Held<EmergencyFacts> { Value = emergency, AsOf = tick, Complete = emergency.AllSeen, Source = "observations_read_status" },
        };
        var work = projection.Facts.ForecastObserved.WorkPawns.Pawns;
        {
            var medical = projection.Facts.MedicalPawns;
            var mood = projection.Facts.MoodPawns;
            var armed = projection.Facts.Armed;
            output = output with
            {
                Pawns = new Held<ReviewPawns>
                {
                    Value = new ReviewPawns { Work = EquatableArray.From(work), Medical = medical, Mood = mood, Armed = armed },
                    AsOf = tick,
                    Complete = true,
                    Source = "observations_list_pawns",
                },
            };
        }
        if (frame.Population is { } population)
        {
            output = output with
            {
                Population = new Held<PrisonerCensus> { Value = population, AsOf = tick, Complete = true, Source = "observations_read_population" },
            };
        }
        output = output with { Research = new Held<ResearchFacts> { Value = projection.Facts.Research, AsOf = tick, Complete = true, Source = "observations_read_research" } };
        if (projection.Facts.Ideology is IdeologyMode.Installed { Primary: var ideology })
        {
            output = output with { Ideology = new Held<Ideoligion> { Value = ideology, AsOf = tick, Complete = true, Source = "snapshot_frame_ideology" } };
        }
        if (rooms is not null)
        {
            output = output with
            {
                Rooms = new Held<RoomObservation> { Value = projection.Facts.Upkeep.Rooms, AsOf = rooms.Context?.Tick ?? 0, Complete = true, Source = "observations_list_rooms" },
            };
        }
        return output;
    }


    internal static EquatableArray<MoodPawn> Mood(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? colony, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? snapshot)
    {

        if (colony is null || snapshot is null
            || colony.ColonistCount != emergency.Colonists.Count)
        {
            throw new ObservationContractException("mood pawn census disagrees with the colony roster");
        }
        var byId = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(StringComparer.Ordinal);
        foreach (var p in snapshot.Pawns)
        {
            if (p is null || byId.ContainsKey((p.Pawn.Id ?? "")))
            {
                throw new ObservationContractException("mood pawn census disagrees with the colony roster");
            }
            byId[(p.Pawn.Id ?? "")] = p;
        }
        var forecasts = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PatientForecast>(StringComparer.Ordinal);
        foreach (var f in colony.Forecast.Patients)
        {
            forecasts[f.PawnId] = f;
        }
        var rows = new List<MoodPawn>();
        foreach (var pawn in emergency.Colonists)
        {
            // A colonist in the emergency roster must have its same-frame pawn row.
            if (!byId.TryGetValue(pawn.Id.Value, out var r))
            {
                throw new ObservationContractException($"mood census misses {pawn.Id}");
            }
            if (r.Dead != pawn.Dead || r.Downed != pawn.Downed || !r.Colonist)
            {
                throw new ObservationContractException("mood pawn census disagrees with the colony roster");
            }
            forecasts.TryGetValue(pawn.Id.Value, out var forecast);
            var mood = new MoodPawn
            {
                Pawn = r,
                Forecast = forecast,
                Thoughts = MoodThoughts(r),
            };
            mood.Validate();
            rows.Add(mood);
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Lifts a pawn row's grouped native thought rows (memories and the situational cache) into the thoughts the
    /// mood census keeps: one row per def with its total offset, most negative first, positive thoughts
    /// (KnowBuriedInSarcophagus) last.
    /// </summary>
    public static EquatableArray<MoodThought> MoodThoughts(RimGovernor.Brain.GameFacts.Observations.PawnState r)
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
    /// Compares independent same-tick censuses before deriving available armed capacity. Missing or contradictory
    /// pawn details must not recover a defense deficit. The second count is the living, conscious colonists able
    /// to fight who hold no weapon: military/defense stays unmet while any remain, so a loose starting club is
    /// equipped (or a weapon crafted) for every one of them, not only the first two.
    /// </summary>
    internal static (long Armed, long Unarmed) Armed(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? colony, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot pawns, CreepJoinerDownsides downsides)
    {
        if (colony is null || pawns is null || colony.ColonistCount != emergency.Colonists.Count || pawns.Pawns.Count != emergency.Colonists.Count)
        {
            throw new ObservationContractException("armed pawn census disagrees with the colony roster");
        }
        var byId = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
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
                throw new ObservationContractException("armed pawn census disagrees with the colony roster");
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
            if (downsides.ArmsHold(row) != "")
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
        return (armed, unarmed);
    }

    /// <summary>
    /// Each census colonist's defense capacity facts from the same pawn rows <see cref="Armed"/> compares:
    /// dead and downed from the emergency census, violence from the biography, melee power as the observed
    /// MeleeDPS times summary health and the observed ranged DPS at the pawn's hit factor. A row the census does not
    /// match, or a frame without the pawn rows, fails the observation contract.
    /// </summary>
    internal static EquatableArray<DefenderStrength> Defenders(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? colony, EmergencyFacts emergency, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? pawns)
    {
        if (colony is null || pawns is null || !emergency.AllSeen || colony.ColonistCount != emergency.Colonists.Count || pawns.Pawns.Count != emergency.Colonists.Count)
        {
            throw new ObservationContractException("defender and colonist censuses disagree");
        }
        var byId = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(pawns.Pawns.Count, StringComparer.Ordinal);
        foreach (var row in pawns.Pawns)
        {
            if (!byId.TryAdd(row.Pawn.Id, row))
            {
                throw new ObservationContractException($"duplicate defender pawn row: {row.Pawn.Id}");
            }
        }
        var output = new List<DefenderStrength>(emergency.Colonists.Count);
        foreach (var pawn in emergency.Colonists)
        {
            if (!byId.TryGetValue(pawn.Id.Value, out var row))
            {
                throw new ObservationContractException($"missing defender pawn row: {pawn.Id.Value}");
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
        return EquatableArray.From(output);
    }
}
