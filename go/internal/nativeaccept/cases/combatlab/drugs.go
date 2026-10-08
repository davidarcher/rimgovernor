package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/drugs",
		Scope: "Enemy drug facts (#1056), a new read contract: on lab-open with the first raider staged on go-juice (GoJuiceHigh) and the second " +
			"luciferium-addicted (LuciferiumAddiction), one step later the snapshot frame's combat_pawns rows carry go_juice_high on the first, " +
			"luciferium_addicted on the second, and neither on the third raider or any colonist.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runDrugs,
	})
}

func runDrugs(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	staged, err := Stage(ctx, h, "lab-open", func(f *Fixture, _, _ int) {
		drugs := [][]string{{"GoJuiceHigh"}, {"LuciferiumAddiction"}}
		for i := range f.Pawns {
			if f.Pawns[i].Side == Hostile && len(drugs) > 0 {
				f.Pawns[i].Hediffs, drugs = drugs[0], drugs[1:]
			}
		}
	})
	if err != nil {
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
	defer frames.reader.Close()
	_, tick, err := Tick(ctx, h, mirrorStepTicks)
	if err != nil {
		return err
	}
	state, err := frames.next(ctx, int64(tick))
	if err != nil {
		return err
	}
	hostiles := staged.Hostiles()
	want := map[string][2]bool{hostiles[0]: {true, false}, hostiles[1]: {false, true}, hostiles[2]: {}}
	for _, id := range staged.Colonists() {
		want[id] = [2]bool{}
	}
	got := map[string][2]bool{}
	for _, row := range state.Pawns {
		if _, ok := want[row.GetId()]; ok {
			got[row.GetId()] = [2]bool{row.GetGoJuiceHigh(), row.GetLuciferiumAddicted()}
		}
	}
	report["drugs"] = got
	for id, w := range want {
		g, ok := got[id]
		if !ok || g != w {
			return fmt.Errorf("combat_pawns %s: [go_juice_high luciferium_addicted] = %v (row present %v), want %v", id, g, ok, w)
		}
	}
	return nil
}
