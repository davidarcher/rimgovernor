// Command workshopaccept is issue #4 M2's bill-based workshop acceptance:
// load a save, run the service with a MaintainResource stock floor for an
// item no bench yet produces (melee clubs on the tribal8 baseline), and
// watch MaintainResource until it recovers or the window ends. Recovery is
// audited against live native facts rather than the journal alone: the
// reachable club count must have risen above the baseline recorded before
// the service started, a CraftingSpot must stand inside a room whose native
// Room.Role is Workshop, and that bench must carry the Make_MeleeWeapon_Club
// bill. A bill receipt, a blueprint or a journal proof alone never passes.
//
// The default save is the tribal8 baseline the other facility harnesses
// use; -save selects another existing save. Like facilityaccept this runs
// the real serve binary and reads its durable journal, so it needs a prebuilt
// rimgovernor binary and the current native mod installed.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const baselineSave = "RimGovernor-tribal8-baseline"

const (
	resource = "MeleeWeapon_Club"
	recipe   = "Make_MeleeWeapon_Club"
	bench    = "CraftingSpot"
	target   = 3
)

// workshopFamilies composes facilityaccept's startup ladder plus the
// resource, workshop and gear families the deficit walks through:
// workshop stages the bench, resource dispatches the bill, work covers the
// bench's Crafting work type, gear reads the same bench census.
const workshopFamilies = "sleeping,shelter,temperature,comfort,work,supply,field,food-storage,acquisition,cooking,production-policy,resource,workshop,gear"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-workshop-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "existing save name to load")
	watch := flag.Duration("watch", 25*time.Minute, "wall-clock duration to observe MaintainResource before giving up")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 40*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout")
	clockSpeed := flag.String("clock-speed", "Superfast", "serve's --clock-speed (Normal, Fast or Superfast)")
	families := flag.String("families", workshopFamilies, "RIMGOVERNOR_ROUTINE_FAMILIES composition for the run")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-workshop-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport(fmt.Sprintf("MaintainResource %s:%d recovers through a bill on a %s staged in a native Workshop room; the live item count must rise above the pre-service baseline (issue #4, M2).", resource, target, bench), !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	var baseline float64
	cfg := sustainedfood.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary, Save: *save,
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout, ClockSpeed: *clockSpeed,
		RequestPrefix: "workshop", Families: *families, Goal: policy.MaintainResource,
		ServeArgs: []string{"--routine-resource-target", fmt.Sprintf("%s:%d", resource, target)},
		Until:     resourceRecovered,
	}
	cfg.Prepare = func(ctx context.Context, h *na.Harness, report na.Report) error {
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
	}
	var journal *store.Store
	statePath := filepath.Join(*output, "service.sqlite")
	cfg.Audit = func(ctx context.Context, h *na.Harness, report na.Report) error {
		var err error
		journal, err = store.Open(ctx, statePath)
		if err != nil {
			return fmt.Errorf("reopen journal: %w", err)
		}
		defer journal.Close()
		return audit(ctx, h, journal, report, baseline)
	}
	timeline, err := sustainedfood.Run(ctx, cfg, report)
	report["timeline_samples"] = len(timeline)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func resourceRecovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
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
func audit(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report, baseline float64) error {
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
	if goal.Goal.Need != domain.NeedRecovered || goal.Goal.Status != domain.GoalSatisfied {
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
	type cell struct{ x, z float64 }
	workshopCells := map[cell]string{}
	roleByRoom := map[string]string{}
	for _, raw := range na.AsSlice(rooms["rooms"]) {
		row, _ := na.AsMap(raw)
		id, role := fmt.Sprint(row["id"]), na.AsString(row["role"])
		roleByRoom[id] = role
		if policy.RoomRole(role) != policy.RoomRoleWorkshop {
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
				hosted = append(hosted, map[string]any{"bench": na.AsString(row["thingId"]), "room": room, "bill": billRow})
			}
		}
	}
	report["hosted_bills"] = hosted
	if len(hosted) == 0 {
		return fmt.Errorf("no %s inside a native Workshop room carries a %s bill", bench, recipe)
	}
	return nil
}
