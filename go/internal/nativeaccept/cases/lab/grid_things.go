package lab

import (
	"context"
	"encoding/base64"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func init() {
	cases.Register(cases.Case{
		Name: "lab/grid-things",
		Scope: "Native thing read (#2261): test/grid_things spawns an item, wall, wall blueprint, wall frame, plant, " +
			"filth and animal corpse on seven cells and replies that row as a native-encoded keyframe; the Go " +
			"decoder must read each category, flag, faction and state as the native side meant it, and the tile " +
			"columns must be known. Reports the whole-map read and encode cost. A Go snapshot test cannot see " +
			"which flags and states the native encoder writes for real game things.",
		Start:       cases.Lab{Colonists: 1},
		RequiredOps: []string{"test/grid_things"},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
			center, _ := na.AsMap(s.Prepared()["center"])
			cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
			reply, err := s.Harness().Call(ctx, "grid-things", "test/grid_things", map[string]any{"cell": fmt.Sprintf("%d,%d", cx+2, cz+9)})
			if err != nil {
				return err
			}
			if ok, _ := na.AsBool(reply["success"]); !ok {
				return fmt.Errorf("grid_things: %#v", reply)
			}
			s.Report()["grid_things"] = map[string]any{"readMs": reply["readMs"], "encodeMs": reply["encodeMs"], "keyframeBytes": reply["keyframeBytes"]}
			raw, err := base64.StdEncoding.DecodeString(na.AsString(reply["grid"]))
			if err != nil {
				return err
			}
			wire := &mp.CellGrid{}
			if err := proto.Unmarshal(raw, wire); err != nil {
				return err
			}
			if !cellgrid.Complete(wire) {
				return fmt.Errorf("grid_things: the native keyframe is not complete")
			}
			grid, err := cellgrid.Apply(nil, true, wire)
			if err != nil {
				return err
			}
			cells := grid.Cells()
			if len(cells) != 7 {
				return fmt.Errorf("grid_things: %d held cells, want 7", len(cells))
			}
			for i, cell := range cells {
				if terrain, ok := cell.Terrain.Value(); !ok || terrain == "" {
					return fmt.Errorf("grid_things: cell %d terrain unknown", i)
				}
				if _, ok := cell.InHome.Value(); !ok {
					return fmt.Errorf("grid_things: cell %d in_home unknown", i)
				}
				if aff, ok := cell.BaseTerrain.Value(); !ok || aff == "" {
					return fmt.Errorf("grid_things: cell %d base terrain unknown", i)
				}
				if _, ok := cell.SnowDepth.Value(); !ok {
					return fmt.Errorf("grid_things: cell %d snow depth unknown", i)
				}
				if _, ok := cell.TopLayerRemovable.Value(); !ok {
					return fmt.Errorf("grid_things: cell %d top layer removable unknown", i)
				}
			}
			find := func(i int, cat policy.ThingCategory) (policy.Thing, error) {
				for _, t := range cells[i].Things {
					if t.Category == cat {
						return t, nil
					}
				}
				return policy.Thing{}, fmt.Errorf("grid_things: cell %d lists no thing of category %d: %+v", i, cat, cells[i].Things)
			}
			steel, err := find(0, policy.ThingItem)
			if err != nil {
				return err
			}
			if steel.Def != "Steel" || steel.Count != 25 || !steel.Has(policy.FlagHaulable) || steel.Has(policy.FlagEdifice) {
				return fmt.Errorf("grid_things: steel = %+v", steel)
			}
			wall, err := find(1, policy.ThingBuilding)
			if err != nil {
				return err
			}
			if wall.Def != "Wall" || wall.Faction != policy.FactionPlayer || !wall.Has(policy.FlagEdifice|policy.FlagImpassable|policy.FlagHoldsRoof|policy.FlagDeconstructible) ||
				wall.Has(policy.FlagBlueprint|policy.FlagFrame) || wall.Building == nil || wall.Building.HitPoints == 0 || wall.Building.Burning {
				return fmt.Errorf("grid_things: wall = %+v", wall)
			}
			blueprint, err := find(2, policy.ThingBuilding)
			if err != nil {
				return err
			}
			if blueprint.Def != "Wall" || !blueprint.Has(policy.FlagBlueprint) || blueprint.Has(policy.FlagFrame) || blueprint.Faction != policy.FactionPlayer ||
				blueprint.Building == nil || len(blueprint.Building.Needed) == 0 {
				return fmt.Errorf("grid_things: blueprint = %+v", blueprint)
			}
			frame, err := find(3, policy.ThingBuilding)
			if err != nil {
				return err
			}
			if frame.Def != "Wall" || !frame.Has(policy.FlagFrame) || frame.Has(policy.FlagBlueprint) || frame.Building == nil || len(frame.Building.Needed) == 0 ||
				uint64(frame.ID) != uint64(na.AsNumber(reply["frameId"])) {
				return fmt.Errorf("grid_things: frame = %+v", frame)
			}
			plant, err := find(4, policy.ThingPlant)
			if err != nil {
				return err
			}
			if plant.Def != "Plant_Potato" || plant.Plant.Growth < 0.45 || plant.Plant.Growth > 0.55 {
				return fmt.Errorf("grid_things: plant = %+v", plant)
			}
			filth, err := find(5, policy.ThingFilth)
			if err != nil {
				return err
			}
			if !strings.HasPrefix(filth.Def, "Filth_") || filth.FilthThickness == 0 {
				return fmt.Errorf("grid_things: filth = %+v", filth)
			}
			corpse, err := find(6, policy.ThingCorpse)
			if err != nil {
				return err
			}
			if corpse.Corpse.Class != policy.CorpseAnimal || corpse.Corpse.Rot > 0.1 {
				return fmt.Errorf("grid_things: corpse = %+v", corpse)
			}
			return nil
		},
	})
}
