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
			"a player mortar with one HE shell behind the line and a stone chunk, one call orders an attack on the ship part, a mortar_fire at the part " +
			"with a man_mortar crew, and the same pair on the chunk; the first three apply (AttackStatic, the aim, ManTurret) and the chunk pair refuses not_a_mortar (#1202). One tick later the rifleman " +
			"shoots the part and the crew mans the mortar; then a mortar_fire with no target applies on the mortar (clearing its forced target) and refuses not_a_mortar on the chunk (#1235).",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runMortar,
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
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-mortar-acquire", identity)
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
		map[string]any{"mortarFire": map[string]any{"mortar": mortar, "target": target}},
		map[string]any{"pawn": pawn(colonists[1]), "manMortar": mortar},
		map[string]any{"mortarFire": map[string]any{"mortar": chunk, "target": target}},
		map[string]any{"pawn": pawn(colonists[2]), "manMortar": chunk})
	results, err := issue(ctx, h, identity, "combat-mortar-1", orders)
	if err != nil {
		return err
	}
	report["results"] = results
	for i, r := range results[:3] {
		if applied, _ := na.AsBool(r["applied"]); !applied {
			return fmt.Errorf("draft %d refused: %v", i, r)
		}
	}
	for i, job := range map[int]string{3: "AttackStatic", 5: "ManTurret"} {
		if applied, _ := na.AsBool(results[i]["applied"]); !applied || na.AsString(results[i]["jobDef"]) != job {
			return fmt.Errorf("order %d: want applied with job %s, got %v", i, job, results[i])
		}
	}
	if applied, _ := na.AsBool(results[4]["applied"]); !applied {
		return fmt.Errorf("order 4 (mortar_fire): want applied, got %v", results[4])
	}
	for _, i := range []int{6, 7} {
		if applied, _ := na.AsBool(results[i]["applied"]); applied || na.AsString(results[i]["refusal"]) != "not_a_mortar" {
			return fmt.Errorf("order %d: want refusal not_a_mortar, got %v", i, results[i])
		}
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
	// A mortar_fire with no target clears the forced target (#1235).
	cleared, err := issue(ctx, h, identity, "combat-mortar-2", []any{
		map[string]any{"mortarFire": map[string]any{"mortar": mortar}},
		map[string]any{"mortarFire": map[string]any{"mortar": chunk}}})
	if err != nil {
		return err
	}
	report["cleared"] = cleared
	if applied, _ := na.AsBool(cleared[0]["applied"]); !applied {
		return fmt.Errorf("clear on the mortar: want applied, got %v", cleared[0])
	}
	if applied, _ := na.AsBool(cleared[1]["applied"]); applied || na.AsString(cleared[1]["refusal"]) != "not_a_mortar" {
		return fmt.Errorf("clear on the chunk: want refusal not_a_mortar, got %v", cleared[1])
	}
	return nil
}
