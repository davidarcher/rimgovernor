package main

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// unpinnedStartExempt are the fixture cases allowed to start on an
// unpinned random debug world, each with its reason. Every other
// fixture case starts on cases.LabStart() and spawns what it needs at
// known coordinates.
var unpinnedStartExempt = map[string]string{
	"speedmatrix/plain":      "hand-run diagnostic outside every tier (#739)",
	"medical/stable-patient": "hand-run diagnostic outside every tier (#739)",
}

// unpinnedFixtureStart reports whether start is a cases.Fixture whose
// innermost start is the random debug colony: no On, or a DebugStart
// with no Seed.
func unpinnedFixtureStart(start cases.Start) bool {
	f, ok := start.(cases.Fixture)
	if !ok {
		return false
	}
	on := f.On
	for {
		inner, ok := on.(cases.Fixture)
		if !ok {
			break
		}
		on = inner.On
	}
	switch s := on.(type) {
	case nil:
		return true
	case cases.DebugStart:
		return s.Size.Seed == ""
	}
	return false
}

// TestFixtureCasesStartPinned: a new fixture case cannot start on
// an unpinned random world by default.
func TestFixtureCasesStartPinned(t *testing.T) {
	seen := map[string]bool{}
	for _, c := range cases.All() {
		if !unpinnedFixtureStart(c.Start) {
			continue
		}
		seen[c.Name] = true
		if _, ok := unpinnedStartExempt[c.Name]; !ok {
			t.Errorf("%s: fixture %v starts on an unpinned random debug world; use On: cases.LabStart() and spawn at known coordinates, or add it to unpinnedStartExempt with a terrain reason", c.Name, c.Start.Describe()["op"])
		}
	}
	for name := range unpinnedStartExempt {
		if !seen[name] {
			t.Errorf("%s is exempt but no longer starts unpinned; drop it from unpinnedStartExempt", name)
		}
	}
}

func TestUnpinnedFixtureStartRefusesRandomWorld(t *testing.T) {
	for _, tc := range []struct {
		name  string
		start cases.Start
		want  bool
	}{
		{"no On", cases.Fixture{Op: "test/x"}, true},
		{"unseeded debug", cases.Fixture{Op: "test/x", On: cases.DebugStart{}}, true},
		{"nested unseeded", cases.Fixture{Op: "test/y", On: cases.Fixture{Op: "test/x"}}, true},
		{"lab", cases.Fixture{Op: "test/x", On: cases.LabStart()}, false},
		{"save", cases.Fixture{Op: "test/x", On: cases.Save{Name: "baseline"}}, false},
		{"plain debug start", cases.DebugStart{}, false},
	} {
		if got := unpinnedFixtureStart(tc.start); got != tc.want {
			t.Errorf("%s: unpinned = %v, want %v", tc.name, got, tc.want)
		}
	}
}
