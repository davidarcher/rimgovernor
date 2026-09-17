package bridge

import (
	"errors"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
)

func validatePlacementRequest(request *p.PlacementRequest) error {
	if request == nil || len(request.Placements) < 1 || len(request.Placements) > 16 {
		return contract("placement count")
	}
	if len(request.ProtoReflect().GetUnknown()) > 0 {
		return contract("unknown request fields")
	}
	if err := ValidateIdentity(request.Identity); err != nil {
		return err
	}
	if len(request.Identity.ProtoReflect().GetUnknown()) > 0 {
		return contract("unknown identity fields")
	}
	for _, v := range request.Placements {
		if v == nil || v.DefName == nil || v.X == nil || v.Z == nil || v.Rotation == nil || v.GetX() < 0 || v.GetZ() < 0 || v.GetRotation() < p.Rotation_ROTATION_NORTH || v.GetRotation() > p.Rotation_ROTATION_ALL {
			return contract("placement candidate presence/coordinates/rotation")
		}
		if len(v.ProtoReflect().GetUnknown()) > 0 {
			return contract("unknown candidate fields")
		}
		if err := validID(v.GetDefName()); err != nil {
			return err
		}
		if v.Stuff != nil && v.GetStuff() != "" {
			if err := validID(v.GetStuff()); err != nil {
				return err
			}
		}
	}
	return nil
}
func validatePlacementBatch(request *p.PlacementRequest, batch *p.PlacementBatch) error {
	if batch == nil {
		return contract("placement batch missing")
	}
	if err := ValidateContext(batch.Context); err != nil {
		return err
	}
	if !sameIdentity(batch.Context.Identity, request.Identity) {
		return contract("placement context mismatch")
	}
	if len(batch.Results) != len(request.Placements) {
		return contract("placement result count")
	}
	for index, row := range batch.Results {
		if row == nil {
			return contract("nil candidate result")
		}
		switch v := row.Outcome.(type) {
		case *p.CandidateReply_Failure:
			if err := failure(v.Failure, Result{}); !errors.As(err, new(*NativeFailure)) {
				return err
			}
		case *p.CandidateReply_Evaluated:
			if err := validateEvaluated(request.Placements[index], v.Evaluated); err != nil {
				return err
			}
		default:
			return contract("candidate outcome missing")
		}
	}
	return nil
}
func validateEvaluated(request *p.PlacementCandidate, v *p.PlacementEvaluated) error {
	if v == nil || v.CanPlace == nil || v.MadeFromStuff == nil || v.Passability == nil || v.IsDoor == nil || v.ResearchFinished == nil || v.BuildableByPlayer == nil || v.GetPassability() < 1 || v.GetPassability() > 3 {
		return contract("evaluated facts missing")
	}
	if len(v.CostList) > 256 {
		return contract("too many costs")
	}
	costs := map[string]bool{}
	for _, cost := range v.CostList {
		if cost == nil || cost.DefName == nil || cost.Count == nil || cost.GetCount() < 0 || validID(cost.GetDefName()) != nil || costs[cost.GetDefName()] {
			return contract("invalid cost")
		}
		costs[cost.GetDefName()] = true
	}
	if v.Materials == nil {
		return contract("materials missing")
	}
	switch m := v.Materials.Availability.(type) {
	case *p.PlacementMaterials_Known:
		if m.Known == nil || len(m.Known.Rows) > 256 {
			return contract("invalid material rows")
		}
		seen := map[string]bool{}
		for _, row := range m.Known.Rows {
			if row == nil || row.DefName == nil || validID(row.GetDefName()) != nil || seen[row.GetDefName()] || (row.Available != nil && row.GetAvailable() < 0) {
				return contract("invalid material stock")
			}
			seen[row.GetDefName()] = true
		}
	case *p.PlacementMaterials_Unavailable:
		if err := validateUnavailable(m.Unavailable); err != nil {
			return err
		}
	default:
		return contract("materials availability missing")
	}
	want := 1
	if request.GetRotation() == p.Rotation_ROTATION_ALL {
		want = 4
	}
	if len(v.Rotations) != want {
		return contract("rotation result count")
	}
	seen := map[p.Rotation]bool{}
	for _, rotation := range v.Rotations {
		if rotation == nil || rotation.Rotation == nil || rotation.Accepted == nil || rotation.GetRotation() < 1 || rotation.GetRotation() > 4 || seen[rotation.GetRotation()] || !diagnostic(rotation.Reason) {
			return contract("rotation facts invalid")
		}
		seen[rotation.GetRotation()] = true
		if want == 1 && rotation.GetRotation() != request.GetRotation() {
			return contract("rotation mismatch")
		}
		if len(rotation.OccupiedCells) == 0 || len(rotation.OccupiedCells) > 4096 || len(rotation.BlockingThings) > 4096 {
			return contract("geometry completeness/limit")
		}
		cells := map[[2]int32]bool{}
		for _, cell := range rotation.OccupiedCells {
			if err := validCell(cell); err != nil {
				return err
			}
			key := [2]int32{cell.GetX(), cell.GetZ()}
			if cells[key] {
				return contract("duplicate footprint cell")
			}
			cells[key] = true
		}
		for _, block := range rotation.BlockingThings {
			if block == nil || block.Category == nil || validID(block.GetCategory()) != nil || block.IsBlueprint == nil || block.IsFrame == nil || block.WouldBeWiped == nil || block.FrameWouldBeCancelled == nil {
				return contract("blocker facts missing")
			}
		}
	}
	return nil
}
func validCell(cell *c.Cell) error {
	if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
		return contract("cell presence/coordinates")
	}
	return nil
}
