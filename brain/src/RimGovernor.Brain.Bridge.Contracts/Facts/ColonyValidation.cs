using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go colony.go: ValidateColonyFacts and the core, planning, growing-environment and threat checks it owns. The
// sections the census carries each have their own validator in a sibling file.
public static partial class BridgeFunctions
{
    /// <summary>A present finite non-negative number, or an absent one: Go's <c>combatNumber(p, true)</c>.</summary>
    private static bool NonNegativeNumber(bool present, double value) => CombatNumber(OptionalDouble(present, value), true);

    /// <summary>A present finite number, or an absent one: Go's <c>combatNumber(p, false)</c>.</summary>
    private static bool FiniteNumber(bool present, double value) => CombatNumber(OptionalDouble(present, value), false);

    /// <summary>Go's <c>colonyQuantities</c>: unique, identified, non-negative quantities.</summary>
    private static void ColonyQuantities(IEnumerable<Quantity> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || !IsValidId(row.DefName) || seen.Contains(row.DefName) || (row.HasUnits && row.Units < 0))
            {
                throw Contract("invalid colony quantity");
            }
            seen.Add(row.DefName);
        }
    }

    /// <summary>Go's <c>colonySize</c>: a map size with both sides stated, positive and at most 4096.</summary>
    private static bool ColonySize(MapSize? size) =>
        size is not null && size.HasWidth && size.HasHeight && size.Width > 0 && size.Height > 0 && size.Width <= 4096 && size.Height <= 4096;

    /// <summary>Go's <c>colonyCell</c>: a cell with both coordinates stated that lies on the map.</summary>
    private static bool ColonyCell(Cell? cell, MapSize? size) =>
        cell is not null && cell.HasX && cell.HasZ && cell.X >= 0 && cell.Z >= 0 && (uint)cell.X < (size?.Width ?? 0) && (uint)cell.Z < (size?.Height ?? 0);

    /// <summary>Go's <c>validIDs</c>: whether every id is valid; <paramref name="unique"/> also refuses a repeat.</summary>
    private static bool ValidIds(IEnumerable<string> ids, bool unique)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!IsValidId(id) || (unique && seen.Contains(id)))
            {
                return false;
            }
            seen.Add(id);
        }
        return true;
    }

    /// <summary>Go's <c>optionalID</c>: an absent id or a valid one.</summary>
    private static bool OptionalId(bool present, string value) => !present || IsValidId(value);

    private static bool ContextValid(ObservationContext? context)
    {
        try
        {
            ValidateContext(context);
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }

    /// <summary>
    /// Go's <c>ValidateColonyFacts</c>: validates the implemented core and planning projection of a colony census
    /// against the identity it was asked for. Unported independent sections must remain explicitly unavailable.
    /// </summary>
    /// <exception cref="BridgeContractException">The census is malformed.</exception>
    public static void ValidateColonyFacts(ColonyFactsSnapshot? v, Identity? identity)
    {
        if (v is null || !ContextValid(v.Context) || !SameIdentity(v.Context.Identity, identity) || !ColonySize(v.MapSize) || !ColonyCell(v.Center, v.MapSize))
        {
            throw Contract("invalid colony context/geometry");
        }
        BuildingUnknown(v);
        PawnsIssues(v.Issues, v);
        ColonyQuantities(v.Resources);
        foreach (var (present, value) in new[]
        {
            (v.HasColonistCount, v.ColonistCount),
            (v.HasWorkerCount, v.WorkerCount),
            (v.HasBedCapacity, v.BedCapacity),
            (v.HasIndoorSleepingCapacity, v.IndoorSleepingCapacity),
        })
        {
            if (present && value > 65536)
            {
                throw Contract("colony count exceeds bound");
            }
        }
        if ((v.HasWorkerCount && v.HasColonistCount && v.WorkerCount > v.ColonistCount)
            || (v.HasIndoorSleepingCapacity && v.HasBedCapacity && v.IndoorSleepingCapacity > v.BedCapacity))
        {
            throw Contract("inconsistent colony capacity");
        }
        foreach (var (present, value) in new[]
        {
            (v.HasFoodNutrition, v.FoodNutrition),
            (v.HasNutritionPerDay, v.NutritionPerDay),
            (v.HasFoodRunwayDays, v.FoodRunwayDays),
            (v.HasPendingFoodNutrition, v.PendingFoodNutrition),
            (v.HasPendingWoodUnits, v.PendingWoodUnits),
        })
        {
            if (!NonNegativeNumber(present, value))
            {
                throw Contract("invalid colony nutrition");
            }
        }
        foreach (var (present, value) in new[]
        {
            (v.HasSleepingTemperatureMinC, v.SleepingTemperatureMinC),
            (v.HasSleepingTemperatureMaxC, v.SleepingTemperatureMaxC),
            (v.HasOutdoorTemperatureC, v.OutdoorTemperatureC),
        })
        {
            if (!FiniteNumber(present, value))
            {
                throw Contract("invalid colony temperature");
            }
        }
        if (v.HasSleepingTemperatureMinC && v.HasSleepingTemperatureMaxC && v.SleepingTemperatureMinC > v.SleepingTemperatureMaxC)
        {
            throw Contract("inverted sleeping temperature range");
        }
        if (v.HasBiome && !IsValidId(v.Biome))
        {
            throw Contract("invalid biome");
        }
        if (!ValidIds(v.TileMutators, true))
        {
            throw Contract("invalid tile mutators");
        }
        if (v.HasPlayerTechLevel && !IsValidId(v.PlayerTechLevel))
        {
            throw Contract("invalid player tech level");
        }
        ValidateEventLoot(v);
        ValidateDeepResources(v);
        ValidatePolicies(v);
        ValidateRoyaltyColony(v);
        ValidateBiotechColony(v);
        ValidateAnomalyColony(v);
        ValidateOdysseyColony(v);
        ValidateFoodChannels(v);
        ValidateJoinerLetters(v.JoinerLetters, v.Context.Tick);
        ValidateChoiceDialog(v.Dialog);
        ValidateColonyWaste(v.Waste);
        if (v.FoodClimate is { } climate)
        {
            PawnsIssues(climate.Issues, climate);
            foreach (var (present, value) in new[]
            {
                (climate.HasGrowingDays, climate.GrowingDays),
                (climate.HasGrowingDaysRemaining, climate.GrowingDaysRemaining),
                (climate.HasGrowingDaysUntil, climate.GrowingDaysUntil),
                (climate.HasNonGrowingDays, climate.NonGrowingDays),
            })
            {
                if (!NonNegativeNumber(present, value) || (present && value > GameTime.DaysPerYear))
                {
                    throw Contract("invalid seasonal crop budget");
                }
            }
            if ((climate.HasDayOfYear && (climate.DayOfYear < 0 || climate.DayOfYear >= GameTime.DaysPerYear)) || (climate.HasSeason && !ValidSeason(climate.Season)))
            {
                throw Contract("invalid calendar");
            }
            if (v.Issues.Any(issue => issue.Field == "food_climate"))
            {
                throw Contract("unavailable climate contains observations");
            }
        }
        ValidateColonyAcquisition(v);
        ValidateColonyEnvironment(v);
        ValidateColonyRecovery(v);
        ValidateColonyProduction(v);
        if (v.FoodSupply?.Observed is { } food)
        {
            ValidateFoodSupply(food);
        }
        else
        {
            ValidateUnavailable(v.FoodSupply?.Unavailable);
        }
        if (v.Forecast?.Observed is { } forecast)
        {
            ValidateForecast(forecast, v.FoodSupply?.Observed);
        }
        else
        {
            ValidateUnavailable(v.Forecast?.Unavailable);
        }
        if (v.Upkeep?.Observed is { } upkeep)
        {
            ValidateDirectUpkeep(upkeep, v.MapSize);
            ValidateColonyUpkeep(upkeep, v.MapSize);
        }
        else
        {
            ValidateUnavailable(v.Upkeep?.Unavailable);
        }
        if (v.Development?.Observed is { } power)
        {
            ValidateColonyPower(power, v.MapSize);
        }
        else
        {
            ValidateUnavailable(v.Development?.Unavailable);
        }
        ValidateColonyThreat(v.Threat);
        if (v.Planning is null)
        {
            throw Contract("missing planning availability");
        }
        switch (v.Planning.OutcomeCase)
        {
            case PlanningSection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Planning.Unavailable);
                return;
            case PlanningSection.OutcomeOneofCase.Observed:
                ValidateColonyPlanning(v.Planning.Observed, v.Context, v.MapSize);
                return;
            default:
                throw Contract("missing planning outcome");
        }
    }

    private static void ValidateColonyPlanning(PlanningFacts? p, ObservationContext context, MapSize? size)
    {
        if (p is null)
        {
            throw Contract("unsupported planning projection");
        }
        PawnsIssues(p.Issues, p);
        if (p.Gear is not null)
        {
            ValidateColonyGear(p.Gear, context);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var crop in p.Crops)
        {
            if (crop is null || !IsValidId(crop.DefName) || seen.Contains(crop.DefName) || !crop.HasDietAllowed || !NonNegativeNumber(crop.HasNutritionDemandPerDay, crop.NutritionDemandPerDay))
            {
                throw Contract("invalid edible crop");
            }
            seen.Add(crop.DefName);
        }
        if (p.Environment is not null)
        {
            ValidateGrowingEnvironment(p.Environment, size);
        }
    }

    /// <summary>
    /// Go's <c>validateGrowingEnvironment</c>: bounds the controlled-growing census. Every row is identified, every
    /// cell lies on the map and every number is finite.
    /// </summary>
    private static void ValidateGrowingEnvironment(ControlledEnvironment e, MapSize? size)
    {
        PawnsIssues(e.Issues, e);
        bool Cells(IEnumerable<Cell> rows) => rows.All(row => ColonyCell(row, size));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Unique(string id) => seen.Add(id);
        foreach (var row in e.Lights)
        {
            if (row is null || !ValidRef(row.Building) || !Unique("light/" + row.Building.Id) || !OptionalRef(row.Room) || !OptionalId(row.HasPowerNetId, row.PowerNetId) || !Cells(row.GrowthCells))
            {
                throw Contract("invalid environment light");
            }
            PawnsIssues(row.Issues, row);
        }
        foreach (var row in e.Growers)
        {
            if (row is null
                || !ValidRef(row.Building)
                || !Unique("grower/" + row.Building.Id)
                || !OptionalRef(row.Room)
                || !OptionalId(row.HasPowerNetId, row.PowerNetId)
                || !OptionalId(row.HasCropDefName, row.CropDefName)
                || !Cells(row.PlantCells))
            {
                throw Contract("invalid environment grower");
            }
            PawnsIssues(row.Issues, row);
        }
        foreach (var row in e.Rooms)
        {
            if (row is null
                || !IsValidId(row.Room?.Id ?? "")
                || !Unique("room/" + row.Room!.Id)
                || !FiniteNumber(row.HasTemperatureC, row.TemperatureC)
                || (row.HasLitCells && row.HasCellCount && row.LitCells > row.CellCount))
            {
                throw Contract("invalid environment room");
            }
        }
        foreach (var row in e.Networks)
        {
            if (row is null || !IsValidId(row.Id) || !Unique("network/" + row.Id))
            {
                throw Contract("invalid environment network");
            }
            foreach (var (present, value) in new[]
            {
                (row.HasGenerationW, row.GenerationW),
                (row.HasSolarW, row.SolarW),
                (row.HasWindW, row.WindW),
                (row.HasConsumptionW, row.ConsumptionW),
                (row.HasStoredWattDays, row.StoredWattDays),
                (row.HasCapacityWattDays, row.CapacityWattDays),
            })
            {
                if (!NonNegativeNumber(present, value))
                {
                    throw Contract("invalid environment network");
                }
            }
        }
    }

    /// <summary>Go's <c>validSeason</c>: accepts the native Season enum names (RimWorld.Season).</summary>
    private static bool ValidSeason(string name) =>
        name is "Undefined" or "Spring" or "Summer" or "Fall" or "Winter" or "PermanentSummer" or "PermanentWinter";

    /// <summary>
    /// Go's <c>validateColonyThreat</c>: preserves absent or unavailable threat as unknown. Observed values must be
    /// finite and non-negative, with completeness on the single colony row.
    /// </summary>
    private static void ValidateColonyThreat(ThreatSection? v)
    {
        if (v is null)
        {
            return;
        }
        switch (v.OutcomeCase)
        {
            case ThreatSection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Unavailable);
                return;
            case ThreatSection.OutcomeOneofCase.Observed:
                var facts = v.Observed;
                if (facts is null)
                {
                    throw Contract("missing threat facts");
                }
                PawnsIssues(facts.Issues, facts);
                foreach (var (present, value) in new[]
                {
                    (facts.HasWealthItems, facts.WealthItems),
                    (facts.HasWealthBuildings, facts.WealthBuildings),
                    (facts.HasWealthPawns, facts.WealthPawns),
                    (facts.HasWealthTotal, facts.WealthTotal),
                    (facts.HasStorytellerWealth, facts.StorytellerWealth),
                    (facts.HasRaidPoints, facts.RaidPoints),
                    (facts.HasAdaptationFactor, facts.AdaptationFactor),
                    (facts.HasDifficultyThreatScale, facts.DifficultyThreatScale),
                })
                {
                    if (!NonNegativeNumber(present, value))
                    {
                        throw Contract("invalid colony threat number");
                    }
                }
                if (facts.HasColonistCount && facts.ColonistCount > 65536)
                {
                    throw Contract("colony count exceeds bound");
                }
                _ = ValidateRaidTracks(facts.Raids);
                return;
            default:
                throw Contract("missing threat outcome");
        }
    }
}
