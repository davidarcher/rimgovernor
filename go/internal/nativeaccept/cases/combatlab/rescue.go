package combatlab

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/rescue",
		Scope: "Rescue under fire (#867), a native op contract no snapshot can prove: on lab-open with colonist 2 downed, a bed and a player door behind the line, " +
			"combat.geometry's rescue_path role returns colonist 0's pathfinder route to the downed colonist, every cell scored with a hostile line-of-fire flag that agrees with its lines, and a second (warm) read within the 50 ms main-thread budget (#881); " +
			"one combat.orders call forbids the door, orders colonist 0 to rescue colonist 2 and refuses colonist 1's rescue of a standing raider (cannot_rescue); " +
			"one tick later colonist 0's job is Rescue on colonist 2 and the door reads forbidden; a second call allows it again.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runRescue,
	})
}

// rescueKit downs colonist 2 in place and adds a bed and a door behind
// the line.
func rescueKit(f *Fixture, cx, cz int) {
	for i := range f.Pawns {
		if f.Pawns[i].Side == Colonist && f.Pawns[i].Index == 2 {
			// A downed pawn drops its weapon: stage it unarmed.
			f.Pawns[i].Downed, f.Pawns[i].Weapon, f.Pawns[i].WeaponStuff = true, "", ""
		}
	}
	f.Things = append(f.Things, Thing{Def: "Bed", Stuff: "WoodLog", X: cx - 6, Z: cz - 16}, Thing{Def: "Door", Stuff: "WoodLog", X: cx, Z: cz - 13})
}

