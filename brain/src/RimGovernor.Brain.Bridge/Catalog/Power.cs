using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_power.go: the power views. A power comp is CompProperties_Power or a subclass; a solar or wind plant is
// told by its compClass (CompPowerPlantSolar, CompPowerPlantWind or a subclass).

public sealed partial class DefinitionCatalog
{
    // The weather the wind profile is priced in: the baseline that storms and snow only improve on.
    private const string ClearWeather = "Clear";

    /// <summary>
    /// The delivery profile of every def whose power comp generates (a negative draw): a solar plant by its comp class
    /// delivers nothing at night, a wind plant the weather average, every other plant its nominal watts around the clock.
    /// </summary>
    public IReadOnlyDictionary<string, P.PowerSourceProfile> PowerSources() => _powerSources.Value;

    private Dictionary<string, P.PowerSourceProfile> BuildPowerSources()
    {
        var sources = new Dictionary<string, P.PowerSourceProfile>(StringComparer.Ordinal);
        foreach (var (name, row) in _thingDefs)
        {
            if (row.GetCompProperties<CompProperties_Power>() is not { } comp || GameMembers.BasePowerConsumption(comp) >= 0)
            {
                continue;
            }

            var profile = ConstantPowerProfile;
            if (comp.compClass is { } kind)
            {
                if (typeof(CompPowerPlantSolar).IsAssignableFrom(kind))
                {
                    profile = SolarPowerProfile;
                }
                else if (typeof(CompPowerPlantWind).IsAssignableFrom(kind))
                {
                    profile = WindProfile();
                }
            }

            sources[name] = profile;
        }

        return sources;
    }

    // The wind turbine profile: the WindManager speed range and the clear weather's wind factor.
    private P.PowerSourceProfile WindProfile()
    {
        var speeds = GameMembers.WindSpeedRange(null);
        var weather = DefRow<WeatherDef>(ClearWeather) ?? throw new CatalogException($"catalog has no {ClearWeather} weather row");
        var profile = P.PolicyFunctions.WindProfile(speeds.min, speeds.max, weather.windSpeedFactor);
        return profile == default
            ? throw new CatalogException($"wind speed range {speeds.min}-{speeds.max} and {ClearWeather} factor {weather.windSpeedFactor} give no wind profile")
            : profile;
    }

    /// <summary>
    /// The named generator def's nominal watts (its power comp's negative draw), build cost and, for a refuelable one,
    /// the single fuel its filter accepts and the tank a full load takes.
    /// </summary>
    public P.GeneratorRow GeneratorRow(string name)
    {
        var row = ThingRow(name);
        if (PowerDraw(row) is not { } watts || watts >= 0)
        {
            throw new CatalogException($"def {name} is not a generator");
        }

        var generator = new P.GeneratorRow
        {
            NominalW = -watts,
            Costs = [.. AdjustedCosts(name, string.Empty).Select(q => new P.Amount(new P.Resource(q.Def), q.Units))],
        };
        var fuels = RefuelFuels(name);
        switch (fuels.Count)
        {
            case 0:
                return generator;
            case 1:
                return generator with { Fuel = new P.Resource(fuels[0]), FuelCapacity = (long)row.GetCompProperties<CompProperties_Refuelable>()!.fuelCapacity };
            default:
                throw new CatalogException($"generator {name} accepts {fuels.Count} fuels");
        }
    }

    /// <summary>
    /// The fuel the named def's CompProperties_Refuelable accepts (fuelFilter's allowed defs, less the disallowed ones),
    /// sorted; none for a def without the comp. A filter that lists categories is an error.
    /// </summary>
    public IReadOnlyList<string> RefuelFuels(string name)
    {
        if (ThingRow(name).GetCompProperties<CompProperties_Refuelable>() is not { } fuel)
        {
            return [];
        }

        // The game's resolved filter has expanded its categories into defs, so the Go refusal of a category list has no counterpart.
        return [.. fuel.fuelFilter.AllowedThingDefs.Select(d => d.defName).Order(StringComparer.Ordinal)];
    }

