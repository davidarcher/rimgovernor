package shelter

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The mid-project scenarios of #63: what the staged excavation does when
// the ground it read at admission changes under it. Each starts the same
// staged fixture as shelter/excavation and either hides a change in the fog
// or applies one through the fixture after stage 0, between a service stop
// and a restart, so the controller meets it as changed geometry on review.
func init() {
	serve := func(prefix string) *cases.ServeSpec {
		return &cases.ServeSpec{
			Families: []string{"shelter", "tend", "rescue", "defense", "naming"},
			Prefix:   prefix,
		}
	}
	cases.Register(cases.Case{
		Name: "shelter/excavation-hazard",
		Scope: "Revealed hazards: two fogged interior cells unfog into an ancient wall as the dig reaches " +
			"them; no stage designates them, the room is dug around them, closed with a door, furnished and " +
			"slept in, and the walls still stand inside it.",
		Start:  excavationStart(map[string]any{"hazard": true}),
		Keep:   []string{string(na.NeedRest)},
		Serve:  serve("excavation-hazard"),
		Budget: 15 * time.Minute,
		Run:    excavationHazard,
	})
	cases.Register(cases.Case{
		Name: "shelter/excavation-reroute",
		Scope: "Blocked access: the corridor mouth is walled shut after stage 0; the stage in flight closes, " +
			"the next stage is admitted under a new target through the fixture's second predug lane with a " +
			"reachable access cell, the sealed corridor is never designated again and the new room closes with a door.",
		Start:  excavationStart(map[string]any{"lanes": 2}),
		Serve:  serve("excavation-reroute"),
		Budget: 15 * time.Minute,
		Run:    excavationReroute,
	})
	cases.Register(cases.Case{
		Name: "shelter/excavation-breach",
		Scope: "Changed support: the rock around the room is levelled after stage 0, so finishing the dig would " +
			"leave its roof unsupported; the stage in flight closes, no further stage is admitted under the target, " +
			"the shelter is re-sited as the open-site shell and the roof over the cleared cells still stands.",
		// The shell the review falls back to is wooden: enough logs to
		// commit it, so the re-siting shows as a committed method.
		Start:  excavationStart(map[string]any{"wood": 450}),
		Serve:  serve("excavation-breach"),
		Budget: 15 * time.Minute,
		Run:    excavationBreach,
	})
}

func excavationHazard(ctx context.Context, s cases.Session) error {
	var kept []domain.Cell
	for _, h := range na.AsSlice(s.Prepared()["hazards"]) {
		row, _ := na.AsMap(h)
		kept = append(kept, domain.Cell{X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))})
	}
	if len(kept) != 2 {
		return fmt.Errorf("fixture hid %d hazard cells, want 2: %#v", len(kept), s.Prepared()["hazards"])
	}
	s.Report()["hazards"] = kept
	return runExcavation(ctx, s, excavationOptions{kept: kept})
}

// faceArgs addresses a fixture change at the target's face: the first
// corridor cell and the direction into the rock.
func faceArgs(run *excavationRun, action string) map[string]any {
	t := run.target
	return map[string]any{"action": action, "x": int(t.Corridor[0].X), "z": int(t.Corridor[0].Z), "dx": int(t.Direction.X), "dz": int(t.Direction.Z)}
}

// waitStage0Done waits for stage 0 to complete and returns its cells.
func waitStage0Done(ctx context.Context, run *excavationRun) ([]domain.Cell, error) {
	method, err := waitStageMethod(ctx, run.store, run.goalID, 0)
	if err != nil || method == nil {
		return nil, fmt.Errorf("stage 0: %v (%w)", method, err)
	}
	cells, err := waitStageCompleted(ctx, run.store, method.Plan)
	if err != nil {
		return nil, fmt.Errorf("stage 0: %w", err)
	}
	run.report["stage0_cells"] = cells
	return cells, nil
}

