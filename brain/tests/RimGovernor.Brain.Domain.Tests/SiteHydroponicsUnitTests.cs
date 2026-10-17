using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

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
                cells.Add(new SiteCell
                {
                    Cell = new Cell(x, z),
                    Walkable = Known(true),
                    Zone = Known(false),
                    Roofed = Known(true),
                    Indoors = Known(true),
                    Room = Known(GridKey),
                });
            }
        }
        var crop = new CropChoice
        {
            Name = "Rice",
            Available = Known(true),
            Edible = Known(true),
            GrowDays = Known(6.0),
            FertilityMin = Known(0.5),
            FertilitySensitivity = Known(1.0),
            HarvestNutrition = Known(0.05),
            Demand = Known(1.6),
            SowTags = Known(EquatableArray.From(["Ground", "Hydroponic"])),
            MinGlow = Known(0.3),
            DietAllowed = Known(true),
        };
        var request = new SiteTypeRequest
        {
            Field = new FieldRequest
            {
                Choices = EquatableArray.From([crop]),
                Climate = new CropClimate { Sowing = Known(false), OutdoorsDark = Known(false) },
                Colonists = Known(3L),
                ReserveDays = 5,
                Coverage = Known(0.0),
                Site = new FarmSiteRequest { Cells = cells },
            },
            Environment = Known(new ControlledEnvironment
            {
                OutdoorTemperatureC = Known(20.0),
                // The native room id differs from the grid key the cells carry.
                Rooms = EquatableArray.From([new GrowRoom { Id = "native-9", GridRoom = Known(GridKey), TemperatureC = Known(-5.0) }]),
            }),
            Lamp = Known(new Infrastructure { Name = "SunLamp", Available = Known(true), PowerW = Known(2900.0) }),
            Basin = Known(new Infrastructure { Name = "HydroponicsBasin", Available = Known(true), PowerW = Known(70.0), Fertility = Known(2.8) }),
            LampGrowthRadius = 5,
        };

        var (plan, _) = SiteRules.PlanSiteType(request);

        var unit = plan.Candidates.Single(c => c.Kind == SiteKind.Hydroponics);
        Assert.Equal("room too cold and no heater available", unit.Reason);
    }
}
