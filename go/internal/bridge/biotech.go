package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// BiotechCatalog is one load's Biotech defs (#1678) by name, the native
// rows as read: effects are the game defs' own, never Go name lists. Nil
// without Biotech.
type BiotechCatalog struct {
	LifeStages    map[string]*o.LifeStageRow
	Races         map[string]*o.RaceLifeStages
	Genes         map[string]*o.GeneRow
	Xenotypes     map[string]*o.XenotypeRow
	MechKinds     map[string]*o.MechKindRow
	MechWorkModes map[string]*o.MechWorkModeRow
}

// biotechIndex indexes rows by name, refusing an invalid or duplicate one.
func biotechIndex[T any](kind string, rows []T, name func(T) string) (map[string]T, error) {
	out := make(map[string]T, len(rows))
	for _, row := range rows {
		n := name(row)
		if validID(n) != nil {
			return nil, contract("invalid biotech %s name", kind)
		}
		if _, dup := out[n]; dup {
			return nil, contract("duplicate biotech %s %s", kind, n)
		}
		out[n] = row
	}
	return out, nil
}

func biotechNumbers(kind string, values ...*float64) error {
	for _, v := range values {
		if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0)) {
			return contract("nonfinite biotech %s number", kind)
		}
	}
	return nil
}

func biotechIDs(kind string, lists ...[]string) error {
	for _, list := range lists {
		seen := map[string]bool{}
		for _, id := range list {
			if validID(id) != nil || seen[id] {
				return contract("invalid or duplicate biotech %s reference", kind)
			}
			seen[id] = true
		}
	}
	return nil
}

func biotechEffects(kind string, rows []*o.StatEffect) error {
	for _, e := range rows {
		if e == nil || validID(e.GetStat()) != nil || (e.Factor == nil) == (e.Offset == nil) {
			return contract("invalid biotech %s stat effect", kind)
		}
		if err := biotechNumbers(kind, e.Factor, e.Offset); err != nil {
			return err
		}
	}
	return nil
}

