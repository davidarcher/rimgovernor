package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Native bounds of observations_read_spatial_access.
const (
	maxSpatialBlockedCells = 16384
	maxSpatialTargetCells  = 128
	maxSpatialPawns        = 32
	maxSpatialLostCells    = 16
)

// AccessTarget is one requested target cell for one colonist: native
// CanReach today, and reachability inside the projected four-neighbour
// component once the blocked cells are impassable.
type AccessTarget struct {
	Cell                                domain.Cell
	NativeReachable, ProjectedReachable bool
	ProjectedSteps                      uint32
}

// PawnAccess is one mobile colonist's audit row. Lost is the count of cells
// reachable now but not in the projection (excluding the blocked cells
// themselves); LostCells lists at most 16 of them. OriginKnown is false when
// the pawn stands on a blocked cell and no safe exit exists.
type PawnAccess struct {
	ID              string
	Position        domain.Cell
	Current, After  uint32
	Lost            uint32
	LostCells       []domain.Cell
	OriginKnown     bool
	ProjectedOrigin domain.Cell
	EgressSteps     uint32
	Targets         []AccessTarget
}

type SpatialAccess struct {
	Context            *c.ObservationContext
	MapCells, Walkable uint32
	Pawns              []PawnAccess
}

// Accepted ports the legacy tool's accepted flag: no colonist loses a
// previously reachable cell or its exit, and every target stays reachable
// both natively and in the projection for at least one colonist.
func (s SpatialAccess) Accepted() bool {
	if len(s.Pawns) == 0 {
		return false
	}
	reached := map[domain.Cell]bool{}
	for _, p := range s.Pawns {
		if !p.OriginKnown || p.Lost != 0 {
			return false
		}
		for _, t := range p.Targets {
			if t.NativeReachable && t.ProjectedReachable {
				reached[t.Cell] = true
			}
		}
	}
	for _, t := range s.Pawns[0].Targets {
		if !reached[t.Cell] {
			return false
		}
	}
	return true
}

