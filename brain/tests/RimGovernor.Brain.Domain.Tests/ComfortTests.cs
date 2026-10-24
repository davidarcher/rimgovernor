using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The comfort census is one total facts record (#3771): every member is set, a recreation source's room a named
/// <see cref="ComfortHost"/> and the joy boredom matrix always read. On the wire it round-trips through
/// <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class ComfortTests
{
    /// <summary>A census with no colonists, surfaces, seats, sources or joy kinds: the base every test comfort census starts from.</summary>
    internal static readonly ComfortFacts NoComfort = new() { People = [], Surfaces = [], Dining = [], Recreation = [], Joy = new([], []) };

    /// <summary>The empty census with no catalog rows: the base every test comfort observation starts from.</summary>
    internal static readonly ComfortObservation NoComfortObservation = new()
    {
        Census = NoComfort, RecreationFoothold = "", WatchBuildings = [], Furniture = new(), JoyMethods = [],
    };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var census = NoComfort with
        {
            People = [new PawnId("Thing_Human1")],
            Surfaces = [new ComfortSurface("7", [new(3, 4)])],
            Dining = [new DiningSeat("Thing_Chair3", "7", [new PawnId("Thing_Human1")], [])],
            Recreation =
            [
                new RecreationSource("Thing_Horseshoes4", "Gaming_Dexterity", new ComfortHost.Outdoors(), [new PawnId("Thing_Human1")], [new PawnId("Thing_Human1")]),
                new RecreationSource("Thing_Chess5", "Gaming_Cerebral", new ComfortHost.Indoors("7"), [], []),
            ],
            Joy = new(["Gaming_Cerebral", "Gaming_Dexterity"], [new JoyTolerance(new PawnId("Thing_Human1"), [false, true])]),
        };

        var json = WireJson.Serialize(census);

        Assert.Equal(
            """{"people":["Thing_Human1"],"surfaces":[{"room":"7","adjacent":[{"x":3,"z":4}]}],"dining":[{"id":"Thing_Chair3","room":"7","accessibleTo":["Thing_Human1"],"users":[]}],"recreation":[{"id":"Thing_Horseshoes4","kind":"Gaming_Dexterity","host":{"type":"outdoors"},"accessibleTo":["Thing_Human1"],"users":["Thing_Human1"]},{"id":"Thing_Chess5","kind":"Gaming_Cerebral","host":{"type":"indoors","room":"7"},"accessibleTo":[],"users":[]}],"joy":{"kinds":["Gaming_Cerebral","Gaming_Dexterity"],"pawns":[{"pawn":"Thing_Human1","bored":[false,true]}]}}""",
            json);
        Assert.Equal(census, WireJson.Deserialize<ComfortFacts>(json));
    }
}
