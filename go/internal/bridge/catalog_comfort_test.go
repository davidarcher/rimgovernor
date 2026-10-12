package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestComfortModelFromRecordedCatalog reads the comfort and beauty curves, the
// furniture that feeds comfort and the thoughts they read through from the
// recorded game (#2975).
func TestComfortModelFromRecordedCatalog(t *testing.T) {
	catalog := fullCatalog(t)
	shapes, err := catalog.PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	m, err := catalog.comfortModel(shapes.Furniture)
	if err != nil {
		t.Fatal(err)
	}
	near := func(a, b float64) bool { return math.Abs(a-b) < 1e-6 }
	if c := m.Comfort; !near(c.RisePerHour, 0.6) || !near(c.FallPerHour, 0.04) || c.Base != 0.5 || c.FreezeAsleep || !c.Valid() {
		t.Errorf("comfort curve = %+v", c)
	}
	if c := m.Beauty; !near(c.RisePerHour, 0.32) || !near(c.FallPerHour, 0.08) || !near(c.Base, 0.4) || !c.FreezeAsleep || !c.Valid() {
		t.Errorf("beauty curve = %+v", c)
	}
	if !near(m.BedOffset, 0.1) {
		t.Errorf("bed offset = %v, want the end table and dresser's 0.05 each", m.BedOffset)
	}
	has := func(options []policy.ComfortOption, def string) bool {
		for _, o := range options {
			if o.Def == def && o.Comfort > 0 {
				return true
			}
		}
		return false
	}
	if !has(m.Chairs, "Armchair") || !has(m.Chairs, "DiningChair") {
		t.Errorf("chairs = %+v", m.Chairs)
	}
	if has(m.Chairs, "Throne") {
		t.Error("a throne is a chair")
	}
	if !has(m.Beds, "Bed") {
		t.Errorf("beds = %+v", m.Beds)
	}
	for _, o := range append(append([]policy.ComfortOption{}, m.Chairs...), m.Beds...) {
		if o.Build < 0 || math.IsNaN(o.Build) {
			t.Errorf("option %+v has no cost", o)
		}
	}
	// The thoughts the curves are read through are in the mood model.
	mood, err := catalog.moodModel()
	if err != nil {
		t.Fatal(err)
	}
	game := policy.SeekerGame()
	for thought, want := range map[string][]float64{game.Comfort.Thought: {-3, 4, 6, 8, 10}, game.Beauty.Thought: {-15, -10, -5, 5, 10, 15}} {
		stages := mood.Stages[thought]
		if len(stages) != len(want) {
			t.Fatalf("%s stages = %+v", thought, stages)
		}
		for i, s := range stages {
			if !s.Present || s.Effect != want[i] {
				t.Errorf("%s stage %d = %+v, want %v", thought, i, s, want[i])
			}
		}
	}
}
