package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The stone-shell bundle (#293) is dispatched under the routine worker like
// every other routine method: its demolition and backup-removal steps are
// WallRemovalActions, and a kind missing from the allowlist leaves the
// admitted plan's demolition at pending forever, the backups standing.
func TestWallRemovalIsARoutineExecutableKind(t *testing.T) {
	t.Parallel()
	for _, kind := range []domain.ActionKind{domain.BuildingAction, domain.WallRemovalAction, domain.HomeCoverageAction} {
		if !routineExecutableKind(kind) {
			t.Fatalf("%s must be routine executable", kind)
		}
	}
}
