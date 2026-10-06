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

// The shelter climate cases (#2076, #2278) prove the shelter's campfires and
// cooler, and a mild map's cooking campfire, follow the real map's temperature
// curve (#2044) and the plan: the plan latches Cold or Hot from the live gear
// snapshot, and the building the planner then places stands on the planned
// interior of the room that owns it, its ring raised with it rather than
// before it (#2264, #2266): the cold map's campfires and the hot map's passive
// cooler in the shelter, a mild map's cooking campfire in the kitchen. A
// snapshot test over recorded facts proves the placement search
// (buildingruntime campfire_climate_test.go); only a real map reads the curve,
// and only the native accepts the placement.
const (
	// The naming family answers the settlement naming dialog the game raises
	// about four days in, which stops the clock until it is confirmed.
	climateFamilies = "shelter,sleeping,cooking,temperature,naming"
	climateWait     = 15 * time.Minute
)

type climate struct {
	name, scope string
	families    string
	start       cases.Start
	cold, hot   bool
	// build is the definition whose first placement is the assertion; room is
	// the planned role whose interior it must stand on.
	build string
	room  policy.PlannedRole
}

func init() {
	for _, c := range []climate{
		{name: "shelter/climate-mild", build: "Campfire", room: policy.PlannedKitchen, families: climateFamilies,
			scope: "the shelter template holds no campfire slot and the cooking campfire is placed on the planned kitchen's interior",
			start: cases.Save{Name: sustained.BaselineSave}},
		{name: "shelter/climate-cold", cold: true, build: "Campfire", room: policy.PlannedShelter, families: climateFamilies + ",armory",
			scope: "the plan latches Cold, the shelter template holds two campfire slots and the cooking campfire is placed on the planned shelter's interior, any crafting spot with it",
			start: quietStart(na.DebugStart{MapSize: 150, PlanetCoverage: 0.05, Biomes: "Tundra,BorealForest,ColdBog,IceSheet", Seed: "shelter-cold-2076"})},
		{name: "shelter/climate-hot", hot: true, build: "PassiveCooler", room: policy.PlannedShelter, families: climateFamilies,
			scope: "the plan latches Hot, the shelter template holds a cooler slot and the temperature planner's passive cooler is placed on the planned shelter's interior",
			// A desert tile at a high latitude is neither hot nor warm all year;
			// a very hot planet puts the whole small patch past HotEnter.
			start: cases.Fixture{Op: na.QuietWorldTool, On: cases.Scenario{Spec: na.ScenarioStart{
				Scenario: na.DebugScenario, Count: na.DebugColonists, Seed: "shelter-hot-2076", Difficulty: na.DebugDifficulty,
				Biome: "ExtremeDesert,Desert", WorldTemperature: "VeryHot",
				Size: na.DebugStart{MapSize: 150, PlanetCoverage: 0.05}, SaveName: "shelter-hot-2076"}}}},
	} {
		c := c
		cases.Register(cases.Case{
			Name:   c.name,
			Scope:  "Issues #2076 and #2278: from a " + c.name[len("shelter/climate-"):] + " map, " + c.scope + "; a refused furniture cell reports its blocker.",
			Start:  c.start,
			Serve:  &cases.ServeSpec{Families: []string{c.families}, NativeTimeout: 30 * time.Second, Prefix: "shelter-climate"},
			Budget: 30 * time.Minute,
			Reason: "one short serve window per climate: the first recorded plan and the first placement of the climate's building",
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
	record, err := waitPlan(ctx, service, planWait, func(p policy.LayoutPlan) bool {
		return len(roomsOf(p, policy.PlannedShelter)) == 1 && len(roomsOf(p, c.room)) > 0
	})
	if err != nil {
		return fmt.Errorf("wait for the first layout plan: %w", err)
	}
	plan := record.Plan
	shelter := roomsOf(plan, policy.PlannedShelter)[0]
	owner := roomsOf(plan, c.room)[0]
	report["plan"] = map[string]any{"tick": record.Tick, "cold": plan.Cold, "hot": plan.Hot, "shelter": shelter.Interior, "owner": owner.Interior,
		"campfire_slots": policy.ShelterCampfires(plan.Cold), "cooler_slots": policy.ShelterCoolers(plan.Hot)}
	// A desert's winter can dip below freezing, so the hot map need not be
	// warm all year: it must latch Hot, and its Cold latch is not asserted.
	if plan.Cold != c.cold && !c.hot || plan.Hot != c.hot {
		return fmt.Errorf("the plan latched cold=%v hot=%v on this map, want cold=%v hot=%v", plan.Cold, plan.Hot, c.cold, c.hot)
	}
	if got, want := policy.ShelterCampfires(plan.Cold), map[bool]int{true: 2, false: 0}[plan.Cold]; got != want {
		return fmt.Errorf("the shelter template plans %d campfire slots, want %d", got, want)
	}
	// The temperature building waits for no standing shelter (#2265) and the
	// shelter's slots are keyed on the planned interior (#2264): no stage is
	// needed, and a placement outside the room that owns it fails the case.
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	cell, seen, err := waitBuilding(ctx, service, c.build, func(cell domain.Cell) bool { return inside(owner.Interior, cell) })
	report["placements"] = seen
	if err != nil {
		core, _ := plan.CoreBounds()
		return fmt.Errorf("%w (%s %+v, core %+v, placements %v)", err, c.room, owner.Interior, core, seen)
	}
	report["placement"] = map[string]any{"definition": c.build, "cell": cell}
	if c.cold {
		// The crafting spot, when the armory family asked for one, takes the
		// shelter's slot too (#2264).
		spots, err := placements(ctx, journal, craftingSpot)
		if err != nil {
			return err
		}
		report["crafting_spots"] = spots
		for _, spot := range spots {
			if !inside(shelter.Interior, spot) {
				return fmt.Errorf("%s placed at %v outside the planned shelter %+v", craftingSpot, spot, shelter.Interior)
			}
		}
	}
	refused, err := na.RefusalsNameBlockers(service.FlightPath)
	report["placement_refused"] = refused
	return err
}

// craftingSpot is the shelter template's bench slot definition.
const craftingSpot = "CraftingSpot"

// placements are the cells every journaled plan places definition on.
func placements(ctx context.Context, journal *store.Store, definition string) ([]domain.Cell, error) {
	plans, err := journal.PlanHistoryWithMethods(ctx, 256, "*")
	if err != nil {
		return nil, err
	}
	var out []domain.Cell
	for _, plan := range plans {
		for _, a := range plan.Spec.Actions() {
			if b, ok := a.Building(); ok && b.Definition() == definition && !slices.Contains(out, b.Cell()) {
				out = append(out, b.Cell())
			}
		}
	}
	return out, nil
}

// waitBuilding waits for a plan that places definition on a cell want accepts
// and returns that cell and every cell of definition the plans placed
// meanwhile.
func waitBuilding(ctx context.Context, service *na.ServiceProcess, definition string, want func(domain.Cell) bool) (domain.Cell, []domain.Cell, error) {
	journal, err := service.Store(ctx)
	if err != nil {
		return domain.Cell{}, nil, err
	}
	var cell domain.Cell
	var seen []domain.Cell
	var readErr error
	_, err = service.WaitReview(ctx, na.Wait{Ceiling: climateWait}, func(store.Rounds) bool {
		plans, err := journal.PlanHistoryWithMethods(ctx, 256, "*")
		if err != nil {
			readErr = err
			return true
		}
		for _, plan := range plans {
			for _, a := range plan.Spec.Actions() {
				if b, ok := a.Building(); ok && b.Definition() == definition {
					if !slices.Contains(seen, b.Cell()) {
						seen = append(seen, b.Cell())
					}
					if want(b.Cell()) {
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
		return domain.Cell{}, seen, fmt.Errorf("no %s placed on its planned interior within the window: %w", definition, err)
	}
	return cell, seen, nil
}
