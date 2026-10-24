namespace RimGovernor.Brain.Policy;

/// <summary>The titled colonists the planned throne room and suites serve (#3807), for their room credit.</summary>
public static class RoyalRoomRules
{
    /// <summary>
    /// Per planned suite the suite step houses a titled colonist in (<see cref="RoomQualityRules.PlannedSuiteOccupants"/>), that
    /// colonist and its title's bedroom requirement (<see cref="SleepingPerson.Title"/>); and the throne room
    /// <paramref name="throne"/>'s holder is owed (<see cref="LayoutPlan.ThroneRoomFor"/>), the holder and the throne-room
    /// requirement of the title it holds now (<see cref="ThroneNeed.Held"/>). A colonist without a title, or a holder holding none
    /// that asks for a throne, gives no row: no title room thought weighs on it.
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
            if (titles.TryGetValue(pawn, out var title))
            {
                output[room] = new RoyalRoomNeed(pawn.Value, title.BedroomMinArea, title.BedroomMinImpressiveness);
            }
        }
        if (throne is { Held: { } held } && plan.ThroneRoomFor(throne.MinArea) is (var throneRoom, true))
        {
            output[throneRoom.Id] = new RoyalRoomNeed(throne.Holder.Value, held.MinArea, held.MinImpressiveness);
        }
        return output;
    }
}
