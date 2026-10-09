package bridge

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// classOverseerSubject is the comp class that makes a mechanoid race a
// controllable mech.
const classOverseerSubject = "RimWorld.CompProperties_OverseerSubject"

// statBandwidthCost is the stat that prices a mech kind in mechanitor bandwidth.
const statBandwidthCost = "BandwidthCost"

// BiotechCatalog is one load's Biotech facts, a facade over the def mirror:
// the gene, race and mech kind defs are the catalog's own rows, and only the
// game's role picks and tuning constants come from the native Biotech
// section. Nil without Biotech.
type BiotechCatalog struct {
	// Genes are the GeneDef rows by name.
	Genes map[string]*d.GeneDef
	// races are the humanlike races' RaceProperties by ThingDef name.
	races map[string]*d.RaceProperties
	// mechKinds are the controllable mech kinds by PawnKindDef name.
	mechKinds map[string]mechKindRow
	// work, escort and recharge are the MechWorkModeDef names the game picks
	// by role.
	work, escort, recharge string
	// GeneTuning is the singleton of GeneTuning constants; nil when
	// the native did not send it.
	GeneTuning *o.GeneTuningFacts
}

// mechKindRow is a controllable mech kind as the planner reads it.
type mechKindRow struct {
	race          string
	workMech      bool
	bandwidthCost float64
	combatPower   float64
	workTypes     []string
}

// buildBiotech builds the facade from the catalog's def rows and the native
// Biotech section; nil in, nil out (the game has no Biotech). It runs after
// the catalog's rows, thing facts and stat table are decoded.
func buildBiotech(catalog *DefinitionCatalog, v *o.BiotechCatalog) (*BiotechCatalog, error) {
	if v == nil {
		return nil, nil
	}
	out := &BiotechCatalog{Genes: map[string]*d.GeneDef{}, races: map[string]*d.RaceProperties{}, mechKinds: map[string]mechKindRow{}, GeneTuning: v.GeneTuning}
	for _, msg := range catalog.Defs[(&d.GeneDef{}).ProtoReflect().Descriptor().FullName()] {
		gene := msg.(*d.GeneDef)
		out.Genes[gene.GetDefName()] = gene
	}
	for name, def := range catalog.ThingDefs {
		race := def.GetRace()
		if race.GetIntelligence() >= d.Intelligence_INTELLIGENCE_HUMANLIKE && len(race.GetLifeStageAges()) > 0 {
			out.races[name] = race
		}
	}
	for _, msg := range catalog.Defs[(&d.PawnKindDef{}).ProtoReflect().Descriptor().FullName()] {
		kind := msg.(*d.PawnKindDef)
		row, ok, err := catalog.mechKind(kind)
		if err != nil {
			return nil, err
		}
		if ok {
			out.mechKinds[kind.GetDefName()] = row
		}
	}
	roles := v.GetMechWorkModes()
	out.work, out.escort, out.recharge = roles.GetWork(), roles.GetEscort(), roles.GetRecharge()
	for role, name := range map[string]string{"work": out.work, "escort": out.escort, "recharge": out.recharge} {
		if validID(name) != nil || DefRow[*d.MechWorkModeDef](catalog, name) == nil {
			return nil, contract("biotech mech work mode role %s is %q, not a def of the catalog", role, name)
		}
	}
	if err := validateGeneTuning(v.GeneTuning); err != nil {
		return nil, err
	}
	return out, nil
}

// mechKind is kind as a controllable mech: a PawnKindDef whose race is a
// mechanoid with an overseer-subject comp. Bandwidth cost is the race's stat.
func (catalog *DefinitionCatalog) mechKind(kind *d.PawnKindDef) (mechKindRow, bool, error) {
	race := catalog.ThingDefs[kind.GetRace()]
	if _, mechanoid, _ := catalog.RaceFlags(kind.GetRace()); race == nil || !mechanoid {
		return mechKindRow{}, false, nil
	}
	controllable, err := catalog.HasComp(race, classOverseerSubject)
	if err != nil || !controllable {
		return mechKindRow{}, false, err
	}
	cost, _, err := catalog.ShownStatValue(race.GetDefName(), "", statBandwidthCost)
	if err != nil {
		return mechKindRow{}, false, err
	}
	row := mechKindRow{race: race.GetDefName(), workMech: len(race.GetRace().GetMechEnabledWorkTypes()) > 0, bandwidthCost: float64(cost),
		combatPower: float64(kind.GetCombatPower()), workTypes: slices.Clone(race.GetRace().GetMechEnabledWorkTypes())}
	slices.Sort(row.workTypes)
	return row, true, nil
}

// validateGeneTuning bounds the GeneTuning singleton: finite
// numbers, ordered ranges, an ascending curve, nonnegative counts.
func validateGeneTuning(g *o.GeneTuningFacts) error {
	if g == nil {
		return nil
	}
	if err := catalogNumbers("gene tuning", g.RegrowDaysMin, g.RegrowDaysMax); err != nil {
		return err
	}
	if g.BiostatMin != nil && g.BiostatMax != nil && g.GetBiostatMin() > g.GetBiostatMax() {
		return contract("gene tuning biostat range is descending")
	}
	if g.RegrowDaysMin != nil && g.RegrowDaysMax != nil && g.GetRegrowDaysMin() > g.GetRegrowDaysMax() {
		return contract("gene tuning regrow range is descending")
	}
	for _, v := range []*int32{g.BaseMaxComplexity, g.ExtractTicks, g.NoPowerEjectTicks} {
		if v != nil && *v < 0 {
			return contract("gene tuning has a negative count")
		}
	}
	last := math.Inf(-1)
	for _, p := range g.CreationHoursCurve {
		if p.X == nil || p.Y == nil {
			return contract("gene tuning curve point is incomplete")
		}
		if err := catalogNumbers("gene tuning curve", p.X, p.Y); err != nil {
			return err
		}
		if p.GetX() <= last {
			return contract("gene tuning curve is not ascending")
		}
		last = p.GetX()
	}
	return nil
}

// validatePawnBiotech bounds a pawn row's Biotech block.
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
	for _, n := range []*int32{b.XenogermRegrowTicksLeft, b.XenogermComaTicksLeft} {
		if n != nil && *n < 0 {
			return contract("negative xenogerm ticks left")
		}
	}
	if b.ExtractableReason != nil && (b.Extractable == nil || b.GetExtractable() || len(b.GetExtractableReason()) > 512 || !asciiOnly(b.GetExtractableReason())) {
		return contract("invalid extractable reason")
	}
	return pawnsIssues(b.Issues, b.ProtoReflect())
}

func asciiOnly(s string) bool {
	for i := 0; i < len(s); i++ {
		if s[i] >= 0x80 {
			return false
		}
	}
	return true
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
	if field("gene_lifecycle") {
		r.XenogermRegrowTicksLeft, r.XenogermComaTicksLeft, r.InExtractor = optionalInt(b.XenogermRegrowTicksLeft), optionalInt(b.XenogermComaTicksLeft), optionalFact(b.InExtractor)
	}
	if field("extractable") && b.Extractable != nil {
		r.Extractable, r.ExtractableReason = domain.Known(b.GetExtractable()), domain.Known(b.GetExtractableReason())
	}
	return domain.Known(r)
}

func optionalInt(p *int32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}
