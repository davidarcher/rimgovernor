package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// excavationSiteLimit mirrors NativeExcavationSite.MaxCells.
const excavationSiteLimit = 64

// ExcavationSiteCell is one requested rock cell as native saw it. Fogged
// cells carry no rock, roof or eligibility facts: they are unknown, never
// assumed empty or safe.
type ExcavationSiteCell struct {
	Cell           domain.Cell
	Fogged         bool
	Definition     string
	HitPoints      int32
	Roof           string
	HoldsRoof      bool
	Walkable       bool
	MineDesignated bool
	Eligible       bool
	Blocker        string
	Token          string
}

// ExcavationSite is the site-level answer for one ordered cell set: the
// counterfactual roof support once every requested cell is gone, plus worker
// access to the access cell.
type ExcavationSite struct {
	Context          *c.ObservationContext
	Cells            []ExcavationSiteCell
	Support          policy.ExcavationSupport
	RoofCellsChecked uint32
	SupportBlocker   string
	CollapsePending  bool
	WorkerAvailable  bool
	Workers          []string
	AccessReachable  bool
}

func excavationCellValid(cell domain.Cell) bool { return cell.X >= 0 && cell.Z >= 0 }

// ReadExcavationSite reads rimgovernor/observations_read_excavation_site
// for the ordered cell set plus its access cell. Reply rows must echo the
// request order exactly so callers can index stages positionally.
func (client *Client) ReadExcavationSite(ctx context.Context, identity *c.Identity, cells []domain.Cell, access domain.Cell) (ExcavationSite, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ExcavationSite{}, Result{}, err
	}
	if len(cells) == 0 || len(cells) > excavationSiteLimit || !excavationCellValid(access) {
		return ExcavationSite{}, Result{}, contract("invalid excavation site request")
	}
	request := &o.ExcavationSiteRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, AccessCell: &c.Cell{X: proto.Int32(access.X), Z: proto.Int32(access.Z)}}
	seen := map[domain.Cell]bool{}
	for _, cell := range cells {
		if !excavationCellValid(cell) || seen[cell] {
			return ExcavationSite{}, Result{}, contract("invalid excavation site request")
		}
		seen[cell] = true
		request.Cells = append(request.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	reply := &o.ExcavationSiteReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_excavation_site", request, reply)
	if err != nil {
		return ExcavationSite{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return ExcavationSite{}, raw, err
	}
	var snapshot *o.ExcavationSiteSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ExcavationSiteReply_Observed:
		snapshot = v.Observed
	case *o.ExcavationSiteReply_Unavailable:
		return ExcavationSite{}, raw, unavailable(v.Unavailable, raw)
	case *o.ExcavationSiteReply_Failure:
		return ExcavationSite{}, raw, failure(v.Failure, raw)
	default:
		return ExcavationSite{}, raw, contract("missing excavation site outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return ExcavationSite{}, raw, contract("invalid excavation site context")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Cells) != len(cells) {
		return ExcavationSite{}, raw, contract("incomplete excavation site")
	}
	out := ExcavationSite{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), Cells: make([]ExcavationSiteCell, 0, len(cells))}
	for i, row := range snapshot.Cells {
		position := row.GetCell()
		if row == nil || position == nil || position.X == nil || position.Z == nil || position.GetX() != cells[i].X || position.GetZ() != cells[i].Z || row.Fogged == nil || row.Eligible == nil || row.MineDesignated == nil {
			return ExcavationSite{}, raw, contract("excavation site row mismatch")
		}
		item := ExcavationSiteCell{Cell: cells[i], Fogged: row.GetFogged(), Definition: row.GetMineableDefName(), HitPoints: row.GetHitPoints(), Roof: row.GetRoofDefName(), HoldsRoof: row.GetHoldsRoof(), Walkable: row.GetWalkable(), MineDesignated: row.GetMineDesignated(), Eligible: row.GetEligible(), Blocker: row.GetBlocker(), Token: row.GetSnapshot().GetToken()}
		if item.Fogged && (item.Definition != "" || item.Eligible || item.Token != "") {
			return ExcavationSite{}, raw, contract("fogged excavation cell carries facts")
		}
		if !item.Fogged && item.Definition != "" && (validID(item.Definition) != nil || validID(item.Token) != nil) {
			return ExcavationSite{}, raw, contract("excavation cell snapshot unavailable")
		}
		// An open cell carries a snapshot too, so an excavation of a cell the
		// pawns already cleared can be adopted as done.
		if !item.Fogged && item.Definition == "" && item.Token != "" && validID(item.Token) != nil {
			return ExcavationSite{}, raw, contract("excavation cell snapshot unavailable")
		}
		if item.Eligible && (item.Definition == "" || item.Blocker != "") {
			return ExcavationSite{}, raw, contract("inconsistent excavation eligibility")
		}
		out.Cells = append(out.Cells, item)
	}
	switch snapshot.GetSupportAfterRemoval() {
	case o.ExcavationSupport_EXCAVATION_SUPPORT_SUPPORTED:
		out.Support = policy.ExcavationSupportSupported
	case o.ExcavationSupport_EXCAVATION_SUPPORT_UNKNOWN:
		out.Support = policy.ExcavationSupportUnknown
	case o.ExcavationSupport_EXCAVATION_SUPPORT_UNSUPPORTED:
		out.Support = policy.ExcavationSupportUnsupported
	default:
		return ExcavationSite{}, raw, contract("excavation support unspecified")
	}
	out.RoofCellsChecked, out.SupportBlocker, out.CollapsePending = snapshot.GetRoofCellsChecked(), snapshot.GetSupportBlocker(), snapshot.GetCollapsePending()
	if out.CollapsePending && out.Support != policy.ExcavationSupportUnsupported {
		return ExcavationSite{}, raw, contract("pending collapse reported supported")
	}
	if snapshot.WorkerAvailable == nil || snapshot.AccessReachable == nil || len(snapshot.WorkerIds) > 32 {
		return ExcavationSite{}, raw, contract("excavation worker facts missing")
	}
	out.WorkerAvailable, out.AccessReachable = snapshot.GetWorkerAvailable(), snapshot.GetAccessReachable()
	for _, id := range snapshot.WorkerIds {
		if validID(id) != nil {
			return ExcavationSite{}, raw, contract("invalid excavation worker")
		}
		out.Workers = append(out.Workers, id)
	}
	if out.WorkerAvailable != (len(out.Workers) > 0) || out.WorkerAvailable && !out.AccessReachable {
		return ExcavationSite{}, raw, contract("inconsistent excavation workers")
	}
	return out, raw, nil
}
