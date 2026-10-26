using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CodecIdentitySchemaTests
{
    [Fact]
    public void MemberNamesKeepThePersistedSchemaAndRoundTripValues()
    {
        Field(Capture.NewArrest(new("capturer"), new("patient"), "bed"), "Bed", "\"bed\"");
        Field(new CombatCommand { Door = "door" }, "Door", "\"door\"");
        Field(new GenerationSnapshot(new("colony"), new(7), new("load"), new("plan"), new(2), new(3)), "Map", "7");
        Field(new CombatOrder { Door = DoorMode.Forbid }, "Door", "\"forbid\"");
        Field(new PlannedRoom { Door = new(2, 3) }, "Door", "{\"X\":2,\"Z\":3}");
        Field(new ReadyWorkReport { Map = new(7) }, "Map", "7");
        Field(new SleepingUse(new("pawn"), "bed", new(9)), "Bed", "\"bed\"");
        Field(new WaitDoor(new(2, 3), new(2, 4)), "Door", "{\"X\":2,\"Z\":3}");
        Field(new RimGovernor.Brain.Store.World(new("colony"), new("load"), new(7)), "Map", "7");
    }

    private static void Field<T>(T value, string member, string expected)
    {
        var json = StoreJson.Serialize(value);
        using var before = JsonDocument.Parse(json);
        Assert.Equal(expected, before.RootElement.GetProperty(member).GetRawText());
        var read = StoreJson.Deserialize<T>(json);
        using var after = JsonDocument.Parse(StoreJson.Serialize(read));
        Assert.Equal(expected, after.RootElement.GetProperty(member).GetRawText());
    }
}
