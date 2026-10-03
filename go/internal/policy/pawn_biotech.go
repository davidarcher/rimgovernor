package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PawnBiotech is one pawn's Biotech facts (#1678) as the row carries them.
// Every field is unknown when native did not read it; a pointer inside a
// Known fact is a known absence (a pawn that is no mechanitor, no mech, no
// deathrester). The defs are the catalog's (bridge.BiotechCatalog).
type PawnBiotech struct {
	// LifeStage is the LifeStageDef name; DevelopmentalStage the
	// DevelopmentalStage name (Newborn, Baby, Child, Adult, None).
	LifeStage, DevelopmentalStage domain.Fact[string]
	// Learning is the learning need level (0-1) and LearningCategory its
	// LearningCategory name; unknown for a pawn with no learning need.
	Learning         domain.Fact[float64]
	LearningCategory domain.Fact[string]
	// Genes is every gene, endogene and xenogene; Xenotype the XenotypeDef
	// name, known "" for a custom or no xenotype; XenotypeName the label.
	Genes        domain.Fact[[]PawnGene]
	Xenotype     domain.Fact[string]
	XenotypeName domain.Fact[string]
	Hybrid       domain.Fact[bool]
	// Effects are the active genes' combined typed effects, resolved from
	// the catalog (#1689); known whenever Genes is.
	Effects domain.Fact[GeneEffects]
	// WorkMinAges is the race's minimum age in years per work type, resolved
	// from the catalog (#1682); known only for a child.
	WorkMinAges domain.Fact[map[WorkType]int]
	Mechanitor  domain.Fact[*PawnMechanitor]
	Mech        domain.Fact[*PawnMech]
	Deathrest   domain.Fact[*PawnDeathrest]
}

// PawnGene is one gene of a pawn; Active is false while another gene
// overrides it.
type PawnGene struct {
	Name             string
	Xenogene, Active domain.Fact[bool]
}

// PawnMechanitor is a mechanitor's bandwidth and control groups.
type PawnMechanitor struct {
	UsedBandwidth, TotalBandwidth, GestationBandwidth domain.Fact[int]
	ControlGroups                                     domain.Fact[int]
	// ControlledMechs are the mechs under the pawn's control.
	ControlledMechs []string
}

// PawnMech is a controllable mech's overseer (known "" when it has none),
// work mode (MechWorkModeDef name) and control group index.
type PawnMech struct {
	Overseer     string
	WorkMode     domain.Fact[string]
	ControlGroup domain.Fact[int]
}

// PawnDeathrest is a deathrester's state.
type PawnDeathrest struct {
	Deathresting      domain.Fact[bool]
	Level             domain.Fact[float64]
	LastDeathrestTick domain.Fact[int]
	DeathrestPercent  domain.Fact[float64]
	Capacity          domain.Fact[int]
	BoundBuildings    domain.Fact[int]
	AutoWake          domain.Fact[bool]
}

// IsChild reports whether the developmental stage is known and is a
// pre-adult one (Newborn, Baby, Child).
func (b PawnBiotech) IsChild() (child, known bool) {
	stage, ok := b.DevelopmentalStage.Value()
	if !ok {
		return false, false
	}
	switch stage {
	case "Newborn", "Baby", "Child":
		return true, true
	}
	return false, true
}

// IsDeathresting reports whether the pawn is known to be deathresting: a
// pawn in deathrest takes neither work nor fights (#1690). An unknown
// deathrest read counts as not resting.
func (b PawnBiotech) IsDeathresting() bool {
	d, ok := b.Deathrest.Value()
	if !ok || d == nil {
		return false
	}
	resting, ok := d.Deathresting.Value()
	return ok && resting
}
