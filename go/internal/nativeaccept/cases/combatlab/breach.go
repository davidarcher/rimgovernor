package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// breachDigTicks bounds the walk to the wall and the first Mine job: the
// sappers stand 15 cells off it.
const breachDigTicks = 3000

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/breach",
		Scope: "The sapper Mine job on lab-breach (#1149, #913-#915): a sapper raid staged against a doorless walled room digs in, and a frame's combat_pawns row " +
			"reads a raider's job Mine while it stands against the wall outside the room, within 3000 ticks. Native because the job is vanilla's sapper AI digging, not a recorded fact.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runBreach,
	})
}

func runBreach(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	staged, err := Stage(ctx, h, "lab-breach")
	if err != nil {
		return err
	}
	raiders := map[string]bool{}
	for _, id := range staged.Hostiles() {
		raiders[id] = true
	}
	// The room's centre is the first colonist's cell two north.
	cx, cz := staged.Fixture.Pawns[0].X+2, staged.Fixture.Pawns[0].Z+2
	identity, err := typedIdentity(s.Identity())
	if err != nil {
		return err
	}
	frames, err := openCombatFrames(ctx, h, identity)
	if err != nil {
		return err
	}
	defer frames.reader.Close()
	jobs := map[string]int{}
	for ticks := mirrorStepTicks; ticks <= breachDigTicks; ticks += mirrorStepTicks {
		_, tick, err := Tick(ctx, h, mirrorStepTicks)
		if err != nil {
			return err
		}
		state, err := frames.next(ctx, int64(tick))
		if err != nil {
			return err
		}
		for _, row := range state.Pawns {
			if !raiders[row.GetId()] {
				continue
			}
			jobs[row.GetJob()]++
			if row.GetJob() != "Mine" {
				continue
			}
			// Mine walks to the wall first: digging is Mine against it,
			// from a cell just outside the ring.
			x, z := int(row.GetCell().GetX()), int(row.GetCell().GetZ())
			if max(abs(x-cx), abs(z-cz)) != breachHalf+1 {
				continue
			}
			report["mine"] = map[string]any{"pawn": row.GetId(), "x": x, "z": z, "ticks": ticks}
			report["jobs"] = jobs
			return nil
		}
	}
	report["jobs"] = jobs
	return fmt.Errorf("no raider read job Mine against the wall in %d ticks of lab-breach: %v", breachDigTicks, jobs)
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}
