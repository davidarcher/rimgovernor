// Package lab holds the lab contract runner's cases: each opens on
// the blank Soil lab (cases.Lab), spawns what it needs with na.LabSpawn and
// makes one op or read assertion in 10-30 s.
package lab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:        "lab/spawn",
		Scope:       "Lab runner: test/lab_start hands the case a blank lab with its colonists, and a building and a pawn spawned by test/lab_spawn read back through the typed observations.",
		Start:       cases.Lab{Colonists: 2},
		RequiredOps: []string{na.LabSpawnTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
			prepared := s.Prepared()
			if n := len(na.AsSlice(prepared["colonists"])); n != 2 {
				return fmt.Errorf("lab start: want 2 colonists, got %d: %#v", n, prepared)
			}
			center, _ := na.AsMap(prepared["center"])
			cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
			h := s.Harness()
			wall, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: "Wall", Stuff: "WoodLog", X: cx, Z: cz + 4})
			if err != nil {
				return err
			}
			animal, pawn, err := na.LabSpawn(ctx, h, na.LabThing{Def: "Muffalo", X: cx + 4, Z: cz, Unowned: true})
			if err != nil {
				return err
			}
			if na.AsString(pawn["kind"]) != "pawn" {
				return fmt.Errorf("lab_spawn Muffalo: kind %v, not a pawn: %#v", pawn["kind"], pawn)
			}
			s.Report()["spawned"] = map[string]any{"wall": wall, "animal": animal}
			reply, err := h.Wire(ctx, "wall", "observations_list_buildings", map[string]any{
				"scope": map[string]any{"expectedIdentity": s.Identity()}, "ids": []string{wall},
			})
			if err != nil {
				return err
			}
			_, observed, err := na.Outcome(reply, "observed")
			if err != nil {
				return err
			}
			rows := na.AsSlice(observed["buildings"])
			if len(rows) != 1 {
				return fmt.Errorf("list_buildings %s: want one row, got %#v", wall, observed)
			}
			row, _ := na.AsMap(rows[0])
			if na.AsString(row["stuff"]) != "WoodLog" {
				return fmt.Errorf("wall %s: want WoodLog stuff, got %#v", wall, row)
			}
			s.Report()["wall"] = row
			return nil
		},
	})
}
