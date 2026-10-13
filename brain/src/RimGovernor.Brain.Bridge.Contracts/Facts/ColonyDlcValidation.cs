using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go biotech_colony.go, anomaly_colony.go and odyssey_colony.go: the validators of the expansion colony sections.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>badNonNegative</c>: a present number that is not finite and non-negative.</summary>
    private static bool BadNonNegative(bool present, double value) => present && (!double.IsFinite(value) || value < 0);

    private static bool UnitFraction(double value) => !double.IsNaN(value) && value >= 0 && value <= 1;

    /// <summary>
    /// Go's <c>validateBiotechColony</c>: every row names a unique thing on the map, counts are non-negative and a
    /// gestator's bill fields agree. Absent scalars stay unknown.
    /// </summary>
    private static void ValidateBiotechColony(ColonyFactsSnapshot v)
    {
        if (v.Biotech is null)
        {
            return;
        }
        switch (v.Biotech.OutcomeCase)
        {
            case BiotechSection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Biotech.Unavailable);
                return;
            case BiotechSection.OutcomeOneofCase.Observed:
                var f = v.Biotech.Observed ?? throw Contract("incomplete biotech colony facts");
                if (f.Pollution is { } p)
                {
                    if ((p.HasTotalPollution && p.TotalPollution < 0) || (p.HasClearAreaCells && p.ClearAreaCells < 0) || (p.HasClearAreaId && !IsValidId(p.ClearAreaId)))
                    {
                        throw Contract("invalid pollution totals");
                    }
                    if ((p.HasPollutedUncoveredCells && (!p.HasPollutedCells || p.PollutedUncoveredCells > p.PollutedCells))
                        || (p.HasPollutedCells && p.HasPollutableCells && p.PollutedCells > p.PollutableCells))
                    {
                        throw Contract("invalid polluted cell counts");
                    }
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var size = v.MapSize;
                bool Head(bool hasId, string id, bool hasDef, string def, Cell? at) =>
                    hasId && hasDef && IsValidId(id) && IsValidId(def) && ColonyCell(at, size);

                // A thing id is unique within its own table: a building may carry several comps (a polluter that is
                // also a pump), so tables do not share one set.
                bool Unique(string table, string id) => seen.Add(table + "/" + id);
                foreach (var r in f.Polluters)
                {
                    if (r is null || !Head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position) || !Unique("polluter", r.ThingId) || (r.HasCellsPerDay && r.CellsPerDay < 0))
                    {
                        throw Contract("invalid biotech polluter");
                    }
                }
                foreach (var r in f.Wastepacks)
                {
                    if (r is null || !Head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position) || !Unique("wastepack", r.ThingId) || !r.HasCount || r.Count <= 0)
                    {
                        throw Contract("invalid wastepack");
                    }
                }
                foreach (var r in f.Atomizers)
                {
                    if (r is null
                        || !Head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                        || !Unique("atomizer", r.ThingId)
                        || (r.HasSpaceLeft && r.SpaceLeft < 0)
                        || (r.HasTicksLeft && r.TicksLeft < 0)
                        || (r.HasFillPercent && !UnitFraction(r.FillPercent)))
                    {
                        throw Contract("invalid wastepack atomizer");
                    }
                }
                foreach (var r in f.Pumps)
                {
                    if (r is null || !Head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position) || !Unique("pump", r.ThingId))
                    {
                        throw Contract("invalid pollution pump");
                    }
                }
                foreach (var r in f.Gestators)
                {
                    if (r is null
                        || !Head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                        || !Unique("gestator", r.ThingId)
                        || (r.HasWasteCount && r.WasteCount < 0)
                        || (r.HasCyclesCompleted && r.CyclesCompleted < 0)
                        || (r.HasBandwidthCost && (!double.IsFinite(r.BandwidthCost) || r.BandwidthCost < 0))
                        || (r.HasFormingPercent && !UnitFraction(r.FormingPercent)))
                    {
                        throw Contract("invalid mech gestator");
                    }
                    if ((r.HasBillId && !IsValidId(r.BillId))
                        || (r.HasState && !IsValidId(r.State))
                        || (r.HasMechKind && !IsValidId(r.MechKind))
                        || (r.HasBoundPawnId && !IsValidId(r.BoundPawnId)))
                    {
                        throw Contract("invalid mech gestator bill");
                    }
                    if (!r.HasBillId && (r.HasState || r.HasMechKind || r.HasBoundPawnId || r.HasCyclesCompleted))
                    {
                        throw Contract("gestator bill fields without a bill");
                    }
                }
                foreach (var r in f.Chargers)
                {
                    if (r is null
                        || !Head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                        || !Unique("charger", r.ThingId)
                        || (r.HasWasteCount && r.WasteCount < 0)
                        || (r.HasChargingMechId && !IsValidId(r.ChargingMechId)))
                    {
                        throw Contract("invalid mech charger");
                    }
                }
                var babies = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in f.Babies)
                {
                    if (r is null || !r.HasPawnId || !IsValidId(r.PawnId) || babies.Contains(r.PawnId))
                    {
                        throw Contract("invalid baby care row");
                    }
                    babies.Add(r.PawnId);
                    var feeders = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var a in r.Autofeeders)
                    {
                        if (a is null || !IsValidId(a.PawnId) || !IsValidId(a.Mode) || feeders.Contains(a.PawnId))
                        {
                            throw Contract("invalid baby autofeeder");
                        }
                        feeders.Add(a.PawnId);
                    }
                }
                var breastfeeders = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in f.Breastfeeders)
                {
                    if (!IsValidId(id) || !breastfeeders.Add(id))
                    {
                        throw Contract("invalid breastfeeder");
                    }
                }
                ValidateGeneBuilding(f, Head, Unique);
                return;
            default:
                throw Contract("missing biotech colony outcome");
        }
    }

    /// <summary>
    /// Go's <c>validateGeneBuilding</c>: the gene-building rows have unique ids per table, non-negative counts and
    /// ticks, distinct valid gene and pack ids, and a genepack that is in exactly one place (a listed bank, or a
    /// cell on the map).
    /// </summary>
    private static void ValidateGeneBuilding(
        BiotechColonyFacts f,
        Func<bool, string, bool, string, Cell?, bool> head,
        Func<string, string, bool> unique)
    {
        static bool NonNegative(bool present, int value) => !present || value >= 0;
        static bool FiniteNonNegative(bool present, double value) => !present || (double.IsFinite(value) && value >= 0);
        var banks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in f.GeneBanks)
        {
            if (r is null
                || !head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                || !unique("gene_bank", r.ThingId)
                || !NonNegative(r.HasCapacity, r.Capacity)
                || !ValidIds(r.PackIds, true)
                || (r.HasCapacity && r.PackIds.Count > r.Capacity))
            {
                throw Contract("invalid gene bank");
            }
            banks.Add(r.ThingId);
        }
        foreach (var r in f.GeneAssemblers)
        {
            if (r is null
                || !head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                || !unique("gene_assembler", r.ThingId)
                || !NonNegative(r.HasArchitesOwed, r.ArchitesOwed)
                || !NonNegative(r.HasMaxComplexity, r.MaxComplexity)
                || !FiniteNonNegative(r.HasProgress, r.Progress)
                || !FiniteNonNegative(r.HasTotalWork, r.TotalWork)
                || !ValidIds(r.PackIds, true)
                || !ValidIds(r.LinkedBankIds, true))
            {
                throw Contract("invalid gene assembler");
            }
        }
        foreach (var r in f.GeneExtractors)
        {
            if (r is null
                || !head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                || !unique("gene_extractor", r.ThingId)
                || !NonNegative(r.HasTicksRemaining, r.TicksRemaining)
                || !NonNegative(r.HasPowerCutTicks, r.PowerCutTicks)
                || (r.HasSelectedPawnId && !IsValidId(r.SelectedPawnId))
                || (r.HasOccupantId && !IsValidId(r.OccupantId)))
            {
                throw Contract("invalid gene extractor");
            }
        }
        foreach (var r in f.Genepacks)
        {
            if (r is null
                || !r.HasThingId
                || !r.HasDefName
                || !IsValidId(r.ThingId)
                || !IsValidId(r.DefName)
                || !unique("genepack", r.ThingId)
                || !ValidIds(r.Genes, true)
                || !NonNegative(r.HasHitPoints, r.HitPoints)
                || !NonNegative(r.HasComplexity, r.Complexity)
                || !NonNegative(r.HasArchites, r.Archites))
            {
                throw Contract("invalid genepack");
            }
            if ((!r.HasBankId) == (r.Position is null) || (r.HasBankId && !banks.Contains(r.BankId)))
            {
                throw Contract("genepack is not in exactly one place");
            }
        }
        foreach (var r in f.Xenogerms)
        {
            if (r is null
                || !head(r.HasThingId, r.ThingId, r.HasDefName, r.DefName, r.Position)
                || !unique("xenogerm", r.ThingId)
                || !ValidIds(r.Genes, true)
                || !NonNegative(r.HasComplexity, r.Complexity)
                || !NonNegative(r.HasArchites, r.Archites)
                || (r.HasTargetPawnId && !IsValidId(r.TargetPawnId)))
            {
                throw Contract("invalid xenogerm");
            }
            var pawns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in r.ImplantMetabolism)
            {
                if (m is null || !m.HasPawnId || !m.HasMetabolismAfter || !IsValidId(m.PawnId) || !pawns.Add(m.PawnId))
                {
                    throw Contract("invalid xenogerm implant metabolism");
                }
            }
        }
    }

    /// <summary>
    /// Go's <c>validateAnomalyColony</c>: names and ids are unique per table, a category's discovered count never
    /// exceeds its entries, knowledge and the threat fraction are finite and non-negative and levels are
    /// non-negative. Absent scalars stay unknown.
    /// </summary>
    private static void ValidateAnomalyColony(ColonyFactsSnapshot v)
    {
        if (v.Anomaly is null)
        {
            return;
        }
        switch (v.Anomaly.OutcomeCase)
        {
            case AnomalySection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Anomaly.Unavailable);
                return;
            case AnomalySection.OutcomeOneofCase.Observed:
                var f = v.Anomaly.Observed ?? throw Contract("incomplete anomaly colony facts");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in f.Knowledge)
                {
                    if (r is null
                        || !IsValidId(r.Category)
                        || seen.Contains("k" + r.Category)
                        || (r.HasCurrentProject && !IsValidId(r.CurrentProject))
                        || BadNonNegative(r.HasKnowledge, r.Knowledge)
                        || (!r.HasCurrentProject && r.HasKnowledge))
                    {
                        throw Contract("invalid anomaly knowledge category");
                    }
                    seen.Add("k" + r.Category);
                }
                foreach (var r in f.Codex)
                {
                    if (r is null || !IsValidId(r.Category) || seen.Contains("c" + r.Category) || !r.HasEntries || !r.HasDiscovered || r.Discovered > r.Entries)
                    {
                        throw Contract("invalid anomaly codex category");
                    }
                    seen.Add("c" + r.Category);
                }
                foreach (var name in f.DiscoveredEntries)
                {
                    if (!IsValidId(name) || seen.Contains("e" + name))
                    {
                        throw Contract("invalid anomaly discovered codex entry");
                    }
                    seen.Add("e" + name);
                }
                foreach (var r in f.HeldEntities)
                {
                    if (r is null || !IsValidId(r.PawnId) || seen.Contains("p" + r.PawnId) || !IsValidId(r.PlatformId) || seen.Contains("h" + r.PlatformId))
                    {
                        throw Contract("invalid anomaly held entity");
                    }
                    seen.Add("p" + r.PawnId);
                    seen.Add("h" + r.PlatformId);
                }
                if (f.Incidents is { } i
                    && ((i.HasLevel && i.Level < 0)
                        || (i.HasHighestLevelReached && i.HighestLevelReached < 0)
                        || (i.HasTicksSinceLevelChange && i.TicksSinceLevelChange < 0)
                        || (i.HasLevelDef && !IsValidId(i.LevelDef))
                        || BadNonNegative(i.HasAnomalyThreatFractionNow, i.AnomalyThreatFractionNow)))
                {
                    throw Contract("invalid anomaly incident state");
                }
                if (f.Monolith is { } m)
                {
                    ValidateMonolith(m);
                }
                return;
            default:
                throw Contract("missing anomaly colony outcome");
        }
    }

    private static void ValidateMonolith(MonolithState m)
    {
        var seenCondition = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in m.BlockingConditions)
        {
            if (!IsValidId(name) || !seenCondition.Add(name))
            {
                throw Contract("invalid anomaly monolith blocking condition");
            }
        }
        var seenId = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in new[] { m.VoidNodeId }.Concat(m.PendingVoidStructureIds).Concat(m.VoidNodePawnIds))
        {
            if (id.Length != 0 && (!IsValidId(id) || seenId.Contains(id)))
            {
                throw Contract("invalid anomaly monolith void thing");
            }
            seenId.Add(id);
        }
        if ((m.HasVoidNodeId && m.VoidNodeId.Length == 0)
            || (m.VoidNodePawnIds.Count > 0 && !m.HasVoidNodeId)
            || (m.VoidNodeId.Length != 0 && m.HasVoidNodeExists && !m.VoidNodeExists))
        {
            throw Contract("invalid anomaly monolith void node");
        }
        if ((m.HasMonolithId && !IsValidId(m.MonolithId))
            || (m.HasNextLevelDef && !IsValidId(m.NextLevelDef))
            || (m.HasNextLevelCodexCategory && !IsValidId(m.NextLevelCodexCategory))
            || m.HasNextLevelCodexCategory != m.HasNextLevelCodexRequired
            || m.HasNextLevelCodexCategory != m.HasCodexShortfall
            || (m.HasCodexShortfall && m.CodexShortfall > m.NextLevelCodexRequired)
            || (m.HasVoidStructuresActivated && m.VoidStructuresActivated > m.VoidStructures)
            || (m.HasVoidAwakeningStage && m.VoidAwakeningStage < 0))
        {
            throw Contract("invalid anomaly monolith state");
        }
    }

    /// <summary>
    /// Go's <c>validateOdysseyColony</c>: ids are unique per table, cells lie on the map, counts and ticks are
    /// non-negative and a hackable's progress is a fraction. Absent scalars stay unknown.
    /// </summary>
    private static void ValidateOdysseyColony(ColonyFactsSnapshot v)
    {
        if (v.Odyssey is null)
        {
            return;
        }
        switch (v.Odyssey.OutcomeCase)
        {
            case OdysseySection.OutcomeOneofCase.Unavailable:
                ValidateUnavailable(v.Odyssey.Unavailable);
                return;
            case OdysseySection.OutcomeOneofCase.Observed:
                var f = v.Odyssey.Observed ?? throw Contract("incomplete odyssey colony facts");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in f.Conditions)
                {
                    if (r is null
                        || !IsValidId(r.ConditionId)
                        || ids.Contains("c" + r.ConditionId)
                        || !IsValidId(r.DefName)
                        || !IsValidId(r.ConditionClass)
                        || (r.HasTicksPassed && r.TicksPassed < 0)
                        || (r.HasTicksLeft && r.TicksLeft < 0)
                        || !r.HasPermanent
                        || (r.Permanent && r.HasTicksLeft)
                        || (r.HasCauserId && !IsValidId(r.CauserId)))
                    {
                        throw Contract("invalid odyssey game condition");
                    }
                    ids.Add("c" + r.ConditionId);
                }
                foreach (var r in f.HazardTerrain)
                {
                    if (r is null
                        || !IsValidId(r.DefName)
                        || ids.Contains("t" + r.DefName)
                        || !r.HasCells
                        || r.Cells == 0
                        || (r.HasBurnDamage && r.BurnDamage < 0)
                        || BadNonNegative(r.HasHeatPerTick, r.HeatPerTick)
                        || BadNonNegative(r.HasToxicBuildupFactor, r.ToxicBuildupFactor))
                    {
                        throw Contract("invalid odyssey hazard terrain");
                    }
                    ids.Add("t" + r.DefName);
                }
                foreach (var r in f.LavaEmergences)
                {
                    if (r is null || !IsValidId(r.ThingId) || ids.Contains("l" + r.ThingId) || !ColonyCell(r.Position, v.MapSize))
                    {
                        throw Contract("invalid odyssey lava emergence");
                    }
                    ids.Add("l" + r.ThingId);
                }
                var maps = new HashSet<int>();
                foreach (var r in f.Sites)
                {
                    if (r is null
                        || !IsValidId(r.HatchId)
                        || ids.Contains("h" + r.HatchId)
                        || !r.HasPocketMapId
                        || maps.Contains(r.PocketMapId)
                        || (r.HasStockpileType && !IsValidId(r.StockpileType))
                        || (r.HasLayout && !IsValidId(r.Layout)))
                    {
                        throw Contract("invalid odyssey underground site");
                    }
                    ids.Add("h" + r.HatchId);
                    maps.Add(r.PocketMapId);
                    var hacks = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var h in r.Hackables)
                    {
                        if (h is null
                            || !IsValidId(h.ThingId)
                            || hacks.Contains(h.ThingId)
                            || !IsValidId(h.DefName)
                            || h.Position is null
                            || (h.HasProgressPercent && !UnitFraction(h.ProgressPercent))
                            || BadNonNegative(h.HasDefence, h.Defence))
                        {
                            throw Contract("invalid odyssey hackable");
                        }
                        hacks.Add(h.ThingId);
                    }
                }
                return;
            default:
                throw Contract("missing odyssey colony outcome");
        }
    }
}
