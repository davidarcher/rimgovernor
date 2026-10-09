package layout

// layout/power-sites: the planned power sites against the native
// footprint. On the layout/grid start the first review records a layout
// plan; every PlannedPowerSites battery and solar site is previewed at its
// centre and rotation, and the footprint RimWorld reports must be exactly
// the planned area. The battery room's first BatterySlots row must be the
// one nearest its door.

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func init() {
	cases.Register(cases.Case{
		Name: "layout/power-sites",
		Scope: "Issue #838: on the layout/grid start the recorded layout plan's battery and solar sites (PlannedPowerSites) " +
			"preview natively with exactly the planned footprint, and the battery room's first slot row is the one nearest its door.",
		Start: cases.Fixture{Op: gridPrepare, ArgsFrom: startersite.Args, Args: map[string]any{"sleepingSpots": bunks, "stoneBlocks": blocks},
			On: cases.Save{Name: sustained.BaselineSave}},
		Keep:   []string{string(na.NeedFood)},
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Shelter, routinefamily.Expansion}, NativeTimeout: 30 * time.Second, Prefix: "layout-power"},
		Budget: 3 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Reason: "one short watch until the first review records a layout plan, then native previews",
		Run: powerSites,
	})
}

func powerSites(ctx context.Context, s cases.Session) error {
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: time.Minute, Extra: []policy.ConcernID{policy.MaintainHousing}, Until: func(sample map[string]any) bool {
			for _, goal := range []policy.ConcernID{policy.MaintainHousing} {
				capacity, _ := sample[string(goal)].(map[string]any)
				if planned(capacity, buildingruntime.IsShellMethod, false) {
					return true
				}
			}
			return false
		}},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			review, err := journal.LoadRounds(ctx)
			if err != nil {
				return err
			}
			layout, laid, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick)
			if err != nil {
				return err
			}
			if !laid {
				return fmt.Errorf("no layout plan recorded by tick %d", review.Tick)
			}
			identity := s.Identity()
			var checked []map[string]any
			for _, def := range []string{policy.BatteryDefinition, policy.SolarDefinition} {
				sites := policy.PlannedPowerSites(layout.Plan, def)
				if len(sites) == 0 {
					return fmt.Errorf("the layout plan has no %s site", def)
				}
				for i, site := range sites {
					placement := map[string]any{"defName": def, "x": site.Cell.X, "z": site.Cell.Z, "rotation": "ROTATION_" + rotationName(site.Rotation)}
					reply, err := h.Wire(ctx, fmt.Sprintf("preview-%s-%d", def, i), "placement_preview", map[string]any{
						"identity": identity, "placements": []any{placement},
					})
					if err != nil {
						return err
					}
					batch, _ := na.AsMap(reply["batch"])
					var evaluation map[string]any
					if results := na.AsSlice(batch["results"]); len(results) == 1 {
						result, _ := na.AsMap(results[0])
						evaluation, _ = na.AsMap(result["evaluated"])
					}
					rotations := na.AsSlice(evaluation["rotations"])
					if len(rotations) != 1 {
						return fmt.Errorf("%s site %d: expected one evaluated rotation, got %#v", def, i, reply)
					}
					row, _ := na.AsMap(rotations[0])
					got := map[domain.Cell]bool{}
					for _, raw := range na.AsSlice(row["occupiedCells"]) {
						c, _ := na.AsMap(raw)
						got[domain.Cell{X: int32(na.AsNumber(c["x"])), Z: int32(na.AsNumber(c["z"]))}] = true
					}
					want := site.Area
					match := len(got) == int(want.Width*want.Height)
					for x := want.X; match && x < want.X+want.Width; x++ {
						for z := want.Z; z < want.Z+want.Height; z++ {
							match = match && got[domain.Cell{X: x, Z: z}]
						}
					}
					checked = append(checked, map[string]any{"def": def, "cell": site.Cell, "rotation": string(site.Rotation), "area": want, "occupied": len(got), "match": match})
					if !match {
						return fmt.Errorf("%s site %d at %v %s: native footprint %v is not the planned area %+v", def, i, site.Cell, site.Rotation, keys(got), want)
					}
				}
			}
			report["sites"] = checked
			for _, room := range layout.Plan.AllRooms() {
				if room.Role != policy.PlannedBattery {
					continue
				}
				slots := policy.BatterySlots(room)
				// A room on a crossing (east or west door) steps its rows along x.
				dist := func(r policy.Rectangle) int32 { return max(r.Z-room.Door.Z, room.Door.Z-r.Z) }
				if room.DoorRot == domain.East || room.DoorRot == domain.West {
					dist = func(r policy.Rectangle) int32 { return max(r.X-room.Door.X, room.Door.X-r.X) }
				}
				report["battery_room"] = map[string]any{"door": room.Door, "door_rot": string(room.DoorRot), "interior": room.Interior, "first_slot": slots[0], "last_slot": slots[len(slots)-1]}
				if dist(slots[0]) > dist(slots[len(slots)-1]) {
					return fmt.Errorf("battery room door %v (%s): first slot %+v is farther from the door than the last %+v", room.Door, room.DoorRot, slots[0], slots[len(slots)-1])
				}
			}
			return nil
		},
	})
	return err
}

func rotationName(r domain.Rotation) string {
	switch r {
	case domain.East:
		return "EAST"
	case domain.South:
		return "SOUTH"
	case domain.West:
		return "WEST"
	}
	return "NORTH"
}

func keys(m map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(m))
	for c := range m {
		out = append(out, c)
	}
	return out
}
