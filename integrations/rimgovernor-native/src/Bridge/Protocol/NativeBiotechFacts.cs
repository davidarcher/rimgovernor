#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Biotech facts: the catalog's Biotech section (the game's role picks of
    // mech work modes and the gene tuning constants; every other Biotech def
    // is a row of the def mirror) and the per-pawn row block. Everything is
    // absent without Biotech.
    internal static class NativeBiotechFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static Obs.BiotechCatalog? Catalog()
        {
            if (!ModsConfig.BiotechActive) return null;
            return new Obs.BiotechCatalog
            {
                GeneTuning = GeneTuningRow(),
                MechWorkModes = new Obs.MechWorkModeRoles { Work = MechWorkModeDefOf.Work.defName, Escort = MechWorkModeDefOf.Escort.defName, Recharge = MechWorkModeDefOf.Recharge.defName },
            };
        }

        // The game's own GeneTuning constants and the extractor's private
        // ones; an unreadable private constant stays absent.
        private static Obs.GeneTuningFacts GeneTuningRow()
        {
            var row = new Obs.GeneTuningFacts { BiostatMin = GeneTuning.BiostatRange.min, BiostatMax = GeneTuning.BiostatRange.max,
                BaseMaxComplexity = GeneTuning.BaseMaxComplexity };
            foreach (var p in GeneTuning.ComplexityToCreationHoursCurve.Points)
                if (Finite(p.x) && Finite(p.y)) row.CreationHoursCurve.Add(new Obs.CurvePointRow { X = p.x, Y = p.y });
            var regrow = GeneTuning.GeneExtractorRegrowingDurationDaysRange;
            if (Finite(regrow.min) && Finite(regrow.max)) { row.RegrowDaysMin = regrow.min; row.RegrowDaysMax = regrow.max; }
            if (ExtractorConstant("TicksToExtract") is int extract) row.ExtractTicks = extract;
            if (ExtractorConstant("NoPowerEjectCumulativeTicks") is int eject) row.NoPowerEjectTicks = eject;
            return row;
        }

        private static int? ExtractorConstant(string name)
        {
            try
            {
                var field = typeof(Building_GeneExtractor).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return field != null && field.IsLiteral ? field.GetRawConstantValue() as int? : null;
            }
            catch (Exception) { return null; }
        }

        private static Obs.ReadIssue Failed(string field, Exception ex) => new Obs.ReadIssue { Field = field,
            Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };

        // The pawn row block; null without Biotech. A sub-read that throws
        // leaves its fields absent and adds a ReadIssue named for it.
        internal static Obs.PawnBiotech? Pawn(Pawn pawn)
        {
            if (!ModsConfig.BiotechActive) return null;
            var row = new Obs.PawnBiotech();
            try { if (Id(pawn.ageTracker?.CurLifeStage?.defName) is string stage) row.LifeStage = stage; }
            catch (Exception ex) { row.Issues.Add(Failed("life_stage", ex)); }
            try { row.DevelopmentalStage = pawn.DevelopmentalStage.ToString(); }
            catch (Exception ex) { row.Issues.Add(Failed("developmental_stage", ex)); }
            try
            {
                if (pawn.needs?.learning is Need_Learning learning)
                {
                    var level = learning.CurLevelPercentage;
                    var category = learning.CurCategory.ToString();
                    if (Finite(level)) { row.Learning = level; row.LearningCategory = category; }
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("learning", ex)); }
            try
            {
                if (pawn.genes is Pawn_GeneTracker genes)
                {
                    var list = new List<Obs.PawnGene>();
                    foreach (var gene in genes.GenesListForReading)
                        if (Id(gene.def?.defName) is string name)
                            list.Add(new Obs.PawnGene { DefName = name, Xenogene = genes.IsXenogene(gene), Active = gene.Active });
                    var xenotype = Id(genes.Xenotype?.defName);
                    var label = PlacementPreviewOperation.Diagnostic(genes.XenotypeLabel ?? "");
                    var hybrid = genes.hybrid;
                    row.Genes.Add(list);
                    if (xenotype != null) row.Xenotype = xenotype;
                    row.XenotypeName = label;
                    row.Hybrid = hybrid;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("genes", ex)); }
            try
            {
                if (pawn.mechanitor is Pawn_MechanitorTracker tracker)
                {
                    var mechanitor = new Obs.PawnMechanitor { UsedBandwidth = tracker.UsedBandwidth, TotalBandwidth = tracker.TotalBandwidth,
                        UsedBandwidthFromGestation = tracker.UsedBandwidthFromGestation, ControlGroups = tracker.controlGroups.Count };
                    foreach (var mech in tracker.ControlledPawns.OrderBy(m => m.thingIDNumber)) mechanitor.ControlledMechs.Add(NativeRef.Thing(mech));
                    row.Mechanitor = mechanitor;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("mechanitor", ex)); }
            try
            {
                if (pawn.RaceProps.IsMechanoid && MechanitorUtility.EverControllable(pawn))
                {
                    var mech = new Obs.PawnMech();
                    if (MechanitorUtility.GetOverseer(pawn) is Pawn overseer) mech.Overseer = NativeRef.Thing(overseer);
                    if (Id(MechanitorUtility.GetMechWorkMode(pawn)?.defName) is string mode) mech.WorkMode = mode;
                    if (MechanitorUtility.GetMechControlGroup(pawn) is MechanitorControlGroup group) mech.ControlGroup = group.Index;
                    if (pawn.needs?.TryGetNeed<Need_MechEnergy>() is Need_MechEnergy energy && Finite(energy.CurLevelPercentage)) mech.Energy = energy.CurLevelPercentage;
                    row.Mech = mech;
                    // The group's own recharge band; a game without the field fails
                    // this read loudly instead of inventing a threshold.
                    try
                    {
                        if (MechanitorUtility.GetMechControlGroup(pawn) is MechanitorControlGroup thresholdGroup)
                        {
                            var thresholds = BridgeCommon.PrivateInstanceField(typeof(MechanitorControlGroup), "mechRechargeThresholds")
                                ?? throw new InvalidOperationException("MechanitorControlGroup.mechRechargeThresholds not found");
                            var band = (FloatRange)thresholds.GetValue(thresholdGroup);
                            if (Finite(band.min) && Finite(band.max)) { mech.RechargeBelow = band.min; mech.RechargeAbove = band.max; }
                        }
                    }
                    catch (Exception ex) { row.Issues.Add(Failed("mech_thresholds", ex)); }
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("mech", ex)); }
            try
            {
                if (pawn.genes?.GetFirstGeneOfType<Gene_Deathrest>() is Gene_Deathrest gene)
                {
                    var deathrest = new Obs.PawnDeathrest { Capacity = gene.DeathrestCapacity, BoundBuildings = gene.BoundBuildings.Count, AutoWake = gene.autoWake };
                    if (gene.DeathrestNeed is Need_Deathrest need)
                    {
                        deathrest.Deathresting = need.Deathresting;
                        if (Finite(need.CurLevelPercentage)) deathrest.Level = need.CurLevelPercentage;
                        deathrest.LastDeathrestTick = need.lastDeathrestTick;
                    }
                    if (Finite(gene.DeathrestPercent)) deathrest.DeathrestPercent = gene.DeathrestPercent;
                    row.Deathrest = deathrest;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("deathrest", ex)); }
            try
            {
                var regrow = TicksLeft(pawn, HediffDefOf.XenogermReplicating);
                var coma = TicksLeft(pawn, HediffDefOf.XenogerminationComa);
                var inExtractor = pawn.ParentHolder is Building_GeneExtractor;
                row.XenogermRegrowTicksLeft = regrow;
                row.XenogermComaTicksLeft = coma;
                row.InExtractor = inExtractor;
            }
            catch (Exception ex) { row.Issues.Add(Failed("gene_lifecycle", ex)); }
            try { Extractable(pawn, row); }
            catch (Exception ex) { row.ClearExtractable(); row.ClearExtractableReason(); row.Issues.Add(Failed("extractable", ex)); }
            return row;
        }

        // The hediff's HediffComp_Disappears.ticksToDisappear, 0 without the
        // hediff; a hediff with no timer throws so the read is an issue, not 0.
        private static int TicksLeft(Pawn pawn, HediffDef def)
        {
            var hediff = pawn.health?.hediffSet?.GetFirstHediffOfDef(def);
            if (hediff == null) return 0;
            var disappears = hediff.TryGetComp<HediffComp_Disappears>() ?? throw new InvalidOperationException(def.defName + " has no disappear timer");
            return Math.Max(0, disappears.ticksToDisappear);
        }

        // The game's own Building_GeneExtractor.CanAcceptPawn verdict: true when
        // any owned extractor of the pawn's map accepts, else the first refusal
        // text. Absent for an unspawned or non-humanlike pawn or a map with no
        // extractor.
        private static void Extractable(Pawn pawn, Obs.PawnBiotech row)
        {
            if (!pawn.Spawned || pawn.Map == null || !pawn.RaceProps.Humanlike || pawn.genes == null) return;
            var extractors = pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_GeneExtractor>().OrderBy(e => e.thingIDNumber).ToList();
            if (extractors.Count == 0) return;
            string? reason = null;
            foreach (var extractor in extractors)
            {
                var verdict = extractor.CanAcceptPawn(pawn);
                if (verdict.Accepted) { row.Extractable = true; return; }
                if (reason == null && !string.IsNullOrEmpty(verdict.Reason)) reason = verdict.Reason;
            }
            row.Extractable = false;
            if (reason != null) row.ExtractableReason = PlacementPreviewOperation.Diagnostic(reason);
        }
    }
}
