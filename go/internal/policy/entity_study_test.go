package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func heldEntity(id domain.PawnID, studiable domain.Fact[bool]) CapturableEntity {
	return CapturableEntity{Pawn: id, Dead: domain.Known(false), Held: domain.Known(true), CurrentlyStudiable: studiable}
}

func studyPlanning(entities ...CapturableEntity) ContainmentPlanning {
	return ContainmentPlanning{Entities: domain.Known(entities)}
}

func TestStudyWorkIsOwedWhileAHeldEntityIsStudiable(t *testing.T) {
	work, reason := StudyWork(studyPlanning(heldEntity("e1", domain.Known(true))))
	rows, ok := work.Value()
	if !ok || reason != "" || len(rows) != 1 || rows[0].Work != WorkDarkStudy {
		t.Fatalf("%+v %v %q", rows, ok, reason)
	}
}

// The interval is the game's: a held entity between studies reports not
// currently studiable and owes no owner.
func TestStudyWorkWaitsOutTheStudyInterval(t *testing.T) {
	work, _ := StudyWork(studyPlanning(heldEntity("e1", domain.Known(false))))
	if rows, ok := work.Value(); !ok || len(rows) != 0 {
		t.Fatalf("%+v %v", rows, ok)
	}
}

func TestStudyWorkIgnoresUnheldAndDeadEntities(t *testing.T) {
	unheld := heldEntity("e1", domain.Known(true))
	unheld.Held = domain.Known(false)
	dead := heldEntity("e2", domain.Known(true))
	dead.Dead = domain.Known(true)
	if rows, ok := mustStudy(t, studyPlanning(unheld, dead)); !ok || len(rows) != 0 {
		t.Fatalf("%+v %v", rows, ok)
	}
	// No Anomaly: nothing is held.
	if rows, ok := mustStudy(t, ContainmentPlanning{Entities: domain.Unknown[[]CapturableEntity]()}); !ok || len(rows) != 0 {
		t.Fatalf("%+v %v", rows, ok)
	}
}

func mustStudy(t *testing.T, p ContainmentPlanning) ([]WorkRequirement, bool) {
	t.Helper()
	work, _ := StudyWork(p)
	return work.Value()
}

func TestStudyWorkFailsLoudlyOnUnreadFacts(t *testing.T) {
	for name, e := range map[string]CapturableEntity{
		"studiable": heldEntity("e1", domain.Unknown[bool]()),
		"held":      {Pawn: "e1", Dead: domain.Known(false), Held: domain.Unknown[bool](), CurrentlyStudiable: domain.Known(true)},
	} {
		work, reason := StudyWork(studyPlanning(heldEntity("e0", domain.Known(true)), e))
		if _, ok := work.Value(); ok || reason == "" {
			t.Fatalf("%s: %v %q", name, ok, reason)
		}
	}
}
