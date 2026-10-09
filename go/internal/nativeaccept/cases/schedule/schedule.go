// Package schedule holds the adaptive-timetable cases:
// the work family's schedule planner writes each pawn's timetable through
// the WorkSettingsIntent, and these cases read the written timetables back
// from native.
//
//   - schedule/timetables: the recreation hour sits right before sleep; a
//     pawn staged Drowsy (rest below RestEnter) gets its Sleep block
//     extended by SleepExtension hours at the wake end, and the extension
//     reverts once the pawn is staged rested.
//   - schedule/meditate: on a Royalty profile a psylinked pawn meditates in
//     its recreation block instead of Joy.
package schedule

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const setupOp = "test/schedule_setup"

func init() {
	cases.Register(cases.Case{
		Name: "schedule/timetables",
		Scope: "Issue #1318: the work family writes each colonist a timetable whose Joy hour sits right before Sleep; " +
			"a colonist staged Drowsy gets two more Sleep hours at the wake end, and loses them once staged rested. " +
			"Each written timetable is read back from native.",
		Start:       cases.LabStart(),
		Service:     true,
		RequiredOps: []string{setupOp},
		// Rest stays live so the fixture's staged level is what the planner reads.
		Keep:   []string{string(na.NeedRest)},
		Budget: 12 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: timetables,
	})
	cases.Register(cases.Case{
		Name: "schedule/meditate",
		Scope: "Issue #1318: on a Royalty profile a colonist given a psylink gets Meditate in its recreation block " +
			"instead of Joy, read back from native; colonists without a psylink keep Joy.",
		Start:      cases.Fixture{Op: setupOp, Args: map[string]any{"rest": 0.9, "psylink": true}, On: cases.DebugStart{Size: na.DebugStart{Seed: "schedule-meditate-1318"}}},
		Expansions: []string{"ludeon.rimworld.royalty"},
		NoKeep:     true,
		Service:    true,
		Keep:       []string{string(na.NeedRest)},
		Budget:     10 * time.Minute,
		Crew:       cases.Crew{Size: 3}, Run: meditate,
	})
}

// run is one serve window's evidence: the timetable the work family wrote
// for the target pawn and every colonist's native timetable afterwards.
type run struct {
	planned []string
	native  map[string][]string
}

// hours is the set of hours slot def holds.
func hours(slots []string, def string) map[int]bool {
	out := map[int]bool{}
	for h, s := range slots {
		if s == def {
			out[h] = true
		}
	}
	return out
}

// blockEnd is the last hour of the Sleep block (the hour whose successor
// is not Sleep), or -1.
func blockEnd(slots []string) int {
	for h, s := range slots {
		if s == policy.ScheduleSleep && slots[(h+1)%24] != policy.ScheduleSleep {
			return h
		}
	}
	return -1
}

// recreationBeforeSleep checks slots carry exactly one block hour, right
// before the Sleep block, and never Work.
func recreationBeforeSleep(slots []string, block string) error {
	if len(slots) != 24 {
		return fmt.Errorf("timetable has %d hours", len(slots))
	}
	if len(hours(slots, "Work")) > 0 {
		return fmt.Errorf("timetable writes Work: %v", slots)
	}
	at := hours(slots, block)
	if len(at) != 1 {
		return fmt.Errorf("want one %s hour, got %v", block, slots)
	}
	for h := range at {
		if slots[(h+1)%24] != policy.ScheduleSleep {
			return fmt.Errorf("%s hour %d is not right before Sleep: %v", block, h, slots)
		}
	}
	return nil
}

// extended checks now is base with the Sleep block SleepExtension hours
// longer at the wake end.
func extended(base, now []string) error {
	end := blockEnd(base)
	want := append([]string(nil), base...)
	for i := 1; i <= policy.SleepExtension; i++ {
		want[(end+i)%24] = policy.ScheduleSleep
	}
	if !same(want, now) {
		return fmt.Errorf("want %v, got %v", want, now)
	}
	return nil
}

func same(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// setup stages the target pawn through the fixture and returns its ID.
func setup(ctx context.Context, h *na.Harness, label string, rest float64) (string, error) {
	reply, err := h.Call(ctx, label, setupOp, map[string]any{"rest": rest})
	if err != nil {
		return "", err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return "", fmt.Errorf("%s: %v", label, reply)
	}
	return na.AsString(reply["pawn"]), nil
}

// readTimetables reads every free colonist's native timetable.
func readTimetables(ctx context.Context, h *na.Harness, label string, identity map[string]any) (map[string][]string, error) {
	reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"colonist": true},
		"details": map[string]any{"schedule": true},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	out := map[string][]string{}
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		settings, _ := na.AsMap(row["settings"])
		slots := make([]string, 24)
		for _, s := range na.AsSlice(settings["schedule"]) {
			slot, _ := na.AsMap(s)
			if h := int(na.AsNumber(slot["hour"])); h >= 0 && h < 24 {
				slots[h] = na.AsString(slot["assignmentDefName"])
			}
		}
		out[na.AsString(pawn["id"])] = slots
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("%s: no colonists observed: %v", label, observed)
	}
	return out, nil
}

