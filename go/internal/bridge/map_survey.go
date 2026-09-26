package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// mapSurveyFields is the planning window's fields plus foundation (#727):
// whether the terrain takes a wall, which is what tells marsh, mud and
// water from buildable ground.
func mapSurveyFields() *o.CellFields {
	f := planningWindowFields()
	f.Foundation = proto.Bool(true)
	return f
}

// mapSurveyBand is the most cells one survey band asks for: ProtoJSON
// carries rows as base64 and fertility as decimal text, so a full
// planningWindowPage band of fertile ground nears the 1 MiB envelope.
const mapSurveyBand = 16384

// thickRoof is the native overhead-mountain roof.
const thickRoof = "RoofRockThick"

// ReadMapSurvey reads every cell of the map once, in compact row bands of
// at most mapSurveyBand cells, for the master layout plan (#727).
// Fogged cells are left out and score as unbuildable. A native without
// the foundation field refuses the request (ErrRefused).
func (client *Client) ReadMapSurvey(ctx context.Context, identity *c.Identity, bounds policy.Bounds) (policy.MapSurvey, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return policy.MapSurvey{}, Result{}, err
	}
	if bounds.Width < 1 || bounds.Height < 1 {
		return policy.MapSurvey{}, Result{}, contract("invalid map survey bounds")
	}
	out := policy.MapSurvey{Bounds: bounds}
	rows := max(mapSurveyBand/int(bounds.Width), 1)
	var last Result
	var context *c.ObservationContext
	for z := int32(0); z < bounds.Height; z += int32(rows) {
		band := policy.Rectangle{Z: z, Width: bounds.Width, Height: min(int32(rows), bounds.Height-z)}
		snapshot, raw, err := client.readCellsBand(ctx, identity, band, 0, mapSurveyFields())
		last = raw
		if err != nil {
			return policy.MapSurvey{}, raw, err
		}
		if context != nil && !proto.Equal(context, snapshot.Context) {
			return policy.MapSurvey{}, raw, contract("map survey bands differ in context")
		}
		context = snapshot.Context
		out.Cells = append(out.Cells, SurveyCells(snapshot)...)
	}
	return out, last, nil
}

// SurveyCells decodes a validated survey snapshot's visible rows.
func SurveyCells(v *o.CellsSnapshot) []policy.SurveyCell {
	cells := make([]policy.SurveyCell, 0, len(v.GetCells()))
	for _, row := range v.GetCells() {
		if row.GetFogged() {
			continue
		}
		rock := row.GetNaturalRock()
		cells = append(cells, policy.SurveyCell{
			Cell:      domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()},
			Walkable:  row.GetWalkable(),
			Rock:      rock,
			Marsh:     !rock && row.SupportsHeavy != nil && !row.GetSupportsHeavy(),
			ThickRoof: row.GetRoof() == thickRoof,
			Fertility: row.GetFertility(),
		})
	}
	return cells
}
