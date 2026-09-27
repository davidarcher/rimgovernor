package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/mortar",
		Scope: "combat.orders on buildings (#930, #931), native op contracts no snapshot can prove: on lab-open plus a hostile crashed ship part 30 cells north, " +
			"a player mortar with one HE shell behind the line and a stone chunk, one call orders an attack on the ship part, a mortar order at the part, " +
			"and a mortar order on the chunk; the first two apply (AttackStatic, ManTurret) and the third refuses not_a_mortar. One tick later the rifleman " +
			"shoots the part and the crew mans the mortar.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runMortar,
	})
}

func runMortar(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-open", func(f *Fixture, x, z int) {
		cx, cz = x, z
		f.Things = append(f.Things,
			Thing{Def: "DefoliatorShipPart", X: x, Z: z + 20, Hostile: true},
			Thing{Def: "Turret_Mortar", X: x, Z: z - 15},
			Thing{Def: "Shell_HighExplosive", X: x + 2, Z: z - 16})
	})
	if err != nil {
		return err
	}
	colonists, things := staged.Colonists(), staged.Things
	part := things[len(things)-3]
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "combat-mortar-acquire", identity)
	if err != nil {
		return err
	}
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	var orders []any
	for _, id := range colonists[:3] {
		orders = append(orders, map[string]any{"pawn": pawn(id), "draft": map[string]any{}})
	}
	mortar, chunk, target := cell(cx, cz-15), cell(cx-3, cz-9), cell(cx, cz+20)
	orders = append(orders,
		map[string]any{"pawn": pawn(colonists[0]), "attack": pawn(part)},
		map[string]any{"pawn": pawn(colonists[1]), "mortar": map[string]any{"mortar": mortar, "target": target}},
		map[string]any{"pawn": pawn(colonists[2]), "mortar": map[string]any{"mortar": chunk, "target": target}})
	results, err := issue(ctx, h, identity, grant, "combat-mortar-1", orders)
	if err != nil {
		return err
	}
	report["results"] = results
	for i, r := range results[:3] {
		if applied, _ := na.AsBool(r["applied"]); !applied {
			return fmt.Errorf("draft %d refused: %v", i, r)
		}
	}
	for i, job := range map[int]string{3: "AttackStatic", 4: "ManTurret"} {
		if applied, _ := na.AsBool(results[i]["applied"]); !applied || na.AsString(results[i]["jobDef"]) != job {
			return fmt.Errorf("order %d: want applied with job %s, got %v", i, job, results[i])
		}
	}
	if applied, _ := na.AsBool(results[5]["applied"]); applied || na.AsString(results[5]["refusal"]) != "not_a_mortar" {
		return fmt.Errorf("order 5: want refusal not_a_mortar, got %v", results[5])
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	report["after"] = after.pawns
	if p := after.pawns[colonists[0]]; na.AsString(p["job"]) != "AttackStatic" || na.AsString(p["jobThing"]) != part {
		return fmt.Errorf("rifleman does not shoot the ship part: %v", p)
	}
	if p := after.pawns[colonists[1]]; na.AsString(p["job"]) != "ManTurret" || na.AsString(p["jobThing"]) != things[len(things)-2] {
		return fmt.Errorf("crew does not man the mortar: %v", p)
	}
	return nil
}
