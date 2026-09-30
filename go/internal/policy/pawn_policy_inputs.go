package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PawnPolicyInputs is what the per-pawn outfit/drug/food/reading planners
// compose from (#1297). Traits, mood and break thresholds and animal bonds
// are read elsewhere on the pawn row. Empty strings are "none".
type PawnPolicyInputs struct {
	OutfitPolicy, DrugPolicy, ReadingPolicy string
	// InventoryStock is the pawn's carry settings (medicine).
	InventoryStock []InventoryStock
	// Chemicals holds each chemical the pawn is addicted to or tolerant of.
	Chemicals []ChemicalState
	// DependencyChemicals names the chemicals of chemical-dependency genes.
	DependencyChemicals []string
	RoyalTitle          string
	TitleApparel        []ApparelRequirement
	// Ideo is the ideoligion load id; Precepts every precept defName of it;
	// IdeoRole the pawn's role precept defName.
	Ideo           string
	Precepts       []string
	IdeoRole       string
	RoleApparel    []ApparelRequirement
	PreceptApparel []string
	// GuestStatus is Guest, Prisoner or Slave; empty for a free pawn.
	GuestStatus, PrisonerInteraction, SlaveInteraction string
	// TendQuality is the MedicalTendQuality stat (#1305); unknown for a
	// pawn that cannot doctor.
	TendQuality domain.Fact[float64]
}

type InventoryStock struct {
	Group, Thing string
	Count        int
}

type ChemicalState struct {
	Chemical   string
	Addiction  domain.Fact[float64]
	Withdrawal bool
	Tolerance  domain.Fact[float64]
}

type ApparelRequirement struct {
	BodyPartGroups, RequiredDefs, RequiredTags, AllowedTags []string
}
