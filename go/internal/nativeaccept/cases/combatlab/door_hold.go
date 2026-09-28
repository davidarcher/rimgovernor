package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The door-hold room (#1155): a granite ring around the lab-manhunter
// colonist and huskies, one door in its north wall facing the wargs.
const (
	doorHoldHalfX = 3
	doorHoldHalfZ = 2
	// doorHoldTicks is how long the forbidden door must hold, in steps of
	// doorHoldStep; the wargs reach the room inside it.
	doorHoldTicks = 600
	doorHoldStep  = 60
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/door-hold",
		Scope: "A forbidden door holds colony animals (#1155, epic #929): lab-manhunter with its colonist and both huskies walled in a granite room whose one door faces the wargs; " +
			"one combat.orders call forbids the door and zones both huskies onto a cell outside it (both applied). For 600 ticks, while the wargs are on the map, " +
			"both huskies stay inside the room and the door reads forbidden; a second call allows the door and a husky then walks out to its area within 600 ticks.",
		Start:       cases.Lab{Colonists: 1},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runDoorHold,
	})
}

// doorHoldKit walls the fixture centre in and puts the door at (cx, cz+halfZ).
func doorHoldKit(f *Fixture, cx, cz int) {
	for x := cx - doorHoldHalfX; x <= cx+doorHoldHalfX; x++ {
		for z := cz - doorHoldHalfZ; z <= cz+doorHoldHalfZ; z++ {
			edge := x == cx-doorHoldHalfX || x == cx+doorHoldHalfX || z == cz-doorHoldHalfZ || z == cz+doorHoldHalfZ
			switch {
			case x == cx && z == cz+doorHoldHalfZ:
				f.Things = append(f.Things, Thing{Def: "Door", Stuff: "Steel", X: x, Z: z})
			case edge:
				f.Things = append(f.Things, Thing{Def: "Wall", Stuff: "BlocksGranite", X: x, Z: z})
			}
		}
	}
}

func runDoorHold(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-manhunter", func(f *Fixture, x, z int) { doorHoldKit(f, x, z); cx, cz = x, z })
	if err != nil {
		return err
	}
	animals, wargs := staged.Animals(), staged.Manhunters()
	if len(animals) != 2 || len(wargs) != 2 {
		return fmt.Errorf("staged %d animals and %d manhunters, want 2 and 2", len(animals), len(wargs))
	}
	inside := func(p map[string]any) bool {
		x, z := int(na.AsNumber(p["x"])), int(na.AsNumber(p["z"]))
		return x > cx-doorHoldHalfX && x < cx+doorHoldHalfX && z > cz-doorHoldHalfZ && z < cz+doorHoldHalfZ
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "combat-door-hold-acquire", identity); err != nil {
		return err
	}
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	door, outside := cell(cx, cz+doorHoldHalfZ), cell(cx, cz+doorHoldHalfZ+3)
	orders := []any{map[string]any{"door": map[string]any{"cell": door, "mode": "COMBAT_DOOR_MODE_FORBID"}}}
	for _, a := range animals {
		orders = append(orders, map[string]any{"pawn": pawn(a), "animalArea": map[string]any{"cell": outside}})
	}
	results, err := issue(ctx, h, identity, "combat-door-hold-1", orders)
	if err != nil {
		return err
	}
	report["results"] = results
	for i, r := range results {
		if applied, _ := na.AsBool(r["applied"]); !applied {
			return fmt.Errorf("order %d refused: %v", i, r)
		}
	}
	for t := doorHoldStep; t <= doorHoldTicks; t += doorHoldStep {
		st, err := tickReadN(ctx, h, doorHoldStep)
		if err != nil {
			return err
		}
		if forbidden, err := doorForbidden(st, cx, cz+doorHoldHalfZ); err != nil || !forbidden {
			return fmt.Errorf("tick +%d: door forbidden %v (%v)", t, forbidden, err)
		}
		present := 0
		for _, w := range wargs {
			if p, ok := st.pawns[w]; ok {
				if dead, _ := na.AsBool(p["dead"]); dead {
					continue
				}
				present++
			}
		}
		if present == 0 {
			return fmt.Errorf("tick +%d: no warg left on the map, the hold proves nothing", t)
		}
		for _, a := range animals {
			if p := st.pawns[a]; !inside(p) {
				return fmt.Errorf("tick +%d: husky %s left the room through the forbidden door: %v", t, a, p)
			}
		}
		report["held"] = t
	}
	allowed, err := issue(ctx, h, identity, "combat-door-hold-2", []any{
		map[string]any{"door": map[string]any{"cell": door, "mode": "COMBAT_DOOR_MODE_ALLOW"}},
	})
	if err != nil {
		return err
	}
	if applied, _ := na.AsBool(allowed[0]["applied"]); !applied {
		return fmt.Errorf("door allow refused: %v", allowed[0])
	}
	// The control: with the door allowed the zoned huskies leave, so the
	// hold was the door's.
	for t := doorHoldStep; t <= doorHoldTicks; t += doorHoldStep {
		st, err := tickReadN(ctx, h, doorHoldStep)
		if err != nil {
			return err
		}
		for _, a := range animals {
			if p, ok := st.pawns[a]; ok && !inside(p) {
				report["leftAfterAllow"] = map[string]any{"pawn": a, "ticks": t}
				return nil
			}
		}
	}
	return fmt.Errorf("no husky left the room within %d ticks of the door being allowed", doorHoldTicks)
}
