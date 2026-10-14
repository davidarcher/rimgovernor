using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HerdRoomsArePlanRoomsTests
{
    private static LayoutPlan Core(params PlannedRoom[] rooms) => new()
    {
        Zones = [new LayoutZone { Kind = ZoneCore, Runs = [.. Enumerable.Range(0, 80).Select(z => new RowRun(z, 0, 80))] }],
        Rooms = rooms,
        NextRoomId = rooms.Length + 1,
    };

    private static PlannedRoom Shelter(int id, int x, int z) =>
        new() { Id = id, Role = PlannedShelter, Region = RectRegion(new Rectangle { X = x, Z = z, Width = 5, Height = 5 }), Door = new Cell(x + 2, z - 1), DoorRot = Rotation.South };

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
    public void HerdSitesAreRoomsOfThePlanWithIdsDoorsAndTheirHerd()
    {
        var plan = PlanHerdSites(Core(Shelter(1, 38, 38)), 12, new HerdCeiling("Muffalo", 6));
        var barns = plan.RoomsOf(PlannedRole.Barn);
        Assert.Equal(["", "Muffalo"], barns.Select(r => r.Herd));
        Assert.All(barns, r =>
        {
            Assert.True(r.Id >= 2);
            Assert.NotNull(r.DoorRot);
        });
        Assert.Equal(barns.Count, barns.Select(r => r.Id).Distinct().Count());
        Assert.True(plan.NextRoomId > barns.Max(r => r.Id));
        Assert.Empty(plan.Reservations);
        // A repeat top-up finds nothing owed.
        Assert.Equal(plan.Rooms.Count, PlanHerdSites(plan, 12, new HerdCeiling("Muffalo", 6)).Rooms.Count);
    }

    [Fact]
    public void APlanSavedWithReservationsLoadsTheBarnAndPenAsRooms()
    {
        var plan = Core(Shelter(1, 38, 38));
        var area = Json(RectRegion(new Rectangle { X = 10, Z = 10, Width = 8, Height = 8 }));
        var pen = Json(RectRegion(new Rectangle { X = 17, Z = 10, Width = 6, Height = 8 }));
        var json = Json(plan).Replace("\"Reservations\":[]", $"\"Reservations\":[{{\"Kind\":\"barn\",\"Area\":{area},\"Herd\":\"Muffalo\"}},{{\"Kind\":\"pen\",\"Area\":{pen},\"Herd\":\"Muffalo\"}}]", StringComparison.Ordinal);
        Assert.Contains("\"barn\"", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var back = Codecs.Default.Read<LayoutPlan>(doc.RootElement);
        Assert.Empty(back.Reservations);
        Assert.Equal([PlannedRole.Barn, PlannedRole.Pen], back.Rooms.Skip(1).Select(r => r.Role));
        Assert.Equal([2, 3], back.Rooms.Skip(1).Select(r => r.Id));
        Assert.All(back.Rooms.Skip(1), r => Assert.Equal("Muffalo", r.Herd));
        Assert.True(back.NextRoomId > 3);
        // The pen is joined to its barn by the wall they share.
        Assert.Single(back.FlapCells(back.Rooms[1]));
    }
}
