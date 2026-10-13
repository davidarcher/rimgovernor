namespace RimGovernor.Brain.Policy;

// The shape of a day the energy budget reasons from (rimworldwiki.com Power, Solar generator, Wind turbine).
// Nominal wattage per producer comes from the native census and a battery's storage from its comp row; only
// the shape of a day is assumed here.
public static partial class PolicyConstants
{
    /// <summary>The share of a day a solar generator makes nothing (about eight hours near the equator).</summary>
    public const double SolarNightFraction = 8.0 / 24.0;

    /// <summary>
    /// A solar generator's average output over a whole day relative to its nominal wattage: ~9 h at full
    /// output and ~7 h at partial output, none at night.
    /// </summary>
    public const double SolarDailyFraction = (9.0 + (7.0 * 0.5)) / 24.0;

    /// <summary>
    /// The wind speed above which a turbine makes no more (CompPowerPlantWind.MaxUsableWindIntensity, a
    /// private game constant the catalog does not carry).
    /// </summary>
    public const double WindUsableMax = 1.5;

    /// <summary>The storage definition the power family compiles.</summary>
    public const string BatteryDefinition = "Battery";

    // The profiles a producer's comp class selects (DefinitionCatalog.PowerSources): fuel-burning,
    // geothermal and watermill generators hold nominal output around the clock while served.
    public static readonly PowerSourceProfile SolarPowerProfile = new(SolarDailyFraction, 0, true);

    public static readonly PowerSourceProfile ConstantPowerProfile = new(1, 1, false);
}

/// <summary>
/// One battery's usable storage and the stored share of a charging surplus (CompProperties_Battery
/// storedEnergyMax and efficiency): one watt of surplus for a day stores Efficiency watt-days.
/// </summary>
public readonly record struct PowerBattery(double CapacityWD, double Efficiency);

/// <summary>
/// How a producer definition delivers its nominal wattage over a day. Daily is the average share of nominal
/// across 24 h; Night is the share delivered while the sun is down. Solar marks a source that stops in an
/// eclipse. The default value is no profile.
/// </summary>
public readonly record struct PowerSourceProfile(double Daily, double Night, bool Solar);

/// <summary>One producer's nominal wattage and delivery profile as the budget sees it.</summary>
public sealed record PowerProducer
{
    public string Definition { get; init; } = "";

    public double NominalW { get; init; }

    public PowerSourceProfile Profile { get; init; }
}

/// <summary>One network's demand, producers and installed storage over the coming day. Eclipse zeroes every solar producer for the day.</summary>
public sealed record PowerBudgetInput
{
    public double DemandW { get; init; }

    public IReadOnlyList<PowerProducer> Producers { get; init; } = [];

    public double CapacityWD { get; init; }

    /// <summary>The battery's PowerBattery.Efficiency.</summary>
    public double ChargeEfficiency { get; init; }

    public bool Eclipse { get; init; }

    /// <summary>The share of the night deficit added to the storage target so the bank does not run flat at dawn.</summary>
    public double StorageMargin { get; init; }
}

/// <summary>
/// A network's 24 h energy balance. Generation and storage shortfalls are independent: a solar colony draining
/// at night with spare daytime surplus is short of storage only; a colony whose producers cannot cover its
/// consumers over a day is short of generation, and no bank fixes that.
/// </summary>
public sealed record PowerBudget
{
    public double DemandWD { get; init; }

    public double SupplyWD { get; init; }

    /// <summary>The energy consumers draw during the solar night beyond what still generates then.</summary>
    public double NightDeficitWD { get; init; }

    /// <summary>The spare daytime generation available to charge a bank.</summary>
    public double DaySurplusWD { get; init; }

    /// <summary>The bank the budget wants: the night deficit the day surplus can refill, plus margin.</summary>
    public double StorageNeededWD { get; init; }

