// Command facilityaccept is issue #4's dining/recreation facility acceptance:
// load a save, run the autonomous service, and watch EnsureComfort until it
// recovers or the window ends. Recovery is then audited against live native
// facts rather than the journal alone: the dining and recreation use proofs
// must name facilities standing in a proper room whose native Room.Role can
// host that function (DiningRoom or RecRoom, including one shared room), the
// room must exist in the typed room census under that role, and every
// eligible colonist must still have an accessible facility of each kind.
// Blueprints, labels and bill receipts prove nothing here; only a room the
// game itself scores as a dining or rec room, actually used, passes.
//
// The default save is the tribal8 baseline sustainedmatrixaccept also uses;
// -save selects another existing save. Like sustainedfoodaccept this runs the
// real serve binary and reads its durable journal, so it needs a prebuilt
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

// facilityFamilies composes the startup ladder EnsureComfort ranks behind
// plus comfort itself: RankDevelopment only grants a comfort slot once every
// priority-0..2 need (starting supplies, work assignments, food, shelter,
// temperature, cooking, storage) has recovered, so those families must be
// able to act. gear rides along now that its bill/recipe reads have native
// handlers (issue #62). The full autonomous composition is still not used:
// with every family on, the parallel planner step exceeds its call timeout
// on a shared machine, so the clock never starts.
const facilityFamilies = "sleeping,shelter,temperature,comfort,work,supply,field,food-storage,acquisition,cooking,production-policy,gear"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-facility-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", baselineSave, "existing save name to load")
	watch := flag.Duration("watch", 25*time.Minute, "wall-clock duration to observe EnsureComfort before giving up")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 40*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout")
	flag.Parse()
	if *root == "" || *rimgovernorBinary == "" || !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-facility-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("EnsureComfort recovers through a native DiningRoom/RecRoom-hosted facility that colonists actually use; a facility outside a hosting room never counts (issue #4, M1).", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	cfg := sustainedfood.RunConfig{
		Root: *root, Output: *output, GameID: *game, Headless: !*rendered,
		RimgovernorBinary: *rimgovernorBinary, Save: *save,
		Watch: *watch, Poll: *poll, NativeTimeout: *nativeTimeout,
		RequestPrefix: "facility", Families: facilityFamilies, Goal: policy.EnsureComfort,
		Until: comfortRecovered,
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
		return audit(ctx, h, journal, report)
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

func comfortRecovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
}

// audit compares the journal's recovered comfort proofs with the live native
// room census and comfort facts after the service has stopped.
func audit(ctx context.Context, h *na.Harness, journal *store.Store, report na.Report) error {
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
