// Package sleeping holds the individual-bedroom case (#786). sleeping/bedrooms
// starts from the layout/grid fixture (tribal baseline, Stonecutting
// finished so the tier reads Masonry, a fixture hut with sleeping spots and
// stone blocks beside it): the sleeping planner first beds everyone in the
// hut, then orders the layout plan's first 5x5 bedroom (the fixture
// raises its ring as soon as it is ordered), stages a bed in it and moves a colonist's ownership there. The case asserts that move
// natively: the colonist owns the new bed, and the barracks bed they left
// still stands as a spare.
package sleeping

import (
	"context"
	"fmt"
	"path/filepath"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// bedroomShellPrefix starts a bedroom shell's method (buildingruntime
// bedroomMethod); other bedroom- and sleeping- methods stage and assign
// beds (#987: plans are recognised by method, not plan id).
const bedroomShellPrefix = "bedroom-shell-"

// sleepingBedMethod reports a bed staging or assignment method.
func sleepingBedMethod(method string) bool {
	return (strings.HasPrefix(method, "bedroom-") || strings.HasPrefix(method, "sleeping-")) && !strings.HasPrefix(method, bedroomShellPrefix) && !strings.HasPrefix(method, "sleeping-shell")
}

func init() {
	cases.Register(cases.Case{
		Name: "sleeping/bedrooms",
		Scope: "Issue #786: on the tribal " + sustained.BaselineSave + " colony at Masonry with a fixture hut, once every colonist " +
			"owns a bed the sleeping planner builds the layout plan's first 5x5 bedroom and moves a colonist from the barracks " +
			"into it: the native pawn read shows the colonist owning the bedroom bed, and the barracks bed they left still " +
			"stands as a spare. A snapshot test cannot cover it: the move is proven by native bed ownership after real " +
			"construction encloses the planned room.",
		Start: cases.Fixture{Op: "test/layout_grid_prepare", ArgsFrom: startersite.Args, Args: map[string]any{"sleepingSpots": 8, "stoneBlocks": 400, "builders": true},
			On: cases.Save{Name: sustained.BaselineSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []string{"shelter", "expansion", "sleeping"}, NativeTimeout: 30 * time.Second, Prefix: "sleeping-bedrooms"},
		Budget: 3 * time.Minute,
		Reason: "one watch: the fixture raises the shell the planner orders, then the furnished, assigned bedroom",
		Run:    bedrooms,
	})
}

// moved reports two completed one-action sleeping plans after a bedroom
// shell plan: the bed staged in the bedroom, then the assignment.
func moved(sample map[string]any) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	shell, done := false, 0
	for _, plan := range append(plans, retired...) {
		method, _ := plan["method"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if strings.HasPrefix(method, bedroomShellPrefix) && actions > 0 {
			shell = true
		}
		if shell && sleepingBedMethod(method) && actions == 1 && stages["completed"] == 1 {
			done++
		}
	}
	return done >= 2
}

func bedrooms(ctx context.Context, s cases.Session) error {
	report := s.Report()
	report["fixture"] = s.Prepared()
	var move domain.BedAssign
	found := false
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 2 * time.Minute, Extra: []policy.GoalID{policy.MaintainHousing}, Until: func(sample map[string]any) bool {
			goal, _ := sample[string(policy.MaintainHousing)].(map[string]any)
			return moved(goal)
		}},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			// The move retires once the colonist sleeps in the bed, so read
			// history (newest first), not the active catalog.
			plans, err := journal.PlanHistoryWithMethods(ctx, 256, "bedroom-*", "sleeping-*")
			if err != nil {
				return err
			}
			slices.Reverse(plans)
			shell := false
			for _, plan := range plans {
				method := string(plan.Method)
				if strings.HasPrefix(method, bedroomShellPrefix) {
					shell = true
					continue
				}
				if !shell || !sleepingBedMethod(method) {
					continue
				}
				for _, a := range plan.Spec.Actions() {
					if assign, ok := a.BedAssign(); ok {
						if assign.PreviousBed().ID() != "" {
							move, found = assign, true
						}
					}
				}
			}
			if !found {
				return fmt.Errorf("no bed assignment moving a colonist out of an owned bed followed a bedroom shell")
			}
			previous := move.PreviousBed().ID()
			report["move"] = map[string]any{"pawn": string(move.Pawn()), "bed": move.Bed(), "previous": previous}
			identity := s.Identity()
			reply, err := h.Wire(ctx, "pawn", "observations_list_pawns", map[string]any{
				"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{string(move.Pawn())}},
				"details": map[string]any{},
			})
			if err != nil {
				return err
			}
			_, observed, err := na.Outcome(reply, "observed")
			if err != nil {
				return err
			}
			rows := na.AsSlice(observed["pawns"])
			if len(rows) != 1 {
				return fmt.Errorf("pawn read: %#v", observed)
			}
			row, _ := na.AsMap(rows[0])
			if owned := na.AsString(row["ownedBedId"]); owned != move.Bed() {
				return fmt.Errorf("colonist %s owns %q, not the bedroom bed %q", move.Pawn(), owned, move.Bed())
			}
			reply, err = h.Wire(ctx, "spare", "observations_list_buildings", map[string]any{
				"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{previous},
			})
			if err != nil {
				return err
			}
			if _, observed, err = na.Outcome(reply, "observed"); err != nil {
				return err
			}
			if len(na.AsSlice(observed["buildings"])) != 1 {
				return fmt.Errorf("the barracks bed %s no longer stands as a spare: %#v", previous, observed)
			}
			return nil
		},
	})
	return err
}
