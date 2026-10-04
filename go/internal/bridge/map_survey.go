package bridge

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// mapSurveyBand is the most cells one survey band asks for: ProtoJSON
// carries a grid's arrays as base64 and its numbers as decimal text, so a
// whole map in one reply is large.
const mapSurveyBand = 16384

// ReadMapSurvey reads every cell of the map once, with its foundation
// (#727), in row bands of at most mapSurveyBand cells, for the master
// layout plan. A fogged cell is not held; it reads as solid rock to mine
// out, since the fog hides mountain far more often than a cavern, and the
// excavation steps check the cell once it is seen.
func (client *Client) ReadMapSurvey(ctx context.Context, identity *c.Identity, bounds policy.Bounds) (policy.MapSurvey, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return policy.MapSurvey{}, Result{}, err
	}
	if bounds.Width < 1 || bounds.Height < 1 {
		return policy.MapSurvey{}, Result{}, contract("invalid map survey bounds")
	}
	catalog, err := client.DefinitionCatalog(ctx, identity)
	if err != nil {
		return policy.MapSurvey{}, Result{}, err
	}
	roofs, err := catalog.RoofRules()
	if err != nil {
		return policy.MapSurvey{}, Result{}, err
	}
	out := policy.MapSurvey{Bounds: bounds}
	start, bands := time.Now(), 0
	rows := max(mapSurveyBand/int(bounds.Width), 1)
	var last Result
	var context *c.ObservationContext
	for z := int32(0); z < bounds.Height; z += int32(rows) {
		band := policy.Rectangle{Z: z, Width: bounds.Width, Height: min(int32(rows), bounds.Height-z)}
		bands++
		read, raw, err := client.readCells(ctx, identity, band, true, false)
		last = raw
		if err != nil {
			return policy.MapSurvey{}, raw, err
		}
		// A live clock moves the tick between bands; the terrain a layout
		// plans on does not, so only the world and its generation must hold.
		if context != nil && (!proto.Equal(context.Identity, read.Context.GetIdentity()) || context.GetNativeGeneration() != read.Context.GetNativeGeneration()) {
			return policy.MapSurvey{}, raw, contract("map survey bands differ in world")
		}
		context = read.Context
		cells, err := surveyCells(read, roofs)
		if err != nil {
			return policy.MapSurvey{}, raw, err
		}
		out.Cells = append(out.Cells, cells...)
	}
	out.Cells = append(out.Cells, unseenRock(out.Cells, bounds)...)
	// The survey read's wall time (#1280) is what an hourly replan pays.
	slog.Default().InfoContext(ctx, "map survey read", telemetry.ComponentKey, "layout", "cells", len(out.Cells), "bands", bands, "ms", time.Since(start).Milliseconds())
	return out, last, nil
}

// unseenRock is a rock cell for every cell of bounds that read left out
// (fog hides it): plannable ground, mined out like the stone around it.
func unseenRock(held []policy.SurveyCell, bounds policy.Bounds) []policy.SurveyCell {
	seen := make([]bool, int(bounds.Width)*int(bounds.Height))
	for _, c := range held {
		if c.Cell.X >= 0 && c.Cell.X < bounds.Width && c.Cell.Z >= 0 && c.Cell.Z < bounds.Height {
			seen[int(c.Cell.Z)*int(bounds.Width)+int(c.Cell.X)] = true
		}
	}
	var out []policy.SurveyCell
	for i, ok := range seen {
		if !ok {
			out = append(out, policy.SurveyCell{Cell: domain.Cell{X: int32(i) % bounds.Width, Z: int32(i) / bounds.Width}, Rock: true, Footing: policy.FootingFirm, ThickRoof: true})
		}
	}
	return out
}

// surveyCells decodes a survey band's held cells. A thick roof is the roof
// rules' (#1890); a roof def they lack is an error wrapping
// policy.ErrUnknownRoof.
func surveyCells(read cellsRead, roofs policy.RoofRules) ([]policy.SurveyCell, error) {
	held := read.Grid.Cells()
	cells := make([]policy.SurveyCell, 0, len(held))
	for _, cell := range held {
		ground := read.foundation(cell.Cell)
		rock, ruin := value(cell.NaturalRock), value(cell.Ruin)
		edifice, roof := value(cell.PlayerEdifice), value(cell.Roof)
		footing := policy.FootingFirm
		if ground&foundationHeavy == 0 {
			footing = policy.FootingNone
			if ground&foundationLight != 0 {
				footing = policy.FootingLight
			}
		}
		rule, known := roofs[roof]
		if roof != "" && !known {
			return nil, fmt.Errorf("%w %q at %v", policy.ErrUnknownRoof, roof, cell.Cell)
		}
		cells = append(cells, policy.SurveyCell{
			Cell:       cell.Cell,
			Walkable:   value(cell.Walkable),
			Rock:       rock,
			Built:      edifice != "",
			Footing:    footing,
			Bridgeable: ground&foundationBridgeable != 0,
			Dries:      ground&foundationDries != 0,
			Hazard:     ground&foundationHazard != 0,
			ThickRoof:  rule.Thick,
			Fertility:  value(cell.Fertility),
			Ore:        ground&foundationOre != 0,
			Tree:       ground&foundationTree != 0,
			// Occupied off rock, player edifice and clearable ruin is a
			// standing prop (#1533).
			Prop: value(cell.Occupied) && !rock && edifice == "" && !ruin,
		})
	}
	return cells, nil
}

// value is a fact's value, the zero value when unknown.
func value[T any](f domain.Fact[T]) T {
	v, _ := f.Value()
	return v
}
