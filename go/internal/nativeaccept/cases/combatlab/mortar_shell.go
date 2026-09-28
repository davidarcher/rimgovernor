package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// shellLoadTicks bounds each wait for the crew to fetch and load a shell
// (a few cells' walk and the load), inside the #845 5,000-tick budget.
const shellLoadTicks = 1800

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/mortar-shell",
		Scope: "The combat.orders mortar shell op (#1051), a native op no snapshot can prove: on lab-siege, a crew ordered to fire HE at the camp's mortar " +
			"loads Shell_HighExplosive (the combat_mortars row's loaded_shell), a second order naming Steel refuses unknown_shell, and a re-order " +
			"naming Shell_EMP unloads the HE (if still loaded) and loads the EMP shell.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runMortarShell,
	})
}

func runMortarShell(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-siege", func(_ *Fixture, x, z int) { cx, cz = x, z })
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
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-mortar-shell-acquire", s.Identity())
	if err != nil {
		return err
	}
	colonists := staged.Colonists()
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	mortar, target := cell(cx, cz+siegeOurMortar), cell(cx, cz+siegeCampMortar)
	order := func(id, shell string) map[string]any {
		return map[string]any{"pawn": pawn(id), "mortar": map[string]any{"mortar": mortar, "target": target, "shell": shell}}
	}
	results, err := issue(ctx, h, s.Identity(), "combat-mortar-shell-1", []any{
		map[string]any{"pawn": pawn(colonists[0]), "draft": map[string]any{}},
		map[string]any{"pawn": pawn(colonists[1]), "draft": map[string]any{}},
		order(colonists[0], "Shell_HighExplosive"),
		order(colonists[1], "Steel"),
	})
	if err != nil {
		return err
	}
	report["first"] = results
	if applied, _ := na.AsBool(results[2]["applied"]); !applied || na.AsString(results[2]["jobDef"]) != "ManTurret" {
		return fmt.Errorf("the HE order: want applied ManTurret, got %v", results[2])
	}
	if applied, _ := na.AsBool(results[3]["applied"]); applied || na.AsString(results[3]["refusal"]) != "unknown_shell" {
		return fmt.Errorf("steel order: want refusal unknown_shell, got %v", results[3])
	}
	if err := awaitLoaded(ctx, h, frames, cx, cz+siegeOurMortar, "Shell_HighExplosive", report, "heTick"); err != nil {
		return err
	}
	results, err = issue(ctx, h, s.Identity(), "combat-mortar-shell-2", []any{order(colonists[0], "Shell_EMP")})
	if err != nil {
		return err
	}
	report["second"] = results
	if applied, _ := na.AsBool(results[0]["applied"]); !applied {
		return fmt.Errorf("the EMP order: want applied, got %v", results[0])
	}
	return awaitLoaded(ctx, h, frames, cx, cz+siegeOurMortar, "Shell_EMP", report, "empTick")
}

// awaitLoaded steps the clock until a frame's combat_mortars row for the
// mortar at (x, z) shows shell loaded, within shellLoadTicks.
func awaitLoaded(ctx context.Context, h *na.Harness, frames *combatFrames, x, z int, shell string, report na.Report, key string) error {
	last := ""
	for waited := 0; waited < shellLoadTicks; waited += mirrorStepTicks {
		_, tick, err := Tick(ctx, h, mirrorStepTicks)
		if err != nil {
			return err
		}
		v, err := frames.snapshot(ctx, int64(tick))
		if err != nil {
			return err
		}
		for _, row := range v.GetCombatMortars() {
			if int(row.GetCell().GetX()) == x && int(row.GetCell().GetZ()) == z {
				if last = row.GetLoadedShell(); last == shell {
					report[key] = tick
					return nil
				}
			}
		}
	}
	return fmt.Errorf("mortar at %d,%d never loaded %s within %d ticks (last %q)", x, z, shell, shellLoadTicks, last)
}
