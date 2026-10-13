using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_climate.go: the heat the climate options move (P.ClimateModel), read from the defs and the game's constants:
// the vent's equalize interval (GenTicks.TickRareInterval, Building_Vent.TickRare) and rate
// (TemperatureTuning.Vent_TempEqualizeRate), the door's intervals and rate from its BuildingProperties, the cheapest
// heater's and the most efficient cooler's heat per second and power draw from their temperature control and power comps,
// and the cheapest cost list of the vent, door and wall.

public sealed partial class DefinitionCatalog
{
    // The comfortable temperature range of the colonists' race, its ComfyTemperatureMin and ComfyTemperatureMax stat bases;
    // the zero band when the race states neither.
    private P.Band LivingBand(P.ClimateTable table)
    {
        if (FindThing(table.RaceDef) is not { } race)
        {
            return new P.Band();
        }

        double? min = null, max = null;
        foreach (var modifier in race.statBases.Seq())
        {
            if (modifier.stat?.defName == table.ComfyMinStat)
            {
                min = Decimal32(modifier.value);
            }
            else if (modifier.stat?.defName == table.ComfyMaxStat)
            {
                max = Decimal32(modifier.value);
            }
        }

        return min is { } low && max is { } high ? new P.Band { MinC = low, MaxC = high } : new P.Band();
    }

    // A catalog without the defs or constants (a hand-built one) has an unknown model, not an error: the advice that reads
    // it is left off. stations is the catalog being built, whose coolers it reads.
    private P.ClimateModel ClimateModel(P.StationCatalog stations)
    {
        var table = P.PolicyFunctions.ClimateDefs();
        if (new[] { table.VentDef, table.DoorDef, table.WallDef }.Any(def => FindThing(def) is null))
        {
            return new P.ClimateModel();
        }

        var doorDef = ThingRow(table.DoorDef);
        var building = doorDef.building;
        double speed;
        try
        {
            speed = StatBase(doorDef, PlanningStatNames.DoorOpenSpeed);
        }
        catch (Exception e) when (e is CatalogException or StatException)
        {
            return new P.ClimateModel();
        }

        if (speed <= 0)
        {
            return new P.ClimateModel();
        }

        var rare = GenTicks.TickRareInterval;
        var model = new P.ClimateModel
        {
            // Building_Door.TicksToOpenNow (a manual door: the unpowered factor) and CloseDelayAdjusted, on the def's
            // stuff-free DoorOpenSpeed.
            Door = new P.DoorTiming
            {
                OpenTicks = Math.Round(GameConstants.DoorOpenTicks / speed * building.unpoweredDoorOpenSpeedFactor, MidpointRounding.AwayFromZero),
                CloseDelayTicks = Math.Floor(GameConstants.DoorCloseDelayTicks * (double)building.unpoweredDoorCloseSpeedFactor),
                MoveTicks = 2 * table.CellMoveTicks,
            },
            Vent = new P.Exchange { IntervalTicks = rare, Rate = TemperatureTuning.Vent_TempEqualizeRate },
            DoorOpen = new P.Exchange { IntervalTicks = building.doorTempEqualizeIntervalOpen, Rate = building.doorTempEqualizeRate },
            DoorClosed = new P.Exchange { IntervalTicks = building.doorTempEqualizeIntervalClosed, Rate = building.doorTempEqualizeRate },
            StepTicks = rare,
            TicksPerSecond = GenTicks.TicksPerRealSecond,
            SwingC = TemperatureTuning.DailyTempVariationAmplitude,
        };

        string heater;
        try
        {
            heater = CheapestHeater();
        }
        catch (CatalogException)
        {
            return new P.ClimateModel();
        }

        var heaterRow = ThingRow(heater);
        var control = heaterRow.GetCompProperties<CompProperties_TempControl>();
        var power = heaterRow.GetCompProperties<CompProperties_Power>();
        model = model with
        {
            Heater = new P.ClimatePlant { HeatPerSecond = control?.energyPerSecond ?? 0, PowerW = power is null ? 0 : GameMembers.BasePowerConsumption(power) },
            HeldC = control?.defaultTargetTemperature ?? 0,
            Living = LivingBand(table),
        };

        var cooler = stations.BestCoolerByPower();
        if (cooler is not null)
        {
            var cooling = stations.Stations[cooler].Cooling;
            model = model with { Cooler = new P.ClimatePlant { HeatPerSecond = cooling.HeatPerSecond, PowerW = cooling.PowerDraw } };
        }

        // A plant's cost that cannot be read is unpriced (zero), not an error: the plants then add no build cost and veto
        // nothing.
        return model with
        {
            Build = new P.ClimateBuildValue
            {
                Vent = CheapestCostValue(table.VentDef),
                Door = CheapestCostValue(table.DoorDef),
                Wall = CheapestCostValue(table.WallDef),
                Heater = TryCheapestCostValue(heater),
                Cooler = cooler is null ? 0 : TryCheapestCostValue(cooler),
            },
        };
    }

    private double TryCheapestCostValue(string def)
    {
        try
        {
            return CheapestCostValue(def);
        }
        catch (Exception e) when (e is CatalogException or StatException)
        {
            return 0;
        }
    }

    // The lamps the plan places (P.PolicyFunctions.LightDefs): for each the power it draws, the market value of its
    // cheapest cost list and its glow radius. A catalog without a def (a hand-built one) has an unknown model for it, not
    // an error: the standing lamp unknown leaves the lamps unpriced, the wall lamp unknown leaves it out of the options.
    private P.LightModel LightModel()
    {
        var defs = P.PolicyFunctions.LightDefs();
        return new P.LightModel { Standing = LampModel(defs.LampDef), Wall = LampModel(defs.WallLampDef) };
    }

    private P.LampModel LampModel(string def)
    {
        if (FindThing(def) is not { } row || PowerDraw(row) is not { } watts)
        {
            return default;
        }

        var radius = GlowRadius(def) ?? 0;

        // The glow colour's brightest channel sets how far the glow stays lit.
        var brightness = row.GetCompProperties<CompProperties_Glower>() is { } glower ? Math.Max(glower.glowColor.r, Math.Max(glower.glowColor.g, glower.glowColor.b)) : 0;
        return new P.LampModel(watts, CheapestCostValue(def), radius, brightness);
    }
}
