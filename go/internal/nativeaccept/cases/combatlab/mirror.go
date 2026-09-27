package combatlab

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

const (
	// mirrorStepTicks is one synchronous lab step between polls.
	mirrorStepTicks = 60
	// mirrorFightTicks bounds the lab-ranged firefight until a pawn goes
	// down (the #845 case budget is 5,000 ticks).
	mirrorFightTicks = 4800
	// geometryBudgetMs is the #851 main-thread budget of one geometry read
	// at the caps.
	geometryBudgetMs = 50.0
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/mirror",
		Scope: "Combat mirror sections and combat.geometry (#851) on lab-ranged: the combat_pawns keyframe lists the 4 riflemen and 4 raiders with rifles; " +
			"combat.geometry gives a sandbagged rifleman cover against the raider north of him, an open cell none, line of fire to every raider, a colonist in the path from the cell behind a rifleman, " +
			"path ticks for a named pawn, and a read at 64 cells x 8 pawns under 50 ms of main thread; the firefight's first downing or death arrives as a combat_events row and, in the same delta, " +
			"the pawn's row (downed, stamped with the event's watermark) or its tombstone.",
		Start:       cases.Lab{Colonists: 4},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runMirror,
	})
}

// wireProto calls a ProtoJSON tool with typed messages.
func wireProto(ctx context.Context, h *na.Harness, label, method string, request, reply proto.Message) error {
	encoded, err := protojson.Marshal(request)
	if err != nil {
		return err
	}
	var args map[string]any
	if err := json.Unmarshal(encoded, &args); err != nil {
		return err
	}
	message, err := h.Wire(ctx, label, method, args)
	if err != nil {
		return err
	}
	raw, err := json.Marshal(message)
	if err != nil {
		return err
	}
	return protojson.Unmarshal(raw, reply)
}

func typedIdentity(identity map[string]any) (*c.Identity, error) {
	raw, err := json.Marshal(identity)
	if err != nil {
		return nil, err
	}
	out := &c.Identity{}
	return out, protojson.Unmarshal(raw, out)
}

// combatPoll is one immediate mirror poll of both combat sections.
type combatPoll struct {
	h        *na.Harness
	identity *c.Identity
	epoch    *mp.Epoch
	pawnsAt  *mp.Watermark
	eventsAt *mp.Watermark
	polls    int
}

func (p *combatPoll) poll(ctx context.Context) (*mp.SectionPage, *mp.SectionPage, error) {
	request := &mp.MirrorPollRequest{Identity: p.identity, Epoch: p.epoch, ByteBudget: proto.Uint32(bridge.MirrorPollMaxBytes), Asks: []*mp.SectionAsk{
		{Section: mp.Section_SECTION_COMBAT_PAWNS.Enum(), Since: p.pawnsAt}, {Section: mp.Section_SECTION_COMBAT_EVENTS.Enum(), Since: p.eventsAt},
	}}
	reply := &mp.MirrorPollReply{}
	p.polls++
	if err := wireProto(ctx, p.h, fmt.Sprintf("combat-mirror-%d", p.polls), "mirror_poll", request, reply); err != nil {
		return nil, nil, err
	}
	if f := reply.GetFailure(); f != nil {
		return nil, nil, fmt.Errorf("mirror poll failed: %v", f)
	}
	page := reply.GetPage()
	if err := bridge.ValidateMirrorPage(page, request); err != nil {
		return nil, nil, err
	}
	p.epoch = page.Epoch
	var pawns, events *mp.SectionPage
	for _, s := range page.Sections {
		mark := s.GetKeyframe().GetAt()
		if mark == nil {
			mark = s.GetDelta().GetTo()
		}
		switch s.GetSection() {
		case mp.Section_SECTION_COMBAT_PAWNS:
			pawns, p.pawnsAt = s, mark
		case mp.Section_SECTION_COMBAT_EVENTS:
			events, p.eventsAt = s, mark
		}
	}
	if pawns == nil || events == nil {
		return nil, nil, fmt.Errorf("mirror page without both combat sections: %v", page)
	}
	return pawns, events, nil
}

