package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/stun",
		Scope: "The combat_pawns stun_ticks_left fact (#1050, #1118) on lab-mech: a scyther staged stunned for 600 ticks reads stun_ticks_left > 0 and at most the " +
			"stun left in the first frame after one 60-tick step, and absent in the first frame after the stun expires.",
		Start:       cases.Lab{Colonists: 1},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runStun,
	})
}

func runStun(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	staged, err := Stage(ctx, h, "lab-mech")
	if err != nil {
		return err
	}
	mechs := staged.Mechs()
	if len(mechs) != 1 {
		return fmt.Errorf("staged %d mechs, want 1", len(mechs))
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
	row := func(tick int) (*mp.CombatPawn, error) {
		state, err := frames.next(ctx, int64(tick))
		if err != nil {
			return nil, err
		}
		for _, r := range state.Pawns {
			if r.GetId() == mechs[0] {
				return r, nil
			}
		}
		return nil, fmt.Errorf("no combat_pawns row for scyther %s in %d rows", mechs[0], len(state.Pawns))
	}
	_, tick, err := Tick(ctx, h, mirrorStepTicks)
	if err != nil {
		return err
	}
	stunned, err := row(tick)
	if err != nil {
		return err
	}
	report["stunnedRow"] = stunned.GetStunTicksLeft()
	// The fact rounds up to 30 ticks.
	if left := int(stunned.GetStunTicksLeft()); left <= 0 || left > mechStunTicks-mirrorStepTicks+30 {
		return fmt.Errorf("stunned scyther reads stun_ticks_left %d after %d of %d ticks: %v", left, mirrorStepTicks, mechStunTicks, stunned)
	}
	_, tick, err = Tick(ctx, h, mechStunTicks)
	if err != nil {
		return err
	}
	after, err := row(tick)
	if err != nil {
		return err
	}
	report["afterRow"] = after.GetStunTicksLeft()
	if after.StunTicksLeft != nil {
		return fmt.Errorf("scyther still reads stun_ticks_left %d %d ticks after a %d-tick stun: %v", after.GetStunTicksLeft(), mirrorStepTicks+mechStunTicks, mechStunTicks, after)
	}
	return nil
}
