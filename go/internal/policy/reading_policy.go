package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BookKind is what reading a book definition does.
type BookKind string

const (
	Textbook  BookKind = "textbook"
	Novel     BookKind = "novel"
	Schematic BookKind = "schematic"
	// Tome is an Anomaly book: research at the cost of a mental break.
	Tome BookKind = "tome"
)

// Book is one book ThingDef and its kind.
type Book struct {
	Def  string
	Kind BookKind
}

// ReadingPolicyEntry is one native reading policy: its load id, label,
// holders and allowed book definitions.
type ReadingPolicyEntry struct {
	ID, Label string
	Pawns     []PawnID
	Allowed   []string
}

// ReadingPolicyChange is what one pawn's reading policy owes: the contents
// to write (nil when the policy labelled with its short name already
// allows exactly them) and the assignment (nil when it already holds it).
type ReadingPolicyChange struct {
	Write  *domain.ReadingPolicy
	Assign *domain.PawnSettings
}

// childAge is the age below which a human is a child (DevelopmentalStage).
const childAge = 13

// ReadingBooks is the book definitions pawn should be allowed: a
// child learning books (textbooks) only; an adult novels for joy,
// textbooks when it has a passion to grow and schematics when it
// researches. Anomaly tomes are never allowed. Unknown when the pawn's age
// is.
func ReadingBooks(pawn WorkPawn, books []Book) ([]string, bool) {
	age, known := pawn.Age.Value()
	if !known {
		return nil, false
	}
	want := map[BookKind]bool{Textbook: age < childAge}
	if age >= childAge {
		want[Novel] = true
		if skills, ok := pawn.Skills.Value(); ok {
			for _, s := range skills {
				if !s.Disabled && s.Passion != "" && s.Passion != "None" {
					want[Textbook] = true
				}
			}
		}
		if work, ok := pawn.Work.Value(); ok {
			for _, w := range work {
				if w.Work == WorkResearch && !w.Disabled && w.Priority > 0 {
					want[Schematic] = true
				}
			}
		}
	}
	defs := []string{}
	for _, b := range books {
		if want[b.Kind] {
			defs = append(defs, b.Def)
		}
	}
	slices.Sort(defs)
	return slices.Compact(defs), true
}

// ReadingPolicyChanges are the per-pawn reading policy writes owed:
// each owned pawn with a reading tracker holds the policy labelled with its
// short name, allowing ReadingBooks. A pawn whose short name another owned
// pawn shares or that several policies carry waits.
func ReadingPolicyChanges(pawns []WorkPawn, names []OwnedName, policies []ReadingPolicyEntry, books []Book) []ReadingPolicyChange {
	short, count := map[PawnID]string{}, map[string]int{}
	for _, n := range names {
		short[n.Pawn] = n.Short
		count[strings.ToLower(n.Short)]++
	}
	var out []ReadingPolicyChange
	for _, pawn := range pawns {
		inputs, ok := pawn.PolicyInputs.Value()
		name := short[pawn.ID]
		if !ok || inputs.ReadingPolicy == "" || name == "" || count[strings.ToLower(name)] != 1 {
			continue
		}
		want, ok := ReadingBooks(pawn, books)
		if !ok {
			continue
		}
		var own []ReadingPolicyEntry
		for _, p := range policies {
			if p.Label == name {
				own = append(own, p)
			}
		}
		if len(own) > 1 {
			continue
		}
		var change ReadingPolicyChange
		if len(own) == 0 || !slices.Equal(sortedCopy(own[0].Allowed), want) {
			v, err := domain.NewReadingPolicy(name, want)
			if err != nil {
				continue
			}
			change.Write = &v
		}
		if len(own) == 0 || inputs.ReadingPolicy != own[0].ID {
			s, err := domain.NewReadingPolicySetting(domain.PawnID(pawn.ID), name)
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

func sortedCopy(v []string) []string {
	r := slices.Clone(v)
	slices.Sort(r)
	return slices.Compact(r)
}