// waitPlanClosed polls until no action of the plan is open any more and
// returns the stages reached: a stage admitted before a change may finish,
// fail on its cancelled designations or be cancelled as stalled.
func waitPlanClosed(ctx context.Context, s *store.Store, planID domain.PlanID) (map[string]int, error) {
	outcome := map[string]int{}
	err := na.WaitProgress(ctx, storeWait(), func(ctx context.Context) (string, bool, error) {
		state, err := s.LoadPlan(ctx, planID)
		if err != nil {
			return "", false, err
		}
		outcome = map[string]int{}
		var signature []any
		for _, p := range state.Progress {
			v := p.View()
			signature = append(signature, v.Stage, v.Attempt)
			outcome[string(v.Stage)]++
		}
		return na.Signature(signature...), !domain.GoalWorkOpen(state.Progress), nil
	})
	if err != nil {
		return nil, fmt.Errorf("plan %s: %w", planID, err)
	}
	return outcome, nil
}

func excavationReroute(ctx context.Context, s cases.Session) error {
	run, err := openExcavation(ctx, s, rectangle)
	if run != nil {
		defer run.close()
	}
	if err != nil {
		return err
	}
	report := s.Report()
	old := run.target
	if _, err := waitStage0Done(ctx, run); err != nil {
		return err
	}
	sealed, err := run.change(ctx, "seal", faceArgs(run, "seal"))
	if err != nil {
		return err
	}
	if len(na.AsSlice(sealed["walls"])) == 0 {
		return fmt.Errorf("seal built no wall: %#v", sealed)
	}

	// Stages after the seal: whichever stage carries the old key must close
	// without completing (its designations were cancelled, its access is
	// gone); the first stage under a new key is the re-sited project, whose
	// access cell is not the sealed one, and it runs to the door.
	var closed []map[string]any
	var fresh bool
	dug := map[domain.Cell]int{}
	for stage := 1; ; stage++ {
		method, err := waitStageMethod(ctx, run.store, run.goalID, stage)
		if err != nil {
			return fmt.Errorf("stage %d: %w", stage, err)
		}
		if method == nil {
			break
		}
		t, err := buildingruntime.ExcavationPlanTarget(method.Plan)
		if err != nil {
			return fmt.Errorf("stage %d plan %s: %w", stage, method.Plan, err)
		}
		if t.Key() == old.Key() {
			if fresh {
				return fmt.Errorf("stage %d returned to the sealed target after re-siting: %s", stage, method.Plan)
			}
			outcome, err := waitPlanClosed(ctx, run.store, method.Plan)
			if err != nil {
				return fmt.Errorf("stage %d under the sealed target: %w", stage, err)
			}
			closed = append(closed, map[string]any{"stage": stage, "plan": string(method.Plan), "outcome": outcome})
			report["closed_under_sealed_target"] = closed
			continue
		}
		if !fresh {
			fresh = true
			if err := run.bindTarget(method.Plan, "resited_target"); err != nil {
				return err
			}
			if run.target.Access == old.Access || containsCell(run.target.Cells(), old.Access) {
				return fmt.Errorf("re-sited target still enters through the sealed access %v: %s", old.Access, run.target.Key())
			}
			report["resited_at_stage"] = stage
		}
		cells, err := waitStageCompleted(ctx, run.store, method.Plan)
		if err != nil {
			return fmt.Errorf("stage %d: %w", stage, err)
		}
		for _, c := range cells {
			if containsCell(old.Corridor, c) || c == old.Door {
				return fmt.Errorf("stage %d re-designated the sealed corridor at %v", stage, c)
			}
			if !containsCell(run.cells, c) {
				return fmt.Errorf("stage %d dug %v outside the re-sited target", stage, c)
			}
			if prev, seen := dug[c]; seen {
				return fmt.Errorf("stage %d re-designated %v already cleared by stage %d", stage, c, prev)
			}
			dug[c] = stage
		}
	}
	if !fresh {
		return fmt.Errorf("no stage was admitted under a new target after the seal")
	}
	report["resited_stage_cells"] = len(dug)
	doorGoal, door, err := waitMethod(ctx, run.store, "", buildingruntime.ExcavationDoorMethod())
	if err != nil {
		return fmt.Errorf("door method: %w", err)
	}
	if doorGoal != run.goalID {
		return fmt.Errorf("door committed under goal %s, want the routine goal %s", doorGoal, run.goalID)
	}
	if t, err := buildingruntime.ExcavationPlanTarget(door.Plan); err != nil || t.Key() != run.target.Key() {
		return fmt.Errorf("door plan %s is not for the re-sited target %s: %v", door.Plan, run.target.Key(), err)
	}
	if err := waitPlanCompleted(ctx, run.store, door.Plan); err != nil {
		return fmt.Errorf("door plan: %w", err)
	}
	report["door_plan"] = string(door.Plan)

	run.store.Close()
	report["authority_reacquisitions"] = run.svc.Stop()
	run.store, run.svc = nil, nil
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause-final", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// The new room is finished and enclosed; a bed is the sleeping planner's
	// business, not this case's.
	if _, err := verifyRoomShape(ctx, h, run, report); err != nil {
		return err
	}
	// The sealed corridor stays as the player left it.
	sealedAt, err := h.Call(ctx, "sealed-access", "test/mountain_fixture", map[string]any{"action": "inspect", "x": int(old.Access.X), "z": int(old.Access.Z)})
	if err != nil {
		return err
	}
	if na.AsString(sealedAt["edifice"]) != "Wall" {
		return fmt.Errorf("the sealed access %v was reopened: %#v", old.Access, sealedAt)
	}
	return nil
}

