package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

const combatGeometryMethod = "rimgovernor/combat_geometry"

// The combat.geometry caps (#851), the native's
// NativeCombatGeometryTools.MaxCells and MaxHostiles: measured on
// lab-ranged (combatlab/mirror) under the 50 ms main-thread budget:
// 64 cells x 8 pawns took 1.3-6.6 ms, about 13 ms projected at 64 x 16.
const (
	CombatGeometryMaxCells    = 64
	CombatGeometryMaxHostiles = 16
)

// CombatGeometry is DecideCombat's one geometry read per stop (#851): for
// candidate cells and hostiles, cover, line of fire and colonist-in-path
// by the game's own rules, and pawnID's path ticks to each cell when
// pawnID is set. The reply's cells and lines are in request order.
func (client *Client) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, Result, error) {
	if err := ValidateCombatGeometryRequest(request); err != nil {
		return nil, Result{}, err
	}
	reply := &mp.CombatGeometryReply{}
	raw, err := client.protoRead(ctx, combatGeometryMethod, request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *mp.CombatGeometryReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	case *mp.CombatGeometryReply_Observed:
		if err := ValidateCombatGeometry(v.Observed, request); err != nil {
			return nil, raw, err
		}
		return v.Observed, raw, nil
	}
	return nil, raw, contract("combat geometry outcome missing")
}

// ValidateCombatGeometryRequest checks the caps and shape: an identity,
// 1..CombatGeometryMaxCells distinct cells, 1..CombatGeometryMaxHostiles
// distinct hostile ids, and a valid pawn id when one is named.
func ValidateCombatGeometryRequest(request *mp.CombatGeometryRequest) error {
	if request == nil {
		return contract("combat geometry request required")
	}
	if err := ValidateIdentity(request.Identity); err != nil {
		return err
	}
	if n := len(request.Cells); n < 1 || n > CombatGeometryMaxCells {
		return contract("combat geometry cells outside 1..%d", CombatGeometryMaxCells)
	}
	if n := len(request.HostileIds); n < 1 || n > CombatGeometryMaxHostiles {
		return contract("combat geometry hostiles outside 1..%d", CombatGeometryMaxHostiles)
	}
	cells := map[[2]int32]bool{}
	for _, cell := range request.Cells {
		if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
			return contract("combat geometry cell invalid")
		}
		key := [2]int32{cell.GetX(), cell.GetZ()}
		if cells[key] {
			return contract("combat geometry cell repeated")
		}
		cells[key] = true
	}
	if err := combatIDs(request.HostileIds); err != nil {
		return err
	}
	if request.PawnId != nil && validID(request.GetPawnId()) != nil {
		return contract("combat geometry pawn id invalid")
	}
	return nil
}

// ValidateCombatGeometry checks a reply against its request: the request's
// world, every cell in order, every hostile's line in order, cover in 0..1.
func ValidateCombatGeometry(g *mp.CombatGeometry, request *mp.CombatGeometryRequest) error {
	if g == nil {
		return contract("combat geometry missing")
	}
	if err := ValidateContext(g.Context); err != nil {
		return err
	}
	if !sameIdentity(g.Context.GetIdentity(), request.Identity) {
		return contract("combat geometry world mismatch")
	}
	if len(g.Cells) != len(request.Cells) {
		return contract("combat geometry cell count")
	}
	for i, row := range g.Cells {
		if !proto.Equal(row.GetCell(), request.Cells[i]) || len(row.Lines) != len(request.HostileIds) {
			return contract("combat geometry cell %d off its request", i)
		}
		if row.PathTicks != nil && (request.PawnId == nil || row.GetPathTicks() < 0) {
			return contract("combat geometry path ticks without a pawn")
		}
		for j, line := range row.Lines {
			cover := line.GetCover()
			if line.GetHostileId() != request.HostileIds[j] || line.Cover == nil || math.IsNaN(cover) || cover < 0 || cover > 1 {
				return contract("combat geometry line %d.%d invalid", i, j)
			}
		}
	}
	return nil
}

// CombatGeometryAsk is a request for identity's cells and hostiles.
func CombatGeometryAsk(identity *c.Identity, cells []*c.Cell, hostiles []string, pawnID string) *mp.CombatGeometryRequest {
	request := &mp.CombatGeometryRequest{Identity: proto.Clone(identity).(*c.Identity), Cells: cells, HostileIds: hostiles}
	if pawnID != "" {
		request.PawnId = proto.String(pawnID)
	}
	return request
}
