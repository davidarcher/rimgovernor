package bridge

import (
	"context"
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

// thickRoof is the native overhead-mountain roof.
const thickRoof = "RoofRockThick"

// ReadMapSurvey reads every cell of the map once, with its foundation
// (#727), in row bands of at most mapSurveyBand cells, for the master
// layout plan. Fogged cells are left out and score as unbuildable.
func (client *Client) ReadMapSurvey(ctx context.Context, identity *c.Identity, bounds policy.Bounds) (policy.MapSurvey, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return policy.MapSurvey{}, Result{}, err
	}
	if bounds.Width < 1 || bounds.Height < 1 {
		return policy.MapSurvey{}, Result{}, contract("invalid map survey bounds")
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
		out.Cells = append(out.Cells, surveyCells(read)...)
	}
	// The survey read's wall time (#1280) is what an hourly replan pays.
	slog.Default().InfoContext(ctx, "map survey read", telemetry.ComponentKey, "layout", "cells", len(out.Cells), "bands", bands, "ms", time.Since(start).Milliseconds())
	return out, last, nil
}

// surveyCells decodes a survey band's held cells.
func surveyCells(read cellsRead) []policy.SurveyCell {
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
		cells = append(cells, policy.SurveyCell{
			Cell:       cell.Cell,
			Walkable:   value(cell.Walkable),
			Rock:       rock,
			Built:      edifice != "",
			Footing:    footing,
			Bridgeable: ground&foundationBridgeable != 0,
			Dries:      ground&foundationDries != 0,
			ThickRoof:  roof == thickRoof,
			Fertility:  value(cell.Fertility),
			Ore:        ground&foundationOre != 0,
			Tree:       ground&foundationTree != 0,
			// Occupied off rock, player edifice and clearable ruin is a
			// standing prop (#1533).
			Prop: value(cell.Occupied) && !rock && edifice == "" && !ruin,
		})
	}
	return cells
}

// value is a fact's value, the zero value when unknown.
func value[T any](f domain.Fact[T]) T {
	v, _ := f.Value()
	return v
}
