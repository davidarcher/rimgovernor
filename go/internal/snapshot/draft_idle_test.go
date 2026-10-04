package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded from acceptance runs draft/idle and draft/idle-hostile (#748,
// replacing those native cases) at 04b0a98c, the first review of each run
// (tick 15; the draft/idle run later stalled on native read timeouts, after
// this review had filed): four armed tribal8 colonists
// idle under an unclaimed player draft, and in the hostile run a ranged
// opponent group beside them. The review decides the goal an idle draft is
// adopted under: RestoreWorkers when the colony is at peace, ActiveCombat
// (squad defense keeps the drafts) when a hostile is present. Which pawns
// are adopted is buildingruntime's idleDrafts, covered over the same pawn
// shapes by TestIdleDraftObservationGuards.
const (
	idlePeaceful = "testdata/draft-idle-peaceful.json"
	idleHostile  = "testdata/draft-idle-hostile.json"
)

func TestIdleDraftsAtPeaceOpenRestoreWorkers(t *testing.T) {
	r, err := Load(idlePeaceful)
	if err != nil {
		t.Fatal(err)
	}
	if n, _ := r.Facts.Hostiles.Value(); n != 0 {
		t.Fatalf("hostiles %v", r.Facts.Hostiles)
	}
	a, err := r.Assessment(policy.RestoreWorkers)
	if err != nil || a.Finding != domain.FindingUnmet {
		t.Fatal("drafted idle colonists at peace must open RestoreWorkers:", a, err)
	}
	if a, err = r.Assessment(policy.ActiveCombat); err == nil && a.Finding == domain.FindingUnmet {
		t.Fatal("no combat at peace", a)
	}
}

func TestIdleDraftsUnderHostilesStayWithCombat(t *testing.T) {
	r, err := Load(idleHostile)
	if err != nil {
		t.Fatal(err)
	}
	a, err := r.Assessment(policy.ActiveCombat)
	if err != nil || a.Finding != domain.FindingUnmet {
		t.Fatal("hostiles must open ActiveCombat:", a, err)
	}
	if a, err = r.Assessment(policy.RestoreWorkers); err == nil && a.Finding == domain.FindingUnmet {
		t.Fatal("idle drafts released while hostiles stand:", a)
	}
	// The same colony at peace releases them: the hostile count alone
	// decides.
	r.Facts.Hostiles = domain.Known(int64(0))
	if a, err = r.Assessment(policy.ActiveCombat); err == nil && a.Finding == domain.FindingUnmet {
		t.Fatal("combat without hostiles", a)
	}
}
