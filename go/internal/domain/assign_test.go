package domain

import "testing"

// An assignment names a valid pawn, an assignable thing and the previous
// assignment of that kind (or none); anything else is refused.
func TestAssignValidation(t *testing.T) {
	known, err := KnownPrevious("Throne_1")
	if err != nil {
		t.Fatal(err)
	}
	for name, tc := range map[string]struct {
		pawn     PawnID
		thing    string
		previous PreviousAssignment
		ok       bool
	}{
		"throne with no previous":        {"Human1", "Throne_2", ClearPrevious(), true},
		"throne replacing another":       {"Human1", "Throne_2", known, true},
		"bed":                            {"Human1", "Bed_2", ClearPrevious(), true},
		"no pawn":                        {"", "Throne_2", ClearPrevious(), false},
		"no thing":                       {"Human1", "", ClearPrevious(), false},
		"pawn as thing":                  {"Human1", "Human1", ClearPrevious(), false},
		"unset previous":                 {"Human1", "Throne_2", PreviousAssignment{}, false},
		"previous is the assigned thing": {"Human1", "Throne_1", known, false},
	} {
		if _, err := NewAssign(tc.pawn, tc.thing, tc.previous); (err == nil) != tc.ok {
			t.Errorf("%s: %v", name, err)
		}
	}
	if _, err := KnownPrevious(""); err == nil {
		t.Error("an empty previous identity was accepted")
	}
	assign, err := NewAssign("Human1", "Throne_2", known)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewAssignAction("", assign); err == nil {
		t.Error("an assign action without an identity was accepted")
	}
	if _, err := NewAssignAction("a1", Assign{}); err == nil {
		t.Error("an empty assignment was accepted")
	}
	action, err := NewAssignAction("a1", assign)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := action.Assign(); !ok || got != assign || action.Kind() != AssignAction {
		t.Fatalf("%v %v", got, ok)
	}
}
