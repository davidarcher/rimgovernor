// The workers/* cases prove the pawn manager (#417) against a live game:
// the roster planner reads the three seeded colonists through the same
// pawn observation the routine census requests, plans the priority matrix
// from skills, passions and traits, writes it through the real native
// PatchPawn dispatch under the work snapshot token, and reads a matching
// matrix back. Each scenario seeds one thing: a passion deciding an
// otherwise tied role (passion), traits forbidding roles and lifting
// fitness (traits), every core role covered and the matrix stable across
// reviews (coverage), and the schedule planner's timetables written beside
// the priorities with a player edit left alone (nightowl, schedule.go).
package workers

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

const owner = "native-workers-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "workers/passion",
		Scope: "Roster planner passion (#417): two colonists tied at Cooking 12, the major passion owns the kitchen " +
			"at priority 1 and the other backs it at 2; the matrix is written through native PatchPawn and read back.",
		Start:       cases.DebugStart{},
		RequiredOps: []string{"test/workers_setup"},
		Budget:      4 * time.Minute,
		Run:         func(ctx context.Context, s cases.Session) error { return runRoster(ctx, s, "passion", checkPassion) },
	})
	cases.Register(cases.Case{
		Name: "workers/traits",
		Scope: "Roster planner traits (#417): a Pyromaniac Brawler with Abrasive never fights fires, hunts or wardens; " +
			"Industrious wins a tied Construction sheet; all written through native PatchPawn and read back.",
		Start:       cases.DebugStart{},
		RequiredOps: []string{"test/workers_setup"},
		Budget:      4 * time.Minute,
		Run:         func(ctx context.Context, s cases.Session) error { return runRoster(ctx, s, "traits", checkTraits) },
	})
	cases.Register(cases.Case{
		Name: "workers/coverage",
		Scope: "Roster planner coverage (#417): a flat three-colonist sheet still covers every core role with one owner, " +
			"Capacity holds, the native write matches on readback and a second plan changes nothing.",
		Start:       cases.DebugStart{},
		RequiredOps: []string{"test/workers_setup"},
		Budget:      4 * time.Minute,
		Run:         func(ctx context.Context, s cases.Session) error { return runRoster(ctx, s, "coverage", checkCoverage) },
	})
}

// seeded is the fixture's reply: the three pawns in role order (A, B, C).
type seeded struct {
	IDs      []string
	Disabled map[string][]string
	Traits   map[string][]string
}

func seed(ctx context.Context, s cases.Session, scenario string) (seeded, error) {
	report := s.Report()
	prepared, err := s.Harness().Call(ctx, "setup", "test/workers_setup", map[string]any{"scenario": scenario})
	if err != nil {
		return seeded{}, err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return seeded{}, fmt.Errorf("setup: workers_setup refused: %#v", prepared)
	}
	out := seeded{Disabled: map[string][]string{}, Traits: map[string][]string{}}
	for _, raw := range na.AsSlice(prepared["pawns"]) {
		row, _ := na.AsMap(raw)
		id := na.AsString(row["id"])
		out.IDs = append(out.IDs, id)
		for _, d := range na.AsSlice(row["disabled"]) {
			out.Disabled[id] = append(out.Disabled[id], na.AsString(d))
		}
		for _, t := range na.AsSlice(row["traits"]) {
			out.Traits[id] = append(out.Traits[id], na.AsString(t))
		}
	}
	if len(out.IDs) != 3 {
		return seeded{}, fmt.Errorf("setup: expected three seeded pawns, got %#v", prepared)
	}
	report["fixture_pawns"] = prepared["pawns"]
	return out, nil
}

