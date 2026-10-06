package shelter

import (
	"context"
	"fmt"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The shelter climate cases (#2076) prove the shelter's campfire and cooler
// follow the real map's temperature curve (#2044): the plan latches Cold or
// Hot from the live gear snapshot, and the building the planner then places
// stands where the climate says. A snapshot test over recorded facts proves
// the placement search (buildingruntime campfire_climate_test.go); only a real
// map reads the curve, and only the native accepts the placement.
const (
	climateFamilies = "shelter,sleeping,cooking,temperature"
	climateWait     = 15 * time.Minute
	// campfireMargin is how far outside the planned core a mild map's cooking
	// campfire may stand (the stand-in margin was deleted in #2266; #2278 revisits the case).
	campfireMargin = 3
)

type climate struct {
	name, scope string
	start       cases.Start
	cold, hot   bool
	// stage raises the planned shelter before the placement is awaited: the
	// indoor slots only exist on a standing shelter.
	stage bool
	// build is the definition whose first placement is the assertion.
	build string
}

func init() {
	for _, c := range []climate{
		{name: "shelter/climate-mild", build: "Campfire",
			scope: "the cooking campfire stands outside every planned room, within the core box plus 3, and the shelter template holds no campfire slot",
			start: cases.Save{Name: sustained.BaselineSave}},
		{name: "shelter/climate-cold", cold: true, stage: true, build: "Campfire",
			scope: "the plan latches Cold, the shelter template holds two campfire slots and, the shelter standing, the cooking campfire is placed inside it",
			start: quietStart(na.DebugStart{MapSize: 150, PlanetCoverage: 0.05, Biomes: "Tundra,BorealForest,ColdBog,IceSheet", Seed: "shelter-cold-2076"})},
		{name: "shelter/climate-hot", hot: true, stage: true, build: "PassiveCooler",
			scope: "the plan latches Hot, the shelter template holds a cooler slot and, the shelter standing, the temperature planner's passive cooler is placed inside it",
			start: quietStart(na.DebugStart{MapSize: 150, PlanetCoverage: 0.05, Biomes: "ExtremeDesert,Desert", Seed: "shelter-hot-2076"})},
	} {
		c := c
		cases.Register(cases.Case{
			Name:   c.name,
			Scope:  "Issue #2076: from a " + c.name[len("shelter/climate-"):] + " map, " + c.scope + ".",
			Start:  c.start,
			Serve:  &cases.ServeSpec{Families: []string{climateFamilies}, NativeTimeout: 30 * time.Second, Prefix: "shelter-climate"},
			Budget: 30 * time.Minute,
			Reason: "one short serve window per climate: the first recorded plan, an optional staged shelter and the first placement of the climate's building",
			Run:    func(ctx context.Context, s cases.Session) error { return runClimate(ctx, s, c) },
		})
	}
}

// quietStart is a pinned fresh world with the quiet-world marker, so no wild
// spawner interrupts the first minutes.
func quietStart(d na.DebugStart) cases.Start {
	return cases.Fixture{Op: na.QuietWorldTool, On: cases.DebugStart{Size: d}}
}

func runClimate(ctx context.Context, s cases.Session, c climate) error {
	report := s.Report()
	service, err := start(ctx, s, nil)
	if err != nil {
		return err
	}
	defer service.Stop()
	record, err := waitPlan(ctx, service, planWait, func(p policy.LayoutPlan) bool { return len(roomsOf(p, policy.PlannedShelter)) == 1 })
	if err != nil {
		return fmt.Errorf("wait for the first layout plan: %w", err)
	}
	plan := record.Plan
	shelter := roomsOf(plan, policy.PlannedShelter)[0]
	report["plan"] = map[string]any{"tick": record.Tick, "cold": plan.Cold, "hot": plan.Hot, "shelter": shelter.Interior,
		"campfire_slots": policy.ShelterCampfires(plan.Cold), "cooler_slots": policy.ShelterCoolers(plan.Hot)}
	// A desert's winter can dip below freezing, so the hot map need not be
	// warm all year: it must latch Hot, and its Cold latch is not asserted.
	if plan.Cold != c.cold && !c.hot || plan.Hot != c.hot {
		return fmt.Errorf("the plan latched cold=%v hot=%v on this map, want cold=%v hot=%v", plan.Cold, plan.Hot, c.cold, c.hot)
	}
	if got, want := policy.ShelterCampfires(plan.Cold), map[bool]int{true: 2, false: 0}[plan.Cold]; got != want {
		return fmt.Errorf("the shelter template plans %d campfire slots, want %d", got, want)
	}
	// Plans admitted before the shelter stands place on the unstaged map.
	earlier := map[domain.PlanID]bool{}
	if c.stage {
		journal, err := service.Store(ctx)
		if err != nil {
			return err
		}
		plans, err := journal.LoadPlans(ctx, 256)
		if err != nil {
			return err
		}
		for _, p := range plans {
			earlier[p.Spec.ID()] = true
		}
		report["keepalive_plan"] = service.Stop()
		h, err := s.Reattach(ctx)
		if err != nil {
			return err
		}
		staged, err := stageRooms(ctx, h, "stage-shelter", shelter)
		if err != nil {
			return err
		}
		report["staged_shelter"] = staged
		if service, err = start(ctx, s, service); err != nil {
			return err
		}
		defer service.Stop()
	}
	// On a staged map a plan admitted before the review reads the staged shelter
	// as standing places the building outside it (a cooking campfire stands
	// outdoors until a shelter stands); only a placement inside counts.
	var want func(domain.Cell) bool
	if c.cold || c.hot {
		want = func(cell domain.Cell) bool { return inside(shelter.Interior, cell) }
	}
	cell, seen, err := waitBuilding(ctx, service, c.build, earlier, want)
	report["placements"] = seen
	if err != nil {
		core, _ := plan.CoreBounds()
		return fmt.Errorf("%w (shelter %+v, core %+v, placements %v)", err, shelter.Interior, core, seen)
	}
	report["placement"] = map[string]any{"definition": c.build, "cell": cell}
	switch {
	case c.cold || c.hot:
	default:
		for _, r := range plan.AllRooms() {
			if inside(r.Interior, cell) {
				return fmt.Errorf("%s placed at %v inside the planned %s room %+v", c.build, cell, r.Role, r.Interior)
			}
		}
		core, ok := plan.CoreBounds()
		if !ok {
			return fmt.Errorf("the plan has no core bounds")
		}
		box := policy.Rectangle{X: core.X - campfireMargin, Z: core.Z - campfireMargin, Width: core.Width + 2*campfireMargin, Height: core.Height + 2*campfireMargin}
		if !inside(box, cell) {
			return fmt.Errorf("%s placed at %v outside the core box %+v plus %d", c.build, cell, core, campfireMargin)
		}
	}
	return nil
}

// waitBuilding waits for a plan outside skip that places definition on a cell
// want accepts (any cell when want is nil) and returns that cell and every
// cell of definition the plans placed meanwhile.
func waitBuilding(ctx context.Context, service *na.ServiceProcess, definition string, skip map[domain.PlanID]bool, want func(domain.Cell) bool) (domain.Cell, []domain.Cell, error) {
	journal, err := service.Store(ctx)
	if err != nil {
		return domain.Cell{}, nil, err
	}
	var cell domain.Cell
	var seen []domain.Cell
	var readErr error
	_, err = service.WaitReview(ctx, na.Wait{Ceiling: climateWait}, func(store.Rounds) bool {
		plans, err := journal.LoadPlans(ctx, 256)
		if err != nil {
			readErr = err
			return true
		}
		for _, plan := range plans {
			if skip[plan.Spec.ID()] {
				continue
			}
			for _, a := range plan.Spec.Actions() {
				if b, ok := a.Building(); ok && b.Definition() == definition {
					if !slices.Contains(seen, b.Cell()) {
						seen = append(seen, b.Cell())
					}
					if want == nil || want(b.Cell()) {
						cell = b.Cell()
						return true
					}
				}
			}
		}
		return false
	})
	if readErr != nil {
		return domain.Cell{}, seen, readErr
	}
	if err != nil {
		return domain.Cell{}, seen, fmt.Errorf("no %s placed in the shelter within the window: %w", definition, err)
	}
	return cell, seen, nil
}
