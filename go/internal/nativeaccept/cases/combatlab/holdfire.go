package combatlab

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
)

// holdFireTicks bounds each phase: a drafted rifleman 25 cells from a
// raider fires well inside it.
const holdFireTicks = 1200

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/holdfire",
		Scope: "fire_mode hold stops a drafted pawn shooting (#861), vanilla behaviour the #850 contract only reads back as a flag: on lab-ranged two drafted riflemen, " +
			"one ordered stop and fire_mode hold, the other ordered to attack; the attacker fires and the held one fires no shot over at least 600 ticks, " +
			"then fire_mode at_will and the formerly held one fires within 1200 ticks.",
		Start:       cases.Lab{Colonists: 4},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runHoldFire,
	})
}

func runHoldFire(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	staged, err := Stage(ctx, h, "lab-ranged")
	if err != nil {
		return err
	}
	typed, err := typedIdentity(identity)
	if err != nil {
		return err
	}
	frames, err := openCombatFrames(ctx, h, typed)
	if err != nil {
		return err
	}
	defer frames.reader.Close()
	colonists, hostiles := staged.Colonists(), staged.Hostiles()
	if len(colonists) < 2 || len(hostiles) < 2 {
		return fmt.Errorf("staged %d colonists and %d hostiles", len(colonists), len(hostiles))
	}
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-holdfire-acquire", identity)
	if err != nil {
		return err
	}
	held, shooter := colonists[0], colonists[1]
	if err := draftAll(ctx, h, identity, "draft", []string{held, shooter}); err != nil {
		return err
	}
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	results, err := issue(ctx, h, identity, "combat-holdfire-1", []any{
		map[string]any{"pawn": pawn(held), "stop": map[string]any{}},
		map[string]any{"pawn": pawn(held), "fireMode": "COMBAT_FIRE_MODE_HOLD"},
		map[string]any{"pawn": pawn(shooter), "attack": pawn(hostiles[1])},
	})
	if err != nil {
		return err
	}
	for i, r := range results {
		if applied, _ := na.AsBool(r["applied"]); !applied {
			return fmt.Errorf("order %d refused: %v", i, r)
		}
	}
	seen := map[string]bool{}
	shots, err := shotsFor(ctx, h, frames, seen, shooter, holdFireTicks/2)
	report["heldPhase"] = shots
	if err != nil {
		return err
	}
	if shots[held] != 0 {
		return fmt.Errorf("held rifleman fired %d shots under hold fire: %v", shots[held], shots)
	}
	results, err = issue(ctx, h, identity, "combat-holdfire-2", []any{
		map[string]any{"pawn": pawn(held), "fireMode": "COMBAT_FIRE_MODE_AT_WILL"},
	})
	if err != nil {
		return err
	}
	if applied, _ := na.AsBool(results[0]["applied"]); !applied {
		return fmt.Errorf("fire at will refused: %v", results[0])
	}
	shots, err = shotsFor(ctx, h, frames, seen, held, 0)
	report["atWillPhase"] = shots
	return err
}

// shotsFor steps the lab, at least min ticks and at most holdFireTicks,
// until until fires a shot, and counts every new shot_fired event by
// shooter.
func shotsFor(ctx context.Context, h *na.Harness, frames *combatFrames, seen map[string]bool, until string, min int) (map[string]int, error) {
	shots := map[string]int{}
	for ticks := 0; ticks < holdFireTicks; ticks += mirrorStepTicks {
		_, tick, err := Tick(ctx, h, mirrorStepTicks)
		if err != nil {
			return shots, err
		}
		state, err := frames.next(ctx, int64(tick))
		if err != nil {
			return shots, err
		}
		for _, e := range state.Events {
			if id := bridge.CombatEventID(e); !seen[id] {
				seen[id] = true
				if e.GetKind() == mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED {
					shots[e.GetThingId()]++
				}
			}
		}
		if shots[until] > 0 && ticks+mirrorStepTicks >= min {
			return shots, nil
		}
	}
	return shots, fmt.Errorf("%s fired no shot in %d ticks: %v", until, holdFireTicks, shots)
}
