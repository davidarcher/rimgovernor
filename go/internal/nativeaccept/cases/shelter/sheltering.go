package shelter

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The sheltering cases (#1329) prove MaintainShelter's Safe area and the
// sheltering planner (#1325, #1326) end to end on the lab: the
// ShelterFixture hut at the centre is the only roofed room, so the Safe
// area covers it. A snapshot test cannot cover them: the assertion is the
// native allowed-area writes landing on colonists and a tame animal, the
// Safe area the game holds and the pawns physically sheltering from a real
// condition or a real manhunter pack.
//
// The controller holds the GABP slot while it runs (#676), so the trigger
// ends between two service runs: the planner keeps no history, so the
// restarted service restores from the facts alone.
const (
	shelteringFamilies = "recovery,sheltering"
	shelterWait        = 4 * time.Minute
	// shelterWalkTicks is the game-time budget for the walk from the
	// staging row (ten cells from the hut) into it.
	shelterWalkTicks = 6000
)

func init() {
	for _, c := range []struct {
		name, trigger, scope string
		animal               bool
	}{
		{"shelter/manhunter_waitout", "manhunter",
			"A manhunter pack enters the lab from the west edge: the colonists, none drafted, are moved into the " +
				"Safe area (the roofed hut) and no one is injured; once the pack has left the map, everyone still " +
				"restricted to the Safe area is unrestricted again. Native area writes, the native Safe area and a " +
				"real manhunter pack are the signal, not a planner decision.", false},
		{"shelter/toxic_fallout", "fallout",
			"Toxic fallout starts on the lab: every colonist and the tame pen-free dog are moved into the Safe area " +
				"(the roofed hut) and stand in it; once the fallout ends, all of them are unrestricted again. Native " +
				"area writes, the native Safe area and a real game condition are the signal, not a planner decision.", true},
	} {
		c := c
		cases.Register(cases.Case{
			Name:        c.name,
			Scope:       c.scope,
			Start:       cases.Fixture{On: cases.LabStart(), Op: "test/shelter_prepare", Args: map[string]any{"trigger": c.trigger}},
			RequiredOps: []string{"test/shelter_prepare", "test/shelter_stage"},
			Serve:       &cases.ServeSpec{Families: []string{shelteringFamilies}, NativeTimeout: 60 * time.Second, Prefix: "sheltering"},
			Budget:      12 * time.Minute,
			Run:         func(ctx context.Context, s cases.Session) error { return runSheltering(ctx, s, c.animal) },
		})
	}
}

func runSheltering(ctx context.Context, s cases.Session, withAnimal bool) error {
	report := s.Report()
	prepared := s.Prepared()
	var expected []string
	for _, raw := range na.AsSlice(prepared["colonists"]) {
		expected = append(expected, na.AsString(raw))
	}
	if withAnimal {
		expected = append(expected, na.AsString(prepared["animal"]))
	}
	if len(expected) == 0 || expected[0] == "" {
		return fmt.Errorf("prepare: no colonists to shelter: %#v", prepared)
	}
	report["sheltered_pawns"] = expected
	w := waits{stall: na.StallBudget()}

	// 1. The trigger holds: every expected pawn is moved into the Safe area.
	service, err := start(ctx, s, nil)
	if err != nil {
		return err
	}
	st, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		service.Stop()
		return err
	}
	if err := waitAreas(ctx, st, expected, true, w.wait(shelterWait, service), report, "shelter"); err != nil {
		st.Close()
		service.Stop()
		return err
	}
	st.Close()
	report["shelter_service"] = service.Stop()
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	// The journal settles when the area writes complete; the walk into the
	// hut takes game time the stopped service no longer runs.
	var sheltered map[string]any
	if _, err := na.RunUntil(ctx, h, "shelter-walk", shelterWalkTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := stage(ctx, h, "status")
		if err != nil {
			return "", false, err
		}
		sheltered = got
		return fmt.Sprint(got["pawns"]), checkPawns(got, expected, true) == nil, nil
	}); err != nil {
		return err
	}
	report["sheltered"] = sheltered
	if err := checkPawns(sheltered, expected, true); err != nil {
		return err
	}

	// 2. The trigger ends: the restarted service restores every pawn.
	ended, err := stage(ctx, h, "end")
	if err != nil {
		return err
	}
	report["ended"] = ended
	if active, _ := na.AsBool(ended["fallout"]); active || na.AsNumber(ended["packOnMap"]) != 0 {
		return fmt.Errorf("the trigger did not end: %#v", ended)
	}
	if service, err = start(ctx, s, service); err != nil {
		return err
	}
	st, err = na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		service.Stop()
		return err
	}
	err = waitAreas(ctx, st, expected, false, w.wait(shelterWait, service), report, "restore")
	st.Close()
	report["restore_service"] = service.Stop()
	if err != nil {
		return err
	}
	if h, err = s.Reattach(ctx); err != nil {
		return err
	}
	restored, err := stage(ctx, h, "status")
	if err != nil {
		return err
	}
	report["restored"] = restored
	return checkPawns(restored, expected, false)
}

