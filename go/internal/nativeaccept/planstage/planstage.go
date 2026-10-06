// Package planstage stages a plan the controller has already derived (#2118,
// epic #2101): a native case reads the layout plan from the journal of a
// first, inert service run, puts a room of that plan on the ground finished
// (walls and doors of a chosen stuff, a roof, a constructed floor, furniture
// of a fixed quality and hit points, stock and a warehouse stockpile through
// test/plan_stage) and lets the real controller reconcile the ground to the
// plan. The controller holds the GABP slot while it runs (#676), so staging
// and native readbacks happen between service runs; a Phase is one run.
package planstage

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Tool is the fixture op (scripts/fixtures/PlanStageFixture.cs); a case lists
// it in RequiredOps.
const Tool = "test/plan_stage"

// Ceiling is the wall-clock bound of one service phase; the stall budget is
// what ends a broken one.
const Ceiling = 10 * time.Minute

// Cell is a map cell in the fixture's JSON.
type Cell struct {
	X int32 `json:"x"`
	Z int32 `json:"z"`
}

// Rect is an inclusive cell rectangle.
type Rect struct {
	MinX int32 `json:"minX"`
	MinZ int32 `json:"minZ"`
	MaxX int32 `json:"maxX"`
	MaxZ int32 `json:"maxZ"`
}

// Building is a finished player building of Stuff at X,Z: a wall, a door
// (Rotation 0..3) or, in Spec.Things, furniture with a fixed Quality (0..6,
// nil leaves the rolled one) and HitFraction (0..1, nil leaves it undamaged).
type Building struct {
	Def         string   `json:"def,omitempty"`
	Stuff       string   `json:"stuff,omitempty"`
	X           int32    `json:"x"`
	Z           int32    `json:"z"`
	Rotation    int      `json:"rotation,omitempty"`
	Quality     *int     `json:"quality,omitempty"`
	HitFraction *float64 `json:"hitFraction,omitempty"`
	// Foreign leaves the thing unowned and a plant fully grown: a tree, a ruin
	// or a casket, what a room's ground holds that the colony did not build.
	Foreign bool `json:"foreign,omitempty"`
	// Filled loads an ancient casket with friendly contents.
	Filled bool `json:"filled,omitempty"`
}

// Floor lays Def on Cells.
type Floor struct {
	Def   string `json:"def"`
	Cells []Cell `json:"cells"`
}

// Drop is Count of Def lying near X,Z, unforbidden.
type Drop struct {
	Def   string `json:"def"`
	Count int    `json:"count"`
	X     int32  `json:"x"`
	Z     int32  `json:"z"`
}

// Spec is one staging call.
type Spec struct {
	Research  []string   `json:"research,omitempty"`
	Walls     []Building `json:"walls,omitempty"`
	Doors     []Building `json:"doors,omitempty"`
	Roof      []Rect     `json:"roof,omitempty"`
	Floor     []Floor    `json:"floor,omitempty"`
	Things    []Building `json:"things,omitempty"`
	Drops     []Drop     `json:"drops,omitempty"`
	Stockpile *Rect      `json:"stockpile,omitempty"`
}

// Staged is the stage reply: the load ids of the walls, doors and things
// spawned, in spec order.
type Staged struct {
	Walls, Doors, Things []string
	Reply                map[string]any
}

