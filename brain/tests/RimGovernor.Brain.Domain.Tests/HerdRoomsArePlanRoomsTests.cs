using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HerdRoomsArePlanRoomsTests
{
    private static LayoutPlan Core(params PlannedRoom[] rooms) => new()
    {
        Zones = [new LayoutZone { Kind = LayoutZoningRules.ZoneCore, Runs = [.. Enumerable.Range(0, 80).Select(z => new RowRun(z, 0, 80))] }],
        Rooms = rooms,
        NextRoomId = rooms.Length + 1,
    };

    private static PlannedRoom Home(int id, int x, int z) =>
        new() { Id = id, Role = LayoutSurveyRules.PlannedBedroom, Region = RegionRules.RectRegion(new Rectangle { X = x, Z = z, Width = 5, Height = 5 }), Door = new Cell(x + 2, z - 1), DoorSide = new DoorSide.Doored(Rotation.South) };

    private static string Json<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Codecs.Default.Write(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void APenIsANodeOfTheGraphPerHerdShortOfCells()
    {
        var plan = Core(Home(1, 38, 38));
        var nodes = LayoutPlan.HerdRoomNodes(plan, [new PenWant("Muffalo", 30), new PenWant("", 12)]);
        Assert.Equal(["", "Muffalo"], nodes.Select(n => n.Herd));
        Assert.All(nodes, n =>
        {
            Assert.Equal(PlannedRole.Pen, n.Role);
            Assert.True(n.Area >= 12);
        });
        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());
        // A standing pen is a node of its own floor and keeps its herd's key; a herd it holds asks for no more.
        var pen = new PlannedRoom { Id = 2, Role = PlannedRole.Pen, Herd = "Muffalo", Outdoor = true, Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 6, Height = 5 }), Door = new Cell(12, 9), DoorSide = new DoorSide.Doored(Rotation.South) };
        var held = LayoutPlan.HerdRoomNodes(plan with { Rooms = [.. plan.Rooms, pen] }, [new PenWant("Muffalo", 30)]);
        Assert.Equal([("Muffalo", 30)], held.Select(n => (n.Herd, n.Area)));
    }

    [Fact]
    public void APlanSavedWithReservationsLoadsThePenAsARoomAndDropsTheBarn()
    {
        var plan = Core(Home(1, 38, 38));
        var area = Json(RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 8, Height = 8 }));
        var pen = Json(RegionRules.RectRegion(new Rectangle { X = 17, Z = 10, Width = 6, Height = 8 }));
        var json = Json(plan).Replace("\"Reservations\":[]", $"\"Reservations\":[{{\"Kind\":\"barn\",\"Area\":{area},\"Herd\":\"Muffalo\"}},{{\"Kind\":\"pen\",\"Area\":{pen},\"Herd\":\"Muffalo\"}}]", StringComparison.Ordinal);
        Assert.Contains("\"barn\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var back = Codecs.Default.Read<LayoutPlan>(doc.RootElement);
        Assert.Empty(back.Reservations);
        Assert.Equal([PlannedRole.Pen], back.Rooms.Skip(1).Select(r => r.Role));
        var room = Assert.Single(back.Rooms.Skip(1));
        Assert.Equal("Muffalo", room.Herd);
        Assert.True(room.Id > 1);
    }

    [Fact]
    public void APlanSavedWithABarnRoomLoadsWithoutIt()
    {
        var plan = Core(Home(1, 38, 38), new PlannedRoom { Id = 2, Role = PlannedRole.Pen, Herd = "Muffalo", Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 6, Height = 5 }), Door = new Cell(12, 9), DoorSide = new DoorSide.Doored(Rotation.South) });
        var json = Json(plan with { Links = [new RoomLink { A = 1, B = 2, Cell = new Cell(12, 9) }] }).Replace("\"Role\":\"pen\"", "\"Role\":\"barn\"", StringComparison.Ordinal);
        Assert.Contains("\"barn\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var back = Codecs.Default.Read<LayoutPlan>(doc.RootElement);
        Assert.Equal([LayoutSurveyRules.PlannedBedroom], back.Rooms.Select(r => r.Role));
        Assert.Empty(back.Links);
    }
}
