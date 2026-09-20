// Package clearance verifies routine clearance through the service and native pawn work.
package clearance

import (
	"context"
	"encoding/json"
	"fmt"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const fixtureKey = "clearance_fixture"

func init() {
	for _, scenario := range []string{"ancient_wall", "roof_support_refused", "standing_designation", "chunk_dump"} {
		cases.Register(cases.Case{
			Name:        "clearance/" + strings.ReplaceAll(scenario, "_", "-"),
			Scope:       "Routine Home clearance: " + scenario + "; exact native targets, journal holds and observed completion.",
			Start:       cases.Save{Name: "RimGovernor-tribal8-baseline"},
			RequiredOps: []string{"test/clearance_prepare", "test/clearance_support", "test/clearance_audit"},
			Serve:       &cases.ServeSpec{Families: []string{"clearance", "tend", "rescue"}, Prefix: "clearance"},
			Stages:      []string{"clearance-ready"}, Budget: 4 * time.Minute, Stall: 60 * time.Second,
			Run: func(ctx context.Context, s cases.Session) error { return run(ctx, s, scenario) },
		})
	}
}

func run(ctx context.Context, s cases.Session, scenario string) error {
	var fixture map[string]any
	if err := s.Stage(ctx, "clearance-ready", func(ctx context.Context) error {
		var err error
		fixture, err = s.Harness().Call(ctx, "prepare-clearance", "test/clearance_prepare", map[string]any{"scenario": scenario})
		if err == nil {
			na.SetCheckpointState(fixtureKey, fixture)
		}
		return err
	}); err != nil {
		return err
	}
	if restored := cases.RestoredState(s, fixtureKey); restored != nil {
		fixture, _ = na.AsMap(restored)
	}
	if fixture == nil {
		return fmt.Errorf("missing staged clearance identities")
	}
	s.Report()["fixture"] = fixture
	target := na.AsString(fixture["target"])
	before, err := census(ctx, s, "before")
	if err != nil {
		return err
	}
	s.Report()["census_before"] = before
	if scenario == "chunk_dump" {
		return runChunks(ctx, s, fixture, before)
	}
	row := targetRow(before, target)
	if row == nil || na.AsString(row["faction"]) != "" || !boolean(row["deconstructible"]) || !boolean(row["inHome"]) {
		return fmt.Errorf("missing unowned deconstructible Home wall %s: %v", target, row)
	}
	reason := ""
	if scenario == "roof_support_refused" {
		reason = "roof_blocker"
	}
	// A standing deconstruct designation, whoever placed it, is no hold: the
	// routine adopts it into an ordinary Deconstruction plan (no ownership
	// ledger) and the native operation keeps the one designation.
	if (na.AsString(row["roofBlocker"]) != "") != (reason == "roof_blocker") || boolean(row["designated"]) != (scenario == "standing_designation") {
		return fmt.Errorf("incorrect initial protection: %v", row)
	}
	if reason != "" {
		service, err := start(ctx, s)
		if err != nil {
			return err
		}
		journal, err := service.Store(ctx)
		if err != nil {
			service.Stop()
			return err
		}
		var first domain.Tick
		err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return "", false, err
			}
			held := slices.ContainsFunc(review.ClearanceHolds, func(h policy.ClearanceHold) bool { return h.Target == target && h.Reason == reason })
			if !held {
				return "waiting for protection", false, nil
			}
			if first == 0 {
				first = review.Tick
			}
			plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-clearance-", 256)
			if err != nil {
				return "", false, err
			}
			for _, plan := range plans {
				for _, action := range plan.Spec.Actions() {
					if d, ok := action.Deconstruction(); ok && d.Target() == target {
						return "", false, fmt.Errorf("protected wall adopted into plan %s", plan.Spec.ID())
					}
				}
			}
			s.Report()["clearance_holds"] = review.ClearanceHolds
			return na.Signature(review.Tick), review.Tick-first >= 600, nil
		})
		service.Stop()
		if err != nil {
			return err
		}
		if err = reattach(ctx, s); err != nil {
			return err
		}
		after, err := census(ctx, s, "protected-after-stop")
		if err != nil {
			return err
		}
		protected := targetRow(after, target)
		if protected == nil || boolean(protected["designated"]) {
			return fmt.Errorf("stop changed protected wall: %v", protected)
		}
		audit, err := audit(ctx, s, fixture, "protected-native")
		if err != nil {
			return err
		}
		if !boolean(audit["present"]) || boolean(audit["designated"]) {
			return fmt.Errorf("protected designation changed: %v", audit)
		}
		if _, err = s.Harness().Call(ctx, "support-column", "test/clearance_support", map[string]any{"x": fixture["x"], "z": fixture["z"], "stuff": fixture["stuff"]}); err != nil {
			return err
		}
		supported, err := census(ctx, s, "supported")
		if err != nil {
			return err
		}
		if row := targetRow(supported, target); row == nil || na.AsString(row["roofBlocker"]) != "" {
			return fmt.Errorf("support column did not clear roof hold: %v", row)
		}
	}
	return demolish(ctx, s, fixture, scenario == "standing_designation")
}

