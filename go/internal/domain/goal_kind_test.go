package domain

import "testing"

func TestGoalKindWhitelistIsStable(t *testing.T) {
	t.Parallel()
	// The exact whitelist, in order. A kind added to the dashboard or docs
	// and not here is a silent divergence.
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

func TestGoalKindRejectsUnlistedAndCarriesPriority(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"", "EnsureComfort", "MaintainHerd", "ensurefoodsupply", "EnsureFoodSupply ", "routine-EnsureFoodSupply"} {
		if kind, err := NewGoalKind(name); err == nil || kind != "" {
			t.Fatal("unlisted goal kind accepted", name, kind)
		}
	}
	if GoalKind("").Set() {
		t.Fatal("unset kind reported as set")
	}
	// priority_class 2 for every kind; MaintainWaste alone starts at 3.
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
