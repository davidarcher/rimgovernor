using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3391: the soil-free hydroponics unit reads a room's temperature by the cell grid's room key (SiteCell.Room),
/// not the native room id, so a cold room raises the heater requirement even when outdoors is warm.
/// </summary>
public sealed class SiteHydroponicsUnitTests
{
    private const int GridKey = 4;

    [Fact]
    public void ColdRoomKeyedByTheGridNeedsAHeaterEvenWhenOutdoorsIsWarm()
    {
        var cells = new List<SiteCell>();
        for (var x = 0; x < 6; x++)
        {
            for (var z = 0; z < 6; z++)
            {
                cells.Add(SiteCellFixtures.Ground with
                {
                    Cell = new Cell(x, z),
                    Walkable = true,
                    Zone = false,
                    Roofed = true,
                    Indoors = true,
                    Room = new CellRoom.InRoom(GridKey),
                    // A floored room: no soil a crop grows in, so the soil-free unit is the kind.
                    Fertility = 0,
                });
            }
        }
        var crop = new CropChoice
        {
            Name = "Rice",
            Available = true,
            Edible = true,
            GrowDays = 6.0,
            FertilityMin = 0.5,
            FertilitySensitivity = 1.0,
            HarvestNutrition = 0.05,
            Demand = 1.6,
            SowTags = EquatableArray.From(["Ground", "Hydroponic"]),
            MinGlow = 0.3,
            DietAllowed = true,
        };
        var request = new SiteTypeRequest
        {
            Field = new FieldRequest
            {
                Choices = EquatableArray.From([crop]),
                Climate = new CropClimate { Sowing = false, OutdoorsDark = false},
                Colonists = 3L,
                ReserveDays = 5,
                Coverage = 0.0,
                Site = new FarmSiteRequest { Cells = cells },
            },
            Environment = ControlledEnvironmentTests.NoEnvironment with
            {
                OutdoorTemperatureC = 20.0,
                // The native room id differs from the grid key the cells carry.
                Rooms = EquatableArray.From([new GrowRoom { Id = "native-9", GridRoom = GridKey, TemperatureC = -5.0 }]),
            },
            Lamp = new Infrastructure { Name = "SunLamp", Available = true, PowerW = 2900.0},
            Basin = new Infrastructure { Name = "HydroponicsBasin", Available = true, PowerW = 70.0, Grower = new InfrastructureGrower(2.8)},
            LampGrowthRadius = 5,
        };

        var (plan, _) = SiteRules.PlanSiteType(request);

        var unit = plan.Candidates.Single(c => c.Kind == SiteKind.Hydroponics);
        Assert.Equal("heater not offered", unit.Reason);
    }
}