// Stage runs spec on the paused game.
func Stage(ctx context.Context, h *na.Harness, label string, spec Spec) (Staged, error) {
	data, err := json.Marshal(spec)
	if err != nil {
		return Staged{}, err
	}
	reply, err := h.Call(ctx, label, Tool, map[string]any{"action": "stage", "spec": string(data)})
	if err != nil {
		return Staged{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return Staged{}, fmt.Errorf("%s stage refused: %#v", Tool, reply)
	}
	out := Staged{Reply: reply}
	for _, raw := range na.AsSlice(reply["spawned"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["def"]) == "Door" {
			out.Doors = append(out.Doors, na.AsString(row["id"]))
		} else {
			out.Walls = append(out.Walls, na.AsString(row["id"]))
		}
	}
	for _, raw := range na.AsSlice(reply["things"]) {
		row, _ := na.AsMap(raw)
		out.Things = append(out.Things, na.AsString(row["id"]))
	}
	return out, nil
}

// CellRead is one cell of an audit: its edifice, terrain and room.
type CellRead struct {
	Cell
	Edifice, EdificeStuff, Terrain, Role string
	Roofed, Enclosed                     bool
}

// ThingRead is one thing of an audit; Found is false when the id is neither
// spawned, packed nor carried. Packed is the minified item holding it. Quality
// is -1 for a thing without one.
type ThingRead struct {
	ID, Def, Stuff         string
	Found, Packed, Spawned bool
	Quality                int
	HitPoints, MaxHP       int
	X, Z                   int32
}

// Audit is the fixture's read of things and cells.
type Audit struct {
	Things map[string]ThingRead
	Cells  map[Cell]CellRead
	Reply  map[string]any
}

// Read audits ids and cells.
func Read(ctx context.Context, h *na.Harness, label string, ids []string, cells []Cell) (Audit, error) {
	data, err := json.Marshal(map[string]any{"ids": ids, "cells": cells})
	if err != nil {
		return Audit{}, err
	}
	reply, err := h.Call(ctx, label, Tool, map[string]any{"action": "audit", "spec": string(data)})
	if err != nil {
		return Audit{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return Audit{}, fmt.Errorf("%s audit refused: %#v", Tool, reply)
	}
	out := Audit{Things: map[string]ThingRead{}, Cells: map[Cell]CellRead{}, Reply: reply}
	for _, raw := range na.AsSlice(reply["things"]) {
		row, _ := na.AsMap(raw)
		t := ThingRead{ID: na.AsString(row["id"]), Def: na.AsString(row["def"]), Stuff: na.AsString(row["stuff"]), Quality: -1,
			HitPoints: int(na.AsNumber(row["hitPoints"])), MaxHP: int(na.AsNumber(row["maxHitPoints"])),
			X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))}
		t.Found = t.Def != ""
		t.Packed, _ = na.AsBool(row["packed"])
		t.Spawned, _ = na.AsBool(row["spawned"])
		if q, present := row["quality"]; present && q != nil {
			t.Quality = int(na.AsNumber(q))
		}
		out.Things[t.ID] = t
	}
	for _, raw := range na.AsSlice(reply["cells"]) {
		row, _ := na.AsMap(raw)
		c := CellRead{Cell: Cell{X: int32(na.AsNumber(row["x"])), Z: int32(na.AsNumber(row["z"]))},
			Edifice: na.AsString(row["edifice"]), EdificeStuff: na.AsString(row["edificeStuff"]), Terrain: na.AsString(row["terrain"]), Role: na.AsString(row["role"])}
		c.Roofed, _ = na.AsBool(row["roofed"])
		c.Enclosed, _ = na.AsBool(row["enclosed"])
		out.Cells[c.Cell] = c
	}
	return out, nil
}

// Ring is a PlannedRoom's ring on the ground: the interior and wall cells
// split into plain walls and doors, the roof rectangle over both.
type Ring struct {
	Interior []Cell
	Walls    []Cell
	Doors    []Building
	Roof     Rect
}

// RingOf is room's ring, its doors the plan's shell doors. The primary door
// keeps the planned rotation; any other faces by the wall it stands in.
func RingOf(plan policy.LayoutPlan, room policy.PlannedRoom) Ring {
	in := room.Interior
	doors := map[domain.Cell]int{}
	for _, d := range plan.ShellDoors(room) {
		doors[d] = -1
	}
	doors[room.Door] = rotationNumber(room.DoorRot)
	var ring Ring
	ring.Roof = Rect{MinX: in.X - 1, MinZ: in.Z - 1, MaxX: in.X + in.Width, MaxZ: in.Z + in.Height}
	for x := in.X - 1; x <= in.X+in.Width; x++ {
		for z := in.Z - 1; z <= in.Z+in.Height; z++ {
			cell := domain.Cell{X: x, Z: z}
			if x >= in.X && x < in.X+in.Width && z >= in.Z && z < in.Z+in.Height {
				ring.Interior = append(ring.Interior, Cell{X: x, Z: z})
				continue
			}
			if rot, door := doors[cell]; door {
				if rot < 0 {
					rot = 1 // a door in a side wall opens east and west
					if z == in.Z-1 || z == in.Z+in.Height {
						rot = 0
					}
				}
				ring.Doors = append(ring.Doors, Building{X: x, Z: z, Rotation: rot})
				continue
			}
			ring.Walls = append(ring.Walls, Cell{X: x, Z: z})
		}
	}
	return ring
}

