package buildingruntime

import (
	"context"
	"errors"
	"log/slog"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestPlannerStepDecisionOnePerRunShape(t *testing.T) {
	entry := plannerEntry{name: "sleeping", concern: policy.MaintainHousing, class: classOptional}
	cases := []struct {
		name            string
		verdict         Verdict
		err             error
		late            bool
		wantVerdict     string
		wantReason      string
		wantLevel       slog.Level
		wantAttr, value string
	}{
		{name: "admitted", verdict: Verdict{Outcome: OutcomeAdmitted}, wantVerdict: "admitted", wantReason: "plan_admitted"},
		{name: "refused", verdict: refuse(RefusalNoSpace, "", ""), wantVerdict: "refused", wantReason: "no_space"},
		{name: "refused subject", verdict: refuse(RefusalSharedAdmission, "no_development_slot", "wood"), wantVerdict: "refused", wantReason: "shared_admission_refused", wantAttr: "subject", value: "no_development_slot"},
		{name: "waiting", verdict: waitOn(WaitExistingWork), wantVerdict: "waiting", wantReason: "already_working_on_it"},
		{name: "nothing to do", verdict: Verdict{Outcome: OutcomeNothingToDo}, wantVerdict: "ok", wantReason: "nothing_to_do"},
		{name: "no verdict", wantVerdict: "ok", wantReason: "no_verdict"},
		{name: "failed", err: errors.New("boom"), wantVerdict: "failed", wantReason: "error", wantLevel: slog.LevelWarn, wantAttr: "error", value: "boom"},
		{name: "late", verdict: Verdict{Outcome: OutcomeAdmitted}, late: true, wantVerdict: "admitted", wantReason: "plan_admitted", wantAttr: "late", value: "true"},
		{name: "optional cutoff", err: context.Canceled, late: true, wantVerdict: "refused", wantReason: "awaiting_plan", wantAttr: "detail", value: "cutoff"},
		{name: "early cancellation", err: context.Canceled, wantVerdict: "failed", wantReason: "error", wantLevel: slog.LevelWarn},
	}
	for _, c := range cases {
		d := plannerStepDecision(entry, c.verdict, c.err, 1500*time.Microsecond, c.late)
		p := d.Payload()
		if d.Kind != "planner_step" || p["target"] != "sleeping" || p["verdict"] != c.wantVerdict || p["reason"] != c.wantReason || d.Level != c.wantLevel {
			t.Fatalf("%s: %+v %+v", c.name, d, p)
		}
		attrs := p["attrs"].(map[string]any)
		if attrs["concern"] != string(policy.MaintainHousing) || attrs["class"] != "optional" {
			t.Fatalf("%s: attrs %v", c.name, attrs)
		}
		if c.wantAttr != "" && (attrs[c.wantAttr] == nil || toText(attrs[c.wantAttr]) != c.value) {
			t.Fatalf("%s: attrs %v want %s=%s", c.name, attrs, c.wantAttr, c.value)
		}
	}
}

func toText(v any) string {
	switch x := v.(type) {
	case bool:
		if x {
			return "true"
		}
		return "false"
	case string:
		return x
	}
	return ""
}

func TestMethodAdmissionDecisionAdmittedAndRefused(t *testing.T) {
	r := store.BuildingMethodRequest{Owner: store.StandardState{}, Method: domain.MethodID("shelter")}
	admitted := methodAdmissionDecision(r, store.BuildingMethodDecision{Admitted: true})
	if p := admitted.Payload(); admitted.Kind != "admission" || p["verdict"] != "admitted" || p["reason"] != "method_committed" || p["target"] != "shelter" {
		t.Fatalf("%+v", p)
	}
	refused := methodAdmissionDecision(r, store.BuildingMethodDecision{Refused: []policy.Refusal{{Reason: policy.NoDevelopmentSlot}}})
	p := refused.Payload()
	if p["verdict"] != "refused" || p["reason"] != string(policy.NoDevelopmentSlot) || p["attrs"].(map[string]any)["refused"] != "["+string(policy.NoDevelopmentSlot)+"]" {
		t.Fatalf("%+v", p)
	}
}

func TestWindowRefusedDecisionNamesFirstReason(t *testing.T) {
	d := windowRefusedDecision([]string{"no_work", "stale"}, map[string]any{"mode": "routine"})
	p := d.Payload()
	if d.Kind != "admission" || p["target"] != "window" || p["verdict"] != "refused" || p["reason"] != "no_work" {
		t.Fatalf("%+v", p)
	}
	if refused := p["attrs"].(map[string]any)["refused"].([]string); len(refused) != 2 {
		t.Fatalf("%v", refused)
	}
}
