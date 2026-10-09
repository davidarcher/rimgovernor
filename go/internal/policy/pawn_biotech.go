package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PawnBiotech is one pawn's Biotech facts as the row carries them.
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
	// the catalog; known whenever Genes is.
	Effects domain.Fact[GeneEffects]
	// WorkMinAges is the race's minimum age in years per work type, resolved
	// from the catalog; known only for a child.
	WorkMinAges domain.Fact[map[WorkType]int]
	Mechanitor  domain.Fact[*PawnMechanitor]
	Mech        domain.Fact[*PawnMech]
	Deathrest   domain.Fact[*PawnDeathrest]
	// XenogermRegrowTicksLeft and XenogermComaTicksLeft are the ticks left on
	// the pawn's XenogermReplicating and XenogerminationComa hediffs, known 0
	// with none. A pawn with regrow ticks left dies if extracted again.
	XenogermRegrowTicksLeft, XenogermComaTicksLeft domain.Fact[int]
	// InExtractor is a pawn held by a gene extractor.
	InExtractor domain.Fact[bool]
	// Extractable is the game's Building_GeneExtractor.CanAcceptPawn verdict
	// (true when any owned extractor of the map accepts); ExtractableReason is
	// the first refusal text, known "" when the game gave none. Unknown with no
	// extractor, off the map or when the read failed.
	Extractable       domain.Fact[bool]
	ExtractableReason domain.Fact[string]
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
// work mode (MechWorkModeDef name) and control group index. Energy is the
// Need_MechEnergy level (0-1); RechargeBelow and RechargeAbove are the
// control group's own recharge band (mechRechargeThresholds, 0-1).
type PawnMech struct {
	Overseer                             string
	WorkMode                             domain.Fact[string]
	ControlGroup                         domain.Fact[int]
	Energy, RechargeBelow, RechargeAbove domain.Fact[float64]
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
// pawn in deathrest takes neither work nor fights. An unknown
// deathrest read counts as not resting.
func (b PawnBiotech) IsDeathresting() bool {
	d, ok := b.Deathrest.Value()
	if !ok || d == nil {
		return false
	}
	resting, ok := d.Deathresting.Value()
	return ok && resting
}
