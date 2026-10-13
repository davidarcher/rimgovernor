using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_comfort.go: the comfort pricing's catalog input (#2975): the NeedDef curves of comfort and beauty, the chairs a
// pawn can sit on and the beds an adult sleeps in, each at every stuff the game allows, with the comfort stat it shows at
// Normal quality and the market value of its adjusted costs. A piece whose comfort the game does not show is left out. The
// bed offset is the comfort the end table and dresser every bedroom template places add (CompProperties_Facility's stat
// offsets). A catalog without the NeedDef rows has an unknown curve, not an error; the pricing then stays off.
//
// A throne is sittable but a room role's building, never a work chair, and is left out of the chairs.

public sealed partial class DefinitionCatalog
{
    private P.ComfortModel ComfortModel(P.RoomFurniture furniture)
    {
        var game = P.PolicyFunctions.SeekerGame();
        P.NeedCurve Curve(string name) =>
            DefRow<NeedDef>(name) is { } row ? new P.NeedCurve(Decimal32(row.baseLevel), Decimal32(row.seekerRisePerHour), Decimal32(row.seekerFallPerHour), row.freezeWhileSleeping) : default;

        var chairs = new List<P.ComfortOption>();
        foreach (var name in _thingDefs.Keys.Order(StringComparer.Ordinal))
        {
            var row = _thingDefs[name];
            if (Buildable(row) && row.building is { isSittable: true } && !IsClass(row, typeof(Building_Throne)))
            {
                chairs.AddRange(ComfortOptions(name));
            }
        }

        var beds = new List<P.ComfortOption>();
        foreach (var bed in Beds().Sleeping.Where(b => b.Slots == 1))
        {
            beds.AddRange(ComfortOptions(bed.Def));
        }

        var facilities = Facilities();
        var offset = 0.0;
        foreach (var link in new[] { furniture.EndTable, furniture.Dresser })
        {
            offset += facilities.TryGetValue(link.Def, out var facility) && facility.Offsets.TryGetValue(StatNames.Comfort, out var value) ? value : 0;
        }

        return new P.ComfortModel { Comfort = Curve(game.Comfort.Need), Beauty = Curve(game.Beauty.Need), Chairs = chairs, Beds = beds, BedOffset = offset };
    }

    // A def's pieces, one per allowed stuff (one for a def with none): the comfort stat the game shows and the cost's market value.
    private List<P.ComfortOption> ComfortOptions(string def)
    {
        var stuffs = AllowedStuffs(def);
        var options = new List<P.ComfortOption>();
        foreach (var stuff in stuffs.Count == 0 ? [string.Empty] : stuffs)
        {
            var (comfort, shown) = ShownStatValue(def, stuff, StatNames.Comfort);
            if (shown)
            {
                options.Add(new P.ComfortOption { Def = def, Stuff = stuff, Comfort = comfort, Build = CostValueOf(def, stuff) });
            }
        }

        return options;
    }
}