// RotationNumber is a rotation as the fixture's 0..3 (north, east, south, west).
func RotationNumber(r domain.Rotation) int { return rotationNumber(r) }

func rotationNumber(r domain.Rotation) int {
	switch r {
	case domain.East:
		return 1
	case domain.South:
		return 2
	case domain.West:
		return 3
	}
	return 0
}

// Spec is the staging of the ring finished: walls and
// doors of stuff, the roof over the ring and, with floor set, the interior
// laid with that constructed floor. The caller adds what else the room holds.
func (r Ring) Spec(stuff, floor string) Spec {
	spec := Spec{Roof: []Rect{r.Roof}}
	for _, c := range r.Walls {
		spec.Walls = append(spec.Walls, Building{X: c.X, Z: c.Z, Stuff: stuff})
	}
	for _, d := range r.Doors {
		d.Stuff = stuff
		spec.Doors = append(spec.Doors, d)
	}
	if floor != "" {
		spec.Floor = []Floor{{Def: floor, Cells: r.Interior}}
	}
	return spec
}

// Cells is every cell of the ring, interior and walls and doors.
func (r Ring) Cells() []Cell {
	out := append([]Cell(nil), r.Interior...)
	out = append(out, r.Walls...)
	for _, d := range r.Doors {
		out = append(out, Cell{X: d.X, Z: d.Z})
	}
	return out
}

// Sealed reports every interior cell enclosed and roofed and every wall and
// door cell holding a building, in a, naming the first failure.
//
// A ring cell holding Frame_Wall is an in-place wall swap in progress (the
// reconciler's wall_up, #2111): it counts as a wall, and the enclosure check is
// skipped while any frame stands, since a frame does not seal the room.
func (r Ring) Sealed(a Audit) error {
	framed := false
	for _, c := range r.Walls {
		if a.Cells[c].Edifice == "Frame_Wall" {
			framed = true
		}
	}
	for _, c := range r.Interior {
		read, ok := a.Cells[c]
		if !ok || !read.Roofed || (!read.Enclosed && !framed) {
			return fmt.Errorf("interior cell %d,%d is not enclosed and roofed: %+v", c.X, c.Z, read)
		}
	}
	for _, c := range r.Walls {
		if read := a.Cells[c]; read.Edifice != "Wall" && read.Edifice != "Frame_Wall" {
			return fmt.Errorf("ring cell %d,%d holds %q, not a wall", c.X, c.Z, read.Edifice)
		}
	}
	for _, d := range r.Doors {
		if read := a.Cells[Cell{X: d.X, Z: d.Z}]; read.Edifice != "Door" {
			return fmt.Errorf("door cell %d,%d holds %q, not a door", d.X, d.Z, read.Edifice)
		}
	}
	return nil
}

// Phase is one run of the controller over the staged game.
type Phase struct {
	s    cases.Session
	Svc  *na.ServiceProcess
	St   *store.Store
	wait na.Wait
}

// Begin launches the controller (a fresh serve of the case's spec, or previous
// restarted on the same journal) and takes authority. families, when set,
// replace the spec's for this launch: an inert first run reads the plan
// without ordering any work.
func Begin(ctx context.Context, s cases.Session, previous *na.ServiceProcess, families []string) (*Phase, error) {
	var svc *na.ServiceProcess
	var err error
	if previous == nil {
		spec := s.Spec()
		if families != nil {
			spec.Families = families
		}
		svc, err = s.Serve(ctx, spec)
	} else {
		if families != nil {
			previous.Spec.Families = families
		}
		svc, err = previous.Restart(ctx)
	}
	if err != nil {
		return nil, err
	}
	if _, err = svc.Acquire(); err != nil {
		svc.Stop()
		return nil, err
	}
	svc.KeepAuthority(ctx)
	st, err := na.OpenStoreWithRetry(ctx, svc.StatePath)
	if err != nil {
		svc.Stop()
		return nil, err
	}
	return &Phase{s: s, Svc: svc, St: st, wait: na.Wait{Ceiling: Ceiling, Stall: na.StallBudget(), Terminal: svc.Exited}}, nil
}

