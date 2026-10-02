package bridge

import (
	"context"
	"fmt"

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
)

// AccessTarget is one requested target cell for one colonist: native
// CanReach today, and reachability inside the projected four-neighbour
// component once the blocked cells are impassable.
type AccessTarget struct {
	Cell                                domain.Cell
	NativeReachable, ProjectedReachable bool
	ProjectedSteps                      uint32
}

// PawnAccess is one mobile colonist's audit row. LosesAccess is true when
// some cell reachable now (other than the blocked cells themselves) is not
// reachable in the projection; it is informational, since cells stranded
// outside a perimeter wall do not matter (#1570). OriginKnown is false when the pawn stands on
// a blocked cell and no safe exit exists.
type PawnAccess struct {
	ID              string
	Position        domain.Cell
	Current, After  uint32
	LosesAccess     bool
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

// Accepted is true when every colonist keeps a safe exit and every target
// it reaches now, and every target stays reachable both natively and in
// the projection for at least one colonist. Losing other cells is allowed:
// the targets name the access the caller needs (#1570).
func (s SpatialAccess) Accepted() bool { return s.Refusal() == "" }

// Refusal names why the audit is not accepted, or "" when it is.
func (s SpatialAccess) Refusal() string {
	if len(s.Pawns) == 0 {
		return "no colonists audited"
	}
	reached := map[domain.Cell]bool{}
	for _, p := range s.Pawns {
		if !p.OriginKnown {
			return fmt.Sprintf("pawn %s at %v has no safe exit", p.ID, p.Position)
		}
		for _, t := range p.Targets {
			if t.NativeReachable && !t.ProjectedReachable {
				return fmt.Sprintf("pawn %s at %v loses target %v", p.ID, p.Position, t.Cell)
			}
			if t.NativeReachable && t.ProjectedReachable {
				reached[t.Cell] = true
			}
		}
	}
	for _, t := range s.Pawns[0].Targets {
		if !reached[t.Cell] {
			return fmt.Sprintf("target %v unreachable", t.Cell)
		}
	}
	return ""
}

// ReadSpatialAccess audits the map's colonist access (paused or running) with blocked
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
	if len(v.Pawns) == 0 || len(v.Pawns) > maxSpatialPawns || len(pawnIDs) != 0 && len(v.Pawns) != len(pawnIDs) {
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
		position, pk := protoCell(row.Position)
		if !pk || row.CurrentCells == nil || row.ProjectedCells == nil || row.LosesAccess == nil || row.EgressSteps == nil || len(row.Targets) != len(targets) {
			return SpatialAccess{}, contract("incomplete spatial access row")
		}
		p := PawnAccess{ID: row.Pawn.GetId(), Position: position, Current: row.GetCurrentCells(), After: row.GetProjectedCells(), LosesAccess: row.GetLosesAccess(), EgressSteps: row.GetEgressSteps()}
		if p.After > p.Current {
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
