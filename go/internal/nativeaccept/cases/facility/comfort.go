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
	"math"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// comfortFamilies composes the startup ladder EnsureComfort ranks behind
// plus comfort itself: RankDevelopment only grants a comfort slot once every
// priority-0..2 need (starting supplies, work assignments, food, shelter,
// temperature, cooking, storage) has recovered, so those families must be
// able to act. gear rides along now that its bill/recipe reads have native
// handlers (issue #62). tend and rescue serve CriticalMedical: a colonist
// downed by food poisoning is a priority-1 emergency that otherwise parks
// every development slot and the clock for the rest of the run (#201). The
// full autonomous composition is still not used: with every family on, the
// parallel planner step exceeds its call timeout on a shared machine, so
// the clock never starts.
const comfortFamilies = "sleeping,shelter,temperature,comfort,work,supply,field,food-storage,acquisition,cooking,production-policy,gear,tend,rescue"

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
		Scope: "EnsureComfort recovers through a native DiningRoom/RecRoom-hosted facility that colonists actually use; a facility outside a hosting room never counts (issue #4, M1); a review that provisions comfort from mood pressure ranks it at least that deep (#286).",
		// The startup ladder is already served in the checkpoint (#201);
		// the watch covers comfort's own planning and use. Until
		// tools/facility-checkpoint has committed the save (#217) the
		// runner refuses to stage it, which is the case's failure.
		Start: cases.Save{Name: StartupCheckpoint, From: cases.CommittedSaves()},
		// The use proofs need colonists to eat at the table and play: Food
		// and Joy stay live.
		Keep:   []string{string(na.NeedFood), string(na.NeedJoy)},
		Serve:  spec("facility", comfortFamilies),
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Goal: policy.EnsureComfort, Until: comfortRecovered},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := openJournal(ctx, s)
					if err != nil {
						return err
					}
					defer journal.Close()
					if err := auditComfort(ctx, h, journal, report); err != nil {
						return err
					}
					return auditProvision(report)
				},
			})
			return err
		},
	})
}

// auditProvision checks the mood provisioning vertical on the served ladder
// (#286): every sample whose mood review provisioned EnsureComfort (a
// fraction of colonists under dominant AteWithoutTable/NeedJoy pressure)
// must rank comfort with a deficit at least that fraction, and the report
// counts the samples where the ranked deficit is provably the raise (a
// fractional pressure above comfort's own .5 census deficit with one
// facility kind recovered), which is when it changes the ranking. The
// checkpoint colonists eat and sleep on the ground of a starter shell, so
// the pressure itself is not asserted: a run where nobody was provisioned
// records provisioned_samples 0 and passes on the facility proofs alone.
func auditProvision(report na.Report) error {
	timeline, _ := report["timeline"].([]map[string]any)
	provisioned, raised := 0, 0
	var maxPressure float64
	for _, sample := range timeline {
		pressure, ok := sample["mood_provision"].(float64)
		if !ok {
			continue
		}
		provisioned++
		maxPressure = max(maxPressure, pressure)
		development, _ := sample["development"].(map[string]any)
		deficit, known := development["deficit"].(float64)
		if !known {
			if development == nil {
				// Comfort recovered this review: no ranked row.
				continue
			}
			return fmt.Errorf("review %v provisioned EnsureComfort at %.2f but ranked it with no deficit: %v", sample["review_revision"], pressure, development)
		}
		if deficit+1e-9 < pressure {
			return fmt.Errorf("review %v provisioned EnsureComfort at %.2f but ranked it at %.2f", sample["review_revision"], pressure, deficit)
		}
		// Comfort's own census ranks 0, .5 or 1; a ranked deficit equal to
		// a fractional pressure above .5 can only be the raise.
		if pressure > .5 && pressure < 1 && math.Abs(deficit-pressure) < 1e-9 {
			raised++
		}
	}
	report["mood_provision"] = map[string]any{"provisioned_samples": provisioned, "raised_samples": raised, "max_pressure": maxPressure}
	return nil
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
