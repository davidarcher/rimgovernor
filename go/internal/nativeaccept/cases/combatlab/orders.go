package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/orders",
		Scope: "Batched combat.orders (#850), a native op contract no snapshot can prove: on lab-open (five colonists, one with frag grenades, a player door) one call " +
			"orders attack_ground, move, attack, fire_mode hold, hold_position, move-then-stop and door hold_open, plus an unowned hostile and a non-door cell; " +
			"every order applies or refuses with its named reason, and one tick later each pawn's job, target, fire mode and the door read back as ordered. A second call closes the door.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runOrders,
	})
}

// ordersKit adds what the seven orders need to lab-open: two more riflemen
// (hold and stop), frag grenades on colonist 0 (attack_ground) and a door
// behind the line.
func ordersKit(f *Fixture, cx, cz int) {
	f.Colonists = 5
	for i := range f.Pawns {
		if f.Pawns[i].Side == Colonist && f.Pawns[i].Index == 0 {
			f.Pawns[i].Weapon, f.Pawns[i].WeaponStuff = "Weapon_GrenadeFrag", ""
		}
	}
	f.Pawns = append(f.Pawns,
		Pawn{Side: Colonist, Index: 3, X: cx - 5, Z: cz - 10, Weapon: rifle},
		Pawn{Side: Colonist, Index: 4, X: cx + 5, Z: cz - 10, Weapon: rifle})
	f.Things = append(f.Things, Thing{Def: "Door", Stuff: "WoodLog", X: cx, Z: cz - 13})
}

func cell(x, z int) map[string]any { return map[string]any{"x": x, "z": z} }

