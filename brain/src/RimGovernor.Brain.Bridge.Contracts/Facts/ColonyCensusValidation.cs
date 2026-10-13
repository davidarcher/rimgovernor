using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go colony_acquisition.go, colony_power.go, colony_recovery.go, colony_production.go and colony_waste.go: the
// validators of the colony censuses (acquisition, development, environment, recovery, production and waste).
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>proto.Equal(x, &amp;T{listed fields})</c> where the literal copies <paramref name="allowed"/> from
    /// <paramref name="message"/>: the message carries no field outside the listed ones.
    /// </summary>
    private static bool OnlyFields(object message, params string[] allowed) =>
        PopulatedFields(message).All(entry => allowed.Contains(entry.Field.Name, StringComparer.Ordinal));

    /// <summary>Go's <c>refSnapshot</c>: whether <paramref name="snapshot"/> is the CAS token of <paramref name="reference"/> read under <paramref name="context"/>.</summary>
    private static bool RefSnapshot(SnapshotRef? snapshot, Ref? reference, ObservationContext? context) =>
        snapshot is not null
        && reference is not null
        && (snapshot.EntityId ?? "") == (reference.Id ?? "")
        && IsValidId((snapshot.Token ?? ""))
        && Equals(snapshot.Context, context);

    /// <summary>
    /// Go's <c>validateColonyAcquisition</c>: binds the acquisition census. A hunt row is a hunt of one unit of
    /// something edible, or of a recognised pest, which is inedible with no nutrition.
    /// </summary>
    private static void ValidateColonyAcquisition(ColonyFactsSnapshot v)
    {
        if (v.PendingHunts.GetValueOrDefault() > 65536)
        {
            throw Contract("acquisition census exceeds bound");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Acquisition)
        {
            if (row is null)
            {
                throw Contract("missing acquisition row");
            }
            var source = row.Source;
            if (!ValidRef(source)
                || seen.Contains((source!.Id ?? ""))
                || !RefSnapshot(row.SourceSnapshot, source, v.Context)
                || !IsValidId((row.Resource ?? ""))
                || row.Hunt is null
                || row.Designated is null
                || row.Yield is null
                || row.Yield.GetValueOrDefault() <= 0
                || !NonNegativeNumber(row.Yield is not null, row.Yield.GetValueOrDefault())
                || (row.Hunt.GetValueOrDefault() && (row.Food is not null || row.Yield.GetValueOrDefault() != 1))
                || (!row.Hunt.GetValueOrDefault() && row.Food is null))
            {
                throw Contract("invalid acquisition source or yield");
            }
            if ((row.Hunt.GetValueOrDefault()
                    && (row.MeatAmount is null
                        || !NonNegativeNumber(row.MeatAmount is not null, row.MeatAmount.GetValueOrDefault())
                        || row.HerdSize is null
                        || row.HerdSize.GetValueOrDefault() == 0
                        || row.HerdSize.GetValueOrDefault() > 65536
                        || row.Downed is null))
                || (!row.Hunt.GetValueOrDefault() && row.MeatAmount is not null))
            {
                throw Contract("missing or invalid hunt cost facts");
            }
            if ((row.Plantation.GetValueOrDefault() && (row.Hunt.GetValueOrDefault() || row.Growth is null || !NonNegativeNumber(row.Growth is not null, row.Growth.GetValueOrDefault()) || row.Growth.GetValueOrDefault() > 1))
                || (row.Growth is not null && (row.Hunt.GetValueOrDefault() || !NonNegativeNumber(row.Growth is not null, row.Growth.GetValueOrDefault()) || row.Growth.GetValueOrDefault() > 1)))
            {
                throw Contract("invalid plantation tree or growth fraction");
            }
            if (row.Hunt.GetValueOrDefault() && (v.HuntCensus is null || row.Fogged is null || row.InMentalState is null))
            {
                throw Contract("hunt row without the hunt census or its fogged and mental-state facts");
            }

            // A designated row carries the tick native first saw it; an undesignated row none. taken is always reported.
            if (row.Taken is null || row.Designated.GetValueOrDefault() != (row.DesignatedTick is not null) || (row.DesignatedTick is not null && (row.DesignatedTick.GetValueOrDefault() < 0 || row.DesignatedTick.GetValueOrDefault() > (v.Context?.Tick).GetValueOrDefault())))
            {
                throw Contract("invalid acquisition designation age or taken");
            }
            seen.Add((source.Id ?? ""));
        }
        ValidateHuntCensus(v.HuntCensus);
        foreach (var issue in v.Issues)
        {
            if (((issue.Field ?? "") == "pending_hunts" && v.PendingHunts is not null)
                || ((issue.Field ?? "") == "acquisition" && v.Acquisition.Count > 0)
                || ((issue.Field ?? "") == "pending_wood_units" && v.PendingWoodUnits is not null)
                || ((issue.Field ?? "") == "pending_food_nutrition" && v.PendingFoodNutrition is not null))
            {
                throw Contract("unavailable acquisition contains facts");
            }
        }
    }

    /// <summary>
    /// Go's <c>validateHuntCensus</c>: binds the raw hunt census. It is bounded, and every colonist and bench is
    /// named with the facts the hunt gate reads stated.
    /// </summary>
    private static void ValidateHuntCensus(HuntCensus? census)
    {
        if (census is null)
        {
            return;
        }
        if (census.Hunters.Count > 4096 || census.Benches.Count > 4096)
        {
            throw Contract("hunt census exceeds bound");
        }
        foreach (var h in census.Hunters)
        {
            if (h is null
                || !IsValidId((h.PawnId ?? ""))
                || h.Position is null
                || h.Downed is null
                || h.InMentalState is null
                || h.HuntingActive is null
                || h.CookingActive is null
                || h.HuntingPriority is null)
            {
                throw Contract("invalid hunter facts");
            }
            var routed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in h.Routes)
            {
                if (r is null || !IsValidId((r.PreyId ?? "")) || routed.Contains((r.PreyId ?? "")) || (r.Safe is not null) == (r.Skipped is not null) || (r.Skipped is not null && !r.Skipped.GetValueOrDefault()))
                {
                    throw Contract("invalid hunt route evidence");
                }
                routed.Add((r.PreyId ?? ""));
            }
        }
        foreach (var b in census.Benches)
        {
            if (b is null || !IsValidId((b.BenchId ?? "")) || b.Usable is null)
            {
                throw Contract("invalid hunt butcher bench");
            }
        }
    }

    /// <summary>Go's <c>validateColonyPower</c>: the development facts are power, networks, the short circuit and geysers, nothing else.</summary>
    private static void ValidateColonyPower(DevelopmentFacts? v, MapSize? size)
    {
        if (v is null || !OnlyFields(v, "power", "networks", "short_circuit_tick", "geysers"))
        {
            throw Contract("unsupported development facts");
        }
        if (v.ShortCircuitTick is not null && v.ShortCircuitTick.GetValueOrDefault() < 0)
        {
            throw Contract("invalid short circuit tick");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Power)
        {
            if (row is null || row.Building is null)
            {
                throw Contract("missing power building");
            }
            var reference = row.Building;
            if (!ValidRef(reference) || !seen.Add((reference.Id ?? "")))
            {
                throw Contract("invalid power building identity");
            }
            if (row.StoredWattDays is not null && (!double.IsFinite(row.StoredWattDays.GetValueOrDefault()) || row.StoredWattDays.GetValueOrDefault() < 0 || row.StoredWattDays.GetValueOrDefault() > 1e12))
            {
                throw Contract("invalid power storage quantity");
            }
        }
        var networks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var net in v.Networks)
        {
            if (net is null
                || !IsValidId((net.Id ?? ""))
                || networks.Contains((net.Id ?? ""))
                || !OnlyFields(
                    net,
                    "id",
                    "producers",
                    "consumers",
                    "batteries",
                    "transmitters",
                    "connectors",
                    "generation_w",
                    "consumption_w",
                    "net_w",
                    "stored_watt_days",
                    "capacity_watt_days",
                    "has_source",
                    "has_active_source"))
            {
                throw Contract("invalid power network");
            }
            networks.Add((net.Id ?? ""));
            foreach (var (present, value) in new[]
            {
                (net.GenerationW is not null, net.GenerationW.GetValueOrDefault()),
                (net.ConsumptionW is not null, net.ConsumptionW.GetValueOrDefault()),
                (net.NetW is not null, net.NetW.GetValueOrDefault()),
                (net.StoredWattDays is not null, net.StoredWattDays.GetValueOrDefault()),
                (net.CapacityWattDays is not null, net.CapacityWattDays.GetValueOrDefault()),
            })
            {
                if (present && (!double.IsFinite(value) || Math.Abs(value) > 1e12))
                {
                    throw Contract("invalid power network wattage");
                }
            }
            if ((net.GenerationW is not null && net.GenerationW.GetValueOrDefault() < 0)
                || (net.ConsumptionW is not null && net.ConsumptionW.GetValueOrDefault() < 0)
                || (net.StoredWattDays is not null && net.StoredWattDays.GetValueOrDefault() < 0)
                || (net.CapacityWattDays is not null && net.CapacityWattDays.GetValueOrDefault() < 0))
            {
                throw Contract("negative power network quantity");
            }
        }
        var geysers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Geysers)
        {
            if (row is null || !OnlyFields(row, "geyser", "cells", "occupied") || !ValidRef(row.Geyser) || row.Occupied is null || geysers.Contains((row.Geyser.Id ?? "")))
            {
                throw Contract("invalid steam geyser");
            }
            geysers.Add((row.Geyser.Id ?? ""));
            if (row.Cells.Count < 1)
            {
                throw Contract("invalid steam geyser footprint");
            }
            var cells = new HashSet<(int, int)>();
            foreach (var cell in row.Cells)
            {
                if (!ColonyCell(cell, size) || !cells.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
                {
                    throw Contract("invalid steam geyser footprint");
                }
            }
        }
    }

    /// <summary>Go's <c>validateColonyEnvironment</c>: the game conditions in force, each identified once.</summary>
    private static void ValidateColonyEnvironment(ColonyFactsSnapshot v)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Environment)
        {
            if (row is null || !IsValidId((row.Id ?? "")) || !IsValidId((row.DefName ?? "")) || seen.Contains((row.Id ?? "")))
            {
                throw Contract("invalid environment condition");
            }

            // Native fills permanent and, for a timed condition, ticks_left >= 0; the condition's class and label are
            // its GameConditionDef row's.
            if (row.TicksLeft is not null && (row.TicksLeft.GetValueOrDefault() < 0 || row.Permanent.GetValueOrDefault()))
            {
                throw Contract("invalid environment condition duration");
            }
            if (!OnlyFields(row, "id", "def_name", "permanent", "ticks_left"))
            {
                throw Contract("invalid environment condition");
            }
            seen.Add((row.Id ?? ""));
        }
        if (v.Environment.Count > 0 && v.Issues.Any(issue => (issue.Field ?? "") == "environment"))
        {
            throw Contract("unavailable environment contains observations");
        }
    }

    /// <summary>Go's <c>validateColonyRecovery</c>: older native producers explicitly omit this section.</summary>
    private static void ValidateColonyRecovery(ColonyFactsSnapshot colony)
    {
        var reply = colony.Recovery;
        if (reply is null)
        {
            return;
        }
        if (colony.Issues.Any(issue => (issue.Field ?? "") == "recovery"))
        {
            throw Contract("unavailable recovery contains a reply");
        }
        if (reply.Outcome?.Unavailable is not null)
        {
            ValidateUnavailable(reply.Outcome?.Unavailable);
            return;
        }
        var v = reply.Outcome?.Observed;
        if (v is null || !Equals(v.Context, colony.Context) || !OnlyFields(v, "context", "restrictions", "buildings"))
        {
            throw Contract("invalid recovery context or outcome");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in v.Buildings)
        {
            if (!ValidRef(reference) || !seen.Add((reference.Id ?? "")))
            {
                throw Contract("invalid recovery building identity");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in v.Restrictions)
        {
            if (r is null || !ValidRef(r.Pawn) || seen.Contains((r.Pawn.Id ?? "")) || (r.AreaId is not null && !IsValidId((r.AreaId ?? ""))) || !OnlyFields(r, "pawn", "area_id"))
            {
                throw Contract("invalid recovery restriction");
            }
            seen.Add((r.Pawn.Id ?? ""));
        }
    }

    /// <summary>
    /// Go's <c>validFarmMeasures</c>: a farm's raw growth facts have counts within the planted cells, growth a
    /// fraction, factors and nutrition non-negative, and every number finite (temperatures may be negative).
    /// </summary>
    private static bool ValidFarmMeasures(FarmFacts farm)
    {
        if (farm.PlantedCells is not null)
        {
            foreach (var (present, count) in new[] { (farm.FertilePlantedCells is not null, farm.FertilePlantedCells.GetValueOrDefault()), (farm.BlightedPlants is not null, farm.BlightedPlants.GetValueOrDefault()) })
            {
                if (present && count > farm.PlantedCells.GetValueOrDefault())
                {
                    return false;
                }
            }
        }
        foreach (var (present, fraction) in new[] { (farm.GrowthMin is not null, farm.GrowthMin.GetValueOrDefault()), (farm.GrowthMean is not null, farm.GrowthMean.GetValueOrDefault()) })
        {
            if (present && (!NonNegativeNumber(present, fraction) || fraction > 1))
            {
                return false;
            }
        }
        return NonNegativeNumber(farm.FertilityFactorMin is not null, farm.FertilityFactorMin.GetValueOrDefault())
            && NonNegativeNumber(farm.FertilityFactorMean is not null, farm.FertilityFactorMean.GetValueOrDefault())
            && NonNegativeNumber(farm.LightFactorMean is not null, farm.LightFactorMean.GetValueOrDefault())
            && FiniteNumber(farm.Temperature is not null, farm.Temperature.GetValueOrDefault());
    }

    /// <summary>Go's <c>validateColonyProduction</c>: the farm, cooking and butchering censuses.</summary>
    private static void ValidateColonyProduction(ColonyFactsSnapshot v)
    {
        foreach (var issue in v.Issues)
        {
            if (((issue.Field ?? "") == "farms" && v.Farms.Count != 0) || ((issue.Field ?? "") == "cooking" && v.Cooking.Count != 0) || ((issue.Field ?? "") == "butchering" && v.Butchering.Count != 0))
            {
                throw Contract("unavailable production census contains rows");
            }
        }
        var farms = new HashSet<string>(StringComparer.Ordinal);
        var mapCells = (ulong)(v.MapSize?.Width ?? 0) * (v.MapSize?.Height ?? 0);
        foreach (var farm in v.Farms)
        {
            if (farm is null || !IsValidId(farm.Zone?.Id ?? "") || !IsValidId((farm.Crop ?? "")) || farms.Contains((farm.Zone!.Id ?? "")))
            {
                throw Contract("invalid or duplicate farm");
            }
            farms.Add((farm.Zone.Id ?? ""));
            foreach (var (present, count) in new[]
            {
                (farm.UsableCells is not null, farm.UsableCells.GetValueOrDefault()),
                (farm.PlantedCells is not null, farm.PlantedCells.GetValueOrDefault()),
                (farm.FertilePlantedCells is not null, farm.FertilePlantedCells.GetValueOrDefault()),
                (farm.BlightedPlants is not null, farm.BlightedPlants.GetValueOrDefault()),
            })
            {
                if (present && count > mapCells)
                {
                    throw Contract("farm cells exceed map");
                }
            }
            if (!ValidFarmMeasures(farm))
            {
                throw Contract("invalid farm production measure");
            }
        }
        var rows = new List<CookingFacts>(v.Cooking);
        foreach (var b in v.Butchering)
        {
            if (b is null)
            {
                throw Contract("nil butcher bench");
            }
            var corpses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var meat in b.HumanCorpseMeat)
            {
                if (meat is null || !IsValidId((meat.Race ?? "")) || corpses.Contains((meat.Race ?? "")) || !NonNegativeNumber(meat.MeatAmount is not null, meat.MeatAmount.GetValueOrDefault()) || meat.MeatAmount is null)
                {
                    throw Contract("invalid human butchery census");
                }
                corpses.Add((meat.Race ?? ""));
            }
            var workers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var worker in b.HumanButchers)
            {
                if (worker is null || !IsValidId(worker.PawnId) || !workers.Add(worker.PawnId))
                {
                    throw Contract("invalid human butcher");
                }
                if (worker.PawnTraits.Any(trait => !IsValidId(trait?.DefName ?? "")))
                {
                    throw Contract("invalid butcher trait");
                }
            }
            var bench = new CookingFacts { Bench = b.Bench, BenchSnapshot = b.BenchSnapshot };
            if (b.Usable is not null)
            {
                bench = bench with { Usable = b.Usable };
            }
            bench = bench with { Bills = [.. bench.Bills, .. b.Bills] };
            bench = bench with { Recipes = [.. bench.Recipes, .. b.Recipes] };
            rows.Add(bench);
        }
        var benches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bench in rows)
        {
            if (bench is null || bench.Bench is null || !ValidRef(bench.Bench) || !benches.Add((bench.Bench.Id ?? "")))
            {
                throw Contract("invalid production bench");
            }
            if (bench.BenchSnapshot is { } snapshot
                && (!Equals(snapshot.Context, v.Context) || (snapshot.EntityId ?? "") != (bench.Bench.Id ?? "") || !IsValidId((snapshot.Token ?? ""))))
            {
                throw Contract("bill stack snapshot mismatch");
            }
            if (bench.Bills.Count > 15)
            {
                throw Contract("bill census exceeds bound");
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                if (recipe is null
                    || recipe.Recipe is null
                    || !IsValidId((recipe.Recipe.DefName ?? ""))
                    || recipes.Contains((recipe.Recipe.DefName ?? ""))
                    || !OnlyFields(recipe, "recipe", "available_now", "available_on_bench"))
                {
                    throw Contract("invalid production recipe");
                }
                recipes.Add((recipe.Recipe.DefName ?? ""));
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bill in bench.Bills)
            {
                if (bill is null
                    || bill.Recipe is null
                    || !IsValidId((bill.Recipe.DefName ?? ""))
                    || !OnlyFields(
                        bill,
                        "default_ingredients",
                        "unrestricted_worker",
                        "managed_unchanged",
                        "id",
                        "recipe",
                        "suspended",
                        "repeat_mode",
                        "repeat_count",
                        "target_count",
                        "unpause_below",
                        "pause_when_satisfied",
                        "paused",
                        "finished",
                        "reservations",
                        "worker",
                        "ingredient_filter"))
                {
                    throw Contract("invalid production bill");
                }
                if (!OptionalRef(bill.Worker))
                {
                    throw Contract("invalid bill worker");
                }
                if (bill.IngredientFilter is { } filter)
                {
                    if (!OnlyFields(filter, "allowed_def_names"))
                    {
                        throw Contract("invalid bill filter");
                    }
                    var allowed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var id in filter.AllowedDefNames)
                    {
                        if (!IsValidId(id) || !allowed.Add(id))
                        {
                            throw Contract("invalid bill filter definition");
                        }
                    }
                }
                if (bill.Id is not null && (!IsValidId((bill.Id ?? "")) || !ids.Add((bill.Id ?? ""))))
                {
                    throw Contract("invalid bill identity");
                }
                foreach (var (present, n) in new[] { (bill.RepeatCount is not null, bill.RepeatCount.GetValueOrDefault()), (bill.TargetCount is not null, bill.TargetCount.GetValueOrDefault()), (bill.UnpauseBelow is not null, bill.UnpauseBelow.GetValueOrDefault()) })
                {
                    if (present && n < 0)
                    {
                        throw Contract("negative bill setting");
                    }
                }
            }
            var produced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var production in bench.Production)
            {
                if (production is null || !IsValidId((production.Recipe ?? "")) || !recipes.Contains((production.Recipe ?? "")) || !produced.Add((production.Recipe ?? "")) || production.Available is null)
                {
                    throw Contract("invalid food recipe output");
                }
                var defs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var product in production.Products)
                {
                    if (product is null
                        || !IsValidId((product.DefName ?? ""))
                        || defs.Contains((product.DefName ?? ""))
                        || product.Count is null
                        || product.Count.GetValueOrDefault() <= 0
                        || product.Edible is null
                        || product.NutritionDemandPerDay is null)
                    {
                        throw Contract("invalid food product");
                    }
                    defs.Add((product.DefName ?? ""));
                    if (!double.IsFinite(product.NutritionDemandPerDay.GetValueOrDefault()) || product.NutritionDemandPerDay.GetValueOrDefault() < 0)
                    {
                        throw Contract("invalid food product measure");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Go's <c>validateColonyWaste</c>: validates the generic per-tick waste census embedded in the colony facts.
    /// Each row's Thing carries an id, def name, map and cell but never a label or snapshot.
    /// </summary>
    private static void ValidateColonyWaste(WasteReply? v)
    {
        if (v is null)
        {
            return;
        }
        switch (v.Outcome)
        {
            case null:
                throw Contract("missing waste outcome");
            case WasteReplyOutcome.UnavailableCase:
                ValidateUnavailable(v.Outcome?.Unavailable);
                return;
            case WasteReplyOutcome.FailureCase:
                throw Contract("waste census carries a failure outcome");
            case WasteReplyOutcome.ObservedCase:
                var snapshot = v.Outcome?.Observed ?? throw Contract("missing waste census");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in snapshot.Items)
                {
                    if (row is null
                        || !UniqueRef(row.Thing, seen)
                        || (row.Count is not null && row.Count.GetValueOrDefault() < 0)
                        || !OptionalRef(row.Zone)
                        || !OptionalRef(row.Grave)
                        || (row.RotStage is not null && !Enum.IsDefined(row.RotStage.GetValueOrDefault()))
                        || (row.Kind is not null && WasteKindName(row.Kind.GetValueOrDefault()).Length == 0)
                        || (row.ProtectedReason is not null && !IsValidId((row.ProtectedReason ?? "")))
                        || (row.CorpseClass is not null && CorpseOf(row.CorpseClass.GetValueOrDefault()) == DomainCorpseOf.None))
                    {
                        throw Contract("invalid waste item");
                    }
                }
                return;
            default:
                throw Contract("unrecognized waste outcome");
        }
    }
}
