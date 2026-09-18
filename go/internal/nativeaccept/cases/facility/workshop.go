package facility

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The workshop case (issue #4 M2): a MaintainResource stock floor for an
// item no bench yet produces (melee clubs on the tribal8 baseline). The
// reachable club count must rise above the pre-service baseline, a
// CraftingSpot must stand inside a room whose native Room.Role hosts the
// Workshop facility (Workshop, or a shared Room/Barracks per
// policy.FacilityCatalog), and that bench must carry the club bill. The
// first observed product ends the watch; the full stock floor can take
// several more in-game days.
const (
	resource = "MeleeWeapon_Club"
	recipe   = "Make_MeleeWeapon_Club"
	bench    = "CraftingSpot"
	target   = 3
)

// workshopFamilies composes the comfort case's startup ladder plus the
// resource, workshop and gear families the deficit walks through:
// workshop stages the bench, resource dispatches the bill, work covers the
// bench's Crafting work type, gear reads the same bench census.
const workshopFamilies = "sleeping,shelter,temperature,comfort,work,supply,field,food-storage,acquisition,cooking,production-policy,resource,workshop,gear"

func init() {
	cases.Register(cases.Case{
		Name:   "facility/workshop",
		Scope:  fmt.Sprintf("MaintainResource %s:%d recovers through a bill on a %s staged in a room whose native role hosts the Workshop facility; the live item count must rise above the pre-service baseline (issue #4, M2).", resource, target, bench),
		Start:  cases.Save{Name: sustained.BaselineSave},
		Keep:   []string{string(na.NeedFood)},
		Serve:  spec("workshop", workshopFamilies, "--routine-resource-target", fmt.Sprintf("%s:%d", resource, target)),
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			var baseline float64
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: window, Poll: 5 * time.Second, Goal: policy.MaintainResource, Until: billProduced},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					count, err := itemCount(ctx, h, "baseline-colony-facts")
					if err != nil {
						return err
					}
					baseline = count
					report["baseline_count"] = count
					if count >= target {
						return fmt.Errorf("save already holds %v %s; the stock floor %d leaves no deficit to recover", count, resource, target)
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := openJournal(ctx, s)
					if err != nil {
						return err
					}
					defer journal.Close()
					return auditWorkshop(ctx, h, journal, report, baseline, false)
				},
			})
			return err
		},
	})
}

// billProduced reports a sample whose MaintainResource goal holds or held a
// resource (production bill) plan with every action completed: native
// production tracking marks a bill action completed only once an iteration
// placed an observed product, so this is the first product landing. The
// full stock floor can take several more in-game days and is the -recovered
// criterion; the audit still requires the live count to have risen.
func billProduced(sample map[string]any) bool {
	return planCompleted(sample, "routine-resource-")
}

// workshopBenchCompleted reports a sample whose MaintainResource goal holds
// or held (retired_plans: a completed method leaves the goal at the next
// review) a workshop bench plan with every action completed: the bench
// stands and the bill path is about to start, the point a checkpoint save
// is worth.
func workshopBenchCompleted(sample map[string]any) bool {
	return planCompleted(sample, "routine-workshop-")
}

func planCompleted(sample map[string]any, prefix string) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if strings.HasPrefix(id, prefix) && actions > 0 && stages["completed"] == actions {
			return true
		}
	}
	return false
}

// itemCount reads the reachable, unforbidden player stock of the resource
// from the same native census MaintainResource's stock floor is judged on.
func itemCount(ctx context.Context, h *na.Harness, label string) (float64, error) {
	facts, err := h.Call(ctx, label, "home/colony_facts", map[string]any{})
	if err != nil {
		return 0, err
	}
	resources, ok := na.AsMap(facts["resources"])
	if !ok {
		return 0, fmt.Errorf("native resource census unavailable: %#v", facts["resources"])
	}
	// An item the census holds none of has no key at all.
	if _, present := resources[resource]; !present {
		return 0, nil
	}
	return na.AsNumber(resources[resource]), nil
}

// audit compares the journal's recovered goal with the live item count, room
// census and bill stacks after the service has stopped.
func auditWorkshop(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, baseline float64, recovered bool) error {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return fmt.Errorf("load routine review: %w", err)
	}
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			goalID = binding.Goal
		}
	}
	if goalID == "" {
		return fmt.Errorf("MaintainResource was never bound in the routine review")
	}
	goal, err := journal.LoadGoal(ctx, goalID)
	if err != nil {
		return err
	}
	report["resource_goal"] = map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "methods": len(goal.Methods)}
	if recovered && (goal.Goal.Need != domain.NeedRecovered || goal.Goal.Status != domain.GoalSatisfied) {
		return fmt.Errorf("MaintainResource did not recover within the watch window: need=%s status=%s", goal.Goal.Need, goal.Goal.Status)
	}

	count, err := itemCount(ctx, h, "audit-colony-facts")
	if err != nil {
		return err
	}
	report["final_count"] = count
	if count <= baseline {
		return fmt.Errorf("%s count did not rise: baseline=%v final=%v", resource, baseline, count)
	}

	rooms, err := h.Call(ctx, "audit-rooms", "home/list_rooms", map[string]any{"cells": true})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(rooms["success"]); !success {
		return fmt.Errorf("home/list_rooms refused")
	}
	workshop, err := policy.Facility(policy.RoomRoleWorkshop)
	if err != nil {
		return err
	}
	type cell struct{ x, z float64 }
	workshopCells := map[cell]string{}
	roleByRoom := map[string]string{}
	for _, raw := range na.AsSlice(rooms["rooms"]) {
		row, _ := na.AsMap(raw)
		id, role := fmt.Sprint(row["id"]), na.AsString(row["role"])
		roleByRoom[id] = role
		if !workshop.Hosts(policy.RoomRole(role)) {
			continue
		}
		for _, c := range na.AsSlice(row["cells"]) {
			p, _ := na.AsMap(c)
			workshopCells[cell{na.AsNumber(p["x"]), na.AsNumber(p["z"])}] = id
		}
	}
	report["native_rooms"] = roleByRoom

	bills, err := h.Call(ctx, "audit-bills", "home/bills", map[string]any{"action": "list"})
	if err != nil {
		return err
	}
	var hosted []map[string]any
	for _, raw := range na.AsSlice(bills["benches"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["defName"]) != bench {
			continue
		}
		position, _ := na.AsMap(row["position"])
		room, inWorkshop := workshopCells[cell{na.AsNumber(position["x"]), na.AsNumber(position["z"])}]
		if !inWorkshop {
			continue
		}
		for _, b := range na.AsSlice(row["bills"]) {
			billRow, _ := na.AsMap(b)
			if na.AsString(billRow["recipe"]) == recipe {
				hosted = append(hosted, map[string]any{"bench": na.AsString(row["thingId"]), "room": room, "room_role": roleByRoom[room], "bill": billRow})
			}
		}
	}
	report["hosted_bills"] = hosted
	if len(hosted) == 0 {
		return fmt.Errorf("no %s inside a room hosting the Workshop facility carries a %s bill", bench, recipe)
	}
	return nil
}
