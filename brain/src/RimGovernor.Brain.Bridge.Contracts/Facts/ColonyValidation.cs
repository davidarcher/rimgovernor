using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

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
            if (row is null || !IsValidId((row.DefName ?? "")) || seen.Contains((row.DefName ?? "")) || (row.Units is not null && row.Units.GetValueOrDefault() < 0))
            {
                throw Contract("invalid colony quantity");
            }
            seen.Add((row.DefName ?? ""));
        }
    }

    /// <summary>Go's <c>colonySize</c>: a map size with both sides stated, positive and at most 4096.</summary>
    private static bool ColonySize(MapSize? size) =>
        size is not null && size.Width is not null && size.Height is not null && size.Width.GetValueOrDefault() > 0 && size.Height.GetValueOrDefault() > 0 && size.Width.GetValueOrDefault() <= 4096 && size.Height.GetValueOrDefault() <= 4096;

    /// <summary>Go's <c>colonyCell</c>: a cell with both coordinates stated that lies on the map.</summary>
    private static bool ColonyCell([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Cell? cell, MapSize? size) =>
        cell is not null && cell.X is not null && cell.Z is not null && cell.X.GetValueOrDefault() >= 0 && cell.Z.GetValueOrDefault() >= 0 && (uint)cell.X.GetValueOrDefault() < (size?.Width ?? 0) && (uint)cell.Z.GetValueOrDefault() < (size?.Height ?? 0);

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

    private static bool ContextValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ObservationContext? context)
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
            (v.ColonistCount is not null, v.ColonistCount.GetValueOrDefault()),
            (v.WorkerCount is not null, v.WorkerCount.GetValueOrDefault()),
            (v.BedCapacity is not null, v.BedCapacity.GetValueOrDefault()),
            (v.IndoorSleepingCapacity is not null, v.IndoorSleepingCapacity.GetValueOrDefault()),
        })
        {
            if (present && value > 65536)
            {
                throw Contract("colony count exceeds bound");
            }
        }
        if ((v.WorkerCount is not null && v.ColonistCount is not null && v.WorkerCount.GetValueOrDefault() > v.ColonistCount.GetValueOrDefault())
            || (v.IndoorSleepingCapacity is not null && v.BedCapacity is not null && v.IndoorSleepingCapacity.GetValueOrDefault() > v.BedCapacity.GetValueOrDefault()))
        {
            throw Contract("inconsistent colony capacity");
        }
        foreach (var (present, value) in new[]
        {
            (v.FoodNutrition is not null, v.FoodNutrition.GetValueOrDefault()),
            (v.NutritionPerDay is not null, v.NutritionPerDay.GetValueOrDefault()),
            (v.FoodRunwayDays is not null, v.FoodRunwayDays.GetValueOrDefault()),
            (v.PendingFoodNutrition is not null, v.PendingFoodNutrition.GetValueOrDefault()),
            (v.PendingWoodUnits is not null, v.PendingWoodUnits.GetValueOrDefault()),
        })
        {
            if (!NonNegativeNumber(present, value))
            {
                throw Contract("invalid colony nutrition");
            }
        }
        foreach (var (present, value) in new[]
        {
            (v.SleepingTemperatureMinC is not null, v.SleepingTemperatureMinC.GetValueOrDefault()),
            (v.SleepingTemperatureMaxC is not null, v.SleepingTemperatureMaxC.GetValueOrDefault()),
            (v.OutdoorTemperatureC is not null, v.OutdoorTemperatureC.GetValueOrDefault()),
        })
        {
            if (!FiniteNumber(present, value))
            {
                throw Contract("invalid colony temperature");
            }
        }
        if (v.SleepingTemperatureMinC is not null && v.SleepingTemperatureMaxC is not null && v.SleepingTemperatureMinC.GetValueOrDefault() > v.SleepingTemperatureMaxC.GetValueOrDefault())
        {
            throw Contract("inverted sleeping temperature range");
        }
        if (v.Biome is not null && !IsValidId((v.Biome ?? "")))
        {
            throw Contract("invalid biome");
        }
        if (!ValidIds(v.TileMutators, true))
        {
            throw Contract("invalid tile mutators");
        }
        if (v.PlayerTechLevel is not null && !IsValidId((v.PlayerTechLevel ?? "")))
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
        ValidateJoinerLetters(v.JoinerLetters, v.Context.Tick.GetValueOrDefault());
        ValidateChoiceDialog(v.Dialog);
        ValidateColonyWaste(v.Waste);
        if (v.FoodClimate is { } climate)
        {
            PawnsIssues(climate.Issues, climate);
            foreach (var (present, value) in new[]
            {
                (climate.GrowingDays is not null, climate.GrowingDays.GetValueOrDefault()),
                (climate.GrowingDaysRemaining is not null, climate.GrowingDaysRemaining.GetValueOrDefault()),
                (climate.GrowingDaysUntil is not null, climate.GrowingDaysUntil.GetValueOrDefault()),
                (climate.NonGrowingDays is not null, climate.NonGrowingDays.GetValueOrDefault()),
            })
            {
                if (!NonNegativeNumber(present, value) || (present && value > GameTime.DaysPerYear))
                {
                    throw Contract("invalid seasonal crop budget");
                }
            }
            if ((climate.DayOfYear is not null && (climate.DayOfYear.GetValueOrDefault() < 0 || climate.DayOfYear.GetValueOrDefault() >= GameTime.DaysPerYear)) || (climate.Season is not null && !ValidSeason((climate.Season ?? ""))))
            {
                throw Contract("invalid calendar");
            }
            if (v.Issues.Any(issue => (issue.Field ?? "") == "food_climate"))
            {
                throw Contract("unavailable climate contains observations");
            }
        }
        ValidateColonyAcquisition(v);
        ValidateColonyEnvironment(v);
        ValidateColonyRecovery(v);
        ValidateColonyProduction(v);
        if (v.FoodSupply?.Outcome?.Observed is { } food)
        {
            ValidateFoodSupply(food);
        }
        else
        {
            ValidateUnavailable(v.FoodSupply?.Outcome?.Unavailable);
        }
        if (v.Forecast?.Outcome?.Observed is { } forecast)
        {
            ValidateForecast(forecast, v.FoodSupply?.Outcome?.Observed);
        }
        else
        {
            ValidateUnavailable(v.Forecast?.Outcome?.Unavailable);
        }
        if (v.Upkeep?.Outcome?.Observed is { } upkeep)
        {
            ValidateDirectUpkeep(upkeep, v.MapSize);
            ValidateColonyUpkeep(upkeep, v.MapSize);
        }
        else
        {
            ValidateUnavailable(v.Upkeep?.Outcome?.Unavailable);
        }
        if (v.Development?.Outcome?.Observed is { } power)
        {
            ValidateColonyPower(power, v.MapSize);
        }
        else
        {
            ValidateUnavailable(v.Development?.Outcome?.Unavailable);
        }
        ValidateColonyThreat(v.Threat);
        if (v.Planning is null)
        {
            throw Contract("missing planning availability");
        }
        switch (v.Planning.Outcome)
        {
            case PlanningSectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Planning.Outcome?.Unavailable);
                return;
            case PlanningSectionOutcome.ObservedCase:
                ValidateColonyPlanning(v.Planning.Outcome?.Observed, v.Context, v.MapSize);
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
            if (crop is null || !IsValidId((crop.DefName ?? "")) || seen.Contains((crop.DefName ?? "")) || crop.DietAllowed is null || !NonNegativeNumber(crop.NutritionDemandPerDay is not null, crop.NutritionDemandPerDay.GetValueOrDefault()))
            {
                throw Contract("invalid edible crop");
            }
            seen.Add((crop.DefName ?? ""));
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
            if (row is null || !ValidRef(row.Building) || !Unique("light/" + (row.Building?.Id ?? "")) || !OptionalRef(row.Room) || !OptionalId(row.PowerNetId is not null, (row.PowerNetId ?? "")) || !Cells(row.GrowthCells))
            {
                throw Contract("invalid environment light");
            }
            PawnsIssues(row.Issues, row);
        }
        foreach (var row in e.Growers)
        {
            if (row is null
                || !ValidRef(row.Building)
                || !Unique("grower/" + (row.Building?.Id ?? ""))
                || !OptionalRef(row.Room)
                || !OptionalId(row.PowerNetId is not null, (row.PowerNetId ?? ""))
                || !OptionalId(row.CropDefName is not null, (row.CropDefName ?? ""))
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
                || !Unique("room/" + (row.Room!.Id ?? ""))
                || !FiniteNumber(row.TemperatureC is not null, row.TemperatureC.GetValueOrDefault())
                || (row.LitCells is not null && row.CellCount is not null && row.LitCells.GetValueOrDefault() > row.CellCount.GetValueOrDefault()))
            {
                throw Contract("invalid environment room");
            }
        }
        foreach (var row in e.Networks)
        {
            if (row is null || !IsValidId((row.Id ?? "")) || !Unique("network/" + (row.Id ?? "")))
            {
                throw Contract("invalid environment network");
            }
            foreach (var (present, value) in new[]
            {
                (row.GenerationW is not null, row.GenerationW.GetValueOrDefault()),
                (row.SolarW is not null, row.SolarW.GetValueOrDefault()),
                (row.WindW is not null, row.WindW.GetValueOrDefault()),
                (row.ConsumptionW is not null, row.ConsumptionW.GetValueOrDefault()),
                (row.StoredWattDays is not null, row.StoredWattDays.GetValueOrDefault()),
                (row.CapacityWattDays is not null, row.CapacityWattDays.GetValueOrDefault()),
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
        switch (v.Outcome)
        {
            case ThreatSectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Outcome?.Unavailable);
                return;
            case ThreatSectionOutcome.ObservedCase:
                var facts = v.Outcome?.Observed;
                if (facts is null)
                {
                    throw Contract("missing threat facts");
                }
                PawnsIssues(facts.Issues, facts);
                foreach (var (present, value) in new[]
                {
                    (facts.WealthItems is not null, facts.WealthItems.GetValueOrDefault()),
                    (facts.WealthBuildings is not null, facts.WealthBuildings.GetValueOrDefault()),
                    (facts.WealthPawns is not null, facts.WealthPawns.GetValueOrDefault()),
                    (facts.WealthTotal is not null, facts.WealthTotal.GetValueOrDefault()),
                    (facts.StorytellerWealth is not null, facts.StorytellerWealth.GetValueOrDefault()),
                    (facts.RaidPoints is not null, facts.RaidPoints.GetValueOrDefault()),
                    (facts.AdaptationFactor is not null, facts.AdaptationFactor.GetValueOrDefault()),
                    (facts.DifficultyThreatScale is not null, facts.DifficultyThreatScale.GetValueOrDefault()),
                })
                {
                    if (!NonNegativeNumber(present, value))
                    {
                        throw Contract("invalid colony threat number");
                    }
                }
                if (facts.ColonistCount is not null && facts.ColonistCount.GetValueOrDefault() > 65536)
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