// readPawns reads the seeded pawns with the routine census's detail
// selection and lifts them the way observation.routineWork does.
func readPawns(ctx context.Context, s cases.Session, label string, ids []string) ([]policy.WorkPawn, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": s.Identity()},
		"filter":  map[string]any{"ids": ids, "includeDead": true},
		"details": map[string]any{"health": true, "equipment": true, "biography": true, "work": true, "needs": true, "settings": true, "schedule": true},
		"page":    map[string]any{"limit": len(ids)},
	})
	if err != nil {
		return nil, err
	}
	encoded, err := json.Marshal(reply)
	if err != nil {
		return nil, err
	}
	typed := &o.ListPawnsReply{}
	if err = protojson.Unmarshal(encoded, typed); err != nil {
		return nil, fmt.Errorf("%s: decode pawn reply: %w", label, err)
	}
	observed := typed.GetObserved()
	if observed == nil || len(observed.Pawns) != len(ids) {
		return nil, fmt.Errorf("%s: expected the %d seeded pawns, got %#v", label, len(ids), reply)
	}
	var pawns []policy.WorkPawn
	for _, row := range observed.Pawns {
		w := observation.WorkPawnRow(row)
		_, ak := w.Available.Value()
		_, pk := w.Applies.Value()
		_, mk := w.Manual.Value()
		_, tk := w.SnapshotToken.Value()
		_, wk := w.Work.Value()
		_, sk := w.Skills.Value()
		_, trk := w.Traits.Value()
		_, ik := w.Incapable.Value()
		_, schk := w.Schedule.Value()
		for name, known := range map[string]bool{"available": ak, "applies": pk, "manual": mk, "token": tk, "work": wk, "skills": sk, "traits": trk, "incapable": ik, "schedule": schk} {
			if !known {
				return nil, fmt.Errorf("%s: pawn %s read left %s unknown", label, w.ID, name)
			}
		}
		if manual, _ := w.Manual.Value(); !manual {
			return nil, fmt.Errorf("%s: fixture left manual priorities off", label)
		}
		pawns = append(pawns, w)
	}
	sort.Slice(pawns, func(i, j int) bool { return pawns[i].ID < pawns[j].ID })
	return pawns, nil
}

// dispatch writes every assignment the readback does not already hold
// through the real native PatchPawn execute, mirroring
// buildingruntime's work review and bridge.workOperation, and returns the
// number of pawns written.
func dispatch(ctx context.Context, s cases.Session, label string, pawns []policy.WorkPawn, decision policy.WorkDecision, schedules map[policy.PawnID][]string) (int, error) {
	h := s.Harness()
	identity := s.Identity()
	byID := map[policy.PawnID]policy.WorkPawn{}
	for _, p := range pawns {
		byID[p.ID] = p
	}
	written := 0
	for _, assignment := range decision.Assignments {
		pawn := byID[assignment.Pawn]
		changed, ok := policy.WorkChanges(pawn, assignment)
		if !ok {
			return 0, fmt.Errorf("%s: pawn %s readback lacks a planned work type", label, pawn.ID)
		}
		schedule := schedules[assignment.Pawn]
		if len(changed) == 0 && len(schedule) == 0 {
			continue
		}
		token, _ := pawn.SnapshotToken.Value()
		patch := map[string]any{"pawn": map[string]any{"entityId": string(pawn.ID), "expectedSnapshotToken": token}}
		var work []map[string]any
		for _, row := range changed {
			work = append(work, map[string]any{"workTypeDef": row.Definition, "priority": row.Priority})
		}
		if len(work) > 0 {
			patch["work"] = work
		}
		if len(schedule) > 0 {
			patch["schedule"] = map[string]any{"assignmentDefs": schedule}
		}
		_, generation, err := na.AuthorityStatus(ctx, h.WireFunc(), label+"-generation", identity)
		if err != nil {
			return 0, err
		}
		written++
		request := map[string]any{
			"precondition": map[string]any{"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": owner, "actionId": fmt.Sprintf("%s-%s", label, pawn.ID), "attemptId": "1"}},
			"operation": map[string]any{"patchPawn": patch},
		}
		reply, err := h.Wire(ctx, fmt.Sprintf("%s-execute-%d", label, written), "operations_execute", request)
		if err != nil {
			return 0, err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		if err != nil {
			return 0, fmt.Errorf("%s: pawn %s: %w", label, pawn.ID, err)
		}
		applied, ok := na.AsMap(receipt["applied"])
		if !ok {
			return 0, fmt.Errorf("%s: pawn %s: expected an applied receipt, got %#v", label, pawn.ID, receipt)
		}
		observed, _ := na.AsMap(applied["observed"])
		settings, _ := na.AsMap(observed["settings"])
		fields := na.AsSlice(settings["fields"])
		expected := len(work)
		if len(schedule) > 0 {
			expected++
		}
		if len(fields) != expected {
			return 0, fmt.Errorf("%s: pawn %s: expected %d applied fields, got %#v", label, pawn.ID, expected, settings)
		}
		for _, raw := range fields {
			field, _ := na.AsMap(raw)
			if na.AsString(field["outcome"]) != "FIELD_OUTCOME_APPLIED" {
				return 0, fmt.Errorf("%s: pawn %s: field not applied: %#v", label, pawn.ID, field)
			}
		}
	}
	return written, nil
}

