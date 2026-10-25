using RimGovernor.Brain.Domain;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

internal static class LootFixtures
{
    // Every census field is explicit; fixtures stage native observations, not independent policy facts.
    internal static Policy.LootItem Stack(string thing, string definition, Cell cell, bool forbidden,
        O.HaulSafety safety, long count, long storageHeadroom, IEnumerable<string> acceptedBy,
        O.LootDeterioration? deterioration) => new()
    {
        Observed = new O.LootItem
        {
            Item = new C.Ref { Id = thing }, Forbidden = forbidden, Safety = safety, Count = count,
            StorageHeadroom = storageHeadroom,
            AcceptedBy = EquatableArray.From(acceptedBy.Select(id => new C.Ref { Id = id })),
            Deterioration = deterioration,
        },
        Entity = new O.EntityRef
        {
            Id = thing, DefName = definition, Label = definition, Place = new O.EntityPlace.OnMap(1, cell),
        },
    };
}
