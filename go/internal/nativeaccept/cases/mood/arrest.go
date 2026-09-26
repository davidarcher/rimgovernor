package mood

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
)

func init() {
	cases.Register(cases.Case{Name: "mood/ancient-arrest", Scope: "Existing Arrest operation delivers a standing neutral Faction.OfAncients pawn to its exact prisoner bed under owned draft; ordinary colonists still require a legal mental state.", Start: cases.Fixture{Op: "test/arrest_prepare", ArgsFrom: startersite.ArgsFor(7)}, Keep: []string{string(na.NeedRest)}, Budget: 2 * time.Minute, Run: func(ctx context.Context, s cases.Session) error { return runArrestTarget(ctx, s, "ancient") }})
	cases.Register(cases.Case{
		Name:   "mood/arrest",
		Scope:  "Vanilla Arrest custody: refuse normal targets, unarmed arresters, hostile Berserk and non-prisoner beds; require owned draft; replay/lookup and observe a living sad-wander target in the exact prisoner bed with its mental state ended.",
		Start:  cases.Fixture{Op: "test/arrest_prepare", ArgsFrom: startersite.ArgsFor(7)},
		Keep:   []string{string(na.NeedRest)},
		Budget: 2 * time.Minute,
		Run:    runArrest,
	})
}

func runArrest(ctx context.Context, s cases.Session) error { return runArrestTarget(ctx, s, "legal") }