func runMirror(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	staged, err := Stage(ctx, h, "lab-ranged")
	if err != nil {
		return err
	}
	identity, err := typedIdentity(s.Identity())
	if err != nil {
		return err
	}
	report := s.Report()
	p := &combatPoll{h: h, identity: identity}
	pawns, _, err := p.poll(ctx)
	if err != nil {
		return err
	}
	rows := map[string]*mp.CombatPawn{}
	for _, row := range pawns.GetKeyframe().GetCombatPawns() {
		rows[row.GetId()] = row
	}
	for _, id := range append(staged.Colonists(), staged.Hostiles()...) {
		row := rows[id]
		if row == nil || row.GetWeapon() != rifle || row.GetWeaponMelee() || row.GetWeaponRange() <= 0 {
			return fmt.Errorf("combat_pawns keyframe row for %s: %v (keyframe %d rows)", id, row, len(rows))
		}
	}
	report["keyframeRows"] = len(rows)
	if err := geometry(ctx, h, identity, staged, report); err != nil {
		return err
	}
	return fight(ctx, h, p, report)
}

// geometry checks the lab-ranged answers and times a read at the caps.
func geometry(ctx context.Context, h *na.Harness, identity *c.Identity, staged Staged, report na.Report) error {
	cell := func(x, z int) *c.Cell { return &c.Cell{X: proto.Int32(int32(x)), Z: proto.Int32(int32(z))} }
	// Spec order: colonist i then its raider straight north.
	var rifleman, raider Pawn
	for _, pawn := range staged.Fixture.Pawns {
		if pawn.Side == Colonist && rifleman.Side == "" {
			rifleman = pawn
		}
		if pawn.Side == Hostile && raider.Side == "" {
			raider = pawn
		}
	}
	hostiles := staged.Hostiles()
	open := cell(rifleman.X-12, rifleman.Z)
	behind := cell(rifleman.X, rifleman.Z-2)
	request := bridge.CombatGeometryAsk(identity, []*c.Cell{cell(rifleman.X, rifleman.Z), open, behind}, hostiles[:1], staged.Colonists()[1])
	reply := &mp.CombatGeometryReply{}
	if err := wireProto(ctx, h, "combat-geometry", "combat_geometry", request, reply); err != nil {
		return err
	}
	g := reply.GetObserved()
	if err := bridge.ValidateCombatGeometry(g, request); err != nil {
		return fmt.Errorf("geometry reply %v: %w", reply, err)
	}
	sandbagged, bare, back := g.Cells[0].Lines[0], g.Cells[1].Lines[0], g.Cells[2].Lines[0]
	report["geometry"] = map[string]any{"sandbaggedCover": sandbagged.GetCover(), "openCover": bare.GetCover(), "behindColonistInPath": back.GetColonistInPath(),
		"pathTicks": []int32{g.Cells[0].GetPathTicks(), g.Cells[1].GetPathTicks(), g.Cells[2].GetPathTicks()}}
	if sandbagged.GetCover() <= 0 || bare.GetCover() != 0 || !sandbagged.GetLineOfFire() || !bare.GetLineOfFire() || !back.GetColonistInPath() || sandbagged.GetColonistInPath() {
		return fmt.Errorf("geometry answers off lab-ranged's layout (raider %d,%d): %v", raider.X, raider.Z, g)
	}
	if g.Cells[1].PathTicks == nil || g.Cells[1].GetPathTicks() <= 0 {
		return fmt.Errorf("no path ticks to the open cell: %v", g.Cells[1])
	}
	// The timing: every staged pawn as a target (only 4 are hostile, and
	// the read does not care which side a target is on) against the
	// cells-cap of cells around the line, with path ticks.
	targets := append(append([]string{}, hostiles...), staged.Colonists()...)
	var cells []*c.Cell
	for dz := 0; len(cells) < bridge.CombatGeometryMaxCells; dz++ {
		for dx := -8; dx < 8 && len(cells) < bridge.CombatGeometryMaxCells; dx++ {
			cells = append(cells, cell(rifleman.X+3+dx, rifleman.Z-2-dz))
		}
	}
	var samples []float64
	for i := 0; i < 5; i++ {
		request := bridge.CombatGeometryAsk(identity, cells, targets, staged.Colonists()[1])
		reply := &mp.CombatGeometryReply{}
		if err := wireProto(ctx, h, fmt.Sprintf("combat-geometry-cap-%d", i), "combat_geometry", request, reply); err != nil {
			return err
		}
		if err := bridge.ValidateCombatGeometry(reply.GetObserved(), request); err != nil {
			return fmt.Errorf("geometry at the caps %v: %w", reply.GetFailure(), err)
		}
		samples = append(samples, reply.GetObserved().GetMainThreadMs())
	}
	sort.Float64s(samples)
	worst := samples[len(samples)-1]
	perPair := worst / float64(len(cells)*len(targets))
	projected := perPair * float64(bridge.CombatGeometryMaxCells*bridge.CombatGeometryMaxHostiles)
	report["geometryTiming"] = map[string]any{"cells": len(cells), "targets": len(targets), "msSorted": samples, "worstMs": worst,
		"projectedAtCapsMs": projected, "caps": fmt.Sprintf("%dx%d", bridge.CombatGeometryMaxCells, bridge.CombatGeometryMaxHostiles)}
	if projected > geometryBudgetMs {
		return fmt.Errorf("geometry at the caps projects %.1f ms (worst %.1f ms at %dx%d), over %.0f ms", projected, worst, len(cells), len(targets), geometryBudgetMs)
	}
	return nil
}

