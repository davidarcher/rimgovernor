package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// siegeBuildTicks bounds the wait for a builder on a frame: the supply
// drop pods land, the raiders haul them to the blueprints and start a
// frame, a few thousand ticks on the vanilla camp.
const (
	siegeBuildTicks = 10000
	siegeStepTicks  = 250
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/siege-stage",
		Scope: "The lab_stage siege option (#1147): lab-siege stages its raiders under a real LordJob_Siege at the camp spot; within " +
			"10,000 ticks the lord is in LordToil_Siege and at least one raider works a FinishFrame job on a camp frame.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runSiegeStage,
	})
}

func runSiegeStage(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	staged, err := Stage(ctx, h, "lab-siege")
	if err != nil {
		return err
	}
	hostiles := staged.Hostiles()
	var last map[string]any
	for waited := 0; waited < siegeBuildTicks; waited += siegeStepTicks {
		state, err := tickReadN(ctx, h, siegeStepTicks)
		if err != nil {
			return err
		}
		for _, id := range hostiles {
			p := state.pawns[id]
			if p == nil {
				continue
			}
			last = p
			if na.AsString(p["lordJob"]) != "LordJob_Siege" {
				return fmt.Errorf("raider %s left the siege lord: %v", id, p)
			}
			if na.AsString(p["lordToil"]) == "LordToil_Siege" && na.AsString(p["job"]) == "FinishFrame" {
				report["builder"], report["frame"], report["ticks"] = id, p["jobThing"], waited+siegeStepTicks
				return nil
			}
		}
	}
	return fmt.Errorf("no raider in LordToil_Siege on a FinishFrame job within %d ticks (last %v)", siegeBuildTicks, last)
}
