// Package facility holds issue #4's facility cases: a room the game itself
// scores as a dining/rec room, workshop or hospital, staged by the
// autonomous service and audited against live native facts rather than
// the journal alone. Each case loads the tribal8 baseline save, runs the
// startup ladder the facility ranks behind plus its own family, watches
// the goal until it recovers or the window ends, then reattaches to
// compare the durable proofs with the room census.
package facility

import (
	"context"
	"fmt"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// comfortFamilies composes the startup ladder EnsureComfort ranks behind
// plus comfort itself: RankDevelopment only grants a comfort slot once every
// priority-0..2 need (starting supplies, work assignments, food, shelter,
// temperature, cooking, storage) has recovered, so those families must be
// able to act. gear rides along now that its bill/recipe reads have native
// handlers (issue #62). The full autonomous composition is still not used:
// with every family on, the parallel planner step exceeds its call timeout
// on a shared machine, so the clock never starts.
const comfortFamilies = "sleeping,shelter,temperature,comfort,work,supply,field,food-storage,acquisition,cooking,production-policy,gear"

// window is how long a facility goal gets to recover; the window ends
// early on recovery.
const window = 12 * time.Minute

// spec is the serve spec the facility cases share, over families.
func spec(prefix, families string, extra ...string) *cases.ServeSpec {
	return &cases.ServeSpec{Families: []string{families}, NativeTimeout: 15 * time.Second, Prefix: prefix, Extra: extra}
}

// openJournal reopens the stopped service's durable journal for the audit.
func openJournal(ctx context.Context, s cases.Session) (*store.Store, error) {
	journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
	if err != nil {
		return nil, fmt.Errorf("reopen journal: %w", err)
	}
	return journal, nil
}

func init() {
	cases.Register(cases.Case{
		Name:  "facility/comfort",
		Scope: "EnsureComfort recovers through a native DiningRoom/RecRoom-hosted facility that colonists actually use; a facility outside a hosting room never counts (issue #4, M1).",
		Start: cases.Save{Name: sustained.BaselineSave},
		// The use proofs need colonists to eat at the table and play: Food
		// and Joy stay live.
		Keep:   []string{string(na.NeedFood), string(na.NeedJoy)},
		Serve:  spec("facility", comfortFamilies),
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Poll: 5 * time.Second, Goal: policy.EnsureComfort, Until: comfortRecovered},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := openJournal(ctx, s)
					if err != nil {
						return err
					}
					defer journal.Close()
					return auditComfort(ctx, h, journal, report)
				},
			})
			return err
		},
	})
}

func comfortRecovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
}

// audit compares the journal's recovered comfort proofs with the live native
// room census and comfort facts after the service has stopped.
func auditComfort(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureComfort {
			goalID = binding.Goal
		}
	}
	if goalID == "" {
		return fmt.Errorf("EnsureComfort was never bound in the routine review")
	}
	goal, err := journal.LoadGoal(ctx, goalID)
	if err != nil {
		return err
	}
	report["comfort_goal"] = map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "methods": len(goal.Methods)}
	report["comfort_history"] = review.Comfort
	if goal.Goal.Need != domain.NeedRecovered || goal.Goal.Status != domain.GoalSatisfied {
		return fmt.Errorf("EnsureComfort did not recover within the watch window: need=%s status=%s", goal.Goal.Need, goal.Goal.Status)
	}

	facts, err := h.Call(ctx, "audit-colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	upkeep, _ := na.AsMap(facts["upkeep"])
	comfort, _ := na.AsMap(upkeep["comfort"])
	if comfort == nil {
		return fmt.Errorf("native comfort facts unavailable: %#v", upkeep)
	}
	rooms, err := h.Call(ctx, "audit-rooms", "home/list_rooms", map[string]any{"cells": false})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(rooms["success"]); !success {
		return fmt.Errorf("home/list_rooms refused")
	}
	roleByRoom := map[string]policy.RoomRole{}
	for _, raw := range na.AsSlice(rooms["rooms"]) {
		row, _ := na.AsMap(raw)
		roleByRoom[fmt.Sprint(row["id"])] = policy.RoomRole(na.AsString(row["role"]))
	}
	report["native_rooms"] = roleByRoom

	people := map[string]bool{}
	for _, raw := range na.AsSlice(comfort["people"]) {
		people[na.AsString(raw)] = true
	}
	if len(people) == 0 {
		return fmt.Errorf("comfort acceptance requires eligible colonists")
	}
	hosted := map[string]any{}
	for _, kind := range []struct {
		name  string
		role  policy.RoomRole
		proof policy.ComfortUse
	}{{"dining", policy.RoomRoleDiningRoom, review.Comfort.Dining}, {"recreation", policy.RoomRoleRecRoom, review.Comfort.Recreation}} {
		facility, err := policy.Facility(kind.role)
		if err != nil {
			return err
		}
		if kind.proof.Facility == "" || kind.proof.Tick <= 0 || kind.proof.Tick > review.Tick {
			return fmt.Errorf("%s use proof is missing or out of bounds: %+v", kind.name, kind.proof)
		}
		accessible := map[string]bool{}
		var proofRoom string
		proofFound := false
		for _, raw := range na.AsSlice(comfort[kind.name]) {
			f, _ := na.AsMap(raw)
			roomID := na.AsString(f["roomId"])
			if !facility.Hosts(roleByRoom[roomID]) {
				continue
			}
			for _, p := range na.AsSlice(f["accessibleTo"]) {
				accessible[na.AsString(p)] = true
			}
			if na.AsString(f["id"]) == kind.proof.Facility {
				proofFound, proofRoom = true, roomID
			}
		}
		if !proofFound {
			return fmt.Errorf("%s use proof %q is not a facility in a native %s-hosting room", kind.name, kind.proof.Facility, kind.role)
		}
		for p := range people {
			if !accessible[p] {
				return fmt.Errorf("colonist %s has no accessible hosted %s facility", p, kind.name)
			}
		}
		hosted[kind.name] = map[string]any{"facility": kind.proof.Facility, "room": proofRoom, "role": string(roleByRoom[proofRoom]), "tick": kind.proof.Tick}
	}
	report["hosted_use"] = hosted
	return nil
}
