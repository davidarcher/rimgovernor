package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A stranger corpse that has lain in a sarcophagus fires no memory on a
// re-burial, so a deconstruct-eject-restage loop never starts: the
// ejected corpse is not owed a sarcophagus and waits for the morgue.
func TestEjectedStrangerCorpseIsNotRestaged(t *testing.T) {
	plan, _ := tombFixture()
	live := StrangerTomb{Live: 1, Funded: true}
	// Bury: a fresh stranger is owed a sarcophagus.
	fresh := []WasteItem{strangerCorpse("Corpse_s1")}
	if step := NextTombStep(plan, fresh, nil, testShapes, true, live); step.Kind != TombReconcile || step.Dead != 1 {
		t.Fatalf("fresh stranger: %+v", step)
	}
	// Dispose: the buried, now-flagged stranger's sarcophagus is deconstructed.
	_, _, built, buried := filledTomb(t, WasteItem{ID: "Corpse_s1", Kind: "corpse", CorpseOf: domain.CorpseStranger, EverBuriedInSarcophagus: true})
	if step := NextTombStep(plan, buried, built, testShapes, true, live); step.Kind != TombDispose {
		t.Fatalf("dispose: %+v", step)
	}
	// Eject: the flagged corpse lies exposed and is not counted or staged.
	ejected := strangerCorpse("Corpse_s1")
	ejected.EverBuriedInSarcophagus = true
	waste := []WasteItem{ejected}
	if step, _ := tombCensus(waste, nil, testSarcophagus, 4); step.Dead != 0 {
		t.Fatalf("tombCensus counted the flagged corpse: %+v", step)
	}
	if step := NextTombStep(plan, waste, nil, testShapes, true, live); step.Kind != TombNone || step.Dead != 0 {
		t.Fatalf("restaged: %+v", step)
	}
	if !MorgueWaiting(waste) {
		t.Fatal("the morgue did not take the flagged corpse")
	}
}
