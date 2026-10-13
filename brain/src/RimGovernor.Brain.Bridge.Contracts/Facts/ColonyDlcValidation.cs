using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

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
        switch (v.Biotech.Outcome)
        {
            case BiotechSectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Biotech.Outcome?.Unavailable);
                return;
            case BiotechSectionOutcome.ObservedCase:
                var f = v.Biotech.Outcome?.Observed ?? throw Contract("incomplete biotech colony facts");
                if (f.Pollution is { } p)
                {
                    if ((p.TotalPollution is not null && p.TotalPollution.GetValueOrDefault() < 0) || (p.ClearAreaCells is not null && p.ClearAreaCells.GetValueOrDefault() < 0) || (p.ClearAreaId is not null && !IsValidId((p.ClearAreaId ?? ""))))
                    {
                        throw Contract("invalid pollution totals");
                    }
                    if ((p.PollutedUncoveredCells is not null && (p.PollutedCells is null || p.PollutedUncoveredCells.GetValueOrDefault() > p.PollutedCells.GetValueOrDefault()))
                        || (p.PollutedCells is not null && p.PollutableCells is not null && p.PollutedCells.GetValueOrDefault() > p.PollutableCells.GetValueOrDefault()))
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
                    if (r is null || !Head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position) || !Unique("polluter", (r.ThingId ?? "")) || (r.CellsPerDay is not null && r.CellsPerDay.GetValueOrDefault() < 0))
                    {
                        throw Contract("invalid biotech polluter");
                    }
                }
                foreach (var r in f.Wastepacks)
                {
                    if (r is null || !Head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position) || !Unique("wastepack", (r.ThingId ?? "")) || r.Count is null || r.Count.GetValueOrDefault() <= 0)
                    {
                        throw Contract("invalid wastepack");
                    }
                }
                foreach (var r in f.Atomizers)
                {
                    if (r is null
                        || !Head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                        || !Unique("atomizer", (r.ThingId ?? ""))
                        || (r.SpaceLeft is not null && r.SpaceLeft.GetValueOrDefault() < 0)
                        || (r.TicksLeft is not null && r.TicksLeft.GetValueOrDefault() < 0)
                        || (r.FillPercent is not null && !UnitFraction(r.FillPercent.GetValueOrDefault())))
                    {
                        throw Contract("invalid wastepack atomizer");
                    }
                }
                foreach (var r in f.Pumps)
                {
                    if (r is null || !Head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position) || !Unique("pump", (r.ThingId ?? "")))
                    {
                        throw Contract("invalid pollution pump");
                    }
                }
                foreach (var r in f.Gestators)
                {
                    if (r is null
                        || !Head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                        || !Unique("gestator", (r.ThingId ?? ""))
                        || (r.WasteCount is not null && r.WasteCount.GetValueOrDefault() < 0)
                        || (r.CyclesCompleted is not null && r.CyclesCompleted.GetValueOrDefault() < 0)
                        || (r.BandwidthCost is not null && (!double.IsFinite(r.BandwidthCost.GetValueOrDefault()) || r.BandwidthCost.GetValueOrDefault() < 0))
                        || (r.FormingPercent is not null && !UnitFraction(r.FormingPercent.GetValueOrDefault())))
                    {
                        throw Contract("invalid mech gestator");
                    }
                    if ((r.BillId is not null && !IsValidId((r.BillId ?? "")))
                        || (r.State is not null && !IsValidId((r.State ?? "")))
                        || (r.MechKind is not null && !IsValidId((r.MechKind ?? "")))
                        || (r.BoundPawnId is not null && !IsValidId((r.BoundPawnId ?? ""))))
                    {
                        throw Contract("invalid mech gestator bill");
                    }
                    if (r.BillId is null && (r.State is not null || r.MechKind is not null || r.BoundPawnId is not null || r.CyclesCompleted is not null))
                    {
                        throw Contract("gestator bill fields without a bill");
                    }
                }
                foreach (var r in f.Chargers)
                {
                    if (r is null
                        || !Head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                        || !Unique("charger", (r.ThingId ?? ""))
                        || (r.WasteCount is not null && r.WasteCount.GetValueOrDefault() < 0)
                        || (r.ChargingMechId is not null && !IsValidId((r.ChargingMechId ?? ""))))
                    {
                        throw Contract("invalid mech charger");
                    }
                }
                var babies = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in f.Babies)
                {
                    if (r is null || r.PawnId is null || !IsValidId((r.PawnId ?? "")) || babies.Contains((r.PawnId ?? "")))
                    {
                        throw Contract("invalid baby care row");
                    }
                    babies.Add((r.PawnId ?? ""));
                    var feeders = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var a in r.Autofeeders)
                    {
                        if (a is null || !IsValidId((a.PawnId ?? "")) || !IsValidId((a.Mode ?? "")) || feeders.Contains((a.PawnId ?? "")))
                        {
                            throw Contract("invalid baby autofeeder");
                        }
                        feeders.Add((a.PawnId ?? ""));
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
                || !head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                || !unique("gene_bank", (r.ThingId ?? ""))
                || !NonNegative(r.Capacity is not null, r.Capacity.GetValueOrDefault())
                || !ValidIds(r.PackIds, true)
                || (r.Capacity is not null && r.PackIds.Count > r.Capacity.GetValueOrDefault()))
            {
                throw Contract("invalid gene bank");
            }
            banks.Add((r.ThingId ?? ""));
        }
        foreach (var r in f.GeneAssemblers)
        {
            if (r is null
                || !head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                || !unique("gene_assembler", (r.ThingId ?? ""))
                || !NonNegative(r.ArchitesOwed is not null, r.ArchitesOwed.GetValueOrDefault())
                || !NonNegative(r.MaxComplexity is not null, r.MaxComplexity.GetValueOrDefault())
                || !FiniteNonNegative(r.Progress is not null, r.Progress.GetValueOrDefault())
                || !FiniteNonNegative(r.TotalWork is not null, r.TotalWork.GetValueOrDefault())
                || !ValidIds(r.PackIds, true)
                || !ValidIds(r.LinkedBankIds, true))
            {
                throw Contract("invalid gene assembler");
            }
        }
        foreach (var r in f.GeneExtractors)
        {
            if (r is null
                || !head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                || !unique("gene_extractor", (r.ThingId ?? ""))
                || !NonNegative(r.TicksRemaining is not null, r.TicksRemaining.GetValueOrDefault())
                || !NonNegative(r.PowerCutTicks is not null, r.PowerCutTicks.GetValueOrDefault())
                || (r.SelectedPawnId is not null && !IsValidId((r.SelectedPawnId ?? "")))
                || (r.OccupantId is not null && !IsValidId((r.OccupantId ?? ""))))
            {
                throw Contract("invalid gene extractor");
            }
        }
        foreach (var r in f.Genepacks)
        {
            if (r is null
                || r.ThingId is null
                || r.DefName is null
                || !IsValidId((r.ThingId ?? ""))
                || !IsValidId((r.DefName ?? ""))
                || !unique("genepack", (r.ThingId ?? ""))
                || !ValidIds(r.Genes, true)
                || !NonNegative(r.HitPoints is not null, r.HitPoints.GetValueOrDefault())
                || !NonNegative(r.Complexity is not null, r.Complexity.GetValueOrDefault())
                || !NonNegative(r.Archites is not null, r.Archites.GetValueOrDefault()))
            {
                throw Contract("invalid genepack");
            }
            if ((r.BankId is null) == (r.Position is null) || (r.BankId is not null && !banks.Contains((r.BankId ?? ""))))
            {
                throw Contract("genepack is not in exactly one place");
            }
        }
        foreach (var r in f.Xenogerms)
        {
            if (r is null
                || !head(r.ThingId is not null, (r.ThingId ?? ""), r.DefName is not null, (r.DefName ?? ""), r.Position)
                || !unique("xenogerm", (r.ThingId ?? ""))
                || !ValidIds(r.Genes, true)
                || !NonNegative(r.Complexity is not null, r.Complexity.GetValueOrDefault())
                || !NonNegative(r.Archites is not null, r.Archites.GetValueOrDefault())
                || (r.TargetPawnId is not null && !IsValidId((r.TargetPawnId ?? ""))))
            {
                throw Contract("invalid xenogerm");
            }
            var pawns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in r.ImplantMetabolism)
            {
                if (m is null || m.PawnId is null || m.MetabolismAfter is null || !IsValidId((m.PawnId ?? "")) || !pawns.Add((m.PawnId ?? "")))
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
        switch (v.Anomaly.Outcome)
        {
            case AnomalySectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Anomaly.Outcome?.Unavailable);
                return;
            case AnomalySectionOutcome.ObservedCase:
                var f = v.Anomaly.Outcome?.Observed ?? throw Contract("incomplete anomaly colony facts");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in f.Knowledge)
                {
                    if (r is null
                        || !IsValidId((r.Category ?? ""))
                        || seen.Contains("k" + (r.Category ?? ""))
                        || (r.CurrentProject is not null && !IsValidId((r.CurrentProject ?? "")))
                        || BadNonNegative(r.Knowledge is not null, r.Knowledge.GetValueOrDefault())
                        || (r.CurrentProject is null && r.Knowledge is not null))
                    {
                        throw Contract("invalid anomaly knowledge category");
                    }
                    seen.Add("k" + (r.Category ?? ""));
                }
                foreach (var r in f.Codex)
                {
                    if (r is null || !IsValidId((r.Category ?? "")) || seen.Contains("c" + (r.Category ?? "")) || r.Entries is null || r.Discovered is null || r.Discovered.GetValueOrDefault() > r.Entries.GetValueOrDefault())
                    {
                        throw Contract("invalid anomaly codex category");
                    }
                    seen.Add("c" + (r.Category ?? ""));
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
                    if (r is null || !IsValidId((r.PawnId ?? "")) || seen.Contains("p" + (r.PawnId ?? "")) || !IsValidId((r.PlatformId ?? "")) || seen.Contains("h" + (r.PlatformId ?? "")))
                    {
                        throw Contract("invalid anomaly held entity");
                    }
                    seen.Add("p" + (r.PawnId ?? ""));
                    seen.Add("h" + (r.PlatformId ?? ""));
                }
                if (f.Incidents is { } i
                    && ((i.Level is not null && i.Level.GetValueOrDefault() < 0)
                        || (i.HighestLevelReached is not null && i.HighestLevelReached.GetValueOrDefault() < 0)
                        || (i.TicksSinceLevelChange is not null && i.TicksSinceLevelChange.GetValueOrDefault() < 0)
                        || (i.LevelDef is not null && !IsValidId((i.LevelDef ?? "")))
                        || BadNonNegative(i.AnomalyThreatFractionNow is not null, i.AnomalyThreatFractionNow.GetValueOrDefault())))
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
        foreach (var id in new[] { (m.VoidNodeId ?? "") }.Concat(m.PendingVoidStructureIds).Concat(m.VoidNodePawnIds))
        {
            if (id.Length != 0 && (!IsValidId(id) || seenId.Contains(id)))
            {
                throw Contract("invalid anomaly monolith void thing");
            }
            seenId.Add(id);
        }
        if ((m.VoidNodeId is not null && (m.VoidNodeId ?? "").Length == 0)
            || (m.VoidNodePawnIds.Count > 0 && m.VoidNodeId is null)
            || ((m.VoidNodeId ?? "").Length != 0 && m.VoidNodeExists is not null && !m.VoidNodeExists.GetValueOrDefault()))
        {
            throw Contract("invalid anomaly monolith void node");
        }
        if ((m.MonolithId is not null && !IsValidId((m.MonolithId ?? "")))
            || (m.NextLevelDef is not null && !IsValidId((m.NextLevelDef ?? "")))
            || (m.NextLevelCodexCategory is not null && !IsValidId((m.NextLevelCodexCategory ?? "")))
            || (m.NextLevelCodexCategory is not null) != (m.NextLevelCodexRequired is not null)
            || (m.NextLevelCodexCategory is not null) != (m.CodexShortfall is not null)
            || (m.CodexShortfall is not null && m.CodexShortfall.GetValueOrDefault() > m.NextLevelCodexRequired.GetValueOrDefault())
            || (m.VoidStructuresActivated is not null && m.VoidStructuresActivated.GetValueOrDefault() > m.VoidStructures.GetValueOrDefault())
            || (m.VoidAwakeningStage is not null && m.VoidAwakeningStage.GetValueOrDefault() < 0))
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
        switch (v.Odyssey.Outcome)
        {
            case OdysseySectionOutcome.UnavailableCase:
                ValidateUnavailable(v.Odyssey.Outcome?.Unavailable);
                return;
            case OdysseySectionOutcome.ObservedCase:
                var f = v.Odyssey.Outcome?.Observed ?? throw Contract("incomplete odyssey colony facts");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in f.Conditions)
                {
                    if (r is null
                        || !IsValidId((r.ConditionId ?? ""))
                        || ids.Contains("c" + (r.ConditionId ?? ""))
                        || !IsValidId((r.DefName ?? ""))
                        || !IsValidId((r.ConditionClass ?? ""))
                        || (r.TicksPassed is not null && r.TicksPassed.GetValueOrDefault() < 0)
                        || (r.TicksLeft is not null && r.TicksLeft.GetValueOrDefault() < 0)
                        || r.Permanent is null
                        || (r.Permanent.GetValueOrDefault() && r.TicksLeft is not null)
                        || (r.CauserId is not null && !IsValidId((r.CauserId ?? ""))))
                    {
                        throw Contract("invalid odyssey game condition");
                    }
                    ids.Add("c" + (r.ConditionId ?? ""));
                }
                foreach (var r in f.HazardTerrain)
                {
                    if (r is null
                        || !IsValidId((r.DefName ?? ""))
                        || ids.Contains("t" + (r.DefName ?? ""))
                        || r.Cells is null
                        || r.Cells.GetValueOrDefault() == 0
                        || (r.BurnDamage is not null && r.BurnDamage.GetValueOrDefault() < 0)
                        || BadNonNegative(r.HeatPerTick is not null, r.HeatPerTick.GetValueOrDefault())
                        || BadNonNegative(r.ToxicBuildupFactor is not null, r.ToxicBuildupFactor.GetValueOrDefault()))
                    {
                        throw Contract("invalid odyssey hazard terrain");
                    }
                    ids.Add("t" + (r.DefName ?? ""));
                }
                foreach (var r in f.LavaEmergences)
                {
                    if (r is null || !IsValidId((r.ThingId ?? "")) || ids.Contains("l" + (r.ThingId ?? "")) || !ColonyCell(r.Position, v.MapSize))
                    {
                        throw Contract("invalid odyssey lava emergence");
                    }
                    ids.Add("l" + (r.ThingId ?? ""));
                }
                var maps = new HashSet<int>();
                foreach (var r in f.Sites)
                {
                    if (r is null
                        || !IsValidId((r.HatchId ?? ""))
                        || ids.Contains("h" + (r.HatchId ?? ""))
                        || r.PocketMapId is null
                        || maps.Contains(r.PocketMapId.GetValueOrDefault())
                        || (r.StockpileType is not null && !IsValidId((r.StockpileType ?? "")))
                        || (r.Layout is not null && !IsValidId((r.Layout ?? ""))))
                    {
                        throw Contract("invalid odyssey underground site");
                    }
                    ids.Add("h" + (r.HatchId ?? ""));
                    maps.Add(r.PocketMapId.GetValueOrDefault());
                    var hacks = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var h in r.Hackables)
                    {
                        if (h is null
                            || !IsValidId((h.ThingId ?? ""))
                            || hacks.Contains((h.ThingId ?? ""))
                            || !IsValidId((h.DefName ?? ""))
                            || h.Position is null
                            || (h.ProgressPercent is not null && !UnitFraction(h.ProgressPercent.GetValueOrDefault()))
                            || BadNonNegative(h.Defence is not null, h.Defence.GetValueOrDefault()))
                        {
                            throw Contract("invalid odyssey hackable");
                        }
                        hacks.Add((h.ThingId ?? ""));
                    }
                }
                return;
            default:
                throw Contract("missing odyssey colony outcome");
        }
    }
}
