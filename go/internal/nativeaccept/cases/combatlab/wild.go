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
		Name: "combatlab/wild",
		Scope: "The combat mirror's wild-animal side (#1116) on lab-ranged with three calm wild animals: a thrumbo 4 cells past the raiders is a " +
			"COMBAT_SIDE_WILD_ANIMAL row; a hare beside them (small, no predator) and a warg 40+ cells away are no row.",
		Start:       cases.Lab{Colonists: 4},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runWild,
	})
}

func runWild(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	staged, err := Stage(ctx, h, "lab-ranged", func(f *Fixture, cx, cz int) {
		f.Pawns = append(f.Pawns,
			Pawn{Side: Wild, Kind: "Thrumbo", X: cx + 7, Z: cz + 18},
			Pawn{Side: Wild, Kind: "Hare", X: cx - 7, Z: cz + 18},
			Pawn{Side: Wild, Kind: "Warg", X: cx + 30, Z: cz - 30})
	})
	if err != nil {
		return err
	}
	wild := staged.ids(Wild)
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
	rows := map[string]*mp.CombatPawn{}
	for _, row := range state.Pawns {
		rows[row.GetId()] = row
	}
	report := s.Report()
	report["frameRows"] = len(rows)
	if row := rows[wild[0]]; row == nil || row.GetSide() != mp.CombatSide_COMBAT_SIDE_WILD_ANIMAL {
		return fmt.Errorf("thrumbo %s near the raiders: row %v, want side wild_animal", wild[0], row)
	}
	for _, id := range wild[1:] {
		if row := rows[id]; row != nil {
			return fmt.Errorf("wild %s has a combat row: %v", id, row)
		}
	}
	return nil
}
