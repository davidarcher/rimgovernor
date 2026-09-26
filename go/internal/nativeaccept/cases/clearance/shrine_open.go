package clearance

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const shrineKey = "shrine_fixture"

// clearance/shrine-open (#460): a two-casket shrine in a roofed stone room
// touching Home, opened under --routine-shrine-open-caskets. The melee lock
// drafts a longsword colonist to each casket, the opener issues one Open,
// and the fight that follows ends with every released ancient dead, downed
// or in custody and no colonist dead.
func init() {
	cases.Register(cases.Case{
		Name: "clearance/shrine-heat", Scope: "Build a door and heaters, observe heat, open filled caskets with a doorway shot and retreat; no colonist dead.",
		Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, RequiredOps: []string{"test/shrine_prepare", "test/shrine_audit"},
		Serve:  &cases.ServeSpec{Families: []string{"shrine", "fire"}, Prefix: "shrine-heat", Extra: []string{"--routine-shrine-open-caskets", "--routine-shrine-heat-fallback"}},
		Stages: []string{"shrine-ready"}, Budget: 8 * time.Minute, Stall: 90 * time.Second,
		Run: func(ctx context.Context, s cases.Session) error { return runShrineOpening(ctx, s, true) },
	})
	cases.Register(cases.Case{
		Name:        "clearance/shrine-open",
		Scope:       "Melee-locked casket opening of a two-casket shrine: one OpenCasket order, every occupant dead, downed or captured, no colonist dead.",
		Start:       cases.Save{Name: "RimGovernor-tribal8-baseline"},
		RequiredOps: []string{"test/shrine_prepare", "test/shrine_audit"},
		Serve:       &cases.ServeSpec{Families: []string{"shrine", "defense", "tend", "rescue"}, Prefix: "shrine-open", Extra: []string{"--routine-shrine-open-caskets"}},
		Stages:      []string{"shrine-ready"}, Budget: 8 * time.Minute, Stall: 90 * time.Second,
		Run: runShrineOpen,
	})
}

func runShrineOpen(ctx context.Context, s cases.Session) error {
	return runShrineOpening(ctx, s, false)
}

