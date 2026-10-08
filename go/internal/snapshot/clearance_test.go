package snapshot

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Home clearance converted from the native clearance/* cases (#746), each
// recorded at 04b0a98c from `acceptance run clearance/<case>`.

// recovery replays path's clearance census through the recovery queue with a
// development slot. The recordings predate salvage evidence for Home rows, so
// a row without any is given the safe, yield-free evidence native now reports.
func recovery(t *testing.T, path string) (Rounds, []policy.ClearanceTarget, policy.RecoveryQueue) {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := r.Facts.Upkeep.Clearance.Value()
	if !known {
		t.Fatal(path, "clearance census unknown")
	}
	rows = slices.Clone(rows)
	for i := range rows {
		if rows[i].Salvage == nil {
			rows[i].Salvage = &policy.SalvageEvidence{Safe: domain.Known(true)}
		}
	}
	req, err := policy.ReviewRecoveryRequest(r.Policy, r.Facts, rows, nil)
	if err != nil {
		t.Fatal(err)
	}
	return r, rows, policy.RankRecovery(req)
}

// entry is the queue's row for id.
func entry(q policy.RecoveryQueue, id string) (policy.RecoveryEntry, bool) {
	for _, e := range q.Entries {
		if e.ID == id {
			return e, true
		}
	}
	return policy.RecoveryEntry{}, false
}

// working is true when the queue admits or queues id for removal.
func working(q policy.RecoveryQueue, id string) bool {
	e, ok := entry(q, id)
	return ok && (e.Status == policy.RecoveryAdmitted || e.Status == policy.RecoveryQueued)
}

// clearance/ancient-wall, tick 15: an unowned deconstructible ancient
// wall inside Home is worked by the queue and opens ClearHomeObstructions; no
// hold protects it.
func TestReplayAncientHomeWallAdmitted(t *testing.T) {
	const wall = "Thing_Wall44690"
	r, _, q := recovery(t, "testdata/clearance-ancient-wall.json")
	if !working(q, wall) {
		t.Errorf("%s not worked: %+v", wall, q.Entries)
	}
	if a, err := r.Assessment(policy.ClearHomeObstructions); err != nil || a.Finding != domain.FindingUnmet {
		t.Errorf("ClearHomeObstructions %+v %v, want deficit", a, err)
	}
}

// clearance/roof-support-refused, ticks 15 and 615: the Home wall holding up a
// roof is no longer held on native's per-building verdict (#2301); the queue
// works it and PlanRecoveryBatch takes the roofs down first. Supported, it
// is worked as before.
func TestReplayRoofBearingWallQueuedForRoofFirstBatch(t *testing.T) {
	const wall = "Thing_Wall44690"
	for _, path := range []string{"testdata/clearance-roof-held.json", "testdata/clearance-roof-supported.json"} {
		_, _, q := recovery(t, path)
		if !working(q, wall) {
			t.Errorf("%s: %s not worked: %+v", path, wall, q.Entries)
		}
	}
}

// clearance/standing-designation, tick 15: a wall already designated for
// deconstruction is no hold; the routine adopts it.
func TestReplayStandingDesignationAdopted(t *testing.T) {
	const wall = "Thing_Wall44690"
	_, rows, q := recovery(t, "testdata/clearance-standing-designation.json")
	if !working(q, wall) {
		t.Errorf("%s not worked: %+v", wall, q.Entries)
	}
	for _, row := range rows {
		if row.EntityID == wall && !row.Designated {
			t.Errorf("%s not designated", wall)
		}
	}
}

// shrineTargets replays the shrine clearance targets of path's census.
func shrineTargets(t *testing.T, r Rounds) []string {
	t.Helper()
	rows, known := r.Facts.Upkeep.Shrines.Value()
	if !known {
		t.Fatal("shrine census unknown")
	}
	return policy.ShrineClearanceTargets(rows, r.Facts.Upkeep.ShrinePolicy)
}

// clearance/shrine-claim, tick 15: a sealed ancient shrine staged inside
// Home is the one shrine target and opens ClearAncientShrine; the
// baseline's shrine outside Home is left alone.
func TestReplaySealedHomeShrineOpensClearance(t *testing.T) {
	r, err := Load("testdata/clearance-shrine-sealed.json")
	if err != nil {
		t.Fatal(err)
	}
	if got := shrineTargets(t, r); !slices.Equal(got, []string{"AncientShrineGroup_9460"}) {
		t.Errorf("shrine targets %v, want only the staged Home shrine", got)
	}
	if a, err := r.Assessment(policy.ClearAncientShrine); err != nil || a.Finding != domain.FindingUnmet {
		t.Errorf("ClearAncientShrine %+v %v, want deficit", a, err)
	}
}

// shrine replays path and returns its in-Home shrine and filled caskets.
func homeShrine(t *testing.T, path string) (policy.AncientShrine, policy.ShrinePolicy) {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := r.Facts.Upkeep.Shrines.Value()
	if !known {
		t.Fatal(path, "shrines unknown")
	}
	for _, row := range rows {
		if row.InHome {
			return row, r.Facts.Upkeep.ShrinePolicy
		}
	}
	t.Fatal(path, "no Home shrine")
	return policy.AncientShrine{}, policy.ShrinePolicy{}
}

// clearance/shrine-open, ticks 15 and 17246: with the opening gate
// (#875) recorded ready both filled caskets of the open Home
// shrine are open targets (the default policy never opens one), and once
// the caskets are emptied there is nothing left to open.
func TestReplayShrineCasketsOpenOnlyWhenReady(t *testing.T) {
	const filled, opened = "testdata/clearance-shrine-open-filled.json", "testdata/clearance-shrine-open-opened.json"
	row, p := homeShrine(t, filled)
	if p.Opening[row.ID] != policy.CasketOpen {
		t.Fatal("recording lacks the ready opening gate")
	}
	if got := policy.ShrineOpenTargets([]policy.AncientShrine{row}, p)[row.ID]; len(got) != 2 {
		t.Errorf("open targets %v, want both caskets", got)
	}
	if got := policy.ShrineOpenTargets([]policy.AncientShrine{row}, policy.ShrinePolicy{}); len(got) != 0 {
		t.Errorf("default policy opens %v", got)
	}
	if a := assess(t, filled, policy.ClearAncientShrine); a.Finding != domain.FindingUnmet {
		t.Errorf("filled caskets: %+v, want deficit", a)
	}
	row, p = homeShrine(t, opened)
	if got := policy.ShrineOpenTargets([]policy.AncientShrine{row}, p); len(got) != 0 {
		t.Errorf("emptied caskets still open targets %v", got)
	}
}
