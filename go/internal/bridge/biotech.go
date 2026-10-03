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

func biotechEffects(kind string, rows []*o.StatEffect) error {
	for _, e := range rows {
		if e == nil || validID(e.GetStat()) != nil || (e.Factor == nil) == (e.Offset == nil) {
			return contract("invalid biotech %s stat effect", kind)
		}
		if err := catalogNumbers("biotech "+kind, e.Factor, e.Offset); err != nil {
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
	if out.LifeStages, err = catalogIndex("biotech life stage", v.LifeStages, (*o.LifeStageRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Races, err = catalogIndex("biotech race", v.Races, (*o.RaceLifeStages).GetRace); err != nil {
		return nil, err
	}
	if out.Genes, err = catalogIndex("biotech gene", v.Genes, (*o.GeneRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Xenotypes, err = catalogIndex("biotech xenotype", v.Xenotypes, (*o.XenotypeRow).GetDefName); err != nil {
		return nil, err
	}
	if out.MechKinds, err = catalogIndex("biotech mech kind", v.MechKinds, (*o.MechKindRow).GetDefName); err != nil {
		return nil, err
	}
	if out.MechWorkModes, err = catalogIndex("biotech mech work mode", v.MechWorkModes, (*o.MechWorkModeRow).GetDefName); err != nil {
		return nil, err
	}
	if len(v.MechWorkModes) > 0 {
		recharge := 0
		for _, row := range v.MechWorkModes {
			if row.GetRecharge() {
				recharge++
			}
		}
		if recharge != 1 {
			return nil, contract("biotech mech work modes carry %d recharge roles, want one", recharge)
		}
	}
	for _, row := range v.LifeStages {
		if err := biotechEffects("life stage", row.Effects); err != nil {
			return nil, err
		}
		if err := catalogNumbers("biotech life stage", row.HungerRateFactor, row.BodySizeFactor, row.HealthScaleFactor); err != nil {
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
		if err := catalogIDs("biotech gene", row.DisabledWorkTags, row.EnablesNeeds, row.DisablesNeeds, row.ForcedTraits, row.SuppressedTraits, row.MakeImmuneTo, row.ExclusionTags); err != nil {
			return nil, err
		}
		if err := catalogNumbers("biotech gene", row.AddictionChanceFactor, row.OverdoseChanceFactor, row.ToleranceBuildupFactor, row.MinAgeActive, row.PainOffset, row.PainFactor); err != nil {
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
			if err := catalogNumbers("biotech gene", c.Offset, c.SetMax, c.PostFactor); err != nil {
				return nil, err
			}
		}
	}
	for _, row := range v.Xenotypes {
		if err := catalogIDs("biotech xenotype", row.Genes); err != nil {
			return nil, err
		}
		for _, gene := range row.Genes {
			if out.Genes[gene] == nil {
				return nil, contract("biotech xenotype %s names unknown gene %s", row.GetDefName(), gene)
			}
		}
	}
	for _, row := range v.MechKinds {
		if err := catalogIDs("biotech mech kind", row.WorkTypes); err != nil {
			return nil, err
		}
		if err := catalogNumbers("biotech mech kind", row.BandwidthCost, row.BodySize, row.CombatPower); err != nil {
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
	if err := catalogNumbers("biotech pawn", b.Learning); err != nil {
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
		if err := catalogNumbers("biotech mech", m.Energy, m.RechargeBelow, m.RechargeAbove); err != nil {
			return err
		}
		for _, n := range []*float64{m.Energy, m.RechargeBelow, m.RechargeAbove} {
			if n != nil && (*n < 0 || *n > 1) {
				return contract("mech energy outside 0-1")
			}
		}
		if m.RechargeBelow != nil && m.RechargeAbove != nil && *m.RechargeBelow > *m.RechargeAbove {
			return contract("mech recharge band inverted")
		}
	}
	if d := b.Deathrest; d != nil {
		if err := catalogNumbers("biotech deathrest", d.Level, d.DeathrestPercent); err != nil {
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
			m = &policy.PawnMech{Overseer: v.GetOverseer().GetId(), WorkMode: optionalFact(v.WorkMode), ControlGroup: optionalInt(v.ControlGroup), Energy: optionalFact(v.Energy)}
			if field("mech_thresholds") {
				m.RechargeBelow, m.RechargeAbove = optionalFact(v.RechargeBelow), optionalFact(v.RechargeAbove)
			}
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

// MechCatalog is the catalog's mech kinds and work mode names as the mech
// planner reads them (#1687); the zero value without Biotech.
func (c *BiotechCatalog) MechCatalog() policy.MechCatalog {
	out := policy.MechCatalog{Kinds: map[string]policy.MechKind{}, Modes: map[string]bool{}}
	if c == nil {
		return out
	}
	for name, row := range c.MechKinds {
		kind := policy.MechKind{Name: name, WorkMech: row.GetWorkMech(), BandwidthCost: row.GetBandwidthCost(), CombatPower: row.GetCombatPower()}
		for _, w := range row.WorkTypes {
			kind.WorkTypes = append(kind.WorkTypes, policy.WorkType(w))
		}
		out.Kinds[name] = kind
	}
	for name, row := range c.MechWorkModes {
		out.Modes[name] = true
		if row.GetRecharge() {
			out.Recharge = name
		}
	}
	return out
}

// GeneEffects resolves the active genes of a pawn into their combined typed
// effects from the catalog's gene rows. A gene the catalog does not define is
// a contract failure, never skipped.
func (c *BiotechCatalog) GeneEffects(genes []policy.PawnGene) (policy.GeneEffects, error) {
	out := policy.GeneEffects{Stats: map[string]policy.StatModifier{}, DisabledNeeds: map[string]bool{}, EnabledNeeds: map[string]bool{}}
	for _, g := range genes {
		if active, ok := g.Active.Value(); ok && !active {
			continue
		}
		var row *o.GeneRow
		if c != nil {
			row = c.Genes[g.Name]
		}
		if row == nil {
			return policy.GeneEffects{}, contract("pawn gene %s is not in the biotech catalog", g.Name)
		}
		for _, e := range row.Effects {
			m, seen := out.Stats[e.GetStat()]
			if !seen {
				m.Factor = 1
			}
			if e.Factor != nil {
				m.Factor *= e.GetFactor()
			} else {
				m.Offset += e.GetOffset()
			}
			out.Stats[e.GetStat()] = m
		}
		for _, n := range row.DisablesNeeds {
			out.DisabledNeeds[n] = true
		}
		for _, n := range row.EnablesNeeds {
			out.EnabledNeeds[n] = true
		}
	}
	return out, nil
}

// WorkMinAges is the race's minimum age in years per work type from the
// catalog (#1682). A race the catalog lacks is a contract failure: a child
// must never be left unrestricted for want of data.
func (c *BiotechCatalog) WorkMinAges(race string) (map[policy.WorkType]int, error) {
	var row *o.RaceLifeStages
	if c != nil {
		row = c.Races[race]
	}
	if row == nil {
		return nil, contract("child race %s is not in the biotech catalog", race)
	}
	out := make(map[policy.WorkType]int, len(row.WorkMinAges))
	for _, w := range row.WorkMinAges {
		out[policy.WorkType(w.GetWorkType())] = int(w.GetMinAge())
	}
	return out, nil
}

func optionalInt(p *int32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}
