package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/animals",
		Scope: "combat.orders animal orders (#1057), native op contracts no snapshot can prove: on lab-manhunter one call releases the trained husky " +
			"at a warg (applied, AttackMelee), releases the untrained husky (untrained), zones a warg (not_ours) and zones the untrained husky onto one " +
			"cell (applied). One tick later the trained husky attacks the warg and the other stands restricted to a one-cell area; a clear order then " +
			"restores it to no area.",
		Start:       cases.Lab{Colonists: 1},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runAnimals,
	})
}

func runAnimals(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-manhunter", func(_ *Fixture, x, z int) { cx, cz = x, z })
	if err != nil {
		return err
	}
	animals, wargs := staged.Animals(), staged.Manhunters()
	if len(animals) != 2 || len(wargs) != 2 {
		return fmt.Errorf("staged %d animals and %d manhunters, want 2 and 2", len(animals), len(wargs))
	}
	trained, pup := animals[0], animals[1]
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-animals-acquire", identity)
	if err != nil {
		return err
	}
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	zone := cell(cx-1, cz-3)
	results, err := issue(ctx, h, identity, "combat-animals-1", []any{
		map[string]any{"pawn": pawn(trained), "release": pawn(wargs[0])},
		map[string]any{"pawn": pawn(pup), "release": pawn(wargs[0])},
		map[string]any{"pawn": pawn(wargs[1]), "animalArea": map[string]any{"cell": zone}},
		map[string]any{"pawn": pawn(pup), "animalArea": map[string]any{"cell": zone}},
	})
	if err != nil {
		return err
	}
	report["results"] = results
	if applied, _ := na.AsBool(results[0]["applied"]); !applied || na.AsString(results[0]["jobDef"]) != "AttackMelee" {
		return fmt.Errorf("release: want applied with AttackMelee, got %v", results[0])
	}
	for i, want := range map[int]string{1: "untrained", 2: "not_ours"} {
		if applied, _ := na.AsBool(results[i]["applied"]); applied || na.AsString(results[i]["refusal"]) != want {
			return fmt.Errorf("order %d: want refusal %s, got %v", i, want, results[i])
		}
	}
	if applied, _ := na.AsBool(results[3]["applied"]); !applied {
		return fmt.Errorf("animal_area: want applied, got %v", results[3])
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	report["after"] = after.pawns
	if p := after.pawns[trained]; na.AsString(p["job"]) != "AttackMelee" || na.AsString(p["jobThing"]) != wargs[0] {
		return fmt.Errorf("released husky does not attack the warg: %v", p)
	}
	if p := after.pawns[pup]; na.AsString(p["area"]) != "RimGovernor "+pup || int(na.AsNumber(p["areaCells"])) != 1 {
		return fmt.Errorf("zoned husky not restricted to one cell: %v", p)
	}
	results, err = issue(ctx, h, identity, "combat-animals-2", []any{
		map[string]any{"pawn": pawn(pup), "animalArea": map[string]any{"clear": map[string]any{}}},
	})
	if err != nil {
		return err
	}
	report["clear"] = results
	if applied, _ := na.AsBool(results[0]["applied"]); !applied {
		return fmt.Errorf("animal_area clear: want applied, got %v", results[0])
	}
	cleared, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	if p := cleared.pawns[pup]; na.AsString(p["area"]) != "" {
		return fmt.Errorf("cleared husky still restricted: %v", p)
	}
	return nil
}
