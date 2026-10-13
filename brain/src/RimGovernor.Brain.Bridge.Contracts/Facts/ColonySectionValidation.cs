using RimGovernor.Protocol.Observations;

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
        switch (v.EventLoot.OutcomeCase)
        {
            case LootSection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.EventLoot.Unavailable);
                return;
            case LootSection.OutcomeOneofCase.Observed:
                if (v.EventLoot.Observed is null)
                {
                    throw Contract("invalid event loot census");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in v.EventLoot.Observed.Items)
                {
                    if (row is null || row.Item is null || !row.HasForbidden)
                    {
                        throw Contract("incomplete event loot item");
                    }
                    if (!ValidRef(row.Item) || seen.Contains(row.Item.Id))
                    {
                        throw Contract("invalid event loot item");
                    }
                    seen.Add(row.Item.Id);
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
        switch (v.DeepResources.OutcomeCase)
        {
            case DeepResourcesSection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.DeepResources.Unavailable);
                return;
            case DeepResourcesSection.OutcomeOneofCase.Observed:
                var f = v.DeepResources.Observed;
                if (f is null)
                {
                    throw Contract("deep resource census exceeds bound");
                }
                var centres = new HashSet<(int, int)>();
                ulong cells = 0;
                foreach (var row in f.Lumps)
                {
                    if (row is null
                        || !IsValidId(row.DefName)
                        || !row.HasCount
                        || row.Count <= 0
                        || !row.HasCellCount
                        || row.CellCount == 0
                        || !ColonyCell(row.Centre, v.MapSize))
                    {
                        throw Contract("invalid deep resource lump");
                    }
                    var key = (row.Centre.X, row.Centre.Z);
                    if (centres.Contains(key) || row.Count < (long)row.CellCount || row.Count > (long)row.CellCount * 65535)
                    {
                        throw Contract("invalid deep resource aggregation");
                    }
                    centres.Add(key);
                    cells += row.CellCount;
                }
                if (cells > (ulong)v.MapSize!.Width * v.MapSize.Height)
                {
                    throw Contract("deep resource cells exceed map");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rows in new[] { f.GroundScanners, f.LongRangeScanners })
                {
                    foreach (var row in rows)
                    {
                        if (row is null
                            || !IsValidId(row.BuildingId)
                            || !IsValidId(row.DefName)
                            || seen.Contains(row.BuildingId)
                            || !ColonyCell(row.Position, v.MapSize)
                            || !row.Built
                            || row.TicksToNextFind < 0
                            || (row.HasTargetMineable && !IsValidId(row.TargetMineable)))
                        {
                            throw Contract("invalid mineral scanner");
                        }
                        seen.Add(row.BuildingId);
                    }
                }

                // A depleted drill carries no deposit; an undepleted one names its exact resource and a positive
                // remainder. Ownership and designation are always stated.
                foreach (var row in f.Drills)
                {
                    if (row is null
                        || !IsValidId(row.BuildingId)
                        || !IsValidId(row.DefName)
                        || seen.Contains(row.BuildingId)
                        || !ColonyCell(row.Position, v.MapSize)
                        || !row.HasDepleted
                        || !row.HasDesignated
                        || !row.HasPowered)
                    {
                        throw Contract("invalid deep drill");
                    }
                    if ((row.Depleted && (row.HasResource || row.HasRemaining))
                        || (!row.Depleted && (!IsValidId(row.Resource) || !row.HasRemaining || row.Remaining <= 0)))
                    {
                        throw Contract("invalid deep drill deposit");
                    }
                    seen.Add(row.BuildingId);
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
        switch (v.Policies.OutcomeCase)
        {
            case PolicySection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Policies.Unavailable);
                return;
            case PolicySection.OutcomeOneofCase.Observed:
                var f = v.Policies.Observed;
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
                        if (row is null || !IsValidId(row.Id) || ids.Contains(row.Id) || !row.HasLabel || !Diagnostic(row.HasLabel, row.Label) || !row.HasDefault)
                        {
                            throw Contract("invalid policy entry");
                        }
                        ids.Add(row.Id);
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
                                || !IsValidId(e.DrugDef)
                                || drugs.Contains(e.DrugDef)
                                || !e.HasAllowedForJoy
                                || !e.HasAllowedForAddiction
                                || !e.HasAllowScheduled
                                || !e.HasDaysFrequency
                                || !e.HasOnlyIfMoodBelow
                                || !e.HasOnlyIfJoyBelow
                                || !e.HasTakeToInventory)
                            {
                                throw Contract("invalid drug policy entry");
                            }
                            drugs.Add(e.DrugDef);
                        }
                        if (row.Default)
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
                    if (row is null || !IsValidId(row.Id) || areaIds.Contains(row.Id) || !row.HasLabel || !Diagnostic(row.HasLabel, row.Label))
                    {
                        throw Contract("invalid allowed area");
                    }
                    areaIds.Add(row.Id);
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
        switch (v.Royalty.OutcomeCase)
        {
            case RoyaltySection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Royalty.Unavailable);
                return;
            case RoyaltySection.OutcomeOneofCase.Observed:
                var facts = v.Royalty.Observed ?? throw Contract("incomplete royalty colony facts");
                var neuroformers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in facts.Neuroformers)
                {
                    if (!IsValidId(row.DefName) || row.Held < 0)
                    {
                        throw Contract("invalid royalty neuroformer");
                    }
                    if (!neuroformers.Add(row.DefName))
                    {
                        throw Contract($"duplicate royalty neuroformer {row.DefName}");
                    }
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in facts.Ceremonies)
                {
                    var pawn = row.Pawn?.Id ?? "";
                    var bestower = row.Bestower?.Id ?? "";
                    var key = pawn + "/" + row.FactionDef;
                    if (!IsValidId(pawn)
                        || (bestower.Length != 0 && !IsValidId(bestower))
                        || !IsValidId(row.Quest)
                        || (row.HasFactionDef && !IsValidId(row.FactionDef))
                        || (row.HasTitle && !IsValidId(row.Title))
                        || seen.Contains(key))
                    {
                        throw Contract("invalid royalty ceremony");
                    }
                    seen.Add(key);
                    if (row.Attendees.Any(a => !IsValidId(a.Id)))
                    {
                        throw Contract("invalid royalty ceremony attendee");
                    }
                }
                var thrones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in facts.Thrones)
                {
                    var id = row.Thing?.Id ?? "";
                    var owner = row.Owner?.Id ?? "";
                    if (!IsValidId(id) || (owner.Length != 0 && !IsValidId(owner)) || (row.HasDefName && !IsValidId(row.DefName)) || !thrones.Add(id))
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
        switch (v.FoodChannels.OutcomeCase)
        {
            case FoodChannelsSection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.FoodChannels.Unavailable);
                return;
            case FoodChannelsSection.OutcomeOneofCase.Observed:
                var f = v.FoodChannels.Observed ?? throw Contract("incomplete food channels");
                var mapCells = unchecked((v.MapSize?.Width ?? 0) * (v.MapSize?.Height ?? 0));
                if (f.PollutedCells > mapCells)
                {
                    throw Contract("pollution exceeds the map");
                }
                var seen = new HashSet<(string, string)>();
                var pens = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.Grazing)
                {
                    if (row is null
                        || !IsValidId(row.PenId)
                        || pens.Contains(row.PenId)
                        || !NonNegativeNumber(row.HasDemandPerDay, row.DemandPerDay)
                        || !NonNegativeNumber(row.HasPasturePerDay, row.PasturePerDay)
                        || !NonNegativeNumber(row.HasStoredNutrition, row.StoredNutrition))
                    {
                        throw Contract("invalid pen grazing forecast");
                    }
                    pens.Add(row.PenId);
                }
                var meat = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.Slaughter)
                {
                    if (row is null || !IsValidId(row.PawnId) || !IsValidId(row.Race) || meat.Contains(row.PawnId) || !NonNegativeNumber(row.HasMeatAmount, row.MeatAmount))
                    {
                        throw Contract("invalid slaughter food facts");
                    }
                    meat.Add(row.PawnId);
                }
                foreach (var row in f.Gatherable)
                {
                    if (row is null
                        || !IsValidId(row.PawnId)
                        || !IsValidId(row.Race)
                        || !NonNegativeNumber(row.HasFullness, row.Fullness)
                        || row.Fullness > 1
                        || (row.HasResource && !IsValidId(row.Resource)))
                    {
                        throw Contract("invalid gatherable animal");
                    }
                    if (!seen.Add((row.PawnId, row.Resource)))
                    {
                        throw Contract("duplicate gatherable animal resource");
                    }
                    if (!NonNegativeNumber(row.HasNutritionPerDay, row.NutritionPerDay) || !NonNegativeNumber(row.HasWorkPerDay, row.WorkPerDay) || !NonNegativeNumber(row.HasLeadDays, row.LeadDays))
                    {
                        throw Contract("invalid gatherable production rate");
                    }
                }
                var eggs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.EggLayer)
                {
                    if (row is null
                        || !IsValidId(row.PawnId)
                        || !IsValidId(row.Race)
                        || eggs.Contains(row.PawnId)
                        || !NonNegativeNumber(row.HasProgress, row.Progress)
                        || row.Progress > 1)
                    {
                        throw Contract("invalid egg layer");
                    }
                    eggs.Add(row.PawnId);
                    if (!NonNegativeNumber(row.HasNutritionPerDay, row.NutritionPerDay) || !NonNegativeNumber(row.HasLeadDays, row.LeadDays))
                    {
                        throw Contract("invalid egg production rate");
                    }
                }
                var dispensers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.PasteDispenser)
                {
                    if (row is null
                        || !IsValidId(row.BuildingId)
                        || dispensers.Contains(row.BuildingId)
                        || !NonNegativeNumber(row.HasHopperNutrition, row.HopperNutrition)
                        || (row.HasAdjacentRoomId && !IsValidId(row.AdjacentRoomId)))
                    {
                        throw Contract("invalid paste dispenser");
                    }
                    dispensers.Add(row.BuildingId);
                }
                var terrains = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.PastureTerrain)
                {
                    if (row is null || !IsValidId(row.TerrainDef) || terrains.Contains(row.TerrainDef) || !NonNegativeNumber(row.HasWorstQuadrumPerDay, row.WorstQuadrumPerDay))
                    {
                        throw Contract("invalid pasture terrain");
                    }
                    terrains.Add(row.TerrainDef);
                }
                var plants = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in f.Forage)
                {
                    if (row is null || !IsValidId(row.DefName) || plants.Contains(row.DefName) || row.GrowingTwelfths.Count > 12)
                    {
                        throw Contract("invalid forage plant");
                    }
                    plants.Add(row.DefName);
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
        if (!NonNegativeNumber(water.HasResearchBaseCost, water.ResearchBaseCost)
            || !NonNegativeNumber(water.HasResearchProgress, water.ResearchProgress)
            || !NonNegativeNumber(water.HasResearchCostFactor, water.ResearchCostFactor)
            || !NonNegativeNumber(water.HasResearchPointsPerWorkTick, water.ResearchPointsPerWorkTick)
            || !NonNegativeNumber(water.HasResearchDifficultySpeedFactor, water.ResearchDifficultySpeedFactor)
            || !NonNegativeNumber(water.HasBaseFishingDurationTicks, water.BaseFishingDurationTicks)
            || water.ResearcherSpeeds.Count > 256
            || water.Fishers.Count > 256)
        {
            throw Contract("invalid fishing research");
        }
        if (water.ResearcherSpeeds.Any(speed => !NonNegativeNumber(true, speed)))
        {
            throw Contract("invalid researcher speed");
        }
        if (water.Fishers.Any(fisher => fisher is null || !NonNegativeNumber(fisher.HasFishingYield, fisher.FishingYield) || !NonNegativeNumber(fisher.HasFishingSpeed, fisher.FishingSpeed)))
        {
            throw Contract("invalid fisher");
        }
        var mapCells = unchecked((size?.Width ?? 0) * (size?.Height ?? 0));
        var roots = new HashSet<(int, int)>();
        foreach (var row in water.Regions)
        {
            if (row is null
                || !ColonyCell(row.Root, size)
                || !NonNegativeNumber(row.HasPopulation, row.Population)
                || !NonNegativeNumber(row.HasMaxPopulation, row.MaxPopulation)
                || (row.HasPopulation && row.HasMaxPopulation && row.Population > row.MaxPopulation)
                || (row.HasCellCount && (row.CellCount == 0 || row.CellCount > mapCells)))
            {
                throw Contract("invalid fishable region");
            }
            var root = (row.Root.X, row.Root.Z);
            if (!FiniteNumber(row.HasNutritionPerFish, row.NutritionPerFish)
                || !FiniteNumber(row.HasFishPerBatch, row.FishPerBatch)
                || !FiniteNumber(row.HasWorkTicksPerBatch, row.WorkTicksPerBatch)
                || !NonNegativeNumber(row.HasYieldCurveValue, row.YieldCurveValue)
                || !NonNegativeNumber(row.HasNearestDistanceSquared, row.NearestDistanceSquared)
                || row.Zones.Count > 256)
            {
                throw Contract("invalid fishing rates or footprint");
            }
            var seenCells = new HashSet<(int, int)>();
            foreach (var fishCell in row.Cells)
            {
                if (fishCell is null || !ColonyCell(fishCell.Cell, size) || !NonNegativeNumber(fishCell.HasDistanceSquared, fishCell.DistanceSquared))
                {
                    throw Contract("invalid fishing cell");
                }
                if (!seenCells.Add((fishCell.Cell.X, fishCell.Cell.Z)))
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
        if (!v.HasWindowId
            || v.WindowId < 0
            || !v.HasWindowType
            || !IsValidId(v.WindowType)
            || !v.HasTitle
            || !v.HasText
            || !v.HasInteractive
            || v.Options.Count == 0
            || UnknownFields.Present(v))
        {
            throw Contract("invalid choice dialog census");
        }
        for (var i = 0; i < v.Options.Count; i++)
        {
            var option = v.Options[i];
            if (option is null
                || !option.HasIndex
                || option.Index != i
                || !option.HasLabel
                || !IsValidId(option.Label)
                || !option.HasSelectable
                || !option.HasResolves
                || (option.Selectable && option.DisabledReason.Length != 0))
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
            if (!row.HasLetterId
                || row.LetterId < 0
                || seen.Contains(row.LetterId)
                || !IsValidId(row.SnapshotToken)
                || !IsValidId(row.PawnId)
                || !row.HasExpiresTick
                || (row.ExpiresTick >= 0 && row.ExpiresTick <= tick)
                || !IsValidId(row.AcceptLabel)
                || !row.HasCanAccept)
            {
                throw Contract("invalid joiner letter census");
            }
            seen.Add(row.LetterId);
        }
    }
}
