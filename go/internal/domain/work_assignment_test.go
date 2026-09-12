package domain

import "testing"

func TestWorkAssignmentImmutableAndCanonical(t *testing.T) {
	input := []WorkSetting{{"Growing", 3}, {"Cooking", 0}}
	work, err := NewWorkAssignment("pawn", "token", false, input)
	if err != nil {
		t.Fatal(err)
	}
	input[0].Priority = 4
	output := work.Settings()
	output[0].Priority = 4
	same, err := NewWorkAssignment("pawn", "token", false, []WorkSetting{{"Cooking", 0}, {"Growing", 3}})
	if err != nil || work != same {
		t.Fatal("mutable or noncanonical work intent", err)
	}
	for _, rows := range [][]WorkSetting{nil, {{"Cooking", 1}}, {{"Cooking", 3}, {"Cooking", 0}}, {{"Cooking", 5}}} {
		if _, err := NewWorkAssignment("pawn", "token", false, rows); err == nil {
			t.Fatal("invalid checkbox assignment", rows)
		}
	}
}