func priority(decision policy.WorkDecision, pawn policy.PawnID, work policy.WorkType) int {
	for _, a := range decision.Assignments {
		if a.Pawn != pawn {
			continue
		}
		for _, p := range a.Priorities {
			if p.Work == work {
				return p.Priority
			}
		}
	}
	return -1
}

func coverage(decision policy.WorkDecision, work policy.WorkType) (policy.WorkCoverage, bool) {
	for _, c := range decision.Coverage {
		if c.Work == work {
			return c, true
		}
	}
	return policy.WorkCoverage{}, false
}

type check func(seeded, policy.WorkDecision) error

// runRoster seeds the scenario, plans, writes, reads back and checks the
// scenario's rows on the plan the readback reproduces; a second plan on
// the written sheet must match and change nothing.
func runRoster(ctx context.Context, s cases.Session, scenario string, verify check) error {
	report := s.Report()
	if !na.Contains(s.Names(), "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}
	fixture, err := seed(ctx, s, scenario)
	if err != nil {
		return err
	}
	pawns, err := readPawns(ctx, s, "before", fixture.IDs)
	if err != nil {
		return err
	}
	decision, err := policy.PlanWork(pawns, nil, nil, policy.WorkDemand{})
	if err != nil {
		return err
	}
	if capacity, ok := decision.Capacity.Value(); !ok || !capacity {
		return fmt.Errorf("before: planner found no capacity: %+v", decision.Coverage)
	}
	report["plan_before"] = decision
	if err := verify(fixture, decision); err != nil {
		return fmt.Errorf("before: %w", err)
	}
	if _, err := na.GrantAuto(ctx, s.Harness().WireFunc(), "acquire", s.Identity()); err != nil {
		return err
	}
	written, err := dispatch(ctx, s, "write", pawns, decision, nil)
	if err != nil {
		return err
	}
	if written == 0 {
		return fmt.Errorf("write: the flat fixture sheet already matched the plan")
	}
	report["pawns_written"] = written
	after, err := readPawns(ctx, s, "after", fixture.IDs)
	if err != nil {
		return err
	}
	replan, err := policy.PlanWork(after, nil, nil, policy.WorkDemand{})
	if err != nil {
		return err
	}
	report["plan_after"] = replan
	if matches, _ := replan.Matches.Value(); !matches {
		return fmt.Errorf("after: the written sheet does not match the plan: %+v", replan.Assignments)
	}
	if err := verify(fixture, replan); err != nil {
		return fmt.Errorf("after: %w", err)
	}
	for _, a := range decision.Assignments {
		for _, p := range a.Priorities {
			if got := priority(replan, a.Pawn, p.Work); got != p.Priority {
				return fmt.Errorf("after: %s %s planned %d before the write and %d after it", a.Pawn, p.Work, p.Priority, got)
			}
		}
	}
	return nil
}

func hasDisabled(fixture seeded, id string, work policy.WorkType) bool {
	for _, d := range fixture.Disabled[id] {
		if d == string(work) {
			return true
		}
	}
	return false
}

func checkPassion(fixture seeded, decision policy.WorkDecision) error {
	a, b, c := policy.PawnID(fixture.IDs[0]), policy.PawnID(fixture.IDs[1]), policy.PawnID(fixture.IDs[2])
	if hasDisabled(fixture, fixture.IDs[0], policy.WorkCooking) || hasDisabled(fixture, fixture.IDs[1], policy.WorkCooking) {
		return fmt.Errorf("fixture pawns cannot cook: %v", fixture.Disabled)
	}
	if got := priority(decision, a, policy.WorkCooking); got != 1 {
		return fmt.Errorf("major passion cook %s planned Cooking %d, want 1", a, got)
	}
	if got := priority(decision, b, policy.WorkCooking); got != 2 {
		return fmt.Errorf("tied passionless cook %s planned Cooking %d, want the backup 2", b, got)
	}
	if got := priority(decision, c, policy.WorkCooking); got != 0 && got != 4 {
		return fmt.Errorf("cook at 3, %s, planned Cooking %d, want under the food-poisoning floor", c, got)
	}
	if row, ok := coverage(decision, policy.WorkCooking); !ok || row.Demand != 1 || row.Owners != 1 {
		return fmt.Errorf("cooking coverage %+v, want one owner", row)
	}
	return nil
}

func checkTraits(fixture seeded, decision policy.WorkDecision) error {
	a, b, c := policy.PawnID(fixture.IDs[0]), policy.PawnID(fixture.IDs[1]), policy.PawnID(fixture.IDs[2])
	// Core disables Firefighter for a Pyromaniac (the planner skips a
	// disabled row); Hunting and Warden stay enabled and the traits zero
	// them.
	for _, work := range []policy.WorkType{policy.WorkFirefighter, policy.WorkHunting, policy.WorkWarden} {
		if got := priority(decision, a, work); got > 0 {
			return fmt.Errorf("pyromaniac brawler abrasive %s planned %s %d, want 0", a, work, got)
		}
	}
	if got := priority(decision, a, policy.WorkHunting); got != 0 {
		return fmt.Errorf("brawler %s planned Hunting %d, want the row at 0", a, got)
	}
	if hasDisabled(fixture, fixture.IDs[1], policy.WorkConstruction) || hasDisabled(fixture, fixture.IDs[2], policy.WorkConstruction) {
		return fmt.Errorf("fixture pawns cannot build: %v", fixture.Disabled)
	}
	if got := priority(decision, b, policy.WorkConstruction); got != 1 {
		return fmt.Errorf("industrious builder %s planned Construction %d, want 1", b, got)
	}
	if got := priority(decision, c, policy.WorkConstruction); got == 1 {
		return fmt.Errorf("tied builder %s without the trait also owns Construction", c)
	}
	return nil
}

func checkCoverage(fixture seeded, decision policy.WorkDecision) error {
	for _, work := range []policy.WorkType{policy.WorkDoctor, policy.WorkCooking, policy.WorkConstruction, policy.WorkGrowing} {
		row, ok := coverage(decision, work)
		if !ok || row.Owners != 1 || row.Demand != 1 {
			return fmt.Errorf("core role %s coverage %+v, want one owner for a demand of one", work, row)
		}
		owners := 0
		for _, id := range fixture.IDs {
			if priority(decision, policy.PawnID(id), work) == 1 {
				owners++
			}
		}
		if owners != 1 {
			return fmt.Errorf("%d pawns own %s at priority 1, want one", owners, work)
		}
	}
	for _, id := range fixture.IDs {
		if got := priority(decision, policy.PawnID(id), policy.WorkFirefighter); got != 1 && !hasDisabled(fixture, id, policy.WorkFirefighter) {
			return fmt.Errorf("%s planned Firefighter %d, want the pinned 1", id, got)
		}
		// Hauling is 3 for everyone but the research owner, who hauls at 4.
		want := 3
		if priority(decision, policy.PawnID(id), policy.WorkResearch) == 1 {
			want = 4
		}
		if got := priority(decision, policy.PawnID(id), policy.WorkHauling); got != want {
			return fmt.Errorf("%s planned Hauling %d, want %d", id, got, want)
		}
	}
	return nil
}