// stage calls test/shelter_stage.
func stage(ctx context.Context, h *na.Harness, op string) (map[string]any, error) {
	reply, err := h.Call(ctx, "shelter-"+op, "test/shelter_stage", map[string]any{"op": op})
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("shelter_stage %s refused: %#v", op, reply)
	}
	return reply, nil
}

// checkPawns asserts the native postcondition: sheltered, every expected
// pawn is restricted to the Safe area, stands in the hut and is unhurt;
// restored, none is restricted to it.
func checkPawns(status map[string]any, expected []string, sheltered bool) error {
	rows := map[string]map[string]any{}
	for _, raw := range na.AsSlice(status["pawns"]) {
		row, _ := na.AsMap(raw)
		rows[na.AsString(row["id"])] = row
	}
	for _, id := range expected {
		row, ok := rows[id]
		if !ok {
			return fmt.Errorf("%s is missing from the fixture status", id)
		}
		if dead, _ := na.AsBool(row["dead"]); dead {
			return fmt.Errorf("%s died: %#v", id, row)
		}
		if na.AsNumber(row["injuries"]) != 0 {
			return fmt.Errorf("%s was harmed: %#v", id, row)
		}
		area := na.AsString(row["area"])
		if !sheltered {
			if area == policy.SafeAreaLabel {
				return fmt.Errorf("%s is still restricted to the Safe area: %#v", id, row)
			}
			continue
		}
		if area != policy.SafeAreaLabel {
			return fmt.Errorf("%s is restricted to %q, not the Safe area: %#v", id, area, row)
		}
		if in, _ := na.AsBool(row["inHut"]); !in {
			return fmt.Errorf("%s is restricted to the Safe area but stands outside the hut: %#v", id, row)
		}
	}
	return nil
}

// waitAreas polls the journal until every pawn's latest completed
// allowed-area action shelters it (a non-empty area) or, restoring, clears
// it. The plans are the sheltering planner's RecoverDisasterServices
// methods; restoring, a completed delete of the Safe area after the pawn's
// last move (MaintainShelter resets its areas in a new service) also
// unrestricts it. The native readback after the service stops is the
// assertion.
func waitAreas(ctx context.Context, st *store.Store, pawns []string, shelter bool, w na.Wait, report na.Report, label string) error {
	var last map[string]string
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		history, err := st.IncidentHistory(ctx, store.World{Colony: review.Snapshot.Colony, Load: review.Snapshot.Load, Map: review.Snapshot.Map}, policy.RecoverDisasterServices)
		if err != nil {
			return "", false, err
		}
		areas := map[string]string{}
		ticks := map[string]domain.Tick{}
		for _, incident := range history {
			for _, m := range incident.Methods {
				plan, err := st.LoadPlan(ctx, m.Plan)
				if err != nil {
					return "", false, err
				}
				for i, a := range plan.Spec.Actions() {
					var pawn, area string
					if work, ok := a.WorkAssignment(); ok && work.HasArea() {
						pawn, area = string(work.Pawn()), work.Area()
						if work.AreaClear() {
							area = ""
						}
					} else if hb, ok := a.Husbandry(); ok && hb.Method() == domain.HusbandryAllowedArea {
						pawn, area = string(hb.Animal()), hb.Argument()
					} else {
						continue
					}
					v := plan.Progress[i].View()
					if v.Stage != domain.Completed || (ticks[pawn] != 0 && v.Tick < ticks[pawn]) {
						continue
					}
					areas[pawn], ticks[pawn] = area, v.Tick
				}
			}
		}
		reset, known := domain.Tick(0), false
		if goal, _, err := st.Workable(ctx, review, policy.MaintainShelter); err != nil {
			return "", false, err
		} else {
			for _, m := range goal.History {
				plan, err := st.LoadPlan(ctx, m.Plan)
				if err != nil {
					return "", false, err
				}
				for i, a := range plan.Spec.Actions() {
					edit, ok := a.Area()
					if v := plan.Progress[i].View(); ok && edit.Key() == policy.SafeAreaKey && edit.Operation() == domain.AreaDelete && v.Stage == domain.Completed && (!known || v.Tick > reset) {
						reset, known = v.Tick, true
					}
				}
			}
		}
		last = areas
		if !areasSettled(areas, ticks, reset, known, pawns, shelter) {
			return na.Signature(len(history), areas, reset, known), false, nil
		}
		return "", true, nil
	})
	report[label+"_areas"] = last
	if err != nil {
		return fmt.Errorf("%s: pawns' allowed areas did not settle (last %v): %w", label, last, err)
	}
	return nil
}

// areasSettled reports whether the journal shows every pawn sheltered (its
// latest completed move names an area) or restored: its latest move clears
// it, or the Safe area was deleted (resetKnown, at reset) after it. A
// pawn with no move at all is neither: an empty journal proves nothing.
func areasSettled(areas map[string]string, ticks map[string]domain.Tick, reset domain.Tick, resetKnown bool, pawns []string, shelter bool) bool {
	for _, p := range pawns {
		area, ok := areas[p]
		if !ok {
			return false
		}
		if shelter && area == "" || !shelter && area != "" && !(resetKnown && reset > ticks[p]) {
			return false
		}
	}
	return true
}