func wait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Ceiling: 2 * time.Minute, Stall: 60 * time.Second, Interval: time.Second, Terminal: service.Exited}
}

func start(ctx context.Context, s cases.Session) (*na.ServiceProcess, error) {
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return nil, err
	}
	if _, err = service.Acquire(); err != nil {
		service.Stop()
		return nil, err
	}
	service.KeepAuthority(ctx)
	return service, nil
}

func reattach(ctx context.Context, s cases.Session) error {
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	_, err = h.Call(ctx, "pause-audit", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false})
	return err
}

func census(ctx context.Context, s cases.Session, label string) (map[string]any, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_get_clearance_targets", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	complete, _ := na.AsMap(observed["completeness"])
	page, _ := na.AsMap(complete["page"])
	if !boolean(page["complete"]) {
		return nil, fmt.Errorf("incomplete clearance census: %v", observed)
	}
	return observed, nil
}

func targetRow(census map[string]any, target string) map[string]any {
	for _, raw := range na.AsSlice(census["targets"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["entityId"]) == target {
			return row
		}
	}
	return nil
}

func audit(ctx context.Context, s cases.Session, fixture map[string]any, label string) (map[string]any, error) {
	var ids []string
	for _, raw := range na.AsSlice(fixture["chunks"]) {
		ids = append(ids, na.AsString(raw))
	}
	result, err := s.Harness().Call(ctx, label, "test/clearance_audit", map[string]any{"target": na.AsString(fixture["target"]), "chunks": strings.Join(ids, ";")})
	s.Report()[label] = result
	return result, err
}

func boolean(raw any) bool { value, _ := na.AsBool(raw); return value }

func demolish(ctx context.Context, s cases.Session, fixture map[string]any, standing bool) error {
	target := na.AsString(fixture["target"])
	// A standing designation is adopted into the plan, but the pawns may work
	// it before the worker dispatches: the plan then cancels on an absent
	// target rather than completing with demolition evidence. Either way the
	// native audit below must find the wall and its designation gone.
	// Follow native evidence from before launch so a fast demolition cannot
	// disappear between the method-admission and completion polls.
	tail := na.NewFlightTail(na.FlightRecorderPath(s.Config().Output))
	service, err := start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	var effect map[string]any
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		rows, err := tail.Next()
		if err != nil {
			return "", false, err
		}
		for _, row := range rows {
			if row.Kind != "native_response" || na.AsString(row.Payload["native_tool"]) != "rimgovernor/receipts_observe_progress" {
				continue
			}
			result, _ := na.AsMap(row.Payload["result"])
			var reply map[string]any
			if err := json.Unmarshal([]byte(na.AsString(result["payload"])), &reply); err != nil {
				continue
			}
			progress, _ := na.AsMap(reply["progress"])
			completed, _ := na.AsMap(progress["completed"])
			evidence, _ := na.AsMap(completed["evidence"])
			deconstruct, _ := na.AsMap(evidence["deconstruct"])
			if na.AsString(deconstruct["targetId"]) == target && boolean(deconstruct["demolitionObserved"]) && len(na.AsSlice(deconstruct["workerIds"])) > 0 {
				effect = deconstruct
			}
		}
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-clearance-", 256)
		if err != nil {
			return "", false, err
		}
		var states []string
		completed := false
		for _, plan := range plans {
			for i, action := range plan.Spec.Actions() {
				if d, ok := action.Deconstruction(); ok && d.Target() == target {
					v := plan.Progress[i].View()
					states = append(states, string(v.Stage))
					if v.Stage == domain.Unsuccessful {
						return "", false, fmt.Errorf("demolition failed: %v", v)
					}
					completed = completed || v.Stage == domain.Completed || standing && v.Stage == domain.Cancelled
				}
			}
		}
		return na.Signature(states, effect != nil), completed && (effect != nil || standing), nil
	})
	if err != nil {
		return err
	}
	s.Report()["demolition_effect"] = effect
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	live, err := audit(ctx, s, fixture, "demolition_after")
	if err != nil {
		return err
	}
	if boolean(live["present"]) || boolean(live["designated"]) {
		return fmt.Errorf("demolished target or designation survives: %v", live)
	}
	return nil
}

