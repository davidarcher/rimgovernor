package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSafetyOverlayDrawsHoldingThreatsEdgeAndVetoes(t *testing.T) {
	bounds := Bounds{Width: 100, Height: 100}
	live := func(id PawnID, kind ThreatKind, animal bool, at domain.Cell) EmergencyThreat {
		return EmergencyThreat{ID: id, Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(animal), Distance: domain.Known(10.0), Position: domain.Known(at)}
	}
	threats := []EmergencyThreat{
		live("raider1", Hostile, false, domain.Cell{X: 5, Z: 50}),
		live("raider2", Hostile, false, domain.Cell{X: 8, Z: 52}),
		live("boar", Hostile, true, domain.Cell{X: 80, Z: 80}),
		// A downed raider holds nothing and draws nothing.
		{ID: "down", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(true), Animal: domain.Known(false), Position: domain.Known(domain.Cell{X: 50, Z: 5})},
	}
	loot := []LootItem{
		{Supply: StartingSupply{Thing: "a", Cell: domain.Cell{X: 40, Z: 40}}, SafetyKnown: true, Forbidden: true},
		{Supply: StartingSupply{Thing: "b", Cell: domain.Cell{X: 41, Z: 40}}, SafetyKnown: true},
		{Supply: StartingSupply{Thing: "c", Cell: domain.Cell{X: 45, Z: 45}}, SafetyKnown: true, SafeToHaul: true},
	}
	o := SafetyOverlay(threats, loot, bounds)
	styles := map[string][]OverlayStyle{}
	for _, l := range o.Layers {
		styles[l.Label] = append(styles[l.Label], l.Style)
	}
	for _, label := range []string{"danger", "raid edge"} {
		if s := styles[label]; len(s) != 2 || s[0] != OverlayFill || s[1] != OverlayOutline {
			t.Fatalf("%s styles %v", label, s)
		}
	}
	if s := styles["unsafe route"]; len(s) != 1 || s[0] != OverlayOutline {
		t.Fatalf("veto styles %v", s)
	}
	cells := 0
	for _, r := range o.Layers[0].Runs {
		cells += int(r.Length)
	}
	// The raiders' squares clipped at x=0 (26x41 and 29x41, overlapping in
	// 26x39) and the boar's clipped at the far edges (40x40); the downed
	// raider draws none.
	if want := 26*41 + 29*41 - 26*39 + 40*40; cells != want {
		t.Fatalf("danger cells %d, want %d", cells, want)
	}
	labels := map[string]domain.Cell{}
	for _, l := range o.Labels {
		if _, dup := labels[l.Text]; dup {
			t.Fatalf("label %q twice: %+v", l.Text, o.Labels)
		}
		labels[l.Text] = l.Cell
	}
	want := map[string]domain.Cell{"raid": {X: 5, Z: 50}, "manhunter": {X: 80, Z: 80}, "raid edge": {X: 50, Z: 7}, "unsafe route": {X: 40, Z: 40}}
	if len(labels) != len(want) {
		t.Fatalf("labels %+v", o.Labels)
	}
	for text, cell := range want {
		if labels[text] != cell {
			t.Fatalf("label %q at %+v, want %+v (%+v)", text, labels[text], cell, o.Labels)
		}
	}
}

func TestSafetyOverlayIsEmptyWithNothingHeldOrVetoed(t *testing.T) {
	distant := EmergencyThreat{ID: "boar", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(true), Distance: domain.Known(DistantThreatCells + 1), Position: domain.Known(domain.Cell{X: 1, Z: 1})}
	safe := []LootItem{{Supply: StartingSupply{Thing: "a", Cell: domain.Cell{X: 4, Z: 4}}, SafetyKnown: true, SafeToHaul: true}}
	if o := SafetyOverlay([]EmergencyThreat{distant}, safe, Bounds{Width: 50, Height: 50}); len(o.Layers) != 0 || len(o.Labels) != 0 {
		t.Fatalf("overlay %+v", o)
	}
}
