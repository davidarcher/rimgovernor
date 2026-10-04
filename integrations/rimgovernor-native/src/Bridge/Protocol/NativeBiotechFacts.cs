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
    // Biotech facts (#1678): the static defs of the definition catalog
    // (life stages, genes, xenotypes, mech kinds, mech work modes) and the
    // per-pawn row block. Effects come from the game defs themselves, never
    // from name lists. Everything is absent without Biotech.
    internal static class NativeBiotechFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static string Label(Def def) => PlacementPreviewOperation.Diagnostic(def.label ?? "");
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static IEnumerable<T> Sorted<T>(IEnumerable<T> defs) where T : Def =>
            defs.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal);
        private static IEnumerable<string> Names(IEnumerable<Def>? defs) =>
            (defs ?? Enumerable.Empty<Def>()).Select(d => Id(d?.defName)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

        private static readonly WorkTags[] SingleTags = ((WorkTags[])Enum.GetValues(typeof(WorkTags)))
            .Where(t => t != WorkTags.None && t != WorkTags.AllWork && ((int)t & ((int)t - 1)) == 0).ToArray();

        private static void Effects(Google.Protobuf.Collections.RepeatedField<Obs.StatEffect> into, List<StatModifier>? factors, List<StatModifier>? offsets)
        {
            foreach (var m in factors ?? new List<StatModifier>())
                if (m?.stat != null && Id(m.stat.defName) is string stat && Finite(m.value)) into.Add(new Obs.StatEffect { Stat = stat, Factor = m.value });
            foreach (var m in offsets ?? new List<StatModifier>())
                if (m?.stat != null && Id(m.stat.defName) is string stat && Finite(m.value)) into.Add(new Obs.StatEffect { Stat = stat, Offset = m.value });
        }

        internal static Obs.BiotechCatalog? Catalog()
        {
            if (!ModsConfig.BiotechActive) return null;
            var catalog = new Obs.BiotechCatalog();
            foreach (var def in Sorted(DefDatabase<LifeStageDef>.AllDefsListForReading)) catalog.LifeStages.Add(LifeStage(def));
            foreach (var race in Sorted(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null && d.race.Humanlike && d.race.lifeStageAges != null)))
                catalog.Races.Add(Race(race));
            foreach (var def in Sorted(DefDatabase<GeneDef>.AllDefsListForReading)) catalog.Genes.Add(Gene(def));
            foreach (var def in Sorted(DefDatabase<XenotypeDef>.AllDefsListForReading)) catalog.Xenotypes.Add(Xenotype(def));
            foreach (var def in Sorted(DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.race?.race != null && k.race.race.IsMechanoid && k.race.HasComp(typeof(CompOverseerSubject)))))
                catalog.MechKinds.Add(MechKind(def));
            foreach (var def in Sorted(DefDatabase<MechWorkModeDef>.AllDefsListForReading))
                catalog.MechWorkModes.Add(new Obs.MechWorkModeRow { DefName = def.defName, Label = Label(def), UiOrder = def.uiOrder, IgnoreGroupChargeLimits = def.ignoreGroupChargeLimits,
                    Recharge = def == MechWorkModeDefOf.Recharge, Work = def == MechWorkModeDefOf.Work, Escort = def == MechWorkModeDefOf.Escort });
            catalog.GeneTuning = GeneTuningRow();
            return catalog;
        }

        // The game's own GeneTuning constants and the extractor's private
        // ones (#1932); an unreadable private constant stays absent.
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

        private static Obs.LifeStageRow LifeStage(LifeStageDef def)
        {
            var row = new Obs.LifeStageRow { DefName = def.defName, Label = Label(def), DevelopmentalStage = def.developmentalStage.ToString(),
                Reproductive = def.reproductive, AlwaysDowned = def.alwaysDowned, Claimable = def.claimable, CanVoluntarilySleep = def.canVoluntarilySleep,
                InvoluntarySleepIsNegativeEvent = def.involuntarySleepIsNegativeEvent };
            if (Finite(def.hungerRateFactor)) row.HungerRateFactor = def.hungerRateFactor;
            if (Finite(def.bodySizeFactor)) row.BodySizeFactor = def.bodySizeFactor;
            if (Finite(def.healthScaleFactor)) row.HealthScaleFactor = def.healthScaleFactor;
            Effects(row.Effects, def.statFactors, def.statOffsets);
            return row;
        }

        private static Obs.RaceLifeStages Race(ThingDef def)
        {
            var row = new Obs.RaceLifeStages { Race = def.defName };
            foreach (var stage in def.race.lifeStageAges)
                if (Id(stage?.def?.defName) is string name && Finite(stage!.minAge)) row.Stages.Add(new Obs.LifeStageAgeRow { LifeStage = name, MinAgeYears = stage.minAge });
            foreach (var work in def.race.lifeStageWorkSettings ?? new List<LifeStageWorkSettings>())
                if (Id(work?.workType?.defName) is string name) row.WorkMinAges.Add(new Obs.WorkMinAge { WorkType = name, MinAge = work!.minAge });
            return row;
        }

        private static Obs.GeneRow Gene(GeneDef def)
        {
            var row = new Obs.GeneRow { DefName = def.defName, Label = Label(def), Complexity = def.biostatCpx, Metabolism = def.biostatMet, Archite = def.biostatArc };
            if (Id(def.displayCategory?.defName) is string category) row.Category = category;
            foreach (var tag in SingleTags) if ((def.disabledWorkTags & tag) != 0) row.DisabledWorkTags.Add(tag.ToString());
            Effects(row.Effects, def.statFactors, def.statOffsets);
            foreach (var a in def.aptitudes ?? new List<Aptitude>())
                if (Id(a?.skill?.defName) is string skill) row.Aptitudes.Add(new Obs.SkillLevel { Skill = skill, Level = a!.level });
            if (def.passionMod != null && Id(def.passionMod.skill?.defName) is string passionSkill)
                row.PassionMods.Add(new Obs.PassionEffect { Skill = passionSkill, ModType = def.passionMod.modType.ToString() });
            foreach (var cap in def.capMods ?? new List<PawnCapacityModifier>())
                if (Id(cap?.capacity?.defName) is string capacity)
                {
                    var effect = new Obs.CapacityEffect { Capacity = capacity };
                    if (Finite(cap!.offset)) effect.Offset = cap.offset;
                    if (cap.SetMaxDefined && Finite(cap.setMax)) effect.SetMax = cap.setMax;
                    if (Finite(cap.postFactor)) effect.PostFactor = cap.postFactor;
                    row.CapacityEffects.Add(effect);
                }
            row.EnablesNeeds.Add(Names(def.enablesNeeds));
            row.DisablesNeeds.Add(Names(def.disablesNeeds));
            row.ForcedTraits.Add((def.forcedTraits ?? new List<GeneticTraitData>()).Select(t => Id(t?.def?.defName)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal));
            row.SuppressedTraits.Add((def.suppressedTraits ?? new List<GeneticTraitData>()).Select(t => Id(t?.def?.defName)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal));
            row.MakeImmuneTo.Add(Names(def.makeImmuneTo));
            if (Id(def.chemical?.defName) is string chemical)
            {
                row.Chemical = chemical;
                if (Finite(def.addictionChanceFactor)) row.AddictionChanceFactor = def.addictionChanceFactor;
                if (Finite(def.overdoseChanceFactor)) row.OverdoseChanceFactor = def.overdoseChanceFactor;
                if (Finite(def.toleranceBuildupFactor)) row.ToleranceBuildupFactor = def.toleranceBuildupFactor;
            }
            if (Finite(def.minAgeActive)) row.MinAgeActive = def.minAgeActive;
            if (Finite(def.painOffset)) row.PainOffset = def.painOffset;
            if (Finite(def.painFactor)) row.PainFactor = def.painFactor;
            row.ExclusionTags.Add((def.exclusionTags ?? new List<string>()).Where(ProtoBoundary.IsIdentifier).OrderBy(t => t, StringComparer.Ordinal));
            return row;
        }

        private static Obs.XenotypeRow Xenotype(XenotypeDef def)
        {
            var row = new Obs.XenotypeRow { DefName = def.defName, Label = Label(def), Inheritable = def.inheritable };
            row.Genes.Add(Names(def.genes));
            return row;
        }

        private static Obs.MechKindRow MechKind(PawnKindDef kind)
        {
            var race = kind.race;
            var row = new Obs.MechKindRow { DefName = kind.defName, Label = Label(kind), Race = race.defName, MaxEnergy = race.race.maxMechEnergy,
                FixedSkillLevel = race.race.mechFixedSkillLevel, WorkMech = race.race.IsWorkMech };
            if (Id(race.race.mechWeightClass?.defName) is string weight) row.WeightClass = weight;
            var cost = race.GetStatValueAbstract(StatDefOf.BandwidthCost);
            if (Finite(cost)) row.BandwidthCost = cost;
            if (Finite(race.race.baseBodySize)) row.BodySize = race.race.baseBodySize;
            if (Finite(kind.combatPower)) row.CombatPower = kind.combatPower;
            row.WorkTypes.Add(Names(race.race.mechEnabledWorkTypes));
            foreach (var p in race.race.mechWorkTypePriorities ?? new List<MechWorkTypePriority>())
                if (Id(p?.def?.defName) is string work) row.WorkPriorities.Add(new Obs.MechWorkPriority { WorkType = work, Priority = p!.priority });
            return row;
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
            return row;
        }
    }
}
