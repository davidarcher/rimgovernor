package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoundsLaborCountsEnabledWorkTypes(t *testing.T) {
	builder := WorkPawn{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkConstruction, Priority: 1}, {Work: WorkResearch, Priority: 0}, {Work: WorkHauling, Priority: 3, Disabled: true}})}
	researcher := WorkPawn{ID: "b", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkResearch, Priority: 2}, {Work: WorkConstruction, Priority: 4}})}
	downed := WorkPawn{ID: "c", Available: domain.Known(false), Work: domain.Unknown[[]WorkPriority]()}
	labor, known := RoundsLabor([]WorkPawn{builder, researcher, downed}).Value()
	if !known || !reflect.DeepEqual(labor, map[WorkType]int{WorkConstruction: 2, WorkResearch: 1}) {
		t.Fatal(labor, known)
	}
	unknownWork := WorkPawn{ID: "d", Available: domain.Known(true), Applies: domain.Known(true)}
	if _, known := RoundsLabor([]WorkPawn{builder, unknownWork}).Value(); known {
		t.Fatal("unknown work settings became labor evidence")
	}
	emptyWork := WorkPawn{ID: "e", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{})}
	if _, known := RoundsLabor([]WorkPawn{builder, emptyWork}).Value(); known {
		t.Fatal("empty work list became labor evidence")
	}
	if ConcernLabor(EnsureComfort)[0] != WorkConstruction || ConcernLabor(EnsureResearch)[0] != WorkResearch || ConcernLabor(ActiveCombat) != nil {
		t.Fatal("unexpected goal labor profiles")
	}
	if ConcernLabor(MaintainHerd)[0] != WorkHandling {
		t.Fatal("unexpected animal labor profiles")
	}
}