// shots requires the fight's shots in the events section (the
// Verb_LaunchProjectile and Verb_MeleeAttack hooks).
func shots(kinds map[string]int) error {
	if kinds[mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED.String()] == 0 {
		return fmt.Errorf("a pawn went down but no shot_fired event was recorded: %v", kinds)
	}
	return nil
}

// fight runs the lab until a pawn goes down or dies, then checks that the
// event and the pawn's change came in one page at one watermark.
func fight(ctx context.Context, h *na.Harness, p *combatPoll, report na.Report) error {
	kinds := map[string]int{}
	for ticks := 0; ticks < mirrorFightTicks; ticks += mirrorStepTicks {
		if _, _, err := Tick(ctx, h, mirrorStepTicks); err != nil {
			return err
		}
		pawns, events, err := p.poll(ctx)
		if err != nil {
			return err
		}
		rows := events.GetDelta().GetCombatEvents()
		if rows == nil {
			rows = events.GetKeyframe().GetCombatEvents()
		}
		changed := map[string]*mp.CombatPawn{}
		for _, row := range append(pawns.GetDelta().GetCombatPawns(), pawns.GetKeyframe().GetCombatPawns()...) {
			changed[row.GetId()] = row
		}
		gone := map[string]bool{}
		for _, id := range pawns.GetDelta().GetTombstones() {
			gone[id] = true
		}
		for _, e := range rows {
			kinds[e.GetKind().String()]++
			switch e.GetKind() {
			case mp.CombatLogKind_COMBAT_LOG_KIND_DOWNED:
				row := changed[e.GetThingId()]
				report["eventKinds"], report["down"] = kinds, map[string]any{"event": e.GetAt().String(), "row": row.String(), "ticks": ticks + mirrorStepTicks}
				if row == nil || !row.GetDowned() || !proto.Equal(row.GetChanged(), e.GetAt()) {
					if gone[e.GetThingId()] {
						return shots(kinds) // downed then killed inside one step: the tombstone is its delta
					}
					return fmt.Errorf("downing %v without its pawn row at the same watermark: %v", e, row)
				}
				return shots(kinds)
			case mp.CombatLogKind_COMBAT_LOG_KIND_KILLED:
				report["eventKinds"], report["down"] = kinds, map[string]any{"event": e.GetAt().String(), "killed": e.GetThingId(), "ticks": ticks + mirrorStepTicks}
				if !gone[e.GetThingId()] {
					return fmt.Errorf("death %v without its tombstone in the same delta", e)
				}
				return shots(kinds)
			}
		}
		report["eventKinds"] = kinds
	}
	if kinds[mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED.String()] == 0 {
		return fmt.Errorf("no shot fired in %d ticks of lab-ranged", mirrorFightTicks)
	}
	return fmt.Errorf("nobody went down in %d ticks of lab-ranged: %v", mirrorFightTicks, kinds)
}
