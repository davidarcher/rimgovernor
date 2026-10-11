package shelter

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// shelter/solo-cold: epic #2745's colony of one. The capability logic (the
// shelter hosting sleep, craft, research and heat, no laboratory owed,
// retirement per capability) is proven by the policy snapshot tests; this case
// keeps what needs a real world: a one-colonist ice-sheet start (LabStart is a
// three-colonist baseline, so it cannot make one) whose recorded plan latches
// Cold, holds the shelter, plans no laboratory, and whose shelter campfire the
// native accepts on the planned interior.
func init() {
	cases.Register(cases.Case{
		Name: "shelter/solo-cold",
		Scope: "Issue #2762: from a one-colonist ice-sheet start the plan latches Cold, plans the shelter and no laboratory " +
			"(research is the shelter's capability), and the shelter's campfire stands on its planned interior. " +
			"A snapshot test covers the capability logic; only a real cold world reads the temperature curve and accepts the placement.",
		Start: cases.Fixture{Op: na.QuietWorldTool, On: cases.Scenario{Spec: na.ScenarioStart{
			Scenario: na.DebugScenario, Count: 1, Seed: "shelter-solo-2762", Difficulty: na.DebugDifficulty,
			Biome: "IceSheet", WorldTemperature: "VeryCold",
			Size: na.DebugStart{MapSize: 150, PlanetCoverage: 0.05}, SaveName: "shelter-solo-2762"}}},
		Serve:  &cases.ServeSpec{Families: climateFamilies, NativeTimeout: 30 * time.Second, Prefix: "shelter-solo"},
		Budget: 30 * time.Minute,
		Crew:   cases.Crew{Size: 1}, Reason: "a solo colony is the claim: one colonist, one serve window to the first recorded plan and campfire placement",
		Run: runSolo,
	})
}

func runSolo(ctx context.Context, s cases.Session) error {
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
	report["plan"] = map[string]any{"tick": record.Tick, "cold": plan.Cold, "shelter": shelter.Region.Bounds(), "labs": len(roomsOf(plan, policy.PlannedLab))}
	if !plan.Cold {
		return fmt.Errorf("the plan did not latch Cold on an ice sheet")
	}
	if n := len(roomsOf(plan, policy.PlannedLab)); n != 0 {
		return fmt.Errorf("a solo colony's plan holds %d laboratories; the shelter hosts research", n)
	}
	cell, seen, err := waitBuilding(ctx, service, policy.ShelterHeatDef, func(c domain.Cell) bool { return inside(shelter.Region.Bounds(), c) })
	report["placements"] = seen
	if err != nil {
		return fmt.Errorf("%w (shelter %+v, placements %v)", err, shelter.Region.Bounds(), seen)
	}
	report["placement"] = map[string]any{"definition": policy.ShelterHeatDef, "cell": cell}
	refused, err := na.RefusalsNameBlockers(service.LogPath)
	report["placement_refused"] = refused
	return err
}