// verifyRoomShape is verifyRoom without the furnishing: the room proper
// spans the interior and every cell is cleared under its roof.
func verifyRoomShape(ctx context.Context, h *na.Harness, run *excavationRun, report na.Report) (map[string]any, error) {
	target := run.target
	for i, c := range target.Cells() {
		row, err := h.Call(ctx, fmt.Sprintf("cell-%02d", i), "test/mountain_fixture", map[string]any{"action": "inspect", "x": int(c.X), "z": int(c.Z)})
		if err != nil {
			return nil, err
		}
		if na.AsString(row["mineable"]) != "" {
			return nil, fmt.Errorf("target cell %v still holds %s", c, row["mineable"])
		}
		if na.AsString(row["roof"]) != "RoofRockThick" || int(na.AsNumber(row["collapsing"])) != 0 {
			return nil, fmt.Errorf("target cell %v lost its rock roof or awaits a collapse: %#v", c, row)
		}
		isDoor, _ := na.AsBool(row["door"])
		if isDoor != (c == target.Door) {
			return nil, fmt.Errorf("door presence at %v: %v, want %v", c, isDoor, c == target.Door)
		}
	}
	room, err := h.Call(ctx, "room", "test/mountain_fixture", map[string]any{"action": "inspect", "x": int(target.Center().X), "z": int(target.Center().Z)})
	if err != nil {
		return nil, err
	}
	proper, _ := na.AsBool(room["properRoom"])
	outdoors, _ := na.AsBool(room["outdoors"])
	if !proper || outdoors {
		return nil, fmt.Errorf("interior is not a proper enclosed room: %#v", room)
	}
	if want := len(target.InteriorCells()); int(na.AsNumber(room["roomCells"])) != want {
		return nil, fmt.Errorf("room spans %v cells, want the %d-cell interior", room["roomCells"], want)
	}
	report["room"] = room
	return room, nil
}

