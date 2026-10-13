using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go event_loot.go, deep_resources.go, policies.go, royalty_colony.go, food_channels.go and dialog.go: the
// validators of the independent colony sections that the census carries.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>validateEventLoot</c>.</summary>
    private static void ValidateEventLoot(ColonyFactsSnapshot v)
    {
        if (v.EventLoot is null)
        {
            return;
        }
        switch (v.EventLoot.Outcome)
        {
            case LootSectionOutcome.UnavailableCase:
                ValidateUnavailable(v.EventLoot.Outcome?.Unavailable);
                return;
            case LootSectionOutcome.ObservedCase:
                if (v.EventLoot.Outcome?.Observed is not { } loot)
                {
                    throw Contract("invalid event loot census");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in loot.Items)
                {
                    if (row is null || row.Item is null || row.Forbidden is null)
                    {
                        throw Contract("incomplete event loot item");
                    }
                    if (!ValidRef(row.Item) || seen.Contains((row.Item.Id ?? "")))
                    {
                        throw Contract("invalid event loot item");
                    }
                    seen.Add((row.Item.Id ?? ""));
                }
                return;
            default:
                throw Contract("missing event loot outcome");
        }
    }

    /// <summary>Go's <c>validateDeepResources</c>.</summary>
    private static void ValidateDeepResources(ColonyFactsSnapshot v)
    {
        if (v.DeepResources is null)
        {
            return;
        }
        switch (v.DeepResources.Outcome)
        {
            case DeepResourcesSectionOutcome.UnavailableCase:
                ValidateUnavailable(v.DeepResources.Outcome?.Unavailable);
                return;
            case DeepResourcesSectionOutcome.ObservedCase:
                var f = v.DeepResources.Outcome?.Observed;
                if (f is null)
                {
                    throw Contract("deep resource census exceeds bound");
                }
                var centres = new HashSet<(int, int)>();
                ulong cells = 0;
                foreach (var row in f.Lumps)
                {
                    if (row is null
                        || !IsValidId((row.DefName ?? ""))
                        || row.Count is null
                        || row.Count.GetValueOrDefault() <= 0
                        || row.CellCount is null
                        || row.CellCount.GetValueOrDefault() == 0
                        || !ColonyCell(row.Centre, v.MapSize))
                    {
                        throw Contract("invalid deep resource lump");
                    }
                    var key = (row.Centre.X.GetValueOrDefault(), row.Centre.Z.GetValueOrDefault());
                    if (centres.Contains(key) || row.Count.GetValueOrDefault() < (long)row.CellCount.GetValueOrDefault() || row.Count.GetValueOrDefault() > (long)row.CellCount.GetValueOrDefault() * 65535)
                    {
                        throw Contract("invalid deep resource aggregation");
                    }
                    centres.Add(key);
                    cells += row.CellCount.GetValueOrDefault();
                }
                if (cells > (ulong)v.MapSize!.Width.GetValueOrDefault() * v.MapSize.Height.GetValueOrDefault())
                {
                    throw Contract("deep resource cells exceed map");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rows in new[] { f.GroundScanners, f.LongRangeScanners })
                {
                    foreach (var row in rows)
                    {
                        if (row is null
                            || !IsValidId((row.BuildingId ?? ""))
                            || !IsValidId((row.DefName ?? ""))
                            || seen.Contains((row.BuildingId ?? ""))
                            || !ColonyCell(row.Position, v.MapSize)
                            || !row.Built.GetValueOrDefault()
                            || row.TicksToNextFind.GetValueOrDefault() < 0
                            || (row.TargetMineable is not null && !IsValidId((row.TargetMineable ?? ""))))
                        {
                            throw Contract("invalid mineral scanner");
                        }
                        seen.Add((row.BuildingId ?? ""));
                    }
                }

                // A depleted drill carries no deposit; an undepleted one names its exact resource and a positive
                // remainder. Ownership and designation are always stated.
                foreach (var row in f.Drills)
                {
                    if (row is null
                        || !IsValidId((row.BuildingId ?? ""))
                        || !IsValidId((row.DefName ?? ""))
                        || seen.Contains((row.BuildingId ?? ""))
                        || !ColonyCell(row.Position, v.MapSize)
                        || row.Depleted is null
                        || row.Designated is null
                        || row.Powered is null)
                    {
                        throw Contract("invalid deep drill");
                    }
                    if ((row.Depleted.GetValueOrDefault() && (row.Resource is not null || row.Remaining is not null))
                        || (!row.Depleted.GetValueOrDefault() && (!IsValidId((row.Resource ?? "")) || row.Remaining is null || row.Remaining.GetValueOrDefault() <= 0)))
                    {
                        throw Contract("invalid deep drill deposit");
                    }
                    seen.Add((row.BuildingId ?? ""));
                }
                return;
            default:
                throw Contract("missing deep resource outcome");
        }
    }

    /// <summary>
    /// Go's <c>validatePolicies</c>: checks the colony's policy databases. Each database has unique ids, at most one
    /// default, and a pawn is the current holder of at most one policy per database and one allowed area.
    /// </summary>
    private static void ValidatePolicies(ColonyFactsSnapshot v)
    {
        if (v.Policies is null)
        {
            return;
        }
        switch (v.Policies.Outcome)
        {
            case PolicySectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Policies.Outcome?.Unavailable);
                return;
            case PolicySectionOutcome.ObservedCase:
                var f = v.Policies.Outcome?.Observed;
                if (f is null)
                {
                    throw Contract("incomplete policy facts");
                }
                var databases = new[] { f.Outfit, f.Drug, f.Food, f.Reading };
                for (var i = 0; i < databases.Length; i++)
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    var pawns = new HashSet<string>(StringComparer.Ordinal);
                    var defaults = 0;
                    foreach (var row in databases[i])
                    {
                        if (row is null || !IsValidId((row.Id ?? "")) || ids.Contains((row.Id ?? "")) || row.Label is null || !Diagnostic(row.Label is not null, (row.Label ?? "")) || row.Default is null)
                        {
                            throw Contract("invalid policy entry");
                        }
                        ids.Add((row.Id ?? ""));
                        if (i != 2 && i != 3 && row.AllowedDefs.Count > 0)
                        {
                            throw Contract("allowed definitions on a policy without contents");
                        }
                        if (row.AllowedDefs.Any(d => !IsValidId(d)))
                        {
                            throw Contract("invalid policy definition");
                        }
                        if (i != 1 && row.DrugEntries.Count > 0)
                        {
                            throw Contract("drug entries on a non-drug policy");
                        }
                        var drugs = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var e in row.DrugEntries)
                        {
                            if (e is null
                                || !IsValidId((e.DrugDef ?? ""))
                                || drugs.Contains((e.DrugDef ?? ""))
                                || e.AllowedForJoy is null
                                || e.AllowedForAddiction is null
                                || e.AllowScheduled is null
                                || e.DaysFrequency is null
                                || e.OnlyIfMoodBelow is null
                                || e.OnlyIfJoyBelow is null
                                || e.TakeToInventory is null)
                            {
                                throw Contract("invalid drug policy entry");
                            }
                            drugs.Add((e.DrugDef ?? ""));
                        }
                        if (row.Default.GetValueOrDefault())
                        {
                            defaults++;
                        }
                        UniquePawns(row.PawnIds, pawns);
                    }
                    if (defaults > 1)
                    {
                        throw Contract("several default policies");
                    }
                }
                var areaIds = new HashSet<string>(StringComparer.Ordinal);
                var areaPawns = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.AllowedAreas)
                {
                    if (row is null || !IsValidId((row.Id ?? "")) || areaIds.Contains((row.Id ?? "")) || row.Label is null || !Diagnostic(row.Label is not null, (row.Label ?? "")))
                    {
                        throw Contract("invalid allowed area");
                    }
                    areaIds.Add((row.Id ?? ""));
                    UniquePawns(row.PawnIds, areaPawns);
                }
                return;
            default:
                throw Contract("policy section outcome");
        }
    }

    /// <summary>Go's <c>uniquePawns</c>: every pawn is valid and held by no other policy of the database.</summary>
    private static void UniquePawns(IEnumerable<string> ids, HashSet<string> seen)
    {
        foreach (var id in ids)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                throw Contract("invalid or doubly assigned policy pawn");
            }
        }
    }

    /// <summary>
    /// Go's <c>validateRoyaltyColony</c> with <c>DecodeRoyaltyColony</c>'s checks: every row is unique and
    /// well-formed with non-negative counts. The decoded policy value is not built here, only validated.
    /// </summary>
    private static void ValidateRoyaltyColony(ColonyFactsSnapshot v)
    {
        if (v.Royalty is null)
        {
            return;
        }
        switch (v.Royalty.Outcome)
        {
            case RoyaltySectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Royalty.Outcome?.Unavailable);
                return;
            case RoyaltySectionOutcome.ObservedCase:
                var facts = v.Royalty.Outcome?.Observed ?? throw Contract("incomplete royalty colony facts");
                var neuroformers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in facts.Neuroformers)
                {
                    if (!IsValidId((row.DefName ?? "")) || row.Held.GetValueOrDefault() < 0)
                    {
                        throw Contract("invalid royalty neuroformer");
                    }
                    if (!neuroformers.Add((row.DefName ?? "")))
                    {
                        throw Contract($"duplicate royalty neuroformer {(row.DefName ?? "")}");
                    }
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in facts.Ceremonies)
                {
                    var pawn = row.Pawn?.Id ?? "";
                    var bestower = row.Bestower?.Id ?? "";
                    var key = pawn + "/" + (row.FactionDef ?? "");
                    if (!IsValidId(pawn)
                        || (bestower.Length != 0 && !IsValidId(bestower))
                        || !IsValidId((row.Quest ?? ""))
                        || (row.FactionDef is not null && !IsValidId((row.FactionDef ?? "")))
                        || (row.Title is not null && !IsValidId((row.Title ?? "")))
                        || seen.Contains(key))
                    {
                        throw Contract("invalid royalty ceremony");
                    }
                    seen.Add(key);
                    if (row.Attendees.Any(a => !IsValidId((a.Id ?? ""))))
                    {
                        throw Contract("invalid royalty ceremony attendee");
                    }
                }
                var thrones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in facts.Thrones)
                {
                    var id = row.Thing?.Id ?? "";
                    var owner = row.Owner?.Id ?? "";
                    if (!IsValidId(id) || (owner.Length != 0 && !IsValidId(owner)) || (row.DefName is not null && !IsValidId((row.DefName ?? ""))) || !thrones.Add(id))
                    {
                        throw Contract("invalid royalty throne");
                    }
                }
                return;
            default:
                throw Contract("missing royalty colony outcome");
        }
    }

    /// <summary>Go's <c>validateFoodChannels</c>.</summary>
    private static void ValidateFoodChannels(ColonyFactsSnapshot v)
    {
        if (v.FoodChannels is null)
        {
            return;
        }
        switch (v.FoodChannels.Outcome)
        {
            case FoodChannelsSectionOutcome.UnavailableCase:
                ValidateUnavailable(v.FoodChannels.Outcome?.Unavailable);
                return;
            case FoodChannelsSectionOutcome.ObservedCase:
                var f = v.FoodChannels.Outcome?.Observed ?? throw Contract("incomplete food channels");
                var mapCells = unchecked((v.MapSize?.Width ?? 0) * (v.MapSize?.Height ?? 0));
                if (f.PollutedCells.GetValueOrDefault() > mapCells)
                {
                    throw Contract("pollution exceeds the map");
                }
                var seen = new HashSet<(string, string)>();
                var pens = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.Grazing)
                {
                    if (row is null
                        || !IsValidId((row.PenId ?? ""))
                        || pens.Contains((row.PenId ?? ""))
                        || !NonNegativeNumber(row.DemandPerDay is not null, row.DemandPerDay.GetValueOrDefault())
                        || !NonNegativeNumber(row.PasturePerDay is not null, row.PasturePerDay.GetValueOrDefault())
                        || !NonNegativeNumber(row.StoredNutrition is not null, row.StoredNutrition.GetValueOrDefault()))
                    {
                        throw Contract("invalid pen grazing forecast");
                    }
                    pens.Add((row.PenId ?? ""));
                }
                var meat = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.Slaughter)
                {
                    if (row is null || !IsValidId((row.PawnId ?? "")) || !IsValidId((row.Race ?? "")) || meat.Contains((row.PawnId ?? "")) || !NonNegativeNumber(row.MeatAmount is not null, row.MeatAmount.GetValueOrDefault()))
                    {
                        throw Contract("invalid slaughter food facts");
                    }
                    meat.Add((row.PawnId ?? ""));
                }
                foreach (var row in f.Gatherable)
                {
                    if (row is null
                        || !IsValidId((row.PawnId ?? ""))
                        || !IsValidId((row.Race ?? ""))
                        || !NonNegativeNumber(row.Fullness is not null, row.Fullness.GetValueOrDefault())
                        || row.Fullness.GetValueOrDefault() > 1
                        || (row.Resource is not null && !IsValidId((row.Resource ?? ""))))
                    {
                        throw Contract("invalid gatherable animal");
                    }
                    if (!seen.Add(((row.PawnId ?? ""), (row.Resource ?? ""))))
                    {
                        throw Contract("duplicate gatherable animal resource");
                    }
                    if (!NonNegativeNumber(row.NutritionPerDay is not null, row.NutritionPerDay.GetValueOrDefault()) || !NonNegativeNumber(row.WorkPerDay is not null, row.WorkPerDay.GetValueOrDefault()) || !NonNegativeNumber(row.LeadDays is not null, row.LeadDays.GetValueOrDefault()))
                    {
                        throw Contract("invalid gatherable production rate");
                    }
                }
                var eggs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.EggLayer)
                {
                    if (row is null
                        || !IsValidId((row.PawnId ?? ""))
                        || !IsValidId((row.Race ?? ""))
                        || eggs.Contains((row.PawnId ?? ""))
                        || !NonNegativeNumber(row.Progress is not null, row.Progress.GetValueOrDefault())
                        || row.Progress.GetValueOrDefault() > 1)
                    {
                        throw Contract("invalid egg layer");
                    }
                    eggs.Add((row.PawnId ?? ""));
                    if (!NonNegativeNumber(row.NutritionPerDay is not null, row.NutritionPerDay.GetValueOrDefault()) || !NonNegativeNumber(row.LeadDays is not null, row.LeadDays.GetValueOrDefault()))
                    {
                        throw Contract("invalid egg production rate");
                    }
                }
                var dispensers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.PasteDispenser)
                {
                    if (row is null
                        || !IsValidId((row.BuildingId ?? ""))
                        || dispensers.Contains((row.BuildingId ?? ""))
                        || !NonNegativeNumber(row.HopperNutrition is not null, row.HopperNutrition.GetValueOrDefault())
                        || (row.AdjacentRoomId is not null && !IsValidId((row.AdjacentRoomId ?? ""))))
                    {
                        throw Contract("invalid paste dispenser");
                    }
                    dispensers.Add((row.BuildingId ?? ""));
                }
                var terrains = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.PastureTerrain)
                {
                    if (row is null || !IsValidId((row.TerrainDef ?? "")) || terrains.Contains((row.TerrainDef ?? "")) || !NonNegativeNumber(row.WorstQuadrumPerDay is not null, row.WorstQuadrumPerDay.GetValueOrDefault()))
                    {
                        throw Contract("invalid pasture terrain");
                    }
                    terrains.Add((row.TerrainDef ?? ""));
                }
                var plants = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.Forage)
                {
                    if (row is null || !IsValidId((row.DefName ?? "")) || plants.Contains((row.DefName ?? "")) || row.GrowingTwelfths.Count > 12)
                    {
                        throw Contract("invalid forage plant");
                    }
                    plants.Add((row.DefName ?? ""));
                    var twelfths = new HashSet<int>();
                    foreach (var t in row.GrowingTwelfths)
                    {
                        if (t is < 0 or > 11 || !twelfths.Add(t))
                        {
                            throw Contract("invalid forage season");
                        }
                    }
                }
                if (f.FishableWater is { } water)
                {
                    ValidateFishableWater(water, v.MapSize);
                }
                return;
            default:
                throw Contract("missing food channels outcome");
        }
    }

    private static void ValidateFishableWater(FishableWater water, MapSize? size)
    {
        if (!NonNegativeNumber(water.ResearchBaseCost is not null, water.ResearchBaseCost.GetValueOrDefault())
            || !NonNegativeNumber(water.ResearchProgress is not null, water.ResearchProgress.GetValueOrDefault())
            || !NonNegativeNumber(water.ResearchCostFactor is not null, water.ResearchCostFactor.GetValueOrDefault())
            || !NonNegativeNumber(water.ResearchPointsPerWorkTick is not null, water.ResearchPointsPerWorkTick.GetValueOrDefault())
            || !NonNegativeNumber(water.ResearchDifficultySpeedFactor is not null, water.ResearchDifficultySpeedFactor.GetValueOrDefault())
            || !NonNegativeNumber(water.BaseFishingDurationTicks is not null, water.BaseFishingDurationTicks.GetValueOrDefault())
            || water.ResearcherSpeeds.Count > 256
            || water.Fishers.Count > 256)
        {
            throw Contract("invalid fishing research");
        }
        if (water.ResearcherSpeeds.Any(speed => !NonNegativeNumber(true, speed)))
        {
            throw Contract("invalid researcher speed");
        }
        if (water.Fishers.Any(fisher => fisher is null || !NonNegativeNumber(fisher.FishingYield is not null, fisher.FishingYield.GetValueOrDefault()) || !NonNegativeNumber(fisher.FishingSpeed is not null, fisher.FishingSpeed.GetValueOrDefault())))
        {
            throw Contract("invalid fisher");
        }
        var mapCells = unchecked((size?.Width ?? 0) * (size?.Height ?? 0));
        var roots = new HashSet<(int, int)>();
        foreach (var row in water.Regions)
        {
            if (row is null
                || !ColonyCell(row.Root, size)
                || !NonNegativeNumber(row.Population is not null, row.Population.GetValueOrDefault())
                || !NonNegativeNumber(row.MaxPopulation is not null, row.MaxPopulation.GetValueOrDefault())
                || (row.Population is not null && row.MaxPopulation is not null && row.Population.GetValueOrDefault() > row.MaxPopulation.GetValueOrDefault())
                || (row.CellCount is not null && (row.CellCount.GetValueOrDefault() == 0 || row.CellCount.GetValueOrDefault() > mapCells)))
            {
                throw Contract("invalid fishable region");
            }
            var root = (row.Root.X.GetValueOrDefault(), row.Root.Z.GetValueOrDefault());
            if (!FiniteNumber(row.NutritionPerFish is not null, row.NutritionPerFish.GetValueOrDefault())
                || !FiniteNumber(row.FishPerBatch is not null, row.FishPerBatch.GetValueOrDefault())
                || !FiniteNumber(row.WorkTicksPerBatch is not null, row.WorkTicksPerBatch.GetValueOrDefault())
                || !NonNegativeNumber(row.YieldCurveValue is not null, row.YieldCurveValue.GetValueOrDefault())
                || !NonNegativeNumber(row.NearestDistanceSquared is not null, row.NearestDistanceSquared.GetValueOrDefault())
                || row.Zones.Count > 256)
            {
                throw Contract("invalid fishing rates or footprint");
            }
            var seenCells = new HashSet<(int, int)>();
            foreach (var fishCell in row.Cells)
            {
                if (fishCell is null || !ColonyCell(fishCell.Cell, size) || !NonNegativeNumber(fishCell.DistanceSquared is not null, fishCell.DistanceSquared.GetValueOrDefault()))
                {
                    throw Contract("invalid fishing cell");
                }
                if (!seenCells.Add((fishCell.Cell.X.GetValueOrDefault(), fishCell.Cell.Z.GetValueOrDefault())))
                {
                    throw Contract("duplicate fishing cell");
                }
            }
            if (!roots.Add(root))
            {
                throw Contract("duplicate fishable region");
            }
        }
    }

    /// <summary>
    /// Go's <c>validateChoiceDialog</c>: admits the colony census's dialog section. Every option carries its own
    /// list position, a bounded valid label, and the selectable and disabled evidence agrees with itself.
    /// </summary>
    private static void ValidateChoiceDialog(ChoiceDialog? v)
    {
        if (v is null)
        {
            return;
        }
        if (v.WindowId is null
            || v.WindowId.GetValueOrDefault() < 0
            || v.WindowType is null
            || !IsValidId((v.WindowType ?? ""))
            || v.Title is null
            || v.Text is null
            || v.Interactive is null
            || v.Options.Count == 0
            )
        {
            throw Contract("invalid choice dialog census");
        }
        for (var i = 0; i < v.Options.Count; i++)
        {
            var option = v.Options[i];
            if (option is null
                || option.Index is null
                || option.Index.GetValueOrDefault() != i
                || option.Label is null
                || !IsValidId((option.Label ?? ""))
                || option.Selectable is null
                || option.Resolves is null
                || (option.Selectable.GetValueOrDefault() && (option.DisabledReason ?? "").Length != 0))
            {
                throw Contract("invalid choice dialog option");
            }
            if (option.Keys.Any(key => !IsValidId(key)))
            {
                throw Contract("invalid choice dialog option key");
            }
        }
    }

    /// <summary>Go's <c>validateJoinerLetters</c>.</summary>
    private static void ValidateJoinerLetters(IEnumerable<JoinerLetter> rows, long tick)
    {
        var seen = new HashSet<int>();
        foreach (var row in rows)
        {
            if (row is null)
            {
                throw Contract("invalid joiner letter census");
            }
            BuildingUnknown(row);
            if (row.LetterId is null
                || row.LetterId.GetValueOrDefault() < 0
                || seen.Contains(row.LetterId.GetValueOrDefault())
                || !IsValidId((row.SnapshotToken ?? ""))
                || !IsValidId((row.PawnId ?? ""))
                || row.ExpiresTick is null
                || (row.ExpiresTick.GetValueOrDefault() >= 0 && row.ExpiresTick.GetValueOrDefault() <= tick)
                || !IsValidId((row.AcceptLabel ?? ""))
                || row.CanAccept is null)
            {
                throw Contract("invalid joiner letter census");
            }
            seen.Add(row.LetterId.GetValueOrDefault());
        }
    }
}