// ReadSpatialAccess audits the paused map's colonist access with blocked
// impassable and targets checked; pawnIDs empty audits every mobile free
// colonist. Unknown never becomes access: an incomplete or partial reply is
// a contract error.
func (client *Client) ReadSpatialAccess(ctx context.Context, identity *c.Identity, blocked, targets []domain.Cell, pawnIDs []string) (SpatialAccess, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return SpatialAccess{}, Result{}, err
	}
	if len(blocked) > maxSpatialBlockedCells || len(targets) > maxSpatialTargetCells || len(pawnIDs) > maxSpatialPawns {
		return SpatialAccess{}, Result{}, contract("spatial access request exceeds native bounds")
	}
	for _, cells := range [][]domain.Cell{blocked, targets} {
		seen := map[domain.Cell]bool{}
		for _, cell := range cells {
			if cell.X < 0 || cell.Z < 0 || cell.X >= maxDefenseSiteExtent || cell.Z >= maxDefenseSiteExtent || seen[cell] {
				return SpatialAccess{}, Result{}, contract("invalid spatial access cell")
			}
			seen[cell] = true
		}
	}
	seenID := map[string]bool{}
	for _, id := range pawnIDs {
		if err := validID(id); err != nil || seenID[id] {
			return SpatialAccess{}, Result{}, contract("invalid spatial access pawn id")
		}
		seenID[id] = true
	}
	request := &o.SpatialAccessRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, PawnIds: append([]string{}, pawnIDs...)}
	for _, cell := range blocked {
		request.BlockedCells = append(request.BlockedCells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	for _, cell := range targets {
		request.TargetCells = append(request.TargetCells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	reply := &o.SpatialAccessReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_spatial_access", request, reply)
	if err != nil {
		return SpatialAccess{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return SpatialAccess{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.SpatialAccessReply_Failure:
		return SpatialAccess{}, raw, failure(v.Failure, raw)
	case *o.SpatialAccessReply_Unavailable:
		return SpatialAccess{}, raw, unavailable(v.Unavailable, raw)
	case *o.SpatialAccessReply_Observed:
		access, err := validateSpatialAccess(v.Observed, identity, blocked, targets, pawnIDs)
		if err != nil {
			return SpatialAccess{}, raw, err
		}
		return access, raw, nil
	default:
		return SpatialAccess{}, raw, contract("spatial access outcome missing")
	}
}

func validateSpatialAccess(v *o.SpatialAccessSnapshot, identity *c.Identity, blocked, targets []domain.Cell, pawnIDs []string) (SpatialAccess, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return SpatialAccess{}, contract("invalid spatial access context")
	}
	if v.MapCells == nil || v.ObservedWalkableCells == nil || v.GetMapCells() == 0 || v.GetObservedWalkableCells() > v.GetMapCells() {
		return SpatialAccess{}, contract("spatial access map census missing")
	}
	if len(v.Pawns) == 0 || len(v.Pawns) > maxSpatialPawns || !completeCount(v.Completeness, len(v.Pawns)) || len(pawnIDs) != 0 && len(v.Pawns) != len(pawnIDs) {
		return SpatialAccess{}, contract("incomplete spatial access census")
	}
	wanted := map[string]bool{}
	for _, id := range pawnIDs {
		wanted[id] = true
	}
	blockedSet := map[domain.Cell]bool{}
	for _, cell := range blocked {
		blockedSet[cell] = true
	}
	access := SpatialAccess{Context: v.Context, MapCells: v.GetMapCells(), Walkable: v.GetObservedWalkableCells()}
	seen := map[string]bool{}
	for _, row := range v.Pawns {
		if row == nil || row.Pawn == nil || row.Pawn.Id == nil || validID(row.Pawn.GetId()) != nil || seen[row.Pawn.GetId()] || len(pawnIDs) != 0 && !wanted[row.Pawn.GetId()] {
			return SpatialAccess{}, contract("invalid spatial access pawn")
		}
		seen[row.Pawn.GetId()] = true
		position, pk := protoCell(row.Pawn.Position)
		if !pk || row.CurrentCells == nil || row.ProjectedCells == nil || row.LostCellCount == nil || row.EgressSteps == nil || !completeCount(row.Completeness, len(targets)) || len(row.Targets) != len(targets) {
			return SpatialAccess{}, contract("incomplete spatial access row")
		}
		p := PawnAccess{ID: row.Pawn.GetId(), Position: position, Current: row.GetCurrentCells(), After: row.GetProjectedCells(), Lost: row.GetLostCellCount(), EgressSteps: row.GetEgressSteps()}
		if p.After > p.Current || p.Lost > p.Current || len(row.LostCells) > maxSpatialLostCells || len(row.LostCells) > int(p.Lost) || p.Lost != 0 && len(row.LostCells) == 0 {
			return SpatialAccess{}, contract("inconsistent spatial access counts")
		}
		if row.ProjectedOrigin != nil {
			origin, ok := protoCell(row.ProjectedOrigin)
			if !ok || blockedSet[origin] {
				return SpatialAccess{}, contract("invalid projected origin")
			}
			p.OriginKnown, p.ProjectedOrigin = true, origin
			if origin == position && p.EgressSteps != 0 || origin != position && (p.EgressSteps == 0 || !blockedSet[position]) {
				return SpatialAccess{}, contract("projected origin disagrees with egress")
			}
		} else if p.After != 0 || !blockedSet[position] {
			return SpatialAccess{}, contract("missing projected origin")
		}
		lostSeen := map[domain.Cell]bool{}
		for _, raw := range row.LostCells {
			cell, ok := protoCell(raw)
			if !ok || lostSeen[cell] || blockedSet[cell] {
				return SpatialAccess{}, contract("invalid lost cell")
			}
			lostSeen[cell] = true
			p.LostCells = append(p.LostCells, cell)
		}
		for i, raw := range row.Targets {
			cell, ok := protoCell(raw.GetCell())
			if raw == nil || !ok || cell != targets[i] || raw.NativeReachable == nil || raw.ProjectedReachable == nil {
				return SpatialAccess{}, contract("spatial access target mismatch")
			}
			t := AccessTarget{Cell: cell, NativeReachable: raw.GetNativeReachable(), ProjectedReachable: raw.GetProjectedReachable()}
			if t.ProjectedReachable != (raw.ProjectedSteps != nil) || t.ProjectedReachable && (!p.OriginKnown || blockedSet[cell]) {
				return SpatialAccess{}, contract("projected steps disagree with reachability")
			}
			t.ProjectedSteps = raw.GetProjectedSteps()
			p.Targets = append(p.Targets, t)
		}
		access.Pawns = append(access.Pawns, p)
	}
	return access, nil
}
