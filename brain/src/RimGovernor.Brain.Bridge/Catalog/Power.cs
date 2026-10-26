using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The power views. A power comp is CompProperties_Power or a subclass; a solar or wind plant is
// told by its compClass (CompPowerPlantSolar, CompPowerPlantWind or a subclass).

public sealed partial class DefinitionCatalog
{
    // The weather the wind profile is priced in: the baseline that storms and snow only improve on.
    private const string ClearWeather = "Clear";

    /// <summary>
    /// The delivery profile of every producer, including an explicit reason for unsupported mechanics.
    /// Only exact native classes with a modeled output function receive a priced profile.
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

            var kind = comp.compClass;
            if (row.comps.Any(comp => comp.compClass == typeof(CompAutoPowered)) || HasComp<CompProperties_Toxifier>(row))
            {
                sources[name] = new(0, 0, false) { UnsupportedReason = "conditional automatic power or pollution capacity" };
                continue;
            }
            sources[name] = kind == typeof(CompPowerPlantSolar) ? PowerRules.SolarPowerProfile
                : kind == typeof(CompPowerPlantWind) ? WindProfile()
                : kind == typeof(CompPowerPlant) || kind == typeof(CompPowerPlantSteam) ? PowerRules.ConstantPowerProfile
                : new(0, 0, false) { UnsupportedReason = "unmodeled generation comp " + (kind?.FullName ?? "absent") };
        }

        return sources;
    }

    /// <summary>Buildable producers whose output and ordinary placement the existing power model evaluates.</summary>
    public IReadOnlyList<string> GeneratorDefinitions() => [.. _thingDefs.Values
        .Where(row => Buildable(row) && PowerSources().TryGetValue(row.defName, out var profile)
            && profile.UnsupportedReason.Length == 0
            && row.GetCompProperties<CompProperties_Power>()!.compClass != typeof(CompPowerPlantSteam))
        .Select(row => row.defName).Order(StringComparer.Ordinal)];

    // The wind turbine profile: the WindManager speed range and the clear weather's wind factor.
    private P.PowerSourceProfile WindProfile()
    {
        var speeds = GameMembers.WindSpeedRange(null);
        var weather = DefRow<WeatherDef>(ClearWeather) ?? throw new CatalogException($"catalog has no {ClearWeather} weather row");
        var profile = P.PowerRules.WindProfile(speeds.min, speeds.max, weather.windSpeedFactor);
        return profile == default
            ? throw new CatalogException($"wind speed range {speeds.min}-{speeds.max} and {ClearWeather} factor {weather.windSpeedFactor} give no wind profile")
            : profile with { Wind = true };
    }

    /// <summary>
    /// The named generator def's nominal watts (its power comp's negative draw), build cost and, for a refuelable one,
    /// every fuel its filter accepts and the tank a full load takes.
    /// </summary>
    public P.GeneratorRow GeneratorRow(string name, double maintenanceCostFactor)
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
        if (row.GetCompProperties<CompProperties_Refuelable>() is not { } fuel)
            return generator;
        var fuels = RefuelFuels(name);
        if (fuels.Count == 0 || !float.IsFinite(fuel.fuelCapacity) || fuel.fuelCapacity <= 0)
            throw new CatalogException($"generator {name} has no accepted fuel or valid capacity");
        // FuelMultiplierCurrentDifficulty's exact law, with the same-frame observed difficulty rather than Find state.
        if (!double.IsFinite(maintenanceCostFactor) || maintenanceCostFactor <= 0)
            throw new CatalogException("unread maintenance cost factor");
        var multiplier = GameMembers.FuelMultiplier(fuel) / (fuel.factorByDifficulty ? maintenanceCostFactor : 1);
        if (!double.IsFinite(multiplier) || multiplier <= 0)
            throw new CatalogException($"generator {name} has no valid item-to-fuel multiplier");
        return generator with { Fuels = [.. fuels.Select(name => new P.Resource(name))], FuelCapacity = checked((long)Math.Ceiling(fuel.fuelCapacity / multiplier)) };
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

        // The game's resolved filter has expanded its categories into defs.
        return [.. fuel.fuelFilter.AllowedThingDefs.Select(d => d.defName).Order(StringComparer.Ordinal)];
    }

    // What the power site count is derived and priced from: the turbine and the solar generator (nominal watts, cheapest
    // build value, delivery profile), the battery, and the value of a conduit and of a ring wall cell. A catalog without
    // one of the defs (a hand-built one) has an unknown model, not an error: the count then refuses with an unknown
    // power supply.
    internal P.PowerModel PowerModel()
    {
        string[] needed = [PowerRules.WindTurbineDefinition, P.LayoutUtilityRules.SolarDefinition, PowerRules.BatteryDefinition, P.LayoutPowerDeriveRules.PowerConduitDefinition, ContainmentCellRules.ShellWallDefinition];
        if (needed.Any(name => !_thingDefs.ContainsKey(name)))
        {
            return new P.PowerModel();
        }

        var profiles = PowerSources();
        P.SupplyRow Supply(string name) => new()
        {
            NominalW = -PowerDraw(ThingRow(name))!.Value,
            Build = CheapestCostValue(name),
            Profile = profiles.GetValueOrDefault(name),
        };

        return new P.PowerModel
        {
            Wind = Supply(PowerRules.WindTurbineDefinition),
            Solar = Supply(P.LayoutUtilityRules.SolarDefinition),
            Battery = PowerBattery(PowerRules.BatteryDefinition),
            Conduit = CheapestCostValue(P.LayoutPowerDeriveRules.PowerConduitDefinition),
            Wall = CheapestCostValue(ContainmentCellRules.ShellWallDefinition),
        };
    }

    /// <summary>
    /// The units of input the cheapest recipe making product takes per unit made, when that recipe's one ingredient slot
    /// accepts only input (the game's own steel-to-components rate); <see cref="P.InputCost.NoRecipe"/> when no recipe is so shaped.
    /// </summary>
    public P.InputCost ProductInputCost(string product, string input)
    {
        double? best = null;
        foreach (var host in RecipeHosts(product, null))
        {
            if (host.Ingredients is not [[var only]] || only.Resource.Value != input)
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

        return best is { } value ? new P.InputCost.PerUnit(value) : new P.InputCost.NoRecipe();
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

}
