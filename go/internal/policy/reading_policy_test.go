package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testBooks = []Book{{"TextBook", Textbook}, {"Novel", Novel}, {"Schematic", Schematic}, {"Tome", Tome}}

func reader(id string, age float64, passion string, research int) WorkPawn {
	return WorkPawn{
		ID:           PawnID(id),
		Age:          domain.Known(age),
		Skills:       domain.Known([]WorkSkill{{Name: "Shooting", Level: 3, Passion: passion}}),
		Work:         domain.Known([]WorkPriority{{Work: WorkResearch, Priority: research}}),
		PolicyInputs: domain.Known(PawnPolicyInputs{ReadingPolicy: "ReadingPolicy_1"}),
	}
}

// A researcher is allowed schematics; a child learning books only; nobody
// tomes (#1306).
func TestReadingBooks(t *testing.T) {
	for _, tc := range []struct {
		name string
		pawn WorkPawn
		want []string
	}{
		{"researcher", reader("A", 30, "None", 1), []string{"Novel", "Schematic"}},
		{"passionate", reader("A", 30, "Major", 0), []string{"Novel", "TextBook"}},
		{"child", reader("A", 8, "Major", 1), []string{"TextBook"}},
		{"plain adult", reader("A", 30, "None", 0), []string{"Novel"}},
	} {
		got, ok := ReadingBooks(tc.pawn, testBooks)
		if !ok || !slices.Equal(got, tc.want) {
			t.Errorf("%s: got %v want %v", tc.name, got, tc.want)
		}
	}
	unknown := reader("A", 30, "None", 0)
	unknown.Age = domain.Unknown[float64]()
	if _, ok := ReadingBooks(unknown, testBooks); ok {
		t.Error("unknown age planned")
	}
}

func TestReadingPolicyChanges(t *testing.T) {
	pawns := []WorkPawn{reader("A", 30, "None", 1), reader("B", 8, "None", 0), reader("C", 30, "None", 0), reader("D", 30, "None", 0), reader("E", 30, "None", 0)}
	names := []OwnedName{{"A", "Ann", 1}, {"B", "Bo", 2}, {"C", "Cy", 3}, {"D", "Dup", 4}, {"E", "dup", 5}}
	pawns[2].PolicyInputs = domain.Known(PawnPolicyInputs{ReadingPolicy: "ReadingPolicy_3"})
	policies := []ReadingPolicyEntry{
		{ID: "ReadingPolicy_1", Label: "All"},
		{ID: "ReadingPolicy_2", Label: "Bo", Allowed: []string{"TextBook"}},
		{ID: "ReadingPolicy_3", Label: "Cy", Allowed: []string{"Novel"}},
	}
	got := ReadingPolicyChanges(pawns, names, policies, testBooks)
	if len(got) != 2 {
		t.Fatalf("got %d changes: %+v", len(got), got)
	}
	// Ann has no policy: write and assign.
	if got[0].Write == nil || got[0].Write.Name() != "Ann" || !slices.Equal(got[0].Write.Definitions(), []string{"Novel", "Schematic"}) || got[0].Assign == nil {
		t.Fatalf("Ann: %+v", got[0])
	}
	if name, _ := got[0].Assign.ReadingPolicy(); name != "Ann" || got[0].Assign.Pawn() != "A" {
		t.Fatalf("Ann assign: %+v", got[0].Assign)
	}
	// Bo's policy matches; only the assignment is owed. Cy holds its own
	// matching policy; the duplicate short names wait for #1310.
	if got[1].Write != nil || got[1].Assign == nil || got[1].Assign.Pawn() != "B" {
		t.Fatalf("Bo: %+v", got[1])
	}
}
