package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

func validateDeepResources(v *o.ColonyFactsSnapshot) error {
	if v.DeepResources == nil {
		return nil
	}
	switch s := v.DeepResources.Outcome.(type) {
	case *o.DeepResourcesSection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.DeepResourcesSection_Observed:
		f := s.Observed
		if f == nil || len(f.Lumps) > 256 || len(f.GroundScanners)+len(f.LongRangeScanners)+len(f.Drills) > 256 {
			return contract("deep resource census exceeds bound")
		}
		centres := map[[2]int32]bool{}
		var cells uint64
		for _, row := range f.Lumps {
			if row == nil || validID(row.GetDefName()) != nil || row.Count == nil || row.GetCount() <= 0 || row.CellCount == nil || row.GetCellCount() == 0 || !colonyCell(row.Centre, v.MapSize) {
				return contract("invalid deep resource lump")
			}
			key := [2]int32{row.Centre.GetX(), row.Centre.GetZ()}
			if centres[key] || row.GetCount() < int64(row.GetCellCount()) || row.GetCount() > int64(row.GetCellCount())*65535 {
				return contract("invalid deep resource aggregation")
			}
			centres[key] = true
			cells += uint64(row.GetCellCount())
		}
		if cells > uint64(v.MapSize.GetWidth())*uint64(v.MapSize.GetHeight()) {
			return contract("deep resource cells exceed map")
		}
		seen := map[string]bool{}
		for _, rows := range [][]*o.MineralScannerState{f.GroundScanners, f.LongRangeScanners} {
			for _, row := range rows {
				if row == nil || validID(row.GetBuildingId()) != nil || validID(row.GetDefName()) != nil || seen[row.GetBuildingId()] || !colonyCell(row.Position, v.MapSize) || !row.GetBuilt() || row.GetTicksToNextFind() < 0 || row.TargetResource != nil && validID(row.GetTargetResource()) != nil {
					return contract("invalid mineral scanner")
				}
				seen[row.GetBuildingId()] = true
			}
		}
		// A depleted drill carries no deposit; an undepleted one names its exact
		// resource and a positive remainder. Ownership and designation are always stated.
		for _, row := range f.Drills {
			if row == nil || validID(row.GetBuildingId()) != nil || validID(row.GetDefName()) != nil || seen[row.GetBuildingId()] || !colonyCell(row.Position, v.MapSize) || row.Depleted == nil || row.Designated == nil || row.Powered == nil {
				return contract("invalid deep drill")
			}
			if row.GetDepleted() && (row.Resource != nil || row.Remaining != nil) || !row.GetDepleted() && (validID(row.GetResource()) != nil || row.Remaining == nil || row.GetRemaining() <= 0) {
				return contract("invalid deep drill deposit")
			}
			seen[row.GetBuildingId()] = true
		}
		return nil
	default:
		return contract("missing deep resource outcome")
	}
}