// completed reports every action of plan completed.
func completed(plan store.PlanState) bool {
	if len(plan.Progress) == 0 || len(plan.Progress) != len(plan.Spec.Actions()) {
		return false
	}
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Completed {
			return false
		}
	}
	return true
}

// window serves the work family until a completed work plan not in seen
// wrote pawn a timetable want accepts, then stops the service and reads the
// native timetables back. seen gains every work plan the window observed.
func window(ctx context.Context, s cases.Session, label, pawn string, seen map[domain.PlanID]bool, want func([]string) error) (run, error) {
	report := s.Report()
	identity := s.Identity()
	service, err := s.Launch(ctx, na.ServiceLaunch{Families: []routinefamily.Family{routinefamily.Work}, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return run{}, err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return run{}, err
	}
	if _, err = service.WaitAttached(identity, 90*time.Second); err != nil {
		return run{}, err
	}
	if _, err = service.Resume(label, identity, token, report); err != nil {
		return run{}, err
	}
	stopKeep := (&na.AuthorityKeepAlive{Service: service, Prefix: label, Identity: identity, Token: token}).Start(ctx)
	defer stopKeep()
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return run{}, err
	}
	defer journal.Close()
	var planned []string
	var last error
	poll := time.NewTicker(500 * time.Millisecond)
	defer poll.Stop()
	deadline := time.NewTimer(3 * time.Minute)
	defer deadline.Stop()
	for planned == nil {
		select {
		case <-ctx.Done():
			return run{}, ctx.Err()
		case <-deadline.C:
			return run{}, fmt.Errorf("%s: no completed work plan wrote %s the wanted timetable (last: %v)", label, pawn, last)
		case <-poll.C:
		}
		plans, err := journal.PlanHistoryWithMethods(ctx, 64, "work-*")
		if err != nil {
			return run{}, err
		}
		for _, plan := range plans {
			id := plan.Spec.ID()
			if seen[id] || !completed(plan) {
				continue
			}
			seen[id] = true
			for _, action := range plan.Spec.Actions() {
				w, ok := action.WorkAssignment()
				if !ok || string(w.Pawn()) != pawn || !w.HasSchedule() {
					continue
				}
				if last = want(w.Schedule()); last == nil {
					planned = w.Schedule()
					report[label+"_plan"] = string(id)
				}
			}
		}
	}
	report[label+"_planned"] = planned
	stopKeep()
	journal.Close()
	service.Stop()
	h, err := s.Reattach(ctx)
	if err != nil {
		return run{}, err
	}
	if _, err := h.Call(ctx, label+"-pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return run{}, err
	}
	native, err := readTimetables(ctx, h, label+"-native", identity)
	if err != nil {
		return run{}, err
	}
	report[label+"_native"] = native
	if !same(native[pawn], planned) {
		return run{}, fmt.Errorf("%s: native timetable %v is not the written %v", label, native[pawn], planned)
	}
	return run{planned: planned, native: native}, nil
}

func timetables(ctx context.Context, s cases.Session) error {
	seen := map[domain.PlanID]bool{}
	pawn, err := setup(ctx, s.Harness(), "rested", 0.9)
	if err != nil {
		return err
	}
	s.Report()["pawn"] = pawn
	base, err := window(ctx, s, "base", pawn, seen, func(slots []string) error {
		return recreationBeforeSleep(slots, policy.ScheduleJoy)
	})
	if err != nil {
		return err
	}
	// Every colonist the planner wrote (not only the target) has Joy
	// right before Sleep: the lab has no recreation places to stagger.
	for id, slots := range base.native {
		if err := recreationBeforeSleep(slots, policy.ScheduleJoy); err != nil {
			return fmt.Errorf("colonist %s: %w", id, err)
		}
	}
	if _, err = setup(ctx, s.Harness(), "drowsy", 0.1); err != nil {
		return err
	}
	if _, err = window(ctx, s, "drowsy", pawn, seen, func(slots []string) error {
		return extended(base.planned, slots)
	}); err != nil {
		return err
	}
	if _, err = setup(ctx, s.Harness(), "restored", 1.0); err != nil {
		return err
	}
	_, err = window(ctx, s, "restored", pawn, seen, func(slots []string) error {
		if !same(slots, base.planned) {
			return fmt.Errorf("want base %v, got %v", base.planned, slots)
		}
		return nil
	})
	return err
}

func meditate(ctx context.Context, s cases.Session) error {
	pawn := na.AsString(s.Prepared()["pawn"])
	if pawn == "" {
		return fmt.Errorf("fixture named no pawn: %v", s.Prepared())
	}
	s.Report()["pawn"] = pawn
	got, err := window(ctx, s, "meditate", pawn, map[domain.PlanID]bool{}, func(slots []string) error {
		if len(hours(slots, policy.ScheduleMeditate)) == 0 || len(hours(slots, policy.ScheduleJoy)) > 0 {
			return fmt.Errorf("want Meditate in place of Joy, got %v", slots)
		}
		return nil
	})
	if err != nil {
		return err
	}
	for id, slots := range got.native {
		if id != pawn && len(hours(slots, policy.ScheduleMeditate)) > 0 {
			return fmt.Errorf("colonist %s without a psylink meditates: %v", id, slots)
		}
	}
	return nil
}