func runRescue(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-open", func(f *Fixture, x, z int) { rescueKit(f, x, z); cx, cz = x, z })
	if err != nil {
		return err
	}
	colonists, hostiles := staged.Colonists(), staged.Hostiles()
	if len(colonists) != 3 || len(hostiles) != 3 {
		return fmt.Errorf("staged %d colonists and %d hostiles, want 3 and 3", len(colonists), len(hostiles))
	}
	var downed Pawn
	for _, p := range staged.Fixture.Pawns {
		if p.Side == Colonist && p.Index == 2 {
			downed = p
		}
	}
	if err := rescuePath(ctx, h, s, colonists[0], downed, hostiles, report); err != nil {
		return err
	}
	_, err = na.GrantAuto(ctx, h.WireFunc(), "combat-rescue-acquire", s.Identity())
	if err != nil {
		return err
	}
	if err := draftAll(ctx, h, s.Identity(), "rescue-draft", colonists[:2]); err != nil {
		return err
	}
	pawn := func(id string) map[string]any { return map[string]any{"entityId": id} }
	door := cell(cx, cz-13)
	results, err := issue(ctx, h, s.Identity(), "combat-rescue-1", []any{
		map[string]any{"door": map[string]any{"cell": door, "mode": "COMBAT_DOOR_MODE_FORBID"}},
		map[string]any{"pawn": pawn(colonists[0]), "rescue": map[string]any{"downed": pawn(colonists[2])}},
		map[string]any{"pawn": pawn(colonists[1]), "rescue": map[string]any{"downed": pawn(hostiles[0])}},
	})
	if err != nil {
		return err
	}
	report["results"] = results
	if applied, _ := na.AsBool(results[0]["applied"]); !applied {
		return fmt.Errorf("door forbid refused: %v", results[0])
	}
	if applied, _ := na.AsBool(results[1]["applied"]); !applied || na.AsString(results[1]["jobDef"]) != "Rescue" {
		return fmt.Errorf("rescue not applied with job Rescue: %v", results[1])
	}
	if applied, _ := na.AsBool(results[2]["applied"]); applied || na.AsString(results[2]["refusal"]) != bridge.CombatRefusalCannotRescue {
		return fmt.Errorf("rescue of a standing raider: want %s, got %v", bridge.CombatRefusalCannotRescue, results[2])
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	if p := after.pawns[colonists[0]]; na.AsString(p["job"]) != "Rescue" || na.AsString(p["jobThing"]) != colonists[2] {
		return fmt.Errorf("colonist 0 after rescue: %v", p)
	}
	if forbidden, err := doorForbidden(after, cx, cz-13); err != nil || !forbidden {
		return fmt.Errorf("door forbidden %v after forbid (%v)", forbidden, err)
	}
	allowed, err := issue(ctx, h, s.Identity(), "combat-rescue-2", []any{
		map[string]any{"door": map[string]any{"cell": door, "mode": "COMBAT_DOOR_MODE_ALLOW"}},
	})
	if err != nil {
		return err
	}
	if applied, _ := na.AsBool(allowed[0]["applied"]); !applied {
		return fmt.Errorf("door allow refused: %v", allowed[0])
	}
	if after, err = tickRead(ctx, h); err != nil {
		return err
	}
	if forbidden, err := doorForbidden(after, cx, cz-13); err != nil || forbidden {
		return fmt.Errorf("door forbidden %v after allow (%v)", forbidden, err)
	}
	report["doorAllowed"] = true
	return nil
}

func doorForbidden(s labState, x, z int) (bool, error) {
	for _, d := range s.doors {
		row, _ := na.AsMap(d)
		if int(na.AsNumber(row["x"])) == x && int(na.AsNumber(row["z"])) == z {
			forbidden, ok := na.AsBool(row["forbidden"])
			if !ok {
				return false, fmt.Errorf("door row without forbidden (fixture build predates #867): %v", row)
			}
			return forbidden, nil
		}
	}
	return false, fmt.Errorf("no door at %d,%d in %v", x, z, s.doors)
}

// rescuePath reads the rescue_path role: a route from the rescuer to the
// downed colonist, ending next to it, whose every cell's hostile line of
// fire flag is the OR of its lines.
func rescuePath(ctx context.Context, h *na.Harness, s cases.Session, rescuer string, downed Pawn, hostiles []string, report na.Report) error {
	identity, err := typedIdentity(s.Identity())
	if err != nil {
		return err
	}
	to := &c.Cell{X: proto.Int32(int32(downed.X)), Z: proto.Int32(int32(downed.Z))}
	propose := &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_RescuePath{RescuePath: &mp.CombatRescuePath{To: to}}}
	request := bridge.CombatGeometryProposeAsk(identity, propose, hostiles, rescuer)
	// Two reads (#881): the first on a fresh game pays one-time costs; the
	// second, warm, must fit the #851 main-thread budget.
	var g *mp.CombatGeometry
	var ms []float64
	for i, label := range []string{"combat-rescue-path", "combat-rescue-path-warm"} {
		reply := &mp.CombatGeometryReply{}
		if err := wireProto(ctx, h, label, "combat_geometry", request, reply); err != nil {
			return err
		}
		g = reply.GetObserved()
		if err := bridge.ValidateCombatGeometry(g, request); err != nil {
			return fmt.Errorf("rescue_path read %d reply %v: %w", i+1, reply, err)
		}
		ms = append(ms, g.GetMainThreadMs())
	}
	report["rescuePathMs"] = map[string]any{"cold": ms[0], "warm": ms[1]}
	if ms[1] > geometryBudgetMs {
		return fmt.Errorf("warm rescue_path read took %.1f ms of main thread (cold %.1f ms), over %.0f ms", ms[1], ms[0], geometryBudgetMs)
	}
	route := g.GetProposed()
	fire := 0
	for i, row := range route {
		any := false
		for _, line := range row.GetLines() {
			any = any || line.GetLineOfFire()
		}
		if row.HostileLineOfFire == nil && any || row.GetHostileLineOfFire() != any {
			return fmt.Errorf("route cell %d line-of-fire flag %v disagrees with its lines: %v", i, row.GetHostileLineOfFire(), row)
		}
		if any {
			fire++
		}
	}
	report["rescuePath"] = map[string]any{"cells": len(route), "underFire": fire, "mainThreadMs": g.GetMainThreadMs()}
	if len(route) == 0 {
		return fmt.Errorf("no rescue route to %d,%d", downed.X, downed.Z)
	}
	last := route[len(route)-1].GetCell()
	if dx, dz := last.GetX()-int32(downed.X), last.GetZ()-int32(downed.Z); dx < -1 || dx > 1 || dz < -1 || dz > 1 {
		return fmt.Errorf("rescue route ends at %d,%d, not next to %d,%d", last.GetX(), last.GetZ(), downed.X, downed.Z)
	}
	return nil
}
