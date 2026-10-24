using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Common = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A row head is total (#3768): its id, def name and label are required, and where it is is an
/// <see cref="EntityPlace"/>, on a map's cell or unplaced, never an absent map and position. Both cases round-trip
/// through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class EntityRefWireTests
{
    [Fact]
    public void OnMapJsonRoundTripIsExact()
    {
        var head = new EntityRef { Id = "Thing_Steel1", DefName = "Steel", Label = "steel x75", Place = new EntityPlace.OnMap(3, new Common.Cell { X = 10, Z = 12 }) };

        var json = WireJson.Serialize(head);

        Assert.Equal("""{"id":"Thing_Steel1","defName":"Steel","label":"steel x75","place":{"type":"on_map","mapId":3,"cell":{"x":10,"z":12}}}""", json);
        Assert.Equal(head, WireJson.Deserialize<EntityRef>(json));
    }

    [Fact]
    public void UnplacedJsonRoundTripIsExact()
    {
        var head = new EntityRef { Id = "Caravan_4", DefName = "Caravan", Label = "Caravan 4", Place = new EntityPlace.Unplaced() };

        var json = WireJson.Serialize(head);

        Assert.Equal("""{"id":"Caravan_4","defName":"Caravan","label":"Caravan 4","place":{"type":"unplaced"}}""", json);
        Assert.Equal(head, WireJson.Deserialize<EntityRef>(json));
    }
}
