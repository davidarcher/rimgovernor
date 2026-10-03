package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PolicyEntry is one row of a native policy database (#1297): the policy's
// load id, label, the player pawns currently holding it and whether it is
// the database default. Allowed is a reading policy's allowed book
// definitions (#1306) or a food policy's allowed foods (#1541); Drugs a
// drug policy's entries that allow anything (#1537).
type PolicyEntry struct {
	ID, Label string
	Pawns     []policy.PawnID
	Default   bool
	Allowed   []string
	Drugs     []domain.DrugPolicyEntry
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
	// Books is every book definition and its kind (#1306), read from the
	// definition catalog by DecodeColony (#1733); empty without one.
	Books []policy.Book
	// BiomeDiseases is the colony map biome's disease hediffs (#1539).
	BiomeDiseases []string
	// Foods is every food definition and its kind (#1541), read from the
	// definition catalog by DecodeColony (#1733); empty without one.
	Foods []policy.Food
	// FoodEaters are the prisoners and tame animals holding food policies (#1543).
	FoodEaters []policy.FoodEater
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
			e := PolicyEntry{ID: row.GetId(), Label: row.GetLabel(), Pawns: pawnIDs(row.PawnIds), Default: row.GetDefault(), Allowed: row.AllowedDefs}
			for _, d := range row.DrugEntries {
				e.Drugs = append(e.Drugs, domain.DrugPolicyEntry{Drug: d.GetDrugDef(), Joy: d.GetAllowedForJoy(), Addiction: d.GetAllowedForAddiction(), Scheduled: d.GetAllowScheduled(),
					DaysFrequency: float64(d.GetDaysFrequency()), OnlyIfMoodBelow: float64(d.GetOnlyIfMoodBelow()), OnlyIfJoyBelow: float64(d.GetOnlyIfJoyBelow()), TakeToInventory: int(d.GetTakeToInventory())})
			}
			r = append(r, e)
		}
		return r
	}
	r := Policies{Outfit: entries(f.Outfit), Drug: entries(f.Drug), Food: entries(f.Food), Reading: entries(f.Reading), BiomeDiseases: f.BiomeDiseases}
	for _, e := range f.FoodEaters {
		r.FoodEaters = append(r.FoodEaters, policy.FoodEater{Pawn: policy.PawnID(e.GetPawnId()), Animal: e.GetKind() == o.FoodEaterKind_FOOD_EATER_KIND_ANIMAL,
			Traits: e.Traits, Precepts: e.Precepts, Edible: e.EdibleDefs})
	}
	for _, row := range f.AllowedAreas {
		r.AllowedAreas = append(r.AllowedAreas, AllowedArea{ID: row.GetId(), Label: row.GetLabel(), Pawns: pawnIDs(row.PawnIds)})
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
