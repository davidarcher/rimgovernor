package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A stranger corpse goes to the butcher while it is fresh and the butchery
// is open, and to the incinerator otherwise; never anywhere else.
func TestStrangerCorpseRouting(t *testing.T) {
	for _, open := range []bool{true, false} {
		for _, rot := range []domain.RotStage{domain.RotFresh, domain.RotRotting, domain.RotDessicated, ""} {
			want := StrangerIncinerate
			if open && !rot.Spoiled() {
				want = StrangerButcher
			}
			if got := RouteStranger(rot, open); got != want {
				t.Errorf("open=%v rot=%q: %s", open, rot, got)
			}
		}
	}
}