func runShrineOpening(ctx context.Context, s cases.Session, heat bool) error {
	var fixture map[string]any
	if err := s.Stage(ctx, "shrine-ready", func(ctx context.Context) error {
		var err error
		args := map[string]any{}
		if heat {
			args["heat"] = true
		}
		fixture, err = s.Harness().Call(ctx, "prepare-shrine", "test/shrine_prepare", args)
		if err == nil {
			na.SetCheckpointState(shrineKey, fixture)
		}
		return err
	}); err != nil {
		return err
	}
	if restored := cases.RestoredState(s, shrineKey); restored != nil {
		fixture, _ = na.AsMap(restored)
	}
	if fixture == nil {
		return fmt.Errorf("missing staged shrine identities")
	}
	s.Report()["fixture"] = fixture
	var caskets []string
	for _, raw := range na.AsSlice(fixture["caskets"]) {
		caskets = append(caskets, na.AsString(raw))
	}
	if len(caskets) != 2 || !heat && !boolean(fixture["properRoom"]) || len(na.AsSlice(fixture["armed"])) < 2 {
		return fmt.Errorf("fixture must stage two caskets in a proper room with two armed colonists: %v", fixture)
	}
	before, err := shrineAudit(ctx, s, caskets, "before")
	if err != nil {
		return err
	}
	for _, raw := range na.AsSlice(before["caskets"]) {
		row, _ := na.AsMap(raw)
		if !boolean(row["hasContents"]) {
			return fmt.Errorf("staged casket is empty: %v", row)
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
	// First the goal must open: a routine-shrine plan whose OpenCasket action
	// completes. Its plan is the receipt the issue asks for.
	var opened string
	ceiling := 3 * time.Minute
	if heat {
		ceiling = 6 * time.Minute
	}
	err = na.WaitProgress(ctx, na.Wait{Ceiling: ceiling, Stall: 90 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		s.Report()["shrine_holds"] = review.ShrineHolds
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-shrine-", 256)
		if err != nil {
			return "", false, err
		}
		var states []string
		for _, plan := range plans {
			completed := true
			for _, progress := range plan.Progress {
				states = append(states, string(progress.View().Stage))
				completed = completed && progress.View().Stage == domain.Completed
			}
			for i, action := range plan.Spec.Actions() {
				open, ok := action.OpenCasket()
				if !ok || !contains(caskets, open.Casket()) {
					continue
				}
				if open.Heat() != heat {
					return "", false, fmt.Errorf("wrong opening strategy: %+v", open)
				}
				v := plan.Progress[i].View()
				states = append(states, string(v.Stage))
				if v.Stage == domain.Unsuccessful {
					return "", false, fmt.Errorf("casket opening failed: %v", v)
				}
				if v.Stage == domain.Completed && (!heat || completed) {
					opened = string(plan.Spec.ID())
				}
			}
		}
		return na.Signature(states, holdReasons(review.ShrineHolds)), opened != "", nil
	})
	if err != nil {
		return err
	}
	s.Report()["open_plan"] = opened
	if heat {
		service.Stop()
		if err = reattach(ctx, s); err != nil {
			return err
		}
		after, err := shrineAudit(ctx, s, caskets, "after")
		if err != nil {
			return err
		}
		if na.AsNumber(after["colonistsDead"]) > 0 {
			return fmt.Errorf("colonist died during heat opening: %v", after)
		}
		seen := map[string]bool{}
		for _, raw := range na.AsSlice(after["caskets"]) {
			row, _ := na.AsMap(raw)
			id := na.AsString(row["id"])
			if !contains(caskets, id) || seen[id] || boolean(row["hasContents"]) {
				return fmt.Errorf("heat opening left a filled casket: %v", row)
			}
			seen[id] = true
		}
		if len(seen) != len(caskets) {
			return fmt.Errorf("heat opening lost a casket: %v", after)
		}
		return nil
	}
	// Then the fight, followed through the journal's occupant decisions: the
	// review lists every released ancient, and one still standing hostile is
	// held as "fight". Settled means every occupant is buried, captured or
	// downed for capture, and the goal's guard hold is gone.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 4 * time.Minute, Stall: 90 * time.Second, Interval: 2 * time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		s.Report()["shrine_holds"] = review.ShrineHolds
		occupants, standing := 0, []string{}
		for _, h := range review.ShrineHolds {
			if h.Occupant == "" {
				continue
			}
			occupants++
			if h.Reason == policy.OccupantFight {
				standing = append(standing, h.Occupant)
			}
		}
		return na.Signature(review.Tick, occupants, standing), occupants > 0 && len(standing) == 0, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	// The native audit is the verdict: every occupant dead, downed or a colony
	// prisoner, every casket empty, no colonist dead.
	after, err := shrineAudit(ctx, s, caskets, "after")
	if err != nil {
		return err
	}
	if na.AsNumber(after["colonistsDead"]) > 0 {
		return fmt.Errorf("colonist died opening the shrine: %v", after)
	}
	occupants := na.AsSlice(after["occupants"])
	if len(occupants) == 0 {
		return fmt.Errorf("no released occupant on the map: %v", after)
	}
	for _, raw := range occupants {
		row, _ := na.AsMap(raw)
		if !boolean(row["dead"]) && !boolean(row["downed"]) && !boolean(row["prisoner"]) {
			return fmt.Errorf("occupant still standing: %v", row)
		}
	}
	for _, raw := range na.AsSlice(after["caskets"]) {
		row, _ := na.AsMap(raw)
		if boolean(row["hasContents"]) {
			return fmt.Errorf("casket still filled after the opening completed: %v", row)
		}
	}
	return nil
}

func shrineAudit(ctx context.Context, s cases.Session, caskets []string, label string) (map[string]any, error) {
	result, err := s.Harness().Call(ctx, label, "test/shrine_audit", map[string]any{"caskets": strings.Join(caskets, ";")})
	s.Report()[label] = result
	return result, err
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
