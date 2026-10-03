package combatlab

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

const (
	// mirrorStepTicks is one synchronous lab step between frames: the
	// stream's capture period.
	mirrorStepTicks = 60
	// frameWait bounds the wait for a frame after a step.
	frameWait = 10 * time.Second
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
		Scope: "Combat frame sections (#851, #858) and combat.geometry on lab-ranged: a snapshot frame's combat_pawns lists the 4 riflemen and 4 raiders with rifles; " +
			"combat.geometry gives a sandbagged rifleman cover against the raider north of him, an open cell none, line of fire to every raider, a colonist in the path from the cell behind a rifleman, " +
			"path ticks for a named pawn, and a read at 64 cells x 8 pawns under 50 ms of main thread; proposing cover_behind_line on the sandbags returns every cell behind them, and a firing_cells proposal at the radius cap fills 64 cells within budget (#871); the firefight's first downing or death arrives as a combat_events row and, in the same frame, " +
			"the pawn's row (downed, stamped with the event's watermark) or its absence; then lab-pods' center drop arrives as a hostile_arrived row with strategy pods, " +
			"landing cells and an open tick by which every raider is out of its pod (#870).",
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

// combatFrames reads the combat sections of the snapshot stream's frames
// (#858): the case maps the ring the way the controller's bridge does.
type combatFrames struct {
	identity *c.Identity
	reader   *snapshotshm.Reader
	last     uint64
}

func openCombatFrames(ctx context.Context, h *na.Harness, identity *c.Identity) (*combatFrames, error) {
	reply := &o.SnapshotStreamReply{}
	if err := wireProto(ctx, h, "combat-open-stream", "observations_open_snapshot_stream", &o.SnapshotStreamRequest{}, reply); err != nil {
		return nil, err
	}
	opened := reply.GetOpened()
	if opened.GetName() == "" {
		return nil, fmt.Errorf("snapshot stream not opened: %v", reply)
	}
	reader, err := snapshotshm.Open(opened.GetName())
	if err != nil {
		return nil, err
	}
	return &combatFrames{identity: identity, reader: reader}, nil
}

// next is the combat state of the first frame published after the last
// one read and captured at or past tick.
func (f *combatFrames) next(ctx context.Context, tick int64) (bridge.Combat, error) {
	v, err := f.snapshot(ctx, tick)
	if err != nil {
		return bridge.Combat{}, err
	}
	return bridge.Combat{Context: v.Context, Pawns: v.CombatPawns, Events: v.CombatEvents}, nil
}

// snapshot is the first frame published after the last one read and
// captured at or past tick.
func (f *combatFrames) snapshot(ctx context.Context, tick int64) (*o.BundleSnapshot, error) {
	deadline := time.Now().Add(frameWait)
	for {
		frame, ok, err := f.reader.Latest()
		if err != nil {
			return nil, err
		}
		if ok && frame.Number > f.last {
			v := &o.BundleSnapshot{}
			if err := proto.Unmarshal(frame.Payload, v); err != nil {
				return nil, err
			}
			if v.GetContext().GetTick() >= tick {
				f.last = frame.Number
				if !proto.Equal(v.GetContext().GetIdentity(), f.identity) {
					return nil, fmt.Errorf("frame %d describes another world: %v", frame.Number, v.GetContext().GetIdentity())
				}
				return v, nil
			}
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("no snapshot frame at or past tick %d within %s (last read %d)", tick, frameWait, f.last)
		}
		after := f.last
		if ok {
			after = frame.Number
		}
		f.reader.Wait(ctx, after, 250*time.Millisecond)
	}
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
	// Geometry reads the staged layout before any tick: the undrafted
	// colonists wander off their cells once the clock runs (#876).
	if err := geometry(ctx, h, identity, staged, report); err != nil {
		return err
	}
	frames, err := openCombatFrames(ctx, h, identity)
	if err != nil {
		return err
	}
	defer frames.reader.Close()
	// One step so a frame is captured past the staging.
	_, tick, err := Tick(ctx, h, mirrorStepTicks)
	if err != nil {
		return err
	}
	state, err := frames.next(ctx, int64(tick))
	if err != nil {
		return err
	}
	rows := map[string]*mp.CombatPawn{}
	for _, row := range state.Pawns {
		rows[row.GetId()] = row
	}
	// The weapon's range and class are the def rows' (#1723), not the frame's.
	catalog, err := h.Client.DefinitionCatalog(ctx, identity)
	if err != nil {
		return err
	}
	rifleFacts, err := catalog.WeaponOf(rifle)
	if err != nil {
		return err
	}
	if !rifleFacts.Ranged || rifleFacts.Range <= 0 {
		return fmt.Errorf("catalog rows give %s no ranged verb: %+v", rifle, rifleFacts)
	}
	for _, id := range append(staged.Colonists(), staged.Hostiles()...) {
		row := rows[id]
		if row == nil || row.GetWeapon() != rifle {
			return fmt.Errorf("combat_pawns row for %s: %v (frame %d rows)", id, row, len(rows))
		}
	}
	report["frameRows"] = len(rows)
	if err := fight(ctx, h, frames, report); err != nil {
		return err
	}
	return dropPods(ctx, h, frames, report)
}

// dropPods (#870) stages lab-pods: the center drop's arrival is a
// combat_events hostile_arrived row with strategy pods, its landing cells
// and an open tick after the arrival, and by that tick every raider is out.
func dropPods(ctx context.Context, h *na.Harness, frames *combatFrames, report na.Report) error {
	staged, err := Stage(ctx, h, "lab-pods")
	if err != nil {
		return err
	}
	_, tick, err := Tick(ctx, h, mirrorStepTicks)
	if err != nil {
		return err
	}
	state, err := frames.next(ctx, int64(tick))
	if err != nil {
		return err
	}
	var row *mp.CombatEventRow
	for _, e := range state.Events {
		if bridge.DropPodArrival(e) {
			row = e
		}
	}
	if row == nil {
		return fmt.Errorf("lab-pods: no combat_events row with strategy pods in %d frame events", len(state.Events))
	}
	report["pods"] = map[string]any{"at": row.GetAt().GetTick(), "openTick": row.GetOpenTick(), "landingCells": len(row.GetLandingCells()), "arrival": row.GetDefName()}
	wait := int(int64(row.GetOpenTick())-row.GetAt().GetTick()) + mirrorStepTicks
	if wait <= mirrorStepTicks || len(row.GetLandingCells()) == 0 {
		return fmt.Errorf("lab-pods row without an open tick after its arrival or landing cells: %v", row)
	}
	pawns, _, err := Tick(ctx, h, wait)
	if err != nil {
		return err
	}
	for _, id := range staged.Hostiles() {
		if _, ok := pawns[id]; !ok {
			return fmt.Errorf("lab-pods raider %s still in its pod %d ticks after the arrival (open tick %d)", id, wait, row.GetOpenTick())
		}
	}
	return nil
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
	return propose(ctx, h, identity, staged, rifleman, report)
}

// propose (#871): cover_behind_line on the sandbag line returns every cell
// behind it (the riflemen's row), and a firing_cells proposal at the
// radius cap, 64 cells scored against every staged pawn, stays in budget.
func propose(ctx context.Context, h *na.Harness, identity *c.Identity, staged Staged, rifleman Pawn, report na.Report) error {
	cell := func(x, z int) *c.Cell { return &c.Cell{X: proto.Int32(int32(x)), Z: proto.Int32(int32(z))} }
	var line []*c.Cell
	behind := map[[2]int32]bool{}
	for _, th := range staged.Fixture.Things {
		if th.Def == "Sandbags" {
			line = append(line, cell(th.X, th.Z))
			behind[[2]int32{int32(th.X), int32(th.Z - 1)}] = true
		}
	}
	request := bridge.CombatGeometryProposeAsk(identity, &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_CoverBehindLine{
		CoverBehindLine: &mp.CombatCoverBehindLine{Line: line}}}, staged.Hostiles(), "")
	reply := &mp.CombatGeometryReply{}
	if err := wireProto(ctx, h, "combat-geometry-propose-cover", "combat_geometry", request, reply); err != nil {
		return err
	}
	g := reply.GetObserved()
	if err := bridge.ValidateCombatGeometry(g, request); err != nil {
		return fmt.Errorf("propose cover_behind_line %v: %w", reply.GetFailure(), err)
	}
	var proposed [][2]int32
	for _, row := range g.GetProposed() {
		key := [2]int32{row.GetCell().GetX(), row.GetCell().GetZ()}
		proposed = append(proposed, key)
		delete(behind, key)
	}
	report["proposeCover"] = map[string]any{"cells": proposed, "ms": g.GetMainThreadMs()}
	if len(behind) != 0 {
		return fmt.Errorf("cover_behind_line on the sandbags missed %v; proposed %v", behind, proposed)
	}
	pawns := append(append([]string{}, staged.Hostiles()...), staged.Colonists()...)
	var targets []*c.Cell
	for _, pawn := range staged.Fixture.Pawns {
		targets = append(targets, cell(pawn.X, pawn.Z))
	}
	firing := &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_FiringCells{FiringCells: &mp.CombatFiringCells{
		Targets: targets, From: cell(rifleman.X, rifleman.Z-3), Radius: proto.Int32(bridge.CombatGeometryMaxRadius)}}}
	var samples []float64
	for i := 0; i < 5; i++ {
		request := bridge.CombatGeometryProposeAsk(identity, firing, pawns, staged.Colonists()[1])
		reply := &mp.CombatGeometryReply{}
		if err := wireProto(ctx, h, fmt.Sprintf("combat-geometry-propose-cap-%d", i), "combat_geometry", request, reply); err != nil {
			return err
		}
		if err := bridge.ValidateCombatGeometry(reply.GetObserved(), request); err != nil {
			return fmt.Errorf("propose firing_cells %v: %w", reply.GetFailure(), err)
		}
		if n := len(reply.GetObserved().GetProposed()); n != bridge.CombatGeometryMaxCells {
			return fmt.Errorf("propose firing_cells at radius %d gave %d cells, want %d", bridge.CombatGeometryMaxRadius, n, bridge.CombatGeometryMaxCells)
		}
		samples = append(samples, reply.GetObserved().GetMainThreadMs())
	}
	sort.Float64s(samples)
	worst := samples[len(samples)-1]
	// Scan and scoring are linear in targets and hostiles: 8 each here,
	// the caps are 16.
	projected := worst * float64(bridge.CombatGeometryMaxHostiles) / float64(len(pawns))
	report["proposeTiming"] = map[string]any{"targets": len(targets), "hostiles": len(pawns), "radius": bridge.CombatGeometryMaxRadius,
		"msSorted": samples, "worstMs": worst, "projectedAtCapsMs": projected}
	if projected > geometryBudgetMs {
		return fmt.Errorf("propose at the caps projects %.1f ms (worst %.1f ms), over %.0f ms", projected, worst, geometryBudgetMs)
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
// event and the pawn's change came in one frame at one watermark. Every
// frame carries the whole event ring, so only events not seen before are
// counted.
func fight(ctx context.Context, h *na.Harness, f *combatFrames, report na.Report) error {
	kinds := map[string]int{}
	seen := map[string]bool{}
	for ticks := 0; ticks < mirrorFightTicks; ticks += mirrorStepTicks {
		_, tick, err := Tick(ctx, h, mirrorStepTicks)
		if err != nil {
			return err
		}
		state, err := f.next(ctx, int64(tick))
		if err != nil {
			return err
		}
		rows := map[string]*mp.CombatPawn{}
		for _, row := range state.Pawns {
			rows[row.GetId()] = row
		}
		for _, e := range state.Events {
			if id := bridge.CombatEventID(e); seen[id] {
				continue
			} else {
				seen[id] = true
			}
			kinds[e.GetKind().String()]++
			switch e.GetKind() {
			case mp.CombatLogKind_COMBAT_LOG_KIND_DOWNED:
				row := rows[e.GetThingId()]
				report["eventKinds"], report["down"] = kinds, map[string]any{"event": e.GetAt().String(), "row": row.String(), "ticks": ticks + mirrorStepTicks}
				if row == nil {
					return shots(kinds) // downed then killed inside one step: its row is gone
				}
				if !row.GetDowned() || !proto.Equal(row.GetChanged(), e.GetAt()) {
					return fmt.Errorf("downing %v without its pawn row at the same watermark: %v", e, row)
				}
				return shots(kinds)
			case mp.CombatLogKind_COMBAT_LOG_KIND_KILLED:
				report["eventKinds"], report["down"] = kinds, map[string]any{"event": e.GetAt().String(), "killed": e.GetThingId(), "ticks": ticks + mirrorStepTicks}
				if rows[e.GetThingId()] != nil {
					return fmt.Errorf("death %v with its pawn row still in the same frame", e)
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