func excavationBreach(ctx context.Context, s cases.Session) error {
	run, err := openExcavation(ctx, s, rectangle)
	if run != nil {
		defer run.close()
	}
	if err != nil {
		return err
	}
	report := s.Report()
	target := run.target
	stage0, err := waitStage0Done(ctx, run)
	if err != nil {
		return err
	}
	breached, err := run.change(ctx, "breach", faceArgs(run, "breach"))
	if err != nil {
		return err
	}
	if na.AsNumber(breached["levelled"]) == 0 || na.AsNumber(breached["collapsing"]) != 0 {
		return fmt.Errorf("breach levelled nothing or left a collapse pending: %#v", breached)
	}

	// The next review reads the whole target unsupported with no collapse
	// pending: the dig is abandoned and the shelter re-sited. A stage
	// admitted before the breach may still close either way; nothing new
	// is admitted under the target, and the general path commits the shell.
	var shell domain.GoalMethod
	var lastStage int
	closed := map[string]any{}
	err = na.WaitProgress(ctx, storeWait(), func(ctx context.Context) (string, bool, error) {
		goal, err := run.store.LoadGoal(ctx, run.goalID)
		if err != nil {
			return "", false, err
		}
		if goal.Goal.Status == domain.GoalInvalidated {
			return "", false, fmt.Errorf("goal %s invalidated after the breach", run.goalID)
		}
		var signature []any
		for _, m := range goal.Methods {
			signature = append(signature, m.Method, m.Plan)
			if strings.HasPrefix(string(m.Plan), "routine-shell-") {
				shell = m
			}
		}
		if shell.Plan == "" {
			return na.Signature(signature...), false, nil
		}
		for stage := 1; stage < 16; stage++ {
			m, err := run.store.LoadGoalMethod(ctx, run.goalID, goal.Goal.Epoch, buildingruntime.ExcavationStageMethod(stage))
			if err != nil {
				break
			}
			lastStage = stage
			t, err := buildingruntime.ExcavationPlanTarget(m.Plan)
			if err != nil || t.Key() != target.Key() {
				return "", false, fmt.Errorf("stage %d plan %s does not carry the breached target: %v", stage, m.Plan, err)
			}
			if stage > 1 {
				return "", false, fmt.Errorf("stage %d was admitted after the breach: %s", stage, m.Plan)
			}
			state, err := run.store.LoadPlan(ctx, m.Plan)
			if err != nil {
				return "", false, err
			}
			outcome := map[string]int{}
			for _, p := range state.Progress {
				outcome[string(p.View().Stage)]++
			}
			closed = map[string]any{"stage": stage, "plan": string(m.Plan), "outcome": outcome}
		}
		return na.Signature(signature...), true, nil
	})
	if err != nil {
		return fmt.Errorf("shell after the breach: %w", err)
	}
	report["shell_plan"] = string(shell.Plan)
	report["last_stage_under_target"] = lastStage
	report["stage_in_flight"] = closed
	if goal, err := run.store.LoadGoal(ctx, run.goalID); err != nil {
		return err
	} else if _, err := run.store.LoadGoalMethod(ctx, run.goalID, goal.Goal.Epoch, buildingruntime.ExcavationDoorMethod()); err == nil {
		return fmt.Errorf("the door was committed for the abandoned dig")
	}

	run.store.Close()
	report["authority_reacquisitions"] = run.svc.Stop()
	run.store, run.svc = nil, nil
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause-final", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// Nothing sound was finished, but nothing fell either: every cell
	// stage 0 cleared keeps its rock roof with no collapse pending, and no
	// door stands on the target.
	for i, c := range append(append([]domain.Cell{}, stage0...), target.Door) {
		row, err := h.Call(ctx, fmt.Sprintf("cell-%02d", i), "test/mountain_fixture", map[string]any{"action": "inspect", "x": int(c.X), "z": int(c.Z)})
		if err != nil {
			return err
		}
		if na.AsString(row["roof"]) != "RoofRockThick" || int(na.AsNumber(row["collapsing"])) != 0 {
			return fmt.Errorf("cell %v lost its rock roof or awaits a collapse after the breach: %#v", c, row)
		}
		if isDoor, _ := na.AsBool(row["door"]); isDoor {
			return fmt.Errorf("a door stands at %v on the abandoned dig", c)
		}
	}
	return nil
}
