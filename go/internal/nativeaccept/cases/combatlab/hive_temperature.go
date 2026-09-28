package combatlab

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/hive-temperature",
		Scope: "The combat frame's hive temperature (#1073) on lab-infestation: one step after staging, a snapshot frame carries " +
			"combat_hive_temperature_c, it decodes as a known fact, and an unheated mountain room reads between -60 and 60 C.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runHiveTemperature,
	})
}

func runHiveTemperature(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	if _, err := Stage(ctx, h, "lab-infestation", nil); err != nil {
		return err
	}
	identity, err := typedIdentity(s.Identity())
	if err != nil {
		return err
	}
	frames, err := openCombatFrames(ctx, h, identity)
	if err != nil {
		return err
	}
	_, tick, err := Tick(ctx, h, mirrorStepTicks)
	if err != nil {
		return err
	}
	v, err := frames.snapshot(ctx, int64(tick))
	if err != nil {
		return err
	}
	if v.CombatHiveTemperatureC == nil {
		return fmt.Errorf("frame at tick %d has no hive temperature", v.GetContext().GetTick())
	}
	combat, err := bridge.DecodeCombat(v)
	if err != nil {
		return err
	}
	temp, known := combat.HiveTemperatureC.Value()
	report["hiveTemperatureC"] = temp
	if !known || temp < -60 || temp > 60 {
		return fmt.Errorf("hive temperature %v (known %v): want an unheated room's", temp, known)
	}
	return nil
}