    /// <summary>The constant generation to add so the bank the surplus can fill covers the night.</summary>
    public double GenerationShortfallW { get; init; }

    /// <summary>The storage to add beyond the installed capacity.</summary>
    public double StorageShortfallWD { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// A wind turbine's delivery profile in a weather whose wind factor is factor, from the catalog's
    /// WindManager speed range [speedMin, speedMax]: the turbine makes nominal times the wind speed up to
    /// WindUsableMax (CompPowerPlantWind), and the speed is taken as uniform over the range scaled by the
    /// weather factor (the noise distribution is game code, not catalog). For Core's clear weather (range
    /// 0.04-2, factor 1) the average is 0.956 of nominal, 2199 W of the turbine's 2300 W, which matches the
    /// wiki's measured clear-weather figure; snow and storms deliver more, so the clear figure is the
    /// conservative planning value. A degenerate range or factor is the zero profile.
    /// </summary>
    public static PowerSourceProfile WindProfile(double speedMin, double speedMax, double factor)
    {
        double lo = speedMin * factor, hi = speedMax * factor;
        if (!(speedMin >= 0 && speedMax > speedMin && factor > 0) || double.IsInfinity(hi))
        {
            return default;
        }
        var max = PolicyConstants.WindUsableMax;
        var mean = (lo + hi) / 2;
        if (hi > max)
        {
            mean = (((max * max) / 2) - ((lo * lo) / 2) + (max * (hi - max))) / (hi - lo);
            if (lo >= max)
            {
                mean = max;
            }
        }
        return new PowerSourceProfile(mean, mean, false);
    }

    /// <summary>
    /// Sizes a network against a 24 h day. A constant source added at g watts cuts the night deficit by g x
    /// night share and raises the day surplus by g x day share; the generation shortfall is the smallest g for
    /// which the surplus, at battery charge efficiency, refills the night. Inputs must be finite and
    /// non-negative.
    /// </summary>
    public static PowerBudget ComputePowerBudget(PowerBudgetInput input)
    {
        const double night = PolicyConstants.SolarNightFraction;
        const double day = 1 - PolicyConstants.SolarNightFraction;
        double supplyWD = 0, nightW = 0, dayW = 0;
        foreach (var p in input.Producers)
        {
            var profile = p.Profile;
            if (profile.Solar && input.Eclipse)
            {
                continue;
            }
            supplyWD += p.NominalW * profile.Daily;
            nightW += p.NominalW * profile.Night;
            // Daytime output is whatever the daily average leaves after the night.
            dayW += p.NominalW * (profile.Daily - (profile.Night * night)) / day;
        }
        var nightDeficit = Math.Max(0, (input.DemandW - nightW) * night);
        var daySurplus = Math.Max(0, (dayW - input.DemandW) * day);
        double generationShortfall = 0;
        // A daytime deficit needs constant generation outright; only then can the remaining night deficit be
        // traded against surplus and storage.
        if (dayW < input.DemandW)
        {
            generationShortfall = input.DemandW - dayW;
            nightW += generationShortfall;
        }
        var fillable = daySurplus * input.ChargeEfficiency;
        var remaining = Math.Max(0, (input.DemandW - nightW) * night);
        if (remaining > fillable)
        {
            generationShortfall += (remaining - fillable) / (night + (input.ChargeEfficiency * day));
        }
        var covered = Math.Min(nightDeficit, fillable);
        double storageNeeded = 0;
        if (covered > 0)
        {
            storageNeeded = covered * (1 + Math.Max(0, input.StorageMargin));
        }
        return new PowerBudget
        {
            DemandWD = input.DemandW,
            SupplyWD = supplyWD,
            NightDeficitWD = nightDeficit,
            DaySurplusWD = daySurplus,
            StorageNeededWD = storageNeeded,
            GenerationShortfallW = generationShortfall,
            StorageShortfallWD = Math.Max(0, storageNeeded - input.CapacityWD),
        };
    }
}