func runChunks(ctx context.Context, s cases.Session, fixture, before map[string]any) error {
	ids := na.AsSlice(fixture["chunks"])
	if len(ids) != 3 {
		return fmt.Errorf("expected three fixture chunks")
	}
	for _, id := range ids {
		found := false
		for _, raw := range na.AsSlice(before["chunks"]) {
			row, _ := na.AsMap(raw)
			if row["entityId"] == id {
				found = true
				if boolean(row["stored"]) || boolean(row["forbidden"]) || boolean(row["destination"]) {
					return fmt.Errorf("chunk is already stored or forbidden: %v", row)
				}
			}
		}
		if !found {
			return fmt.Errorf("chunk %v absent from clearance census", id)
		}
	}
	service, err := start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	admitted := false
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.ClearHomeObstructions {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				return "", false, err
			}
			plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-chunk-dump", 256)
			if err != nil {
				return "", false, err
			}
			admitted = admitted || len(plans) > 0
			s.Report()["chunk_goal"] = goal.Goal
			return na.Signature(goal.Goal.Need, len(goal.Methods)), admitted && goal.Goal.Need == domain.NeedRecovered, nil
		}
		return "waiting for chunk goal", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	// Goal recovery means a destination exists. Ordinary native hauling must
	// still move all three items; never equate zone admission with storage.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: time.Minute, Stall: 30 * time.Second}, func(ctx context.Context) (string, bool, error) {
		live, err := audit(ctx, s, fixture, "chunks_after")
		if err != nil {
			return "", false, err
		}
		if err = checkDump(live, fixture); err == nil {
			return "stored", true, nil
		}
		if _, advanceErr := s.Advance(ctx, 250); advanceErr != nil {
			return "", false, advanceErr
		}
		return na.Signature(live), false, nil
	})
	if err != nil {
		return err
	}
	after, err := census(ctx, s, "chunks-stored")
	if err != nil {
		return err
	}
	for _, id := range ids {
		stored := false
		for _, raw := range na.AsSlice(after["chunks"]) {
			row, _ := na.AsMap(raw)
			stored = stored || row["entityId"] == id && boolean(row["stored"])
		}
		if !stored {
			return fmt.Errorf("chunk %v is not stored in native clearance census", id)
		}
	}
	return nil
}

func checkDump(live, fixture map[string]any) error {
	zones := na.AsSlice(live["zones"])
	if len(zones) != 1 {
		return fmt.Errorf("expected exactly one dumping stockpile, got %v", zones)
	}
	zone, _ := na.AsMap(zones[0])
	if zone["label"] != "RimGovernor dumping" || zone["priority"] != "Low" {
		return fmt.Errorf("incorrect dumping settings: %v", zone)
	}
	var allow, want []string
	for _, raw := range na.AsSlice(zone["allow"]) {
		allow = append(allow, na.AsString(raw))
	}
	for _, raw := range na.AsSlice(fixture["defs"]) {
		want = append(want, na.AsString(raw))
	}
	slices.Sort(allow)
	slices.Sort(want)
	if !slices.Equal(allow, want) {
		return fmt.Errorf("dump allow list %v, want %v", allow, want)
	}
	cells := na.AsSlice(zone["cells"])
	if len(cells) < 4 || len(cells) > 16 {
		return fmt.Errorf("dump has %d cells", len(cells))
	}
	for _, raw := range cells {
		c, _ := na.AsMap(raw)
		if !boolean(c["home"]) || boolean(c["roofed"]) || boolean(c["building"]) {
			return fmt.Errorf("invalid dump cell: %v", c)
		}
	}
	chunks := na.AsSlice(live["chunks"])
	if len(chunks) != 3 {
		return fmt.Errorf("native audit lost chunks: %v", chunks)
	}
	for _, raw := range chunks {
		c, _ := na.AsMap(raw)
		if !boolean(c["stored"]) || c["zone"] != "RimGovernor dumping" {
			return fmt.Errorf("chunk not hauled to dump: %v", c)
		}
	}
	return nil
}