    // What the power site count is derived and priced from: the turbine and the solar generator (nominal watts, cheapest
    // build value, delivery profile), the battery, and the value of a conduit and of a ring wall cell. A catalog without
    // one of the defs (a hand-built one) has an unknown model, not an error: the count then refuses with an unknown
    // power supply.
    internal P.PowerModel PowerModel()
    {
        string[] needed = [WindTurbineDefinition, SolarDefinition, BatteryDefinition, PowerConduitDefinition, ShellWallDefinition];
        if (needed.Any(name => !_thingDefs.ContainsKey(name)))
        {
            return new P.PowerModel();
        }

        var profiles = PowerSources();
        P.SupplyRow Supply(string name) => new()
        {
            NominalW = GeneratorRow(name).NominalW,
            Build = CheapestCostValue(name),
            Profile = profiles.GetValueOrDefault(name),
        };

        return new P.PowerModel
        {
            Wind = Supply(WindTurbineDefinition),
            Solar = Supply(SolarDefinition),
            Battery = PowerBattery(BatteryDefinition),
            Conduit = CheapestCostValue(PowerConduitDefinition),
            Wall = CheapestCostValue(ShellWallDefinition),
        };
    }

    /// <summary>
    /// The units of input the cheapest recipe making product takes per unit made, when that recipe's one ingredient slot
    /// accepts only input (the game's own steel-to-components rate); unknown when no recipe is so shaped.
    /// </summary>
    public Fact<double> ProductInputCost(string product, string input)
    {
        double? best = null;
        foreach (var host in RecipeHosts(product, null))
        {
            if (!host.Ingredients.TryGetValue(out var slots) || slots is not [[var only]] || only.Resource.Value != input)
            {
                continue;
            }

            var made = Recipe(host.Definition).products.Seq().Where(p => p.thingDef.defName == product).Sum(p => (long)p.count);
            if (made <= 0)
            {
                continue;
            }

            var cost = (double)only.Count / made;
            best = best is null || cost < best ? cost : best;
        }

        return best is { } value ? Known(value) : Unknown<double>();
    }

    /// <summary>The storage of the named battery def: the capacity and charge efficiency of its CompProperties_Battery. A def without the comp is an error.</summary>
    public P.PowerBattery PowerBattery(string name)
    {
        var battery = ThingRow(name).GetCompProperties<CompProperties_Battery>() ?? throw new CatalogException($"def {name} has no battery comp");
        var result = new P.PowerBattery(battery.storedEnergyMax, battery.efficiency);
        return result.CapacityWD > 0 && result.Efficiency is > 0 and <= 1
            ? result
            : throw new CatalogException($"battery {name} has capacity {result.CapacityWD} and efficiency {result.Efficiency}");
    }

    /// <summary>The stored-energy capacity of the named def's CompProperties_Battery; a def without the comp is unknown, not an error.</summary>
    public Fact<double> BatteryCapacityWd(string name) =>
        ThingRow(name).GetCompProperties<CompProperties_Battery>() is { } battery ? Known((double)battery.storedEnergyMax) : Unknown<double>();

    /// <summary>Whether the named def is a power conduit: a plain Building whose power comp is a bare CompPowerTransmitter (no generator, consumer, battery or switch class).</summary>
    public bool PlainConduit(string name)
    {
        var row = ThingRow(name);
        return row.thingClass == typeof(Verse.Building) && row.GetCompProperties<CompProperties_Power>()?.compClass == typeof(CompPowerTransmitter);
    }

    /// <summary>
    /// The base wattage a power row states for the named def, in the sign the power policy reads: negative for a consumer,
    /// positive for a producer, zero for a battery. It is the def's basePowerConsumption with the factor of every power
    /// upgrade whose research is finished applied in order, as the game's <c>CompProperties_Power.PowerConsumption</c>
    /// does. A def with upgrades and no finished-research census yields an unknown fact; a def with no power comp is an error.
    /// </summary>
    public Fact<double> PowerBaseW(string name, Fact<IReadOnlyList<string>> finished)
    {
        var comp = ThingRow(name).GetCompProperties<CompProperties_Power>() ?? throw new CatalogException($"def {name} has no power comp");
        var draw = GameMembers.BasePowerConsumption(comp);
        if (comp.powerUpgrades.Seq().Any() is false)
        {
            return Known(0 - (double)draw);
        }

        if (!finished.TryGetValue(out var done))
        {
            return Unknown<double>();
        }

        var num = draw;
        foreach (var upgrade in comp.powerUpgrades)
        {
            if (upgrade is null)
            {
                throw new CatalogException($"def {name} has an empty power upgrade");
            }

            if (upgrade.researchProject is { } project && done.Contains(project.defName))
            {
                num *= upgrade.factor;
            }
        }

        return Known(0 - (double)num);
    }
}
