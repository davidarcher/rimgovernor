namespace RimGovernor.Brain.Policy;

/// <summary>The colonists the planned throne room and suites serve (#3807, #3826), for their room credit.</summary>
public static class RoyalRoomRules
{
    /// <summary>
    /// Per planned suite the suite step houses a colonist in (<see cref="RoomQualityRules.PlannedSuiteOccupants"/>), that
    /// colonist and its title's bedroom requirement (<see cref="SleepingPerson.Title"/>; none, 0 and 0, for an untitled one, whose
    /// suite relieves its Greedy or Jealous thought, #3826); and the throne room <paramref name="throne"/>'s holder is owed
    /// (<see cref="LayoutPlan.ThroneRoomFor"/>), the holder and the throne-room requirement of the title it holds now
    /// (<see cref="ThroneNeed.Held"/>). A holder holding no title that asks for a throne gives no row: no throne room thought weighs on it.
    /// </summary>
    public static IReadOnlyDictionary<int, RoyalRoomNeed> PlannedRoyalRooms(LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping,
        IReadOnlyList<SuiteClaim> claims, ThroneNeed? throne)
    {
        var output = new Dictionary<int, RoyalRoomNeed>();
        var titles = new Dictionary<PawnId, RoyalTitle>();
        foreach (var person in sleeping.People)
        {
            if (person.Title is { } title)
            {
                titles[person.Id] = title;
            }
        }
        foreach (var (room, pawn) in RoomQualityRules.PlannedSuiteOccupants(plan, rooms, sleeping, claims))
        {
            output[room] = titles.TryGetValue(pawn, out var title)
                ? new RoyalRoomNeed(pawn.Value, title.BedroomMinArea, title.BedroomMinImpressiveness)
                : new RoyalRoomNeed(pawn.Value, 0, 0);
        }
        if (throne is { Held: { } held } && plan.ThroneRoomFor(throne.MinArea) is (var throneRoom, true))
        {
            output[throneRoom.Id] = new RoyalRoomNeed(throne.Holder.Value, held.MinArea, held.MinImpressiveness);
        }
        return output;
    }
}
