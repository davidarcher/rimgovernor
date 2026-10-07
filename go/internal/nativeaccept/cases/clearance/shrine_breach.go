package clearance

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ShrineFixture is compiled by setup -rebuild -fixture ShrineFixture. RequiredOps
// also lets the runner discover and heal the fixture package before launch.
func init() {
	// clearance/shrine-claim (claim=true) replays as a colony snapshot
	// instead (internal/snapshot, #746).
	// The repair family composes because the fight damages buildings: the
	// breach waits on repairs (ClearAncientShrine is method_unavailable
	// while the repairs latch holds), so without it recovery never comes
	// (#1145).
	for _, claim := range []bool{false} {
		name := "clearance/shrine-breach"
		if claim {
			name = "clearance/shrine-claim"
		}
		cases.Register(cases.Case{
			Name: name, Scope: "Sealed shrine breach, ActiveCombat handoff and observed recovery; empty casket claim and default never-open protection.",
			Start:       cases.Save{Name: "RimGovernor-tribal8-baseline"},
			RequiredOps: []string{"test/shrine_prepare", "test/shrine_audit"},
			Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Shrine, routinefamily.Defense, routinefamily.Clearance, routinefamily.Repair, routinefamily.Tend, routinefamily.Rescue}, Prefix: "shrine-breach"},
			Stages:      []string{"sealed-shrine-ready"}, Budget: 8 * time.Minute, Stall: 90 * time.Second,
			Run: func(ctx context.Context, s cases.Session) error { return runShrineBreach(ctx, s, claim) },
		})
	}
}

