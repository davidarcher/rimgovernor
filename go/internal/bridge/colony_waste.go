package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// validateColonyWaste validates the generic per-tick waste census embedded in
// ColonyFactsSnapshot, mirroring validateDirectUpkeep's entity shape: each
// row's Thing carries an id, def name, map and cell but never a Label or
// Snapshot -- refreshing a waste item's own CAS token is ReadWasteTarget's
// job (see waste_target.go), not this census's.
func validateColonyWaste(v *o.WasteReply, size *o.MapSize, mapID int32) error {
	if v == nil {
		return nil
	}
	switch outcome := v.Outcome.(type) {
	case nil:
		return contract("missing waste outcome")
	case *o.WasteReply_Unavailable:
		return validateUnavailable(outcome.Unavailable)
	case *o.WasteReply_Failure:
		return contract("waste census carries a failure outcome")
	case *o.WasteReply_Observed:
		snapshot := outcome.Observed
		if snapshot == nil {
			return contract("missing waste census")
		}
		if err := colonyCounts(snapshot.Completeness, len(snapshot.Items), 256); err != nil {
			return err
		}
		entity := func(e *o.EntityRef, seen map[string]bool) bool {
			if e == nil || validID(e.GetId()) != nil || validID(e.GetDefName()) != nil || e.MapId == nil || e.GetMapId() != mapID || !colonyCell(e.Position, size) || e.Label != nil || e.Snapshot != nil || seen[e.GetId()] {
				return false
			}
			seen[e.GetId()] = true
			return true
		}
		seen := map[string]bool{}
		for _, row := range snapshot.Items {
			if row == nil || !entity(row.Thing, seen) || row.Count != nil && row.GetCount() < 0 ||
				row.ZoneId != nil && validID(row.GetZoneId()) != nil || row.GraveId != nil && validID(row.GetGraveId()) != nil ||
				row.RotStage != nil && validID(row.GetRotStage()) != nil || row.Kind != nil && validID(row.GetKind()) != nil ||
				row.ProtectedReason != nil && validID(row.GetProtectedReason()) != nil {
				return contract("invalid waste item")
			}
		}
		return nil
	default:
		return contract("unrecognized waste outcome")
	}
}
