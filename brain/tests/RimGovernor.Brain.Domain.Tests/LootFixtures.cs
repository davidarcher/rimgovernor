using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Domain.Tests;

internal static class LootFixtures
{
    // Every census field is explicit; fixtures stage native observations, not independent policy facts.
    internal static Policy.LootItem Stack(string thing, string definition, Cell cell, bool forbidden,
        RimGovernor.Brain.GameFacts.Observations.HaulSafety safety, long count, long storageHeadroom, IEnumerable<string> acceptedBy,
        RimGovernor.Brain.GameFacts.Observations.LootDeterioration? deterioration) => new()
    {
        Observed = new RimGovernor.Brain.GameFacts.Observations.LootItem
        {
            Item = new RimGovernor.Brain.GameFacts.Common.Ref { Id = thing }, Forbidden = forbidden, Safety = safety, Count = count,
            StorageHeadroom = storageHeadroom,
            AcceptedBy = EquatableArray.From(acceptedBy.Select(id => new RimGovernor.Brain.GameFacts.Common.Ref { Id = id })),
            Deterioration = deterioration,
        },
        Entity = new RimGovernor.Brain.GameFacts.Observations.EntityRef
        {
            Id = thing, DefName = definition, Label = definition, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, cell),
        },
    };
}
