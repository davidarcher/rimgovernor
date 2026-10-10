package explain

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func step(planner, concern, verdict, reason string, extra map[string]any) bridge.TimelineRecord {
	attrs := map[string]any{"concern": concern, "class": "optional"}
	for k, v := range extra {
		attrs[k] = v
	}
	return bridge.TimelineRecord{Kind: plannerKind, Payload: map[string]any{"verdict": verdict, "reason": reason, "target": planner, "dur_ms": 1.0, "attrs": attrs}}
}

func transition(target, verdict, reason string, attrs map[string]any) bridge.TimelineRecord {
	if attrs == nil {
		attrs = map[string]any{"subject": "", "method": ""}
	}
	return bridge.TimelineRecord{Kind: transitionKind, Stream: bridge.ExplainStream, Sequence: 1,
		Payload: map[string]any{"verdict": verdict, "reason": reason, "target": target, "dur_ms": 0.0, "attrs": attrs}}
}

func TestAuditPassesWhenEveryRefusalIsExplained(t *testing.T) {
	rows := []bridge.TimelineRecord{
		step("housing", "MaintainHousing", "refused", string(policy.CauseNoWorker), nil),
		step("tend", "", "waiting", string(policy.CauseMethodUsed), nil),
		step("food", "EnsureFoodSupply", "ok", "nothing_to_do", nil),
		transition("MaintainHousing", "refused", string(policy.CauseNoWorker), map[string]any{"subject": "north", "method": "build_beds"}),
		transition("tend", "waiting", string(policy.CauseMethodUsed), nil),
		transition("tend", "admitted", "", map[string]any{"subject": "", "method": "", "previous_reason": string(policy.CauseMethodUsed), "held_ticks": 500.0}),
	}
	active := map[string]policy.Cause{"MaintainHousing": policy.CauseNoWorker, "EnsureFoodSupply": ""}
	res, err := Audit(rows, active)
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
		transition("MaintainHousing", "refused", string(policy.CauseNoWorker), nil),
	}
	_, err := Audit(rows, map[string]policy.Cause{"MaintainHousing": policy.CauseNoWorker})
	if err == nil || !strings.Contains(err.Error(), "tend") || strings.Contains(err.Error(), "MaintainHousing (") {
		t.Fatalf("want only tend missing, got %v", err)
	}
}

func TestAuditFailsOnAProgressCauseWithNoRow(t *testing.T) {
	rows := []bridge.TimelineRecord{
		step("housing", "MaintainHousing", "refused", string(policy.CauseNoWorker), nil),
		transition("MaintainHousing", "refused", string(policy.CauseNoWorker), nil),
	}
	_, err := Audit(rows, map[string]policy.Cause{"MaintainHousing": policy.CauseNoWorker, "EnsureResearch": policy.CauseNoMethod})
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
		transition("D", "refused", string(policy.CauseNoWorker), nil),
	}
	if _, err := Audit(rows, map[string]policy.Cause{"D": ""}); err != nil {
		t.Fatal(err)
	}
}

func TestAuditRejectsFreeTextAndUnnamedRows(t *testing.T) {
	good := step("d", "D", "refused", string(policy.CauseNoWorker), nil)
	for name, bad := range map[string]bridge.TimelineRecord{
		"unknown cause":    transition("D", "refused", "something odd", nil),
		"verdict":          transition("D", "scolded", string(policy.CauseNoWorker), nil),
		"clear verdict":    transition("D", "refused", "", nil),
		"extra attr":       transition("D", "refused", string(policy.CauseNoWorker), map[string]any{"subject": "", "method": "", "msg": "free text"}),
		"sentence method":  transition("D", "refused", string(policy.CauseNoWorker), map[string]any{"subject": "", "method": "build a bed now"}),
		"long subject":     transition("D", "refused", string(policy.CauseNoWorker), map[string]any{"subject": strings.Repeat("x", 80), "method": ""}),
		"unknown previous": transition("D", "refused", string(policy.CauseNoWorker), map[string]any{"previous_reason": "because", "method": "", "subject": ""}),
	} {
		rows := []bridge.TimelineRecord{good, transition("D", "refused", string(policy.CauseNoWorker), nil), bad}
		if _, err := Audit(rows, map[string]policy.Cause{"D": ""}); err == nil {
			t.Errorf("%s passed", name)
		}
	}
	extra := transition("D", "refused", string(policy.CauseNoWorker), nil)
	extra.Payload["msg"] = "free text"
	if _, err := Audit([]bridge.TimelineRecord{good, extra}, map[string]policy.Cause{"D": ""}); err == nil {
		t.Error("payload msg passed")
	}
}

func TestAuditFailsWhenNothingWasChecked(t *testing.T) {
	if _, err := Audit(nil, nil); err == nil {
		t.Fatal("empty window passed")
	}
	rows := []bridge.TimelineRecord{transition("D", "refused", string(policy.CauseNoWorker), nil)}
	if _, err := Audit(rows, map[string]policy.Cause{"D": ""}); err == nil {
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