// DecodeBiotechCatalog validates the catalog's Biotech section; nil in,
// nil out (the game has no Biotech).
func DecodeBiotechCatalog(v *o.BiotechCatalog) (*BiotechCatalog, error) {
	if v == nil {
		return nil, nil
	}
	out := &BiotechCatalog{}
	var err error
	if out.LifeStages, err = biotechIndex("life stage", v.LifeStages, (*o.LifeStageRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Races, err = biotechIndex("race", v.Races, (*o.RaceLifeStages).GetRace); err != nil {
		return nil, err
	}
	if out.Genes, err = biotechIndex("gene", v.Genes, (*o.GeneRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Xenotypes, err = biotechIndex("xenotype", v.Xenotypes, (*o.XenotypeRow).GetDefName); err != nil {
		return nil, err
	}
	if out.MechKinds, err = biotechIndex("mech kind", v.MechKinds, (*o.MechKindRow).GetDefName); err != nil {
		return nil, err
	}
	if out.MechWorkModes, err = biotechIndex("mech work mode", v.MechWorkModes, (*o.MechWorkModeRow).GetDefName); err != nil {
		return nil, err
	}
	for _, row := range v.LifeStages {
		if err := biotechEffects("life stage", row.Effects); err != nil {
			return nil, err
		}
		if err := biotechNumbers("life stage", row.HungerRateFactor, row.BodySizeFactor, row.HealthScaleFactor); err != nil {
			return nil, err
		}
	}
	for _, row := range v.Races {
		last := math.Inf(-1)
		for _, stage := range row.Stages {
			if out.LifeStages[stage.GetLifeStage()] == nil || stage.MinAgeYears == nil || math.IsNaN(stage.GetMinAgeYears()) || stage.GetMinAgeYears() < last {
				return nil, contract("invalid biotech race life stage")
			}
			last = stage.GetMinAgeYears()
		}
		seen := map[string]bool{}
		for _, work := range row.WorkMinAges {
			if validID(work.GetWorkType()) != nil || seen[work.GetWorkType()] || work.MinAge == nil || work.GetMinAge() < 0 {
				return nil, contract("invalid biotech work minimum age")
			}
			seen[work.GetWorkType()] = true
		}
	}
	for _, row := range v.Genes {
		if err := biotechEffects("gene", row.Effects); err != nil {
			return nil, err
		}
		if err := biotechIDs("gene", row.DisabledWorkTags, row.EnablesNeeds, row.DisablesNeeds, row.ForcedTraits, row.SuppressedTraits, row.MakeImmuneTo, row.ExclusionTags); err != nil {
			return nil, err
		}
		if err := biotechNumbers("gene", row.AddictionChanceFactor, row.OverdoseChanceFactor, row.ToleranceBuildupFactor, row.MinAgeActive, row.PainOffset, row.PainFactor); err != nil {
			return nil, err
		}
		for _, a := range row.Aptitudes {
			if validID(a.GetSkill()) != nil || a.Level == nil {
				return nil, contract("invalid biotech gene aptitude")
			}
		}
		for _, p := range row.PassionMods {
			if validID(p.GetSkill()) != nil || validID(p.GetModType()) != nil {
				return nil, contract("invalid biotech gene passion effect")
			}
		}
		for _, c := range row.CapacityEffects {
			if validID(c.GetCapacity()) != nil {
				return nil, contract("invalid biotech gene capacity effect")
			}
			if err := biotechNumbers("gene", c.Offset, c.SetMax, c.PostFactor); err != nil {
				return nil, err
			}
		}
	}
	for _, row := range v.Xenotypes {
		if err := biotechIDs("xenotype", row.Genes); err != nil {
			return nil, err
		}
		for _, gene := range row.Genes {
			if out.Genes[gene] == nil {
				return nil, contract("biotech xenotype %s names unknown gene %s", row.GetDefName(), gene)
			}
		}
	}
	for _, row := range v.MechKinds {
		if err := biotechIDs("mech kind", row.WorkTypes); err != nil {
			return nil, err
		}
		if err := biotechNumbers("mech kind", row.BandwidthCost, row.BodySize, row.CombatPower); err != nil {
			return nil, err
		}
		for _, p := range row.WorkPriorities {
			if validID(p.GetWorkType()) != nil || p.Priority == nil {
				return nil, contract("invalid biotech mech work priority")
			}
		}
	}
	return out, nil
}

// validatePawnBiotech bounds a pawn row's Biotech block (#1678).
func validatePawnBiotech(b *o.PawnBiotech) error {
	if b == nil {
		return nil
	}
	if err := biotechNumbers("pawn", b.Learning); err != nil {
		return err
	}
	if b.Learning != nil && (b.GetLearning() < 0 || b.GetLearning() > 1) {
		return contract("biotech learning outside 0..1")
	}
	for _, id := range []*string{b.LifeStage, b.DevelopmentalStage, b.LearningCategory, b.Xenotype} {
		if id != nil && validID(*id) != nil {
			return contract("invalid biotech pawn name")
		}
	}
	seen := map[string]bool{}
	for _, g := range b.Genes {
		if g == nil || validID(g.GetDefName()) != nil || seen[g.GetDefName()] || g.Xenogene == nil || g.Active == nil {
			return contract("invalid or duplicate biotech pawn gene")
		}
		seen[g.GetDefName()] = true
	}
	if m := b.Mechanitor; m != nil {
		for _, n := range []*int32{m.UsedBandwidth, m.TotalBandwidth, m.UsedBandwidthFromGestation, m.ControlGroups} {
			if n != nil && *n < 0 {
				return contract("negative mechanitor number")
			}
		}
		for _, mech := range m.ControlledMechs {
			if !validRef(mech) {
				return contract("invalid controlled mech")
			}
		}
	}
	if m := b.Mech; m != nil {
		if m.Overseer != nil && !validRef(m.Overseer) || m.WorkMode != nil && validID(m.GetWorkMode()) != nil || m.ControlGroup != nil && m.GetControlGroup() < 0 {
			return contract("invalid biotech mech")
		}
	}
	if d := b.Deathrest; d != nil {
		if err := biotechNumbers("deathrest", d.Level, d.DeathrestPercent); err != nil {
			return err
		}
		for _, n := range []*int32{d.Capacity, d.BoundBuildings} {
			if n != nil && *n < 0 {
				return contract("negative deathrest number")
			}
		}
	}
	return pawnsIssues(b.Issues, b.ProtoReflect())
}

// PawnBiotech lifts a pawn row's Biotech block; unknown when the row carries
// none (no Biotech). A field a read issue names stays unknown.
func PawnBiotech(b *o.PawnBiotech) domain.Fact[policy.PawnBiotech] {
	if b == nil {
		return domain.Unknown[policy.PawnBiotech]()
	}
	failed := map[string]bool{}
	for _, issue := range b.Issues {
		failed[issue.GetField()] = true
	}
	// field is the fact when its read did not fail.
	field := func(name string) bool { return !failed[name] }
	r := policy.PawnBiotech{LifeStage: optionalFact(b.LifeStage), DevelopmentalStage: optionalFact(b.DevelopmentalStage),
		Learning: optionalFact(b.Learning), LearningCategory: optionalFact(b.LearningCategory)}
	if field("genes") {
		genes := make([]policy.PawnGene, 0, len(b.Genes))
		for _, g := range b.Genes {
			genes = append(genes, policy.PawnGene{Name: g.GetDefName(), Xenogene: domain.Known(g.GetXenogene()), Active: domain.Known(g.GetActive())})
		}
		// Genes exist only on a pawn with a gene tracker; the xenotype name
		// is read with them.
		if b.XenotypeName != nil {
			r.Genes, r.Xenotype, r.XenotypeName, r.Hybrid = domain.Known(genes), domain.Known(b.GetXenotype()), domain.Known(b.GetXenotypeName()), optionalFact(b.Hybrid)
		}
	}
	if field("mechanitor") {
		var m *policy.PawnMechanitor
		if v := b.Mechanitor; v != nil {
			m = &policy.PawnMechanitor{UsedBandwidth: optionalInt(v.UsedBandwidth), TotalBandwidth: optionalInt(v.TotalBandwidth),
				GestationBandwidth: optionalInt(v.UsedBandwidthFromGestation), ControlGroups: optionalInt(v.ControlGroups)}
			for _, mech := range v.ControlledMechs {
				m.ControlledMechs = append(m.ControlledMechs, mech.GetId())
			}
		}
		r.Mechanitor = domain.Known(m)
	}
	if field("mech") {
		var m *policy.PawnMech
		if v := b.Mech; v != nil {
			m = &policy.PawnMech{Overseer: v.GetOverseer().GetId(), WorkMode: optionalFact(v.WorkMode), ControlGroup: optionalInt(v.ControlGroup)}
		}
		r.Mech = domain.Known(m)
	}
	if field("deathrest") {
		var d *policy.PawnDeathrest
		if v := b.Deathrest; v != nil {
			d = &policy.PawnDeathrest{Deathresting: optionalFact(v.Deathresting), Level: optionalFact(v.Level), LastDeathrestTick: optionalInt(v.LastDeathrestTick),
				DeathrestPercent: optionalFact(v.DeathrestPercent), Capacity: optionalInt(v.Capacity), BoundBuildings: optionalInt(v.BoundBuildings), AutoWake: optionalFact(v.AutoWake)}
		}
		r.Deathrest = domain.Known(d)
	}
	return domain.Known(r)
}

func optionalInt(p *int32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}
