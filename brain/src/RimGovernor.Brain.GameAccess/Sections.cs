using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public sealed record Sections
{
    public Held<ColonyReading>? Colony { get; init; }

    public Held<PlanningCells>? PlanningCells { get; init; }

    public Held<PrisonerCensus>? Population { get; init; }

    public Held<ResearchFacts>? Research { get; init; }

    public Held<ReviewPawns>? Pawns { get; init; }

    public Held<EmergencyFacts>? Emergency { get; init; }

    public Held<RoomObservation>? Rooms { get; init; }

    public Held<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>? Zones { get; init; }

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

/// <summary>
/// The colonists' routine pawn detail projected against the colony and emergency census: the work, medical and
/// mood views and the armed count.
/// </summary>
public sealed record ReviewPawns
{
    public EquatableArray<WorkPawn> Work { get; init; }

    public EquatableArray<CarePawn> Medical { get; init; }

    public EquatableArray<MoodPawn> Mood { get; init; }

    public long Armed { get; init; }
}

