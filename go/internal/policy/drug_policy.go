package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecreationDrug is a social drug a pawn may take for joy and the chemical
// whose tolerance and addiction it builds.
type RecreationDrug struct {
	Def, Chemical string
}

// RecreationDrugs are the Core joy drugs a per-pawn policy may allow
// (#1537). All three are addictive.
var RecreationDrugs = []RecreationDrug{{"Beer", "Alcohol"}, {"SmokeleafJoint", "Smokeleaf"}, {"PsychiteTea", "Psychite"}}

// HighTolerance is the chemical tolerance severity at and above which a
// pawn stops taking that chemical for joy: the addiction risk is high.
const HighTolerance = 0.5

// DrugPolicyEntry is one native drug policy: its load id, label, holders
// and the entries that allow anything.
type DrugPolicyEntry struct {
	ID, Label string
	Pawns     []PawnID
	Entries   []domain.DrugPolicyEntry
}

// DrugPolicyChange is what one pawn's drug policy owes: the contents to
// write (nil when the policy labelled with its short name already carries
// exactly them) and the assignment (nil when it already holds it).
type DrugPolicyChange struct {
	Write  *domain.DrugPolicy
	Assign *domain.PawnSettings
}

// joyEntry is a drug taken for joy only, scheduling fields at vanilla's
// defaults.
func joyEntry(def string) domain.DrugPolicyEntry {
	return domain.DrugPolicyEntry{Drug: def, Joy: true, DaysFrequency: 1, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1}
}

// DrugEntries is the drug policy pawn should hold (#1537): beer, smokeleaf
// and psychite tea for joy, each unless the pawn is addicted to its
// chemical or its tolerance is high. A child, a teetotaler and a pawn with
// a chemical interest or fascination (every recreation drug is addictive)
// get none. Everything else is off. Unknown while the pawn's age, traits or
// chemical state are.
func DrugEntries(pawn WorkPawn) ([]domain.DrugPolicyEntry, bool) {
	age, ak := pawn.Age.Value()
	traits, tk := pawn.Traits.Value()
	inputs, ik := pawn.PolicyInputs.Value()
	if !ak || !tk || !ik {
		return nil, false
	}
	entries := []domain.DrugPolicyEntry{}
	interest := 0
	for _, t := range traits {
		interest += TraitEffect(t).ChemicalInterest
	}
	if age < childAge || interest != 0 {
		return entries, true
	}
	for _, d := range RecreationDrugs {
		risky := false
		for _, c := range inputs.Chemicals {
			if c.Chemical != d.Chemical {
				continue
			}
			_, addicted := c.Addiction.Value()
			tolerance, tolerant := c.Tolerance.Value()
			risky = risky || addicted || c.Withdrawal || tolerant && tolerance >= HighTolerance
		}
		if !risky {
			entries = append(entries, joyEntry(d.Def))
		}
	}
	return entries, true
}

// DrugPolicyChanges are the per-pawn drug policy writes owed (#1537): each
// owned pawn with a drug tracker holds the policy labelled with its short
// name, carrying DrugEntries. A pawn whose short name another owned pawn
// shares (#1310 renames it) or that several policies carry waits.
func DrugPolicyChanges(pawns []WorkPawn, names []OwnedName, policies []DrugPolicyEntry) []DrugPolicyChange {
	short, count := map[PawnID]string{}, map[string]int{}
	for _, n := range names {
		short[n.Pawn] = n.Short
		count[strings.ToLower(n.Short)]++
	}
	var out []DrugPolicyChange
	for _, pawn := range pawns {
		inputs, ok := pawn.PolicyInputs.Value()
		name := short[pawn.ID]
		if !ok || inputs.DrugPolicy == "" || name == "" || count[strings.ToLower(name)] != 1 {
			continue
		}
		want, ok := DrugEntries(pawn)
		if !ok {
			continue
		}
		v, err := domain.NewDrugPolicy(name, want)
		if err != nil {
			continue
		}
		var own []DrugPolicyEntry
		for _, p := range policies {
			if p.Label == name {
				own = append(own, p)
			}
		}
		if len(own) > 1 {
			continue
		}
		var change DrugPolicyChange
		if len(own) == 0 || !drugEntriesEqual(own[0].Entries, v.Entries()) {
			change.Write = &v
		}
		if len(own) == 0 || inputs.DrugPolicy != own[0].ID {
			s, err := domain.NewDrugPolicySetting(domain.PawnID(pawn.ID), name)
			if err != nil {
				continue
			}
			change.Assign = &s
		}
		if change.Write != nil || change.Assign != nil {
			out = append(out, change)
		}
	}
	return out
}

// drugEntriesEqual compares observed entries with canonical ones; the
// observed order is native's.
func drugEntriesEqual(observed, want []domain.DrugPolicyEntry) bool {
	got := slices.Clone(observed)
	slices.SortFunc(got, func(a, b domain.DrugPolicyEntry) int { return strings.Compare(a.Drug, b.Drug) })
	return slices.Equal(got, want)
}
