package combatlab

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/native-hold",
		Scope: "Go assigns one ranged firing cell; vanilla native execution acquires and retargets without Go micro orders (#2477, #1857). " +
			"A Go snapshot cannot prove vanilla target acquisition, autonomous retargeting, shots or position retention. " +
			"On a pinned lab one defender moves to the cell, holds fire for 600 ticks with live reachable hostiles, then receives hold-position and fire-at-will once. " +
			"Native shot events must name two targets, with a later shot at another live hostile after the first target is observed unavailable through ordinary combat. " +
			"Every observed hold position must equal the assigned cell; no attack, movement or renewed hold orders follow assignment.",
		Start: cases.Lab{Colonists: 1}, RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld: true, NoCheckpoint: true, Budget: cases.LabBudget,
		Crew: cases.Crew{Size: 3}, Run: runNativeHold,
	})
}

func runNativeHold(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var x, z int
	staged, err := Stage(ctx, h, "lab-ranged", func(f *Fixture, cx, cz int) {
		x, z = cx, cz-9
		f.Colonists, f.Layout = 1, nil
		f.Pawns = []Pawn{
			{Side: Colonist, Index: 0, X: x, Z: z - 2, Weapon: "Gun_ChargeRifle", Apparel: "Apparel_ArmorMarine"},
			{Side: Hostile, Kind: gunner, X: x - 2, Z: z + 16, Weapon: "Bow_Short"},
			{Side: Hostile, Kind: gunner, X: x + 2, Z: z + 20, Weapon: "Bow_Short"},
		}
	})
	if err != nil {
		return err
	}
	defender := staged.Colonists()[0]
	hostiles := map[string]bool{}
	for _, id := range staged.Hostiles() {
		hostiles[id] = true
	}
	typed, err := typedIdentity(identity)
	if err != nil {
		return err
	}
	frames, err := openCombatFrames(ctx, h, typed)
	if err != nil {
		return err
	}
	defer frames.reader.Close()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "native-hold-acquire", identity); err != nil {
		return err
	}
	if err := draftAll(ctx, h, identity, "native-hold-draft", []string{defender}); err != nil {
		return err
	}
	pawn := map[string]any{"entityId": defender}
	assign := func(label string, orders []any) error {
		results, err := issue(ctx, h, identity, label, orders)
		if err != nil {
			return err
		}
		for i, row := range results {
			if applied, _ := na.AsBool(row["applied"]); !applied {
				return fmt.Errorf("%s order %d refused: %v", label, i, row)
			}
		}
		return nil
	}
	if err := assign("native-hold-move", []any{
		map[string]any{"pawn": pawn, "fireMode": "COMBAT_FIRE_MODE_HOLD"},
		map[string]any{"pawn": pawn, "move": cell(x, z)},
	}); err != nil {
		return err
	}
	arrived := false
	for ticks := 0; ticks < 300; ticks++ {
		rows, _, err := Tick(ctx, h, 1)
		if err != nil {
			return err
		}
		p := rows[defender]
		if p.X == x && p.Z == z {
			arrived = true
			break
		}
	}
	if !arrived {
		return fmt.Errorf("defender did not reach assigned cell %d,%d", x, z)
	}
	if err := assign("native-hold-negative", []any{map[string]any{"pawn": pawn, "holdPosition": map[string]any{}}}); err != nil {
		return err
	}
	seen := map[string]bool{}
	first := ""
	unavailableTick := 0
	report["defender"], report["assignedCell"] = defender, cell(x, z)
	report["positionSampleTicks"] = mirrorStepTicks
	var shotRows []map[string]any
	report["shots"] = shotRows
	for elapsed := 0; elapsed < 600+mirrorFightTicks; elapsed += mirrorStepTicks {
		if elapsed == 600 {
			// This is the final Go combat assignment. The remainder only advances
			// simulation and reads native outcomes, even if Wait_Combat ends.
			if err := assign("native-hold-engage", []any{
				map[string]any{"pawn": pawn, "holdPosition": map[string]any{}},
				map[string]any{"pawn": pawn, "fireMode": "COMBAT_FIRE_MODE_AT_WILL"},
			}); err != nil {
				return err
			}
		}
		rows, tick, err := Tick(ctx, h, mirrorStepTicks)
		if err != nil {
			return err
		}
		p, exists := rows[defender]
		if !exists || p.Downed || p.Dead || p.X != x || p.Z != z {
			return fmt.Errorf("hold lost at tick %d: defender %+v (present %v), assigned %d,%d", tick, p, exists, x, z)
		}
		state, err := frames.next(ctx, int64(tick))
		if err != nil {
			return err
		}
		if elapsed < 600 {
			eligible := false
			for id := range hostiles {
				if target, ok := rows[id]; ok && !target.Downed && !target.Dead && (target.X-x)*(target.X-x)+(target.Z-z)*(target.Z-z) <= 24*24 {
					eligible = true
				}
			}
			if !eligible {
				return fmt.Errorf("hold-fire phase has no live ranged target at tick %d", tick)
			}
		}
		for _, event := range state.Events {
			id := bridge.CombatEventID(event)
			if seen[id] {
				continue
			}
			seen[id] = true
			if event.GetKind() != mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED || event.GetThingId() != defender {
				continue
			}
			if elapsed < 600 {
				return fmt.Errorf("defender fired under hold-fire at tick %d", event.GetAt().GetTick())
			}
			target := event.GetTargetId()
			if !hostiles[target] {
				return fmt.Errorf("defender shot unexpected target %q", target)
			}
			shotRows = append(shotRows, map[string]any{"tick": event.GetAt().GetTick(), "target": target})
			report["shots"] = shotRows
			if first == "" {
				first = target
				report["firstTarget"] = first
			}
			other, live := rows[target]
			if unavailableTick > 0 && target != first && event.GetAt().GetTick() > int64(unavailableTick) && live && !other.Downed && !other.Dead {
				report["retargetTick"], report["heldFireTicks"] = event.GetAt().GetTick(), 600
				return nil
			}
		}
		if first != "" && unavailableTick == 0 {
			target, live := rows[first]
			if !live || target.Downed || target.Dead {
				unavailableTick = tick
				report["firstUnavailable"] = map[string]any{"tick": tick, "present": live, "downed": target.Downed, "dead": target.Dead}
			}
		}
	}
	return fmt.Errorf("native hold did not shoot a different live hostile after first target became unavailable in %d ticks (first %q, unavailable %d, shots %v)", mirrorFightTicks, first, unavailableTick, shotRows)
}
