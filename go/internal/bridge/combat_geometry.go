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
// Named plus proposed cells (#871) share the cells cap.
const (
	CombatGeometryMaxCells    = 64
	CombatGeometryMaxHostiles = 16
	// CombatGeometryMaxRadius bounds a firing_cells proposal's search
	// square around its from cell (#871): at most 25x25 cells scanned.
	CombatGeometryMaxRadius = 12
)

// CombatGeometry is DecideCombat's one geometry read per stop (#851): for
// candidate cells and hostiles, cover, line of fire and colonist-in-path
// by the game's own rules, and pawnID's path ticks to each cell when
// pawnID is set. The reply's cells and lines are in request order; with
// a propose block (#871) its proposed cells follow, ranked, scored alike.
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
// 1..CombatGeometryMaxCells distinct cells (0..cap-1 with propose),
// 1..CombatGeometryMaxHostiles distinct hostile ids, a valid pawn id when
// one is named, and a propose block's role and anchor.
func ValidateCombatGeometryRequest(request *mp.CombatGeometryRequest) error {
	if request == nil {
		return contract("combat geometry request required")
	}
	if err := ValidateIdentity(request.Identity); err != nil {
		return err
	}
	min := 1
	if request.Propose != nil {
		min = 0
		if len(request.Cells) >= CombatGeometryMaxCells {
			return contract("combat geometry propose leaves no room under %d cells", CombatGeometryMaxCells)
		}
	}
	if err := geometryCells(request.Cells, min); err != nil {
		return err
	}
	if n := len(request.HostileIds); n < 1 || n > CombatGeometryMaxHostiles {
		return contract("combat geometry hostiles outside 1..%d", CombatGeometryMaxHostiles)
	}
	if err := combatIDs(request.HostileIds); err != nil {
		return err
	}
	if request.PawnId != nil && validID(request.GetPawnId()) != nil {
		return contract("combat geometry pawn id invalid")
	}
	if request.GetPropose().GetRescuePath() != nil && request.PawnId == nil {
		return contract("combat geometry rescue_path needs a pawn")
	}
	if request.Propose != nil {
		return validatePropose(request.Propose)
	}
	return nil
}

// geometryCells checks min..cap distinct, valid cells.
func geometryCells(cells []*c.Cell, min int) error {
	if n := len(cells); n < min || n > CombatGeometryMaxCells {
		return contract("combat geometry cells outside %d..%d", min, CombatGeometryMaxCells)
	}
	seen := map[[2]int32]bool{}
	for _, cell := range cells {
		if !geometryCell(cell) {
			return contract("combat geometry cell invalid")
		}
		key := [2]int32{cell.GetX(), cell.GetZ()}
		if seen[key] {
			return contract("combat geometry cell repeated")
		}
		seen[key] = true
	}
	return nil
}

func geometryCell(cell *c.Cell) bool {
	return cell != nil && cell.X != nil && cell.Z != nil && cell.GetX() >= 0 && cell.GetZ() >= 0
}

// validatePropose checks one role and its anchor (#871).
func validatePropose(p *mp.CombatGeometryPropose) error {
	switch role := p.Role.(type) {
	case *mp.CombatGeometryPropose_CoverBehindLine:
		return geometryCells(role.CoverBehindLine.GetLine(), 1)
	case *mp.CombatGeometryPropose_AdjacentToChoke:
		a := role.AdjacentToChoke
		if !geometryCell(a.GetChoke()) || !geometryCell(a.GetOurSide()) || proto.Equal(a.GetChoke(), a.GetOurSide()) {
			return contract("combat geometry adjacent_to_choke needs a choke and a distinct our_side cell")
		}
		return nil
	case *mp.CombatGeometryPropose_RescuePath:
		if !geometryCell(role.RescuePath.GetTo()) {
			return contract("combat geometry rescue_path needs a to cell")
		}
		return nil
	case *mp.CombatGeometryPropose_FiringCells:
		f := role.FiringCells
		if !geometryCell(f.GetFrom()) {
			return contract("combat geometry firing_cells needs a from cell")
		}
		if r := f.GetRadius(); f.Radius == nil || r < 1 || r > CombatGeometryMaxRadius {
			return contract("combat geometry firing_cells radius outside 1..%d", CombatGeometryMaxRadius)
		}
		if len(f.GetTargets()) > CombatGeometryMaxHostiles {
			return contract("combat geometry firing_cells targets over %d", CombatGeometryMaxHostiles)
		}
		return geometryCells(f.GetTargets(), 1)
	}
	return contract("combat geometry propose without a role")
}

// ValidateCombatGeometry checks a reply against its request: the request's
// world, every cell in order, every hostile's line in order, cover in 0..1,
// and proposals only for a propose block: standable, distinct from each
// other and the named cells, within the cells cap.
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
	seen := map[[2]int32]bool{}
	for i, row := range g.Cells {
		if !proto.Equal(row.GetCell(), request.Cells[i]) {
			return contract("combat geometry cell %d off its request", i)
		}
		seen[[2]int32{row.GetCell().GetX(), row.GetCell().GetZ()}] = true
		if err := geometryLines(row, request, i); err != nil {
			return err
		}
	}
	if request.Propose == nil {
		if len(g.Proposed) != 0 {
			return contract("combat geometry proposals without propose")
		}
		return nil
	}
	if len(g.Proposed) > CombatGeometryMaxCells-len(request.Cells) {
		return contract("combat geometry proposals over the cells cap")
	}
	for i, row := range g.Proposed {
		cell := row.GetCell()
		key := [2]int32{cell.GetX(), cell.GetZ()}
		route := request.GetPropose().GetRescuePath() != nil
		if !geometryCell(cell) || (!route && (!row.GetStandable() || seen[key])) {
			return contract("combat geometry proposal %d invalid or repeated", i)
		}
		if !route {
			seen[key] = true
		}
		if err := geometryLines(row, request, len(request.Cells)+i); err != nil {
			return err
		}
	}
	return nil
}

// geometryLines checks one scored row: a line per request hostile in
// order, cover in 0..1, path ticks only for a pawn.
func geometryLines(row *mp.CombatGeometryCell, request *mp.CombatGeometryRequest, i int) error {
	if len(row.Lines) != len(request.HostileIds) {
		return contract("combat geometry cell %d lines off its request", i)
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

// CombatGeometryProposeAsk is a request whose cells are all proposed by
// the native for propose's role (#871), scored against hostiles.
func CombatGeometryProposeAsk(identity *c.Identity, propose *mp.CombatGeometryPropose, hostiles []string, pawnID string) *mp.CombatGeometryRequest {
	request := CombatGeometryAsk(identity, nil, hostiles, pawnID)
	request.Propose = propose
	return request
}
