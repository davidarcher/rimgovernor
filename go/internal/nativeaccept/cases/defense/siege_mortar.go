package defense

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/combatlab"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// siegeMortarWait bounds the wait, in wall time on a Fast served clock,
	// for the fight to crew the mortar and fire on the camp: the camp is
	// already in LordToil_Siege a few hundred ticks after staging.
	siegeMortarWait = 4 * time.Minute
	// siegeCampNorth is lab-siege's camp spot north of the lab centre and
	// siegeAimSlack how far from it, in cells, a camp aim may fall: the
	// besiegers wander their camp while they build.
	siegeCampNorth = 20
	siegeAimSlack  = 12
	// letterPauseModeTool sets Prefs.AutomaticPauseMode
	// (scripts/fixtures/LetterFixture.cs).
	letterPauseModeTool = "test/letter_pause_mode"
)

// defense/siege-mortar serves the routine defense planner on lab-siege:
// three riflemen by an unroofed player mortar with HE and
// EMP shells, four raiders under a real LordJob_Siege camped 35 cells off.
// The fight must answer the stationary siege with its own mortar: within
// the wait, its combat evidence records an applied man_mortar order on the
// mortar and an applied counter-battery mortar_fire aimed at the camp.
func init() {
	cases.Register(cases.Case{
		Name: "defense/siege-mortar",
		Scope: "A forced siege answered by mortar fire (#1208): on lab-siege, served by the routine defense planner, the fight's combat evidence records " +
			"an applied man_mortar on the colony mortar and an applied counter_battery mortar_fire aimed at the camped besiegers. Native: a live siege lord, the served fight and the native mortar ops end to end, which no colony-facts snapshot replays.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, combatlab.StageTool, letterPauseModeTool},
		QuietWorld:  true,
		// A checkpoint capture pauses the served game mid-fight.
		NoCheckpoint: true,
		Serve:        &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Defense, routinefamily.Tend, routinefamily.Rescue}, PlayerSpeed: "Fast", Prefix: "defense-siege-mortar"},
		Budget:       2 * cases.LabBudget,
		Crew:         cases.Crew{Size: 3}, Run: runSiegeMortar,
	})
}

func runSiegeMortar(ctx context.Context, s cases.Session) error {
	report := s.Report()
	var cx, cz int
	if _, err := combatlab.Stage(ctx, s.Harness(), "lab-siege", func(_ *combatlab.Fixture, x, z int) { cx, cz = x, z }); err != nil {
		return err
	}
	camp := domain.Cell{X: int32(cx), Z: int32(cz + siegeCampNorth)}
	// A MajorThreat letter would pause the game as a player.
	if reply, err := s.Harness().Call(ctx, "pause-mode-never", letterPauseModeTool, map[string]any{"mode": "Never"}); err != nil {
		return err
	} else if na.AsString(reply["mode"]) != "Never" {
		return fmt.Errorf("%s did not take: %#v", letterPauseModeTool, reply)
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	deadline := time.Now().Add(siegeMortarWait)
	var seen []store.CombatOrderRecord
	for {
		if err := service.Exited(); err != nil {
			return fmt.Errorf("service exited before the mortar fired: %w", err)
		}
		fights, err := journal.OpenCombatFights(ctx)
		if err != nil {
			return err
		}
		var manned, fired *store.CombatOrderRecord
		seen = seen[:0]
		for plan := range fights {
			stops, err := journal.CombatEvidence(ctx, plan)
			if err != nil {
				return err
			}
			for _, stop := range stops {
				for _, o := range stop.Orders {
					if o.Kind != policy.OrderManMortar && o.Kind != policy.OrderMortarFire {
						continue
					}
					seen = append(seen, o)
					switch {
					case !o.Applied:
					case o.Kind == policy.OrderManMortar && manned == nil:
						manned = &o
					case o.Kind == policy.OrderMortarFire && fired == nil && o.Reason == policy.ReasonCounterBattery && near(o.Aim, camp, siegeAimSlack):
						fired = &o
					}
				}
			}
		}
		if manned != nil && fired != nil {
			report["manMortar"], report["mortarFire"] = *manned, *fired
			return nil
		}
		if time.Now().After(deadline) {
			report["mortarOrders"] = seen
			return fmt.Errorf("within %s no applied man_mortar and counter-battery mortar_fire at the camp near %d,%d (mortar orders seen: %v)", siegeMortarWait, camp.X, camp.Z, seen)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func near(a, b domain.Cell, cells int32) bool {
	dx, dz := a.X-b.X, a.Z-b.Z
	return dx*dx+dz*dz <= cells*cells
}
