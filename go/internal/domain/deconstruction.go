package domain

import (
	"encoding/json"
	"errors"
)

const DeconstructionAction ActionKind = "deconstruction"

// Deconstruction designates one observed building (a DECONSTRUCT Designate).
// Native safety and identity are checked at apply; applied means designated.
type Deconstruction struct {
	target, definition string
	cell               Cell
	ground             string // canonical JSON []GroundRect, empty without cleared ground
	wall               bool   // swap the door for a wall (#1245)
}

// WithWallReplacement returns the deconstruction swapping its door for a
// wall of the door's stuff (#1245): native places the wall blueprint and
// orders a builder through both jobs.
func (c Deconstruction) WithWallReplacement() Deconstruction { c.wall = true; return c }

// ReplacesWithWall reports a door-to-wall swap.
func (c Deconstruction) ReplacesWithWall() bool { return c.wall }

// GroundRect is one rectangle of cleared ground (#1366): Width x Height
// cells from Origin.
type GroundRect struct {
	Origin        Cell
	Width, Height int32
}

// WithClearedGround returns the deconstruction carrying the ground clearance
// is emptying (#1366): native then allows a player wall or door whose every
// enclosed room lies inside it, and holds the pawns while those rooms keep
// roof. Rectangles keep their order; none clears the ground.
func (c Deconstruction) WithClearedGround(rects []GroundRect) (Deconstruction, error) {
	c.ground = ""
	if len(rects) == 0 {
		return c, nil
	}
	for _, r := range rects {
		if r.Origin.X < 0 || r.Origin.Z < 0 || r.Width <= 0 || r.Height <= 0 || r.Width > 4096 || r.Height > 4096 {
			return Deconstruction{}, errors.New("invalid cleared ground rectangle")
		}
	}
	data, _ := json.Marshal(rects)
	c.ground = string(data)
	return c, nil
}

func (c Deconstruction) ClearedGround() []GroundRect {
	if c.ground == "" {
		return nil
	}
	var rects []GroundRect
	_ = json.Unmarshal([]byte(c.ground), &rects)
	return rects
}

func NewDeconstruction(target, definition string, cell Cell) (Deconstruction, error) {
	if !validID(target) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Deconstruction{}, errors.New("invalid deconstruction identity or cell")
	}
	return Deconstruction{target: target, definition: definition, cell: cell}, nil
}

func (c Deconstruction) Target() string     { return c.target }
func (c Deconstruction) Definition() string { return c.definition }
func (c Deconstruction) Cell() Cell         { return c.cell }
func NewDeconstructionAction(id ActionID, cut Deconstruction) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	base, err := NewDeconstruction(cut.target, cut.definition, cut.cell)
	if err != nil {
		return Action{}, err
	}
	if cut.wall {
		base = base.WithWallReplacement()
	}
	if canonical, err := base.WithClearedGround(cut.ClearedGround()); err != nil || canonical != cut {
		return Action{}, errors.New("invalid deconstruction cleared ground")
	}
	return Action{id: id, kind: DeconstructionAction, deconstruction: cut}, nil
}
func (a Action) Deconstruction() (Deconstruction, bool) {
	return a.deconstruction, a.kind == DeconstructionAction
}
