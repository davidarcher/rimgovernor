package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

// BuildingPreview contains native placement facts only. Runtime supplies observed
// map bounds and complete cross-plan commitments before policy admission.
type BuildingPreview struct {
	Preview policy.Preview
	Stock   policy.StockObservation
	// NativeWorkPending reports a blueprint or frame already on the
	// footprint: native construction, not a new placement, is what changes
	// the cell next (the game's own auto-rebuild of a destroyed building,
	// a trap's auto-rearm blueprint, an earlier order still in progress).
	NativeWorkPending bool
}

// PreviewBuilding binds a single native evaluation to the exact immutable action
// and authority generation. It neither acquires authority nor reserves resources.
func (caller *Client) PreviewBuilding(ctx context.Context, action domain.Action, snapshot domain.GenerationSnapshot) (BuildingPreview, Result, error) {
	if err := snapshot.Validate(); err != nil || snapshot.Native == 0 {
		return BuildingPreview{}, Result{}, contract("building preview snapshot")
	}
	b, ok := action.Building()
	if !ok {
		return BuildingPreview{}, Result{}, contract("building action required")
	}
	if _, err := domain.NewBuildingAction(action.ID(), b); err != nil {
		return BuildingPreview{}, Result{}, err
	}
	rotation := map[domain.Rotation]p.Rotation{domain.North: p.Rotation_ROTATION_NORTH, domain.East: p.Rotation_ROTATION_EAST, domain.South: p.Rotation_ROTATION_SOUTH, domain.West: p.Rotation_ROTATION_WEST}[b.Rotation()]
	request := &p.PlacementRequest{
		Identity:   &c.Identity{ColonyId: proto.String(string(snapshot.Colony)), LoadToken: proto.String(string(snapshot.Load)), MapId: proto.Int32(int32(snapshot.Map))},
		Placements: []*p.PlacementCandidate{{DefName: proto.String(b.Definition()), Stuff: proto.String(b.Stuff()), X: proto.Int32(b.Cell().X), Z: proto.Int32(b.Cell().Z), Rotation: rotation.Enum()}},
	}
	reply, raw, err := caller.PlacementPreviews(ctx, request)
	if err != nil {
		return BuildingPreview{}, raw, err
	}
	batch := reply.GetBatch()
	if batch.Context.NativeGeneration == nil || batch.Context.GetNativeGeneration() != uint64(snapshot.Native) {
		return BuildingPreview{}, raw, contract("building preview generation changed or unavailable")
	}
	row := batch.Results[0]
	if row.GetFailure() != nil {
		return BuildingPreview{}, raw, failure(row.GetFailure(), raw)
	}
	evaluation := row.GetEvaluated()
	orientation := evaluation.Rotations[0]
	tick := domain.Tick(batch.Context.GetTick())
	out := BuildingPreview{Preview: policy.Preview{Action: action, Snapshot: snapshot, Tick: tick}, Stock: policy.StockObservation{Snapshot: snapshot, Tick: tick}}
	out.Preview.CanPlace = domain.Known(evaluation.GetCanPlace() && orientation.GetAccepted() && evaluation.GetResearchFinished() && evaluation.GetBuildableByPlayer())
	out.Preview.MadeFromStuff = domain.Known(evaluation.GetMadeFromStuff())
	if orientation.WatchCellsAccessible != nil {
		out.Preview.WatchCellsAccessible = domain.Known(orientation.GetWatchCellsAccessible())
	}
	if orientation.WindBlockedCells != nil {
		out.Preview.WindBlockedCells = domain.Known(int32(min(orientation.GetWindBlockedCells(), 4096)))
	}
	safe := true
	for _, blocker := range orientation.BlockingThings {
		replaceConduit := b.Definition() == "HiddenConduit" && blocker.GetDefName() == "PowerConduit" && blocker.GetCategory() == "Building" && !blocker.GetIsBlueprint() && !blocker.GetIsFrame() && !blocker.GetFrameWouldBeCancelled()
		if blocker.GetWouldBeWiped() && !replaceConduit || blocker.GetFrameWouldBeCancelled() || blocker.GetIsBlueprint() || blocker.GetIsFrame() {
			safe = false
		}
		out.NativeWorkPending = out.NativeWorkPending || blocker.GetIsBlueprint() || blocker.GetIsFrame()
		out.Preview.Blockers = append(out.Preview.Blockers, policy.PlacementBlocker{Category: blocker.GetCategory(), Wiped: blocker.GetWouldBeWiped(), Blueprint: blocker.GetIsBlueprint(), Frame: blocker.GetIsFrame(), Cancelled: blocker.GetFrameWouldBeCancelled()})
	}
	// Even a zero-cost evaluation cannot turn an unreadable material scan into
	// authorization. The next complete preview can release this unknown hold.
	if evaluation.Materials.GetKnown() != nil {
		out.Stock.NativeConstruction = true
		out.Preview.SafeToPlace = domain.Known(safe)
		for _, material := range evaluation.Materials.GetKnown().Rows {
			stock := policy.Stock{Resource: policy.Resource(material.GetDefName())}
			if material.Available != nil {
				stock.Available = domain.Known(int64(material.GetAvailable()))
			}
			out.Stock.Values = append(out.Stock.Values, stock)
		}
	} else if !safe {
		out.Preview.SafeToPlace = domain.Known(false)
	}
	costs := make([]policy.Amount, 0, len(evaluation.CostList))
	for _, cost := range evaluation.CostList {
		costs = append(costs, policy.Amount{Resource: policy.Resource(cost.GetDefName()), Count: int64(cost.GetCount())})
	}
	out.Preview.Costs = domain.Known(costs)
	// The footprint is the placement's claim: its occupied cells plus the
	// interaction cell a worker must stand on, so a rival placement there is
	// refused instead of blocking the bench for good (a campfire on a
	// crafting spot's interaction cell held run 16 of #4 M2 forever).
	cells := make([]domain.Cell, 0, len(orientation.OccupiedCells)+len(orientation.InteractionCells))
	seen := map[domain.Cell]bool{}
	for _, cell := range append(append([]*c.Cell(nil), orientation.OccupiedCells...), orientation.InteractionCells...) {
		claim := domain.Cell{X: cell.GetX(), Z: cell.GetZ()}
		if seen[claim] {
			continue
		}
		seen[claim] = true
		cells = append(cells, claim)
	}
	out.Preview.Footprint = domain.Known(cells)
	return out, raw, nil
}