// Until polls the journal until done; the signature is the review's game day
// and what done reports through detail, so a broken phase stalls and a long
// build does not.
func (p *Phase) Until(ctx context.Context, what string, done func(review store.Rounds) (detail string, ok bool, err error)) error {
	err := na.WaitProgress(ctx, p.wait, func(ctx context.Context) (string, bool, error) {
		review, err := p.St.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review"), false, nil
		}
		detail, ok, err := done(review)
		return na.Signature(detail, review.Tick/na.TicksPerDay), ok, err
	})
	if err != nil {
		return fmt.Errorf("%s: %w", what, err)
	}
	return nil
}

// Plan waits for the recorded layout plan to satisfy want and returns it.
func (p *Phase) Plan(ctx context.Context, what string, want func(policy.LayoutPlan) bool) (policy.LayoutPlan, error) {
	var plan policy.LayoutPlan
	err := p.Until(ctx, what, func(review store.Rounds) (string, bool, error) {
		if review.Revision == 0 {
			return "no-review", false, nil // the controller has not reviewed yet: no world identity to scope the plan by
		}
		record, ok, err := p.St.LayoutPlan(ctx, review.Snapshot, review.Tick)
		if err != nil || !ok {
			return "no-plan", false, err
		}
		if !want(record.Plan) {
			return fmt.Sprint("plan-", record.Tick), false, nil
		}
		plan = record.Plan
		return "plan", true, nil
	})
	return plan, err
}

// Stop ends the phase and takes the bridge slot back, paused.
func (p *Phase) Stop(ctx context.Context, label string) (*na.Harness, error) {
	p.St.Close()
	p.s.Report()[label] = p.Svc.Stop()
	h, err := p.s.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause-"+label, "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	return h, nil
}

// Abort ends a failed phase without taking the slot back.
func (p *Phase) Abort() {
	p.St.Close()
	p.Svc.Stop()
}

// FirstRoom is the first room of role in plan order, the room the planners
// serve first.
func FirstRoom(plan policy.LayoutPlan, role policy.PlannedRole) (policy.PlannedRoom, bool) {
	for _, r := range plan.AllRooms() {
		if r.Role == role {
			return r, true
		}
	}
	return policy.PlannedRoom{}, false
}

// FreeRect is a width by height rectangle near centre that no planned room
// (walls included, with a margin of two) and no hallway (margin one) touches,
// where a case lays stock and a stockpile the plan's own sites never meet.
func FreeRect(plan policy.LayoutPlan, centre Cell, width, height int32) (Rect, bool) {
	blocked := func(x, z int32) bool {
		for _, r := range plan.AllRooms() {
			in := r.Interior
			if x >= in.X-3 && x <= in.X+in.Width+2 && z >= in.Z-3 && z <= in.Z+in.Height+2 {
				return true
			}
		}
		for _, h := range plan.Hallways() {
			if x >= min(h.From.X, h.To.X)-1 && x <= max(h.From.X, h.To.X)+1 && z >= min(h.From.Z, h.To.Z)-1 && z <= max(h.From.Z, h.To.Z)+1 {
				return true
			}
		}
		return false
	}
	for radius := int32(8); radius <= 40; radius++ {
		for dx := -radius; dx <= radius; dx++ {
			for dz := -radius; dz <= radius; dz++ {
				if max(abs(dx), abs(dz)) != radius {
					continue
				}
				x0, z0 := centre.X+dx, centre.Z+dz
				if x0 < 2 || z0 < 2 || x0+width >= na.LabMapSize-2 || z0+height >= na.LabMapSize-2 {
					continue
				}
				free := true
				for x := x0; x < x0+width && free; x++ {
					for z := z0; z < z0+height && free; z++ {
						free = !blocked(x, z)
					}
				}
				if free {
					return Rect{MinX: x0, MinZ: z0, MaxX: x0 + width - 1, MaxZ: z0 + height - 1}, true
				}
			}
		}
	}
	return Rect{}, false
}

func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}