func runOrders(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-open", func(f *Fixture, x, z int) { ordersKit(f, x, z); cx, cz = x, z })
	if err != nil {
		return err
	}
	colonists, hostiles := staged.Colonists(), staged.Hostiles()
	if len(colonists) != 5 || len(hostiles) != 3 {
		return fmt.Errorf("staged %d colonists and %d hostiles, want 5 and 3", len(colonists), len(hostiles))
	}
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-orders-acquire", identity)
	if err != nil {
		return err
	}
	if err := draftAll(ctx, h, identity, "draft", colonists); err != nil {
		return err
	}
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	ground, moveTo, stopFrom, door := cell(cx-2, cz-3), cell(cx+7, cz-12), cell(cx+5, cz-15), cell(cx, cz-13)
	orders := []any{
		map[string]any{"pawn": pawn(colonists[0]), "attackGround": ground},
		map[string]any{"pawn": pawn(colonists[1]), "move": moveTo},
		map[string]any{"pawn": pawn(colonists[2]), "attack": pawn(hostiles[2])},
		map[string]any{"pawn": pawn(colonists[3]), "fireMode": "COMBAT_FIRE_MODE_HOLD"},
		map[string]any{"pawn": pawn(colonists[3]), "holdPosition": map[string]any{}},
		map[string]any{"pawn": pawn(colonists[4]), "move": stopFrom},
		map[string]any{"pawn": pawn(colonists[4]), "stop": map[string]any{}},
		map[string]any{"door": map[string]any{"cell": door, "mode": "COMBAT_DOOR_MODE_HOLD_OPEN"}},
		map[string]any{"pawn": pawn(hostiles[0]), "move": moveTo},
		map[string]any{"door": map[string]any{"cell": cell(cx+10, cz), "mode": "COMBAT_DOOR_MODE_CLOSE"}},
	}
	wantJob := []string{"AttackStatic", "Goto", "AttackStatic", "", "Wait_Combat", "Goto", "", "", "", ""}
	wantRefusal := map[int]string{8: "not_drafted", 9: "not_a_door"}
	results, err := issue(ctx, h, identity, "combat-orders-1", orders)
	if err != nil {
		return err
	}
	report["results"] = results
	for i, r := range results {
		applied, _ := na.AsBool(r["applied"])
		if want, refused := wantRefusal[i]; refused {
			if applied || na.AsString(r["refusal"]) != want {
				return fmt.Errorf("order %d: want refusal %s, got %v", i, want, r)
			}
			continue
		}
		if !applied || na.AsString(r["jobDef"]) != wantJob[i] {
			return fmt.Errorf("order %d: want applied with job %q, got %v", i, wantJob[i], r)
		}
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	report["after"] = after
	check := func(i int, job string, forced bool, target any) error {
		p := after.pawns[colonists[i]]
		if na.AsString(p["job"]) != job {
			return fmt.Errorf("colonist %d job %v, want %s: %v", i, p["job"], job, p)
		}
		if f, _ := na.AsBool(p["playerForced"]); f != forced {
			return fmt.Errorf("colonist %d playerForced %v, want %v: %v", i, f, forced, p)
		}
		switch t := target.(type) {
		case string:
			if na.AsString(p["jobThing"]) != t {
				return fmt.Errorf("colonist %d targets %v, want %s", i, p["jobThing"], t)
			}
		case map[string]any:
			got, _ := na.AsMap(p["jobCell"])
			if na.AsNumber(got["x"]) != float64(t["x"].(int)) || na.AsNumber(got["z"]) != float64(t["z"].(int)) {
				return fmt.Errorf("colonist %d targets cell %v, want %v", i, got, t)
			}
		}
		return nil
	}
	for _, err := range []error{
		check(0, "AttackStatic", true, ground),
		check(1, "Goto", true, moveTo),
		check(2, "AttackStatic", true, hostiles[2]),
		check(3, "Wait_Combat", true, nil),
	} {
		if err != nil {
			return err
		}
	}
	if fire, ok := na.AsBool(after.pawns[colonists[3]]["fireAtWill"]); !ok || fire {
		return fmt.Errorf("colonist 3 fire at will %v after hold", after.pawns[colonists[3]]["fireAtWill"])
	}
	if p := after.pawns[colonists[4]]; na.AsString(p["job"]) == "Goto" {
		return fmt.Errorf("colonist 4 still moving after stop: %v", p)
	} else if forced, _ := na.AsBool(p["playerForced"]); forced {
		return fmt.Errorf("colonist 4 holds a player-forced job after stop: %v", p)
	}
	if open, err := after.door(cx, cz-13); err != nil || !open {
		return fmt.Errorf("door hold-open %v after hold_open (%v)", open, err)
	}
	if open, err := after.physicalDoor(cx, cz-13); err != nil || open {
		return fmt.Errorf("latch alone opened door: %v (%v)", open, err)
	}
	if _, err := issue(ctx, h, identity, "door-passage", []any{map[string]any{"pawn": pawn(colonists[4]), "move": door}}); err != nil {
		return err
	}
	opened := false
	for ticks := 0; ticks < 1000; ticks += 20 {
		after, err = tickReadN(ctx, h, 20)
		if err != nil {
			return err
		}
		opened, err = after.physicalDoor(cx, cz-13)
		if err != nil {
			return err
		}
		if opened {
			break
		}
	}
	if !opened {
		return fmt.Errorf("ordinary pawn passage did not open held door")
	}
	if _, err := issue(ctx, h, identity, "door-clearance", []any{map[string]any{"pawn": pawn(colonists[4]), "move": cell(cx+5, cz-10)}}); err != nil {
		return err
	}
	if _, err := tickReadN(ctx, h, 600); err != nil {
		return err
	}
	closed, err := issue(ctx, h, identity, "combat-orders-2", []any{
		map[string]any{"door": map[string]any{"cell": door, "mode": "COMBAT_DOOR_MODE_CLOSE"}},
	})
	if err != nil {
		return err
	}
	if applied, _ := na.AsBool(closed[0]["applied"]); !applied {
		return fmt.Errorf("door close refused: %v", closed[0])
	}
	after, err = tickReadN(ctx, h, 180)
	if err != nil {
		return err
	}
	if open, err := after.door(cx, cz-13); err != nil || open {
		return fmt.Errorf("door hold-open %v after close (%v)", open, err)
	}
	if open, err := after.physicalDoor(cx, cz-13); err != nil || open {
		return fmt.Errorf("door still physically open after clearance and close: %v (%v)", open, err)
	}
	report["doorClosed"] = true
	return nil
}

// issue sends one combat_orders batch through Actions/Apply (#939) and
// returns its per-order results.
func issue(ctx context.Context, h *na.Harness, identity map[string]any, action string, orders []any) ([]map[string]any, error) {
	return na.ApplyCombatOrders(ctx, h, action, identity, action, orders)
}

// draftAll drafts every pawn through a DraftIntent (#939).
func draftAll(ctx context.Context, h *na.Harness, identity map[string]any, label string, pawns []string) error {
	for i, id := range pawns {
		if _, err := na.ApplyDraft(ctx, h, fmt.Sprintf("%s-%d", label, i), identity, fmt.Sprintf("%s-%s", label, id), id, true); err != nil {
			return fmt.Errorf("draft %s: %w", id, err)
		}
	}
	return nil
}

type labState struct {
	pawns map[string]map[string]any
	doors []any
}

func (s labState) door(x, z int) (bool, error) {
	return s.doorField(x, z, "holdOpen")
}

func (s labState) physicalDoor(x, z int) (bool, error) {
	return s.doorField(x, z, "open")
}

func (s labState) doorField(x, z int, field string) (bool, error) {
	for _, d := range s.doors {
		row, _ := na.AsMap(d)
		if int(na.AsNumber(row["x"])) == x && int(na.AsNumber(row["z"])) == z {
			open, known := na.AsBool(row[field])
			if !known {
				return false, fmt.Errorf("door %s unknown: %v", field, row)
			}
			return open, nil
		}
	}
	return false, fmt.Errorf("no door at %d,%d in %v", x, z, s.doors)
}

// tickRead runs one game tick on the paused lab and reads every pawn's job
// and fire mode and every door's hold-open.
func tickRead(ctx context.Context, h *na.Harness) (labState, error) { return tickReadN(ctx, h, 1) }

// tickReadN is tickRead over n ticks.
func tickReadN(ctx context.Context, h *na.Harness, n int) (labState, error) {
	reply, err := h.Call(ctx, "combat-orders-tick", StageTool, map[string]any{"action": "tick", "ticks": n})
	if err != nil {
		return labState{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return labState{}, fmt.Errorf("%s tick refused: %v", StageTool, reply)
	}
	out := labState{pawns: map[string]map[string]any{}, doors: na.AsSlice(reply["doors"])}
	for _, r := range na.AsSlice(reply["pawns"]) {
		row, _ := na.AsMap(r)
		out.pawns[na.AsString(row["id"])] = row
	}
	return out, nil
}
