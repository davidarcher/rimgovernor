package domain

import "testing"

func TestGoalKindWhitelistMatchesPythonContract(t *testing.T) {
	t.Parallel()
	// The exact Literal from controller/rimgovernor/player_commands.py's
	// CreateGoal, in order. A kind added on one side and not the other is a
	// silent divergence between the two controllers.
	want := []string{"EnsureFoodSupply", "EnsureInitialShelter", "EnsureFoodStorage", "EnsureCooking",
		"EnsureTemperatureSafety", "EnsureBasicPower", "EnsureBasicDefense", "MaintainWood", "MaintainResource", "MaintainWaste"}
	kinds := GoalKinds()
	if len(kinds) != len(want) {
		t.Fatal("goal kind whitelist changed size", kinds)
	}
	seen := map[GoalKind]bool{}
	for n, kind := range kinds {
		if string(kind) != want[n] {
			t.Fatal("goal kind out of contract order", n, kind)
		}
		if seen[kind] {
			t.Fatal("duplicate goal kind", kind)
		}
		seen[kind] = true
		known, err := NewGoalKind(want[n])
		if err != nil || known != kind || !known.Set() {
			t.Fatal(known, err)
		}
	}
}

func TestGoalKindRejectsUnlistedAndCarriesPythonPriority(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"", "EnsureComfort", "MaintainHerd", "ensurefoodsupply", "EnsureFoodSupply ", "routine-EnsureFoodSupply"} {
		if kind, err := NewGoalKind(name); err == nil || kind != "" {
			t.Fatal("unlisted goal kind accepted", name, kind)
		}
	}
	if GoalKind("").Set() {
		t.Fatal("unset kind reported as set")
	}
	// Python's handler uses priority_class 2 for every kind and raises
	// MaintainWaste alone to 3.
	for _, kind := range GoalKinds() {
		want := 2
		if kind == MaintainWasteGoal {
			want = 3
		}
		if kind.Priority() != want {
			t.Fatal("unexpected activation priority", kind, kind.Priority())
		}
		// Every activation priority must be a priority a Goal accepts.
		if _, err := NewGoal(GoalID(kind), PlayerGoal, kind.Priority(), GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}, 0); err != nil {
			t.Fatal(kind, err)
		}
	}
}
