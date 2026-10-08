package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/emp",
		Scope: "EMP ground targeting (#1049), vanilla physics no snapshot can prove: on lab-ranged with rifleman 0 holding EMP grenades and the four raiders " +
			"downed in a clump 11 cells past the sandbags, each wearing a charged shield belt, one combat.orders call drafts the thrower and orders attack_ground " +
			"at the clump's centre; within 600 ticks every raider's shield energy reads 0.",
		Start:       cases.Lab{Colonists: 4},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runEMP,
	})
}

// empKit arms colonist 0 with EMP grenades and stages the raiders downed
// (so they hold still) in a clump within throwing range, shield belts on.
func empKit(f *Fixture, cx, cz int) {
	clump := [][2]int{{-1, 2}, {0, 2}, {1, 2}, {0, 3}}
	h := 0
	for i := range f.Pawns {
		p := &f.Pawns[i]
		switch {
		case p.Side == Colonist && p.Index == 0:
			p.Weapon, p.WeaponStuff = "Weapon_GrenadeEMP", ""
		case p.Side == Hostile:
			p.X, p.Z = cx+clump[h][0], cz+clump[h][1]
			p.Downed, p.Apparel = true, "Apparel_ShieldBelt"
			h++
		}
	}
}

func runEMP(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-ranged", func(f *Fixture, x, z int) { empKit(f, x, z); cx, cz = x, z })
	if err != nil {
		return err
	}
	colonists, hostiles := staged.Colonists(), staged.Hostiles()
	if len(colonists) != 4 || len(hostiles) != 4 {
		return fmt.Errorf("staged %d colonists and %d hostiles, want 4 and 4", len(colonists), len(hostiles))
	}
	before, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	for _, id := range hostiles {
		if e := na.AsNumber(before.pawns[id]["shield"]); e <= 0 {
			return fmt.Errorf("raider %s shield energy %v before the EMP, want charged: %v", id, e, before.pawns[id])
		}
	}
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-emp-acquire", identity)
	if err != nil {
		return err
	}
	pawn := map[string]any{"entityId": colonists[0]}
	results, err := issue(ctx, h, identity, "combat-emp-1", []any{
		map[string]any{"pawn": pawn, "draft": map[string]any{}},
		map[string]any{"pawn": pawn, "attackGround": cell(cx, cz+2)},
	})
	if err != nil {
		return err
	}
	report["results"] = results
	for i, r := range results {
		if applied, _ := na.AsBool(r["applied"]); !applied {
			return fmt.Errorf("order %d refused: %v", i, r)
		}
	}
	var after labState
	for ticked := 0; ticked < 600; ticked += 60 {
		if after, err = tickReadN(ctx, h, 60); err != nil {
			return err
		}
		dropped := 0
		for _, id := range hostiles {
			if v := after.pawns[id]["shield"]; v != nil && na.AsNumber(v) == 0 {
				dropped++
			}
		}
		report["ticks"], report["dropped"] = ticked+60, dropped
		if dropped == len(hostiles) {
			return nil
		}
	}
	return fmt.Errorf("shields still up 600 ticks after the EMP: thrower %v, raiders %v", after.pawns[colonists[0]], hostileRows(after, hostiles))
}

func hostileRows(s labState, ids []string) []map[string]any {
	out := make([]map[string]any, 0, len(ids))
	for _, id := range ids {
		out = append(out, s.pawns[id])
	}
	return out
}
