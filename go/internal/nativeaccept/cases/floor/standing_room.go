package floor

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// floor/standing-room-reconcile: a planned room that already
// stands is reconciled in place and its enclosure is kept. The plan's first
// bedroom stands finished: wooden walls, a door, a roof and a Concrete floor
// (beauty -1, which the Living tier fails). Granite blocks are in a stockpile
// and Stonecutting is known, so the reconciler's per-cell diff has two jobs: the
// floor_out/floor_in of the Concrete, and the wall_up swap of the wooden walls
// for stone. The case runs the controller in bursts and audits the
// ground between them. At every audit the room must still be sealed (a wall
// mid-swap stands as a frame) and none of the staged walls or doors may be a
// deconstruction target: a standing room is never torn down to be rebuilt. It
// passes when every ring wall is stone and no interior cell is Concrete.
//
// A Go snapshot cannot cover it: the claim is the native designation, vanilla's
// in-place Wall-over-Wall replacement with its frame, and the room staying a
// room for every colonist while pawn jobs run. Which cells the diff lists, and
// that a wall_up is upgrade-only, are policy.Reconcile snapshot tests.
const (
	standingBursts    = 16
	standingBurstTick = 5000
	standingStone     = "BlocksGranite"
)

func init() {
	cases.Register(cases.Case{
		Name: "floor/standing-room-reconcile",
		Scope: "A standing planned bedroom with wooden walls and a Concrete floor, stone blocks in stock: the reconciler lays a " +
			"fitting floor and swaps the walls to stone in place while the room stays enclosed and no staged wall or door " +
			"is ever deconstructed (#2118). Native: floor and wall jobs, vanilla's in-place frame, the enclosure read.",
		Start:       cases.LabStart(),
		RequiredOps: []string{planstage.Tool},
		Serve: &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Sleeping, routinefamily.Clearance, routinefamily.Work, routinefamily.Flooring},
			NativeTimeout: 60 * time.Second, Prefix: "standing-room"},
		Budget: 14 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Reason: "an inert service run reads the plan the room is staged from, then up to sixteen controller bursts with an audit " +
			"between each; the floor and the walls are real pawn work over game time",
		Run: runStandingRoom,
	})
}

func runStandingRoom(ctx context.Context, s cases.Session) error {
	report, h := s.Report(), s.Harness()
	center, _ := na.AsMap(s.Prepared()["center"])
	middle := planstage.Cell{X: int32(na.AsNumber(center["x"])), Z: int32(na.AsNumber(center["z"]))}
	if middle == (planstage.Cell{}) {
		return fmt.Errorf("lab start: no centre in %#v", s.Prepared())
	}
	if _, err := planstage.Stage(ctx, h, "stage-research", planstage.Spec{Research: []string{"Stonecutting", "ComplexFurniture"}}); err != nil {
		return err
	}

	one, err := planstage.Begin(ctx, s, nil, []routinefamily.Family{routinefamily.Work})
	if err != nil {
		return err
	}
	plan, err := one.Plan(ctx, "layout plan with a bedroom", func(p policy.LayoutPlan) bool {
		_, ok := planstage.FirstRoom(p, policy.PlannedBedroom)
		return ok
	})
	if err != nil {
		one.Abort()
		return err
	}
	bedroom, _ := planstage.FirstRoom(plan, policy.PlannedBedroom)
	if h, err = one.Stop(ctx, "plan_service"); err != nil {
		return err
	}

	ring := planstage.RingOf(plan, bedroom)
	pile, ok := planstage.FreeRect(plan, middle, 6, 4)
	if !ok {
		return fmt.Errorf("no free ground for the stockpile in the layout plan %s", plan.Summary())
	}
	spec := ring.Spec("WoodLog", "Concrete")
	spec.Drops = []planstage.Drop{
		{Def: standingStone, Count: 400, X: pile.MinX, Z: pile.MinZ},
		{Def: "WoodLog", Count: 200, X: pile.MinX + 1, Z: pile.MinZ},
	}
	spec.Stockpile = &pile
	staged, err := planstage.Stage(ctx, h, "stage-room", spec)
	if err != nil {
		return err
	}
	staticIDs := append(append([]string{}, staged.Walls...), staged.Doors...)
	before, err := planstage.Read(ctx, h, "audit-before", nil, ring.Cells())
	if err != nil {
		return err
	}
	if err := ring.Sealed(before); err != nil {
		return fmt.Errorf("the staged bedroom is not a standing room: %w", err)
	}
	report["staged"] = map[string]any{"interior": bedroom.Interior, "walls": len(staged.Walls), "doors": len(staged.Doors), "stockpile": pile}

	protected := map[string]bool{}
	for _, id := range staticIDs {
		protected[id] = true
	}
	var bursts []map[string]any
	report["bursts"] = &bursts
	prev := one.Svc
	for burst := 1; burst <= standingBursts; burst++ {
		phase, err := planstage.Begin(ctx, s, prev, s.Spec().Families)
		if err != nil {
			return err
		}
		var first int64 = -1
		err = phase.Until(ctx, fmt.Sprintf("burst %d: %d ticks of game time", burst, standingBurstTick), func(r store.Rounds) (string, bool, error) {
			if first < 0 {
				first = int64(r.Tick)
			}
			return fmt.Sprint(int64(r.Tick) - first), int64(r.Tick)-first >= standingBurstTick, nil
		})
		if err != nil {
			phase.Abort()
			return err
		}
		torn, err := tornDown(ctx, phase.St, protected)
		if err != nil {
			phase.Abort()
			return err
		}
		prev = phase.Svc
		if h, err = phase.Stop(ctx, fmt.Sprintf("burst_%d", burst)); err != nil {
			return err
		}
		if torn != "" {
			return fmt.Errorf("burst %d: the controller targeted a staged %s for deconstruction: a standing room is reconciled in place", burst, torn)
		}
		audit, err := planstage.Read(ctx, h, fmt.Sprintf("audit-%d", burst), nil, ring.Cells())
		if err != nil {
			return err
		}
		if err := ring.Sealed(audit); err != nil {
			return fmt.Errorf("burst %d: the room lost its enclosure: %w", burst, err)
		}
		wood, concrete := 0, 0
		for _, c := range ring.Walls {
			if audit.Cells[c].EdificeStuff == "WoodLog" {
				wood++
			}
		}
		for _, c := range ring.Interior {
			if audit.Cells[c].Terrain == "Concrete" {
				concrete++
			}
		}
		bursts = append(bursts, map[string]any{"burst": burst, "wood_walls": wood, "concrete_cells": concrete})
		if wood == 0 && concrete == 0 {
			report["result"] = fmt.Sprintf("stone walls and no concrete after %d bursts", burst)
			return nil
		}
	}
	return fmt.Errorf("after %d bursts of %d ticks the room still has wooden walls or a Concrete floor: %v", standingBursts, standingBurstTick, bursts)
}

// tornDown names the first staged wall or door the journal holds a
// deconstruction of, "" for none.
func tornDown(ctx context.Context, st *store.Store, staged map[string]bool) (string, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, "*")
	if err != nil {
		return "", err
	}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if cut, ok := progress.Action().Deconstruction(); ok && staged[cut.Target()] {
				return fmt.Sprintf("%s %s at %d,%d", cut.Definition(), cut.Target(), cut.Cell().X, cut.Cell().Z), nil
			}
		}
	}
	return "", nil
}
