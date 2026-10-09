package combatlab

import (
	"context"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// liveTicks is how long the liveness check lets the staged fight run:
// 5 game seconds, far enough for walking raiders to close several cells.
const liveTicks = 300

func init() {
	cases.Register(cases.Case{
		Name:        "combatlab/stage",
		Scope:       "Combat lab fixtures (#854): lab-open, lab-choke and lab-ranged each stage on a wiped lab with every pawn at its cell, armed as specified, hostiles under an assault lord, a second staging reads back the same digest, combat.geometry's adjacent_to_choke proposes exactly lab-choke's three inner cells behind the gap (#881), and lab-open's raiders close on the colonists within 300 ticks.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			report := map[string]any{}
			for _, name := range Names {
				first, err := Stage(ctx, h, name)
				if err != nil {
					return err
				}
				second, err := Stage(ctx, h, name)
				if err != nil {
					return err
				}
				if first.Digest == "" || first.Digest != second.Digest {
					return fmt.Errorf("%s: stagings differ, digest %q then %q: %s", name, first.Digest, second.Digest, rowDiff(first.Rows, second.Rows))
				}
				row := map[string]any{"digest": first.Digest, "faction": first.Faction, "colonists": len(first.Colonists()), "hostiles": len(first.Hostiles())}
				report[name] = row
				if name == "lab-open" {
					if row["live"], err = closesIn(ctx, s); err != nil {
						return err
					}
				}
				if name == "lab-choke" {
					if row["adjacentToChoke"], err = proposeChoke(ctx, s, second); err != nil {
						return err
					}
				}
			}
			s.Report()["fixtures"] = report
			return nil
		},
	})
}

// closesIn proves a staged fixture is a live fight, not a tableau: under
// their assault lord lab-open's melee raiders close on the colonists.
func closesIn(ctx context.Context, s cases.Session) (map[string]any, error) {
	h := s.Harness()
	before, _, err := Read(ctx, h)
	if err != nil {
		return nil, err
	}
	after, tick, err := Tick(ctx, h, liveTicks)
	if err != nil {
		return nil, err
	}
	row := map[string]any{"gapBefore": Gap(before), "gapAfter": Gap(after), "tick": tick}
	if Gap(after) < 0 || Gap(after) >= Gap(before) {
		return row, fmt.Errorf("lab-open: raiders did not close in %d ticks (squared gap %d then %d)", liveTicks, Gap(before), Gap(after))
	}
	return row, nil
}

// proposeChoke: adjacent_to_choke on lab-choke's gap, our side
// three cells inside, proposes exactly the three floor cells inside the
// gap, the one straight behind it first; the walls either side and the
// cells outside are not proposed.
func proposeChoke(ctx context.Context, s cases.Session, staged Staged) (map[string]any, error) {
	identity, err := typedIdentity(s.Identity())
	if err != nil {
		return nil, err
	}
	// The riflemen stand at (cx-2, cz) and (cx+2, cz).
	var cx, cz int
	for _, p := range staged.Fixture.Pawns {
		if p.Side == Colonist && p.Index == 3 {
			cx, cz = p.X+2, p.Z
		}
	}
	cell := func(x, z int) *c.Cell { return &c.Cell{X: proto.Int32(int32(x)), Z: proto.Int32(int32(z))} }
	request := bridge.CombatGeometryProposeAsk(identity, &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_AdjacentToChoke{
		AdjacentToChoke: &mp.CombatAdjacentToChoke{Choke: cell(cx, cz+chokeHalf), OurSide: cell(cx, cz+chokeHalf-3)}}}, staged.Hostiles(), "")
	reply := &mp.CombatGeometryReply{}
	if err := wireProto(ctx, s.Harness(), "combat-geometry-propose-choke", "combat_geometry", request, reply); err != nil {
		return nil, err
	}
	g := reply.GetObserved()
	if err := bridge.ValidateCombatGeometry(g, request); err != nil {
		return nil, fmt.Errorf("propose adjacent_to_choke %v: %w", reply.GetFailure(), err)
	}
	var proposed [][2]int
	for _, row := range g.GetProposed() {
		proposed = append(proposed, [2]int{int(row.GetCell().GetX()), int(row.GetCell().GetZ())})
	}
	row := map[string]any{"cells": proposed, "ms": g.GetMainThreadMs()}
	inside := cz + chokeHalf - 1
	want := map[[2]int]bool{{cx, inside}: true, {cx - 1, inside}: true, {cx + 1, inside}: true}
	if len(proposed) != len(want) || proposed[0] != [2]int{cx, inside} {
		return row, fmt.Errorf("adjacent_to_choke on the gap at (%d,%d) proposed %v, want the three cells at z=%d, x=%d first", cx, cz+chokeHalf, proposed, inside, cx)
	}
	for _, p := range proposed {
		if !want[p] {
			return row, fmt.Errorf("adjacent_to_choke on the gap at (%d,%d) proposed %v outside the room's inner row", cx, cz+chokeHalf, p)
		}
	}
	return row, nil
}

// rowDiff names the digest rows only one of two stagings has.
func rowDiff(a, b []string) string {
	count := map[string]int{}
	for _, r := range a {
		count[r]++
	}
	for _, r := range b {
		count[r]--
	}
	var only []string
	for r, n := range count {
		if n > 0 {
			only = append(only, "first only "+r)
		} else if n < 0 {
			only = append(only, "second only "+r)
		}
	}
	sort.Strings(only)
	return strings.Join(only, "; ")
}