func runShrineBreach(ctx context.Context, s cases.Session, claim bool) error {
	const key = "sealed_shrine_fixture"
	var fixture map[string]any
	if err := s.Stage(ctx, "sealed-shrine-ready", func(ctx context.Context) error {
		var err error
		fixture, err = s.Harness().Call(ctx, "prepare-sealed-shrine", "test/shrine_prepare", map[string]any{"sealedBreach": true})
		if err == nil {
			na.SetCheckpointState(key, fixture)
		}
		return err
	}); err != nil {
		return err
	}
	if restored := cases.RestoredState(s, key); restored != nil {
		fixture, _ = na.AsMap(restored)
	}
	s.Report()["fixture"] = fixture
	var caskets []string
	for _, raw := range na.AsSlice(fixture["caskets"]) {
		caskets = append(caskets, na.AsString(raw))
	}
	if len(caskets) != 2 || !boolean(fixture["properRoom"]) || na.AsString(fixture["guard"]) == "" || na.AsString(fixture["breach"]) == "" {
		return fmt.Errorf("invalid sealed shrine fixture: %v", fixture)
	}
	audit := func(label string) (map[string]any, error) {
		args := map[string]any{"caskets": strings.Join(caskets, ";"), "guard": fixture["guard"], "breach": fixture["breach"], "salvage": fixture["salvage"]}
		r, err := s.Harness().Call(ctx, label, "test/shrine_audit", args)
		s.Report()[label] = r
		return r, err
	}
	before, err := audit("sealed_before")
	if err != nil {
		return err
	}
	if !boolean(before["breachPresent"]) || !boolean(before["guardPresent"]) || boolean(before["guardDead"]) {
		return fmt.Errorf("fixture already breached: %v", before)
	}
	if len(na.AsSlice(before["caskets"])) != 2 {
		return fmt.Errorf("fixture must contain exactly two caskets: %v", before)
	}
	for _, raw := range na.AsSlice(before["caskets"]) {
		row, _ := na.AsMap(raw)
		if !boolean(row["fogged"]) || boolean(row["playerOwned"]) || boolean(row["hasContents"]) != (na.AsString(row["id"]) == caskets[1]) {
			return fmt.Errorf("fixture caskets must be fogged, unowned, one empty and one filled: %v", row)
		}
	}
	reply, err := s.Harness().Wire(ctx, "sealed_shrine_census", "observations_get_ancient_shrines", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	s.Report()["sealed_shrine_census"] = observed
	if err := checkSealedBreachWall(observed, na.AsString(fixture["breach"])); err != nil {
		return err
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
	var shrineProject domain.ProjectID
	drafted, breached, combat, claimed, salvaged, recovered := false, false, false, false, false, false
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 6 * time.Minute, Stall: 90 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		s.Report()["shrine_holds"] = review.ShrineHolds
		if id, ok := review.ProjectFor(policy.ClearAncientShrine); ok {
			shrineProject = id
		}
		if binding, ok := review.Incident(policy.ActiveCombat); ok {
			incident, err := journal.LoadIncident(ctx, binding.Incident)
			if err != nil {
				return "", false, err
			}
			if len(incident.Methods) > 0 {
				combat = true
				s.Report()["combat_incident"] = incident
			}
		}
		if shrineProject != "" {
			goal, err := journal.LoadProject(ctx, shrineProject)
			if err != nil {
				return "", false, err
			}
			recovered = goal.Project.Status == domain.ProjectCompleted
			s.Report()["shrine_project"] = goal
		}
		var states []string
		for _, patterns := range [][]string{shrinePlanMethods, {"deconstruct-*"}} {
			plans, err := journal.PlanHistoryWithMethods(ctx, 256, patterns...)
			if err != nil {
				return "", false, err
			}
			for _, plan := range plans {
				for i, action := range plan.Spec.Actions() {
					v := plan.Progress[i].View()
					states = append(states, string(action.ID())+":"+string(v.Stage))
					if open, ok := action.OpenCasket(); ok && contains(caskets, open.Casket()) {
						return "", false, fmt.Errorf("default policy issued OpenCasket: %s", action.ID())
					}
					if v.Stage != domain.Completed {
						continue
					}
					if _, ok := action.OwnedDraft(); ok && patterns[0] == shrinePlanMethods[0] {
						drafted = true
					}
					if d, ok := action.Deconstruction(); ok {
						breached = breached || d.Target() == na.AsString(fixture["breach"])
						salvaged = salvaged || d.Target() == na.AsString(fixture["salvage"])
					}
					if c, ok := action.ClaimBuilding(); ok && c.Thing() == caskets[0] {
						claimed = true
					}
				}
			}
		}
		s.Report()["action_states"] = states
		s.Report()["milestones"] = map[string]bool{"drafted": drafted, "breached": breached, "active_combat": combat, "claimed": claimed, "salvaged": salvaged, "recovery_observed": recovered}
		return na.Signature(states, drafted, breached, combat, claimed, salvaged, recovered, holdReasons(review.ShrineHolds)), drafted && breached && combat && recovered && (!claim || claimed && salvaged), nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	after, err := audit("sealed_after")
	if err != nil {
		return err
	}
	return checkShrineBreach(after, caskets, claim)
}

func checkSealedBreachWall(observed map[string]any, wall string) error {
	for _, raw := range na.AsSlice(observed["shrines"]) {
		row, _ := na.AsMap(raw)
		for _, rawWall := range na.AsSlice(row["breachWalls"]) {
			candidate, _ := na.AsMap(rawWall)
			if na.AsString(candidate["entityId"]) != wall {
				continue
			}
			if !boolean(row["sealed"]) || boolean(row["guardsKnown"]) || len(na.AsSlice(row["guards"])) != 0 || len(na.AsSlice(row["caskets"])) != 0 {
				return fmt.Errorf("sealed breach census exposed the interior: %v", row)
			}
			return nil
		}
	}
	return fmt.Errorf("sealed shrine census omitted fixture breach wall %s: %v", wall, observed)
}

func checkShrineBreach(after map[string]any, caskets []string, claim bool) error {
	if !boolean(after["guardDead"]) || boolean(after["breachPresent"]) || na.AsNumber(after["colonistsDead"]) != 0 {
		return fmt.Errorf("breach must kill guard and spare every colonist: %v", after)
	}
	rows := na.AsSlice(after["caskets"])
	if len(rows) != 2 {
		return fmt.Errorf("both caskets must survive: %v", after)
	}
	seen := map[string]bool{}
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		id := na.AsString(row["id"])
		if !contains(caskets, id) || seen[id] || boolean(row["designated"]) || boolean(row["hasContents"]) != (id == caskets[1]) {
			return fmt.Errorf("casket contents or protection changed: %v", row)
		}
		seen[id] = true
		if claim && id == caskets[0] && !boolean(row["playerOwned"]) {
			return fmt.Errorf("empty casket not claimed: %v", row)
		}
	}
	if claim && boolean(after["salvagePresent"]) {
		return fmt.Errorf("clearance salvage remains: %v", after)
	}
	return nil
}
func contains(ids []string, id string) bool {
	for _, candidate := range ids {
		if candidate == id {
			return true
		}
	}
	return false
}

func holdReasons(holds []policy.ShrineHold) []string {
	var out []string
	for _, h := range holds {
		out = append(out, h.Shrine+":"+h.Reason+":"+h.Occupant)
	}
	return out
}

// shrinePlanMethods match the shrine planner's claim, breach and open
// methods (#987: plans are found by method, not plan id).
var shrinePlanMethods = []string{"claim-*", "breach-*", "open-*"}
