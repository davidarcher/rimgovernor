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
        var nodes = LayoutPlan.HerdRoomNodes(plan, [new PenWant("Muffalo", 30), new PenWant("", 12)], []);
        Assert.Equal(["", "Muffalo"], nodes.Select(n => n.Herd));
        Assert.All(nodes, n =>
        {
            Assert.Equal(PlannedRole.Pen, n.Role);
            Assert.True(n.Area >= 12);
        });
        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());
        // A standing pen is a node of its own floor and keeps its herd's key; a herd it holds asks for no more.
        var pen = new PlannedRoom { Id = 2, Role = PlannedRole.Pen, Herd = "Muffalo", Outdoor = true, Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 6, Height = 5 }), Door = new Cell(12, 9), DoorSide = new DoorSide.Doored(Rotation.South) };
        var held = LayoutPlan.HerdRoomNodes(plan with { Rooms = [.. plan.Rooms, pen] }, [new PenWant("Muffalo", 30)], []);
        Assert.Equal([("Muffalo", 30)], held.Select(n => (n.Herd, n.Area)));
        // A standing barn of the herd is a node of its own floor beside the herd's pen, whether or not a barn is still wanted.
        var barn = new PlannedRoom { Id = 3, Role = PlannedRole.Barn, Herd = "Muffalo", Region = RegionRules.RectRegion(new Rectangle { X = 17, Z = 10, Width = 2, Height = 2 }), Door = new Cell(17, 12), DoorSide = new DoorSide.Doored(Rotation.North) };
        var sheltered = LayoutPlan.HerdRoomNodes(plan with { Rooms = [.. plan.Rooms, pen, barn] }, [new PenWant("Muffalo", 30)], []);
        Assert.Equal([(PlannedRole.Pen, "", 30), (PlannedRole.Barn, sheltered[0].Id, 4)], sheltered.Select(n => (n.Role, n.Beside, n.Area)));
    }

    // Task 8.4: the legacy-site read is deleted, not shimmed. A plan saved with its pens and barns as reservations no longer
    // decodes (no reservation kind names them), so a save restores it as no plan and the next survey derives a fresh one.
    [Fact]
    public void APlanSavedWithPenReservationsNoLongerDecodes()
    {
        var plan = Core(Home(1, 38, 38));
        var area = Json(RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 8, Height = 8 }));
        var json = Json(plan).Replace("\"Reservations\":[]", $"\"Reservations\":[{{\"Kind\":\"pen\",\"Area\":{area},\"Herd\":\"Muffalo\"}}]", StringComparison.Ordinal);
        Assert.Contains("\"pen\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Assert.ThrowsAny<Exception>(() => Codecs.Default.Read<LayoutPlan>(doc.RootElement));
    }

    [Fact]
    public void APlanSavedWithABarnRoomLoadsItWithItsLinks()
    {
        var plan = Core(Home(1, 38, 38), new PlannedRoom { Id = 2, Role = PlannedRole.Pen, Herd = "Muffalo", Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 6, Height = 5 }), Door = new Cell(12, 9), DoorSide = new DoorSide.Doored(Rotation.South) });
        var json = Json(plan with { Links = [new RoomLink { A = 1, B = 2, Cell = new Cell(12, 9) }] }).Replace("\"Role\":\"pen\"", "\"Role\":\"barn\"", StringComparison.Ordinal);
        Assert.Contains("\"barn\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var back = Codecs.Default.Read<LayoutPlan>(doc.RootElement);
        Assert.Equal([LayoutSurveyRules.PlannedBedroom, PlannedRole.Barn], back.Rooms.Select(r => r.Role));
        Assert.Equal("Muffalo", back.Rooms[1].Herd);
        Assert.Equal([(1, 2)], back.Links.Select(l => (l.A, l.B)));
    }
}