func runArrestTarget(ctx context.Context, s cases.Session, legalFixture string) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	pawnID, targetID := na.AsString(prepared["pawn"]), na.AsString(prepared["target"])
	bedID, ordinaryBedID := na.AsString(prepared["bed"]), na.AsString(prepared["ordinaryBed"])
	if pawnID == "" || targetID == "" || bedID == "" || ordinaryBedID == "" {
		return fmt.Errorf("arrest fixture lacks identities: %#v", prepared)
	}
	read := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{pawnID}}})
		if err != nil {
			return nil, err
		}
		return na.PawnRow(reply, identity, pawnID)
	}
	var request, receipt, attempt map[string]any
	scenarios := []struct {
		name, fixture, reason string
		draft                 bool
	}{
		{"normal", "normal", "not living in a mental state", true},
		{"unarmed", "unarmed", "unarmed or incapable of violence", true},
		{"berserk", "berserk", "native arrest eligibility", true},
		{"ordinary-bed", "legal", "prisoner bed", true},
		{"unowned", "legal", "owned draft claim", false},
		{"stale", "legal", "snapshot changed", true},
		{"legal", legalFixture, "", true},
	}
	for i, scenario := range scenarios {
		staged, err := h.Call(ctx, "stage-"+scenario.name, "test/arrest_stage", map[string]any{"pawnId": pawnID, "targetId": targetID, "scenario": scenario.fixture})
		if err != nil {
			return err
		}
		if ok, _ := na.AsBool(staged["success"]); !ok {
			return fmt.Errorf("stage %s: %#v", scenario.name, staged)
		}
		grant, err := na.GrantAuto(ctx, h.WireFunc(), "grant-"+scenario.name, identity)
		if err != nil {
			return err
		}
		row, err := read("before-" + scenario.name)
		if err != nil {
			return err
		}
		if scenario.draft {
			drafted, err := h.Wire(ctx, "draft-"+scenario.name, "operations_execute", na.ExecuteRequest(identity, grant, row, i+1))
			if err != nil {
				return err
			}
			_, r, err := na.Outcome(drafted, "receipt")
			if err != nil {
				return err
			}
			if _, ok := r["applied"]; !ok {
				return fmt.Errorf("draft %s: %#v", scenario.name, r)
			}
			row, err = read("drafted-" + scenario.name)
			if err != nil {
				return err
			}
		}
		bed := bedID
		if scenario.name == "ordinary-bed" {
			bed = ordinaryBedID
		}
		pawn := na.Target(row)
		if scenario.name == "stale" {
			pawn["expectedSnapshotToken"] = "stale-arrest-token"
		}
		operation := map[string]any{"arrest": map[string]any{"pawn": pawn, "target": map[string]any{"entityId": targetID}, "bed": map[string]any{"entityId": bed}}}
		request = map[string]any{"operation": operation, "precondition": map[string]any{"identity": identity, "expectedGeneration": fmt.Sprint(na.GrantGeneration(grant)), "attempt": map[string]any{"controllerSessionId": na.Controller, "actionId": "arrest-" + scenario.name, "attemptId": "1"}}}
		preview, err := h.Wire(ctx, "preview-"+scenario.name, "operations_preview", map[string]any{"identity": identity, "operation": operation})
		if err != nil {
			return err
		}
		if scenario.reason != "" {
			if err := arrestRefusal(preview, scenario.reason); err != nil {
				return fmt.Errorf("preview %s: %w", scenario.name, err)
			}
		} else {
			evaluated, _ := na.AsMap(preview["evaluated"])
			if accepted, _ := na.AsBool(evaluated["accepted"]); !accepted {
				return fmt.Errorf("legal preview: %#v", preview)
			}
		}
		unchanged, err := read("after-preview-" + scenario.name)
		if err != nil {
			return err
		}
		if err := na.SameControl(row, unchanged); err != nil {
			return fmt.Errorf("preview mutated arrester: %w", err)
		}
		executed, err := h.Wire(ctx, "execute-"+scenario.name, "operations_execute", request)
		if err != nil {
			return err
		}
		if scenario.reason != "" {
			if err := arrestRefusal(executed, scenario.reason); err != nil {
				return fmt.Errorf("execute %s: %w", scenario.name, err)
			}
			s.Report()["refused_"+scenario.name] = executed
			after, err := read("after-refusal-" + scenario.name)
			if err != nil {
				return err
			}
			if err := na.SameControl(unchanged, after); err != nil {
				return fmt.Errorf("refusal mutated arrester: %w", err)
			}
			continue
		}
		_, receipt, err = na.Outcome(executed, "receipt")
		if err != nil {
			return err
		}
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		job, _ := na.AsMap(observed["job"])
		if issued, _ := na.AsBool(job["issued"]); !issued || na.AsString(job["jobDef"]) != "Arrest" {
			return fmt.Errorf("arrest was not issued: %#v", receipt)
		}
		pre, _ := na.AsMap(request["precondition"])
		attempt = map[string]any{"identity": identity, "attempt": pre["attempt"]}
	}
	pending, err := h.Wire(ctx, "arrest-pending", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, progress, err := na.Outcome(pending, "progress")
	if err != nil {
		return err
	}
	if _, ok := progress["pending"]; !ok {
		return fmt.Errorf("admission must remain pending: %#v", progress)
	}
	completed, err := na.ObserveCompleted(ctx, h, "arrest-complete", 6000, attempt)
	if err != nil {
		return err
	}
	s.Report()["completed"] = completed
	inspected, err := h.Call(ctx, "custody-postcondition", "test/arrest_inspect", map[string]any{"targetId": targetID})
	if err != nil {
		return err
	}
	alive, _ := na.AsBool(inspected["alive"])
	mental, hasMental := na.AsBool(inspected["mental"])
	prisoner, _ := na.AsBool(inspected["prisoner"])
	if !alive || !hasMental || mental || !prisoner || na.AsString(inspected["bed"]) != bedID || na.AsNumber(inspected["deadColonists"]) != 0 {
		return fmt.Errorf("native custody postcondition: %#v", inspected)
	}
	s.Report()["custody"] = inspected
	for _, check := range []struct {
		label, tool string
		body        map[string]any
	}{{"arrest-replay", "operations_execute", request}, {"arrest-lookup", "receipts_lookup", attempt}} {
		reply, err := h.Wire(ctx, check.label, check.tool, check.body)
		if err != nil {
			return err
		}
		_, got, err := na.Outcome(reply, "receipt")
		if err != nil {
			return err
		}
		if !na.DeepEqual(got, receipt) {
			return fmt.Errorf("%s changed original receipt", check.label)
		}
	}
	return nil
}

func arrestRefusal(reply map[string]any, reason string) error {
	_, failure, err := na.Outcome(reply, "failure")
	if err != nil {
		return err
	}
	if !strings.Contains(na.AsString(failure["detail"]), reason) {
		return fmt.Errorf("expected refusal %q: %#v", reason, failure)
	}
	return nil
}
