package explain

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func step(planner, concern, verdict, reason string, extra map[string]any) bridge.TimelineRecord {
	attrs := map[string]any{"concern": concern, "class": "optional"}
	for k, v := range extra {
		attrs[k] = v
	}
	return bridge.TimelineRecord{Kind: plannerKind, Payload: map[string]any{"verdict": verdict, "reason": reason, "target": planner, "dur_ms": 1.0, "attrs": attrs}}
}

func transition(target, verdict, reason string, edit func(*store.ConcernTransition)) store.ConcernTransition {
	r := store.ConcernTransition{Sequence: 1, Concern: target, Verdict: verdict, Cause: reason}
	if edit != nil {
		edit(&r)
	}
	return r
}

func TestAuditPassesWhenEveryRefusalIsExplained(t *testing.T) {
	rows := []bridge.TimelineRecord{
		step("housing", "MaintainHousing", "refused", string(policy.CauseNoWorker), nil),
		step("tend", "", "waiting", string(policy.CauseMethodUsed), nil),
		step("food", "EnsureFoodSupply", "ok", "nothing_to_do", nil),
	}
	held := int64(500)
	transitions := []store.ConcernTransition{
		transition("MaintainHousing", "refused", string(policy.CauseNoWorker), func(r *store.ConcernTransition) { r.Subject, r.Method = "north", "build_beds" }),
		transition("tend", "waiting", string(policy.CauseMethodUsed), nil),
		transition("tend", "admitted", "", func(r *store.ConcernTransition) {
			r.PreviousKnown, r.Previous, r.Held = true, string(policy.CauseMethodUsed), &held
		}),
	}
	active := map[string]policy.Cause{"MaintainHousing": policy.CauseNoWorker, "EnsureFoodSupply": ""}
	res, err := Audit(rows, transitions, active)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Silent) != 1 || res.Silent[0] != "EnsureFoodSupply" || len(res.Explained) != 2 {
		t.Fatalf("result %+v", res)
	}
}

func TestAuditFailsWhenAPlannerStopsEmitting(t *testing.T) {
	rows := []bridge.TimelineRecord{
		step("housing", "MaintainHousing", "refused", string(policy.CauseNoWorker), nil),
		step("tend", "", "waiting", string(policy.CauseMethodUsed), nil),
	}
	transitions := []store.ConcernTransition{transition("MaintainHousing", "refused", string(policy.CauseNoWorker), nil)}
	_, err := Audit(rows, transitions, map[string]policy.Cause{"MaintainHousing": policy.CauseNoWorker})
	if err == nil || !strings.Contains(err.Error(), "tend") || strings.Contains(err.Error(), "MaintainHousing (") {
		t.Fatalf("want only tend missing, got %v", err)
	}
}

func TestAuditFailsOnAProgressCauseWithNoRow(t *testing.T) {
	rows := []bridge.TimelineRecord{step("housing", "MaintainHousing", "refused", string(policy.CauseNoWorker), nil)}
	transitions := []store.ConcernTransition{transition("MaintainHousing", "refused", string(policy.CauseNoWorker), nil)}
	_, err := Audit(rows, transitions, map[string]policy.Cause{"MaintainHousing": policy.CauseNoWorker, "EnsureResearch": policy.CauseNoMethod})
	if err == nil || !strings.Contains(err.Error(), "EnsureResearch") {
		t.Fatalf("got %v", err)
	}
}

func TestAuditIgnoresLateImmediateAndUnnamedPlannerSteps(t *testing.T) {
	rows := []bridge.TimelineRecord{
		step("a", "A", "refused", string(policy.CauseNoWorker), map[string]any{"late": true}),
		step("b", "B", "refused", string(policy.CauseNoWorker), map[string]any{"scope": "immediate"}),
		step("c", "C", "refused", "not_a_cause", nil),
		step("d", "D", "refused", string(policy.CauseNoWorker), nil),
	}
	transitions := []store.ConcernTransition{transition("D", "refused", string(policy.CauseNoWorker), nil)}
	if _, err := Audit(rows, transitions, map[string]policy.Cause{"D": ""}); err != nil {
		t.Fatal(err)
	}
}

func TestAuditRejectsFreeTextAndUnnamedRows(t *testing.T) {
	rows := []bridge.TimelineRecord{step("d", "D", "refused", string(policy.CauseNoWorker), nil)}
	for name, bad := range map[string]store.ConcernTransition{
		"unknown cause":   transition("D", "refused", "something odd", nil),
		"verdict":         transition("D", "scolded", string(policy.CauseNoWorker), nil),
		"clear verdict":   transition("D", "refused", "", nil),
		"sentence method": transition("D", "refused", string(policy.CauseNoWorker), func(r *store.ConcernTransition) { r.Method = "build a bed now" }),
		"long subject":    transition("D", "refused", string(policy.CauseNoWorker), func(r *store.ConcernTransition) { r.Subject = strings.Repeat("x", 80) }),
		"unknown previous": transition("D", "refused", string(policy.CauseNoWorker), func(r *store.ConcernTransition) {
			r.PreviousKnown, r.Previous = true, "because"
		}),
	} {
		transitions := []store.ConcernTransition{transition("D", "refused", string(policy.CauseNoWorker), nil), bad}
		if _, err := Audit(rows, transitions, map[string]policy.Cause{"D": ""}); err == nil {
			t.Errorf("%s passed", name)
		}
	}
}

func TestAuditFailsWhenNothingWasChecked(t *testing.T) {
	if _, err := Audit(nil, nil, nil); err == nil {
		t.Fatal("empty window passed")
	}
	transitions := []store.ConcernTransition{transition("D", "refused", string(policy.CauseNoWorker), nil)}
	if _, err := Audit(nil, transitions, map[string]policy.Cause{"D": ""}); err == nil {
		t.Fatal("window with no planner refusal passed")
	}
}

func TestEveryCauseHasWording(t *testing.T) {
	for _, c := range policy.Causes {
		if err := named(c); err != nil {
			t.Error(err)
		}
	}
}
