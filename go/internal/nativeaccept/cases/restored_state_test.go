package cases

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// RestoredState reads the case state of the bundle the run opened on: a
// ring resume first (later on the same timeline), else the stage hit;
// nothing on a fresh run or from a postmortem session.
func TestRestoredStatePrefersTheResumeOverTheStage(t *testing.T) {
	staged := &session{stagePlan: staging{hit: 0, entry: na.Checkpoint{Stage: "shell", State: map[string]any{"baseline_count": 2.0, "only_staged": "x"}}}}
	if entry, ok := staged.Staged(); !ok || entry.Stage != "shell" {
		t.Fatal("a stage hit did not report as staged")
	}
	if v := RestoredState(staged, "baseline_count"); v != 2.0 {
		t.Fatalf("stage state: %v", v)
	}
	both := &session{resumed: &na.Checkpoint{State: map[string]any{"baseline_count": 3.0}}, stagePlan: staged.stagePlan}
	if v := RestoredState(both, "baseline_count"); v != 3.0 {
		t.Fatalf("a resume did not win over the stage: %v", v)
	}
	if v := RestoredState(both, "only_staged"); v != "x" {
		t.Fatalf("a key the resume lacks did not fall back to the stage: %v", v)
	}
	fresh := &session{stagePlan: staging{hit: -1}}
	if _, ok := fresh.Staged(); ok {
		t.Fatal("a fresh run reported as staged")
	}
	if v := RestoredState(fresh, "baseline_count"); v != nil {
		t.Fatalf("fresh run: %v", v)
	}
	postmortem := &session{resumed: &na.Checkpoint{}, stagePlan: staging{hit: -1}}
	if _, ok := postmortem.Staged(); ok {
		t.Fatal("a postmortem session reported as staged")
	}
}
