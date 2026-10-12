package policy

import "testing"

// ParseCombatTuning is inert when unset, overlays the default when given and
// fails loudly on anything it cannot trust.
func TestParseCombatTuning(t *testing.T) {
	for _, raw := range []string{"", "  "} {
		if got, err := ParseCombatTuning(raw); got != nil || err != nil {
			t.Errorf("ParseCombatTuning(%q) = %v, %v; want nil, nil", raw, got, err)
		}
	}
	got, err := ParseCombatTuning(`{"ScatterCells": 8}`)
	if err != nil {
		t.Fatal(err)
	}
	want := DefaultCombatTuning()
	want.ScatterCells = 8
	if *got != want {
		t.Errorf("got %+v, want the default with ScatterCells 8", *got)
	}
	for _, raw := range []string{`{"ScatterCells": 61}`, `{"Scatter": 8}`, `{"ScatterCells": 8} {}`, `not json`} {
		if got, err := ParseCombatTuning(raw); err == nil {
			t.Errorf("ParseCombatTuning(%q) = %+v, want an error", raw, got)
		}
	}
}
