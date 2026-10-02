package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PolicyEntry is one row of a native policy database (#1297): the policy's
// load id, label, the player pawns currently holding it and whether it is
// the database default. Allowed is a reading policy's allowed book
// definitions (#1306).
type PolicyEntry struct {
	ID, Label string
	Pawns     []policy.PawnID
	Default   bool
	Allowed   []string
}

// AllowedArea is one Area_Allowed on the colony map and the pawns
// restricted to it there.
type AllowedArea struct {
	ID, Label string
	Pawns     []policy.PawnID
}

// Policies is every outfit, drug, food and reading policy and every allowed
// area the colony holds.
type Policies struct {
	Outfit, Drug, Food, Reading []PolicyEntry
	AllowedAreas                []AllowedArea
	// Books is every book definition and its kind (#1306).
	Books []policy.Book
}

var bookKinds = map[o.BookKind]policy.BookKind{
	o.BookKind_BOOK_KIND_TEXTBOOK:  policy.Textbook,
	o.BookKind_BOOK_KIND_NOVEL:     policy.Novel,
	o.BookKind_BOOK_KIND_SCHEMATIC: policy.Schematic,
	o.BookKind_BOOK_KIND_TOME:      policy.Tome,
}

func pawnIDs(ids []string) []policy.PawnID {
	r := make([]policy.PawnID, 0, len(ids))
	for _, id := range ids {
		r = append(r, policy.PawnID(id))
	}
	return r
}

func ColonyPolicies(section *o.PolicySection) domain.Fact[Policies] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[Policies]{}
	}
	entries := func(rows []*o.PolicyEntry) []PolicyEntry {
		r := make([]PolicyEntry, 0, len(rows))
		for _, row := range rows {
			r = append(r, PolicyEntry{ID: row.GetId(), Label: row.GetLabel(), Pawns: pawnIDs(row.PawnIds), Default: row.GetDefault(), Allowed: row.AllowedDefs})
		}
		return r
	}
	r := Policies{Outfit: entries(f.Outfit), Drug: entries(f.Drug), Food: entries(f.Food), Reading: entries(f.Reading)}
	for _, b := range f.Books {
		r.Books = append(r.Books, policy.Book{Def: b.GetDefName(), Kind: bookKinds[b.GetKind()]})
	}
	for _, row := range f.AllowedAreas {
		r.AllowedAreas = append(r.AllowedAreas, AllowedArea{ID: row.GetId(), Label: row.GetLabel(), Pawns: pawnIDs(row.PawnIds)})
	}
	return domain.Known(r)
}

func apparelRequirements(rows []*o.ApparelRequirementFact) []policy.ApparelRequirement {
	r := make([]policy.ApparelRequirement, 0, len(rows))
	for _, row := range rows {
		r = append(r, policy.ApparelRequirement{BodyPartGroups: row.BodyPartGroups, RequiredDefs: row.RequiredDefs, RequiredTags: row.RequiredTags, AllowedTags: row.AllowedTags})
	}
	return r
}

// pawnPolicyInputs lifts a settings row's policy inputs; unknown when the
// producer carried none.
func pawnPolicyInputs(p *o.PawnPolicyInputs) domain.Fact[policy.PawnPolicyInputs] {
	if p == nil {
		return domain.Fact[policy.PawnPolicyInputs]{}
	}
	r := policy.PawnPolicyInputs{
		OutfitPolicy: p.GetOutfitPolicyId(), DrugPolicy: p.GetDrugPolicyId(), ReadingPolicy: p.GetReadingPolicyId(),
		DependencyChemicals: p.DependencyChemicals, RoyalTitle: p.GetRoyalTitle(), TitleApparel: apparelRequirements(p.TitleApparel),
		Ideo: p.GetIdeoId(), Precepts: p.Precepts, IdeoRole: p.GetIdeoRole(), RoleApparel: apparelRequirements(p.RoleApparel), PreceptApparel: p.PreceptApparel,
		GuestStatus: p.GetGuestStatus(), PrisonerInteraction: p.GetPrisonerInteraction(), SlaveInteraction: p.GetSlaveInteraction(),
		TendQuality: optional(p.MedicalTendQuality),
	}
	for _, s := range p.InventoryStock {
		r.InventoryStock = append(r.InventoryStock, policy.InventoryStock{Group: s.GetGroup(), Thing: s.GetThingDef(), Count: int(s.GetCount())})
	}
	for _, c := range p.Chemicals {
		r.Chemicals = append(r.Chemicals, policy.ChemicalState{Chemical: c.GetChemical(), Addiction: optional(c.Addiction), Withdrawal: c.GetWithdrawal(), Tolerance: optional(c.Tolerance)})
	}
	return domain.Known(r)
}

// shelterArea is the Safe allowed area's load id, "" when the map has none.
func shelterArea(p domain.Fact[Policies]) domain.Fact[string] {
	return allowedAreaID(p, policy.SafeAreaLabel)
}

// allowedAreaID is the load id of the allowed area labelled label, "" when
// the map has none.
func allowedAreaID(p domain.Fact[Policies], label string) domain.Fact[string] {
	v, known := p.Value()
	if !known {
		return domain.Unknown[string]()
	}
	for _, a := range v.AllowedAreas {
		if a.Label == label {
			return domain.Known(a.ID)
		}
	}
	return domain.Known("")
}
