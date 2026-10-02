package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Replaces the native service/development case (#9, #947): recorded from
// `acceptance run service/development` at b0c790095 on the tribal
// baseline, the last review before the case killed the controller (tick
// 3357) and the last after its restart on the same state (tick 35544).
const (
	developmentBefore = "testdata/service-development-before-restart.json.gz"
	developmentAfter  = "testdata/service-development-after-restart.json.gz"
)

func loadDevelopment(t *testing.T, path string) (Routine, store.RoutineDevelopment) {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if r.Review == nil || len(r.Review.Development.Rows) == 0 {
		t.Fatal("no development ranking recorded", path)
	}
	return r, r.Review.Development
}

// Admission stays within the project limit, the worker count and the
// admitted slots; every deferral names a reason, a labor deferral a
// censused bottleneck; no goal waits from after its review.
func TestDevelopmentRankingBoundedAndExplained(t *testing.T) {
	for _, path := range []string{developmentBefore, developmentAfter} {
		_, d := loadDevelopment(t, path)
		admitted := len(d.Committed)
		for _, row := range d.Rows {
			if row.Selected {
				admitted++
				if row.Committed || row.Reason != "" {
					t.Errorf("%s: %s selected with reason %q", path, row.Goal, row.Reason)
				}
			} else if !row.Committed && row.Reason == "" {
				t.Errorf("%s: %s deferred without a reason", path, row.Goal)
			}
			if row.Reason == policy.DevelopmentLabor {
				if _, known := d.Labor[row.Bottleneck]; !known {
					t.Errorf("%s: %s labor deferral without a censused bottleneck %q", path, row.Goal, row.Bottleneck)
				}
			}
			if row.WaitingSince > d.Tick {
				t.Errorf("%s: %s waits since %d, after review tick %d", path, row.Goal, row.WaitingSince, d.Tick)
			}
		}
		if d.Workers == nil || admitted > d.Capacity || d.Capacity > *d.Workers {
			t.Errorf("%s: admitted %d, capacity %d, workers %v", path, admitted, d.Capacity, d.Workers)
		}
	}
}

// The restarted controller's review does not rewind, and a goal deferred
// on both sides of the kill keeps its waiting age.
func TestDevelopmentSurvivesRestart(t *testing.T) {
	_, before := loadDevelopment(t, developmentBefore)
	_, after := loadDevelopment(t, developmentAfter)
	if after.Tick < before.Tick {
		t.Fatal("restart rewound the review tick", before.Tick, after.Tick)
	}
	prior := map[policy.GoalID]store.RoutineDevelopmentRow{}
	for _, row := range before.Rows {
		prior[row.Goal] = row
	}
	for _, row := range after.Rows {
		p, ok := prior[row.Goal]
		if !ok || p.Selected || p.Committed || row.Selected || row.Committed {
			continue
		}
		if row.WaitingSince != p.WaitingSince {
			t.Errorf("%s waiting age rewritten %d -> %d", row.Goal, p.WaitingSince, row.WaitingSince)
		}
	}
}

// EnsureResearch is in deficit at Foothold but the colony stage keeps it off
// the ranking (raisedAtStage): the stage ladder, not the development slots,
// holds research back on a fresh colony.
func TestDevelopmentStageHoldsResearchAtFoothold(t *testing.T) {
	r, d := loadDevelopment(t, developmentBefore)
	if a, err := r.Assessment(policy.EnsureResearch); err != nil || a.Need != "deficit" {
		t.Fatal("research assessment", a, err)
	}
	if policy.StageGoalAllowed(policy.EnsureResearch, r.Policy.ColonyStage) {
		t.Fatal("stage allows research; the recording no longer shows the hold", r.Policy.ColonyStage)
	}
	for _, row := range d.Rows {
		if row.Goal == policy.EnsureResearch && row.Selected {
			t.Fatal("research ranked under the stage hold")
		}
	}
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	for _, g := range needs.Goals {
		if g.ID == policy.EnsureResearch && !g.Staged {
			t.Fatal("replay raised research at the recorded stage")
		}
	}
}
