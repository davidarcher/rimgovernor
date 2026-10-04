package snapshot

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Home clearance converted from the native clearance/* cases (#746), each
// recorded at 04b0a98c from `acceptance run clearance/<case>`.

// clearance replays path's clearance census: the Home selection and the
// review's journalled holds, which must match what the review recorded.
func clearance(t *testing.T, path string) (Rounds, policy.ClearanceSelection, []policy.ClearanceHold) {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	rows, known := r.Facts.Upkeep.Clearance.Value()
	if !known {
		t.Fatal(path, "clearance census unknown")
	}
	holds, _, err := policy.ReviewClearanceHolds(r.Policy, r.Facts, rows)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(holds, r.Review.ClearanceHolds) {
		t.Fatalf("%s: replayed holds %v, recorded %v", path, holds, r.Review.ClearanceHolds)
	}
	return r, policy.SelectHomeClearance(rows, domain.Cell{}), holds
}

func targetIDs(s policy.ClearanceSelection) []string {
	var ids []string
	for _, row := range s.Targets {
		ids = append(ids, row.EntityID)
	}
	return ids
}

func held(holds []policy.ClearanceHold, target string) string {
	for _, h := range holds {
		if h.Target == target {
			return h.Reason
		}
	}
	return ""
}

// clearance/ancient-wall, tick 15: an unowned deconstructible ancient
// wall inside Home is the one admitted target and opens
// ClearHomeObstructions; no hold protects it.
func TestReplayAncientHomeWallAdmitted(t *testing.T) {
	const wall = "Thing_Wall44690"
	r, sel, holds := clearance(t, "testdata/clearance-ancient-wall.json")
	if ids := targetIDs(sel); !slices.Equal(ids, []string{wall}) {
		t.Errorf("targets %v, want only %s", ids, wall)
	}
	if reason := held(holds, wall); reason != "" {
		t.Errorf("%s held for %s", wall, reason)
	}
	if a, err := r.Assessment(policy.ClearHomeObstructions); err != nil || a.Finding != domain.FindingUnmet {
		t.Errorf("ClearHomeObstructions %+v %v, want deficit", a, err)
	}
}

// clearance/roof-support-refused, ticks 15 and 615: the same Home wall
// holding up a roof is held for roof_blocker and never admitted; once a
// support column stands beside it, it is the admitted target.
func TestReplayRoofBearingWallHeldUntilSupported(t *testing.T) {
	const wall = "Thing_Wall44690"
	_, sel, holds := clearance(t, "testdata/clearance-roof-held.json")
	if reason := held(holds, wall); reason != "roof_blocker" {
		t.Errorf("%s held for %q, want roof_blocker", wall, reason)
	}
	if slices.Contains(targetIDs(sel), wall) {
		t.Errorf("roof-bearing %s admitted", wall)
	}
	_, sel, holds = clearance(t, "testdata/clearance-roof-supported.json")
	if ids := targetIDs(sel); !slices.Equal(ids, []string{wall}) || held(holds, wall) != "" {
		t.Errorf("supported: targets %v holds %v, want only %s admitted", ids, holds, wall)
	}
}

// clearance/standing-designation, tick 15: a wall already designated for
// deconstruction is no hold; the routine adopts it as its target.
func TestReplayStandingDesignationAdopted(t *testing.T) {
	const wall = "Thing_Wall44690"
	_, sel, holds := clearance(t, "testdata/clearance-standing-designation.json")
	if len(sel.Targets) != 1 || sel.Targets[0].EntityID != wall || !sel.Targets[0].Designated {
		t.Errorf("targets %+v, want the designated %s", sel.Targets, wall)
	}
	if reason := held(holds, wall); reason != "" {
		t.Errorf("%s held for %s", wall, reason)
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
