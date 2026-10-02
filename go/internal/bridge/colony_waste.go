package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateColonyWaste validates the generic per-tick waste census embedded in
// ColonyFactsSnapshot, mirroring validateDirectUpkeep's entity shape: each
// row's Thing carries an id, def name, map and cell but never a Label or
// Snapshot: WasteIntent names the item by id and native checks it live.
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
		seen := map[string]bool{}
		for _, row := range snapshot.Items {
			if row == nil || !uniqueRef(row.Thing, seen) || row.Count != nil && row.GetCount() < 0 ||
				!optionalRef(row.Zone) || !optionalRef(row.Grave) ||
				row.RotStage != nil && o.RotStage_name[int32(row.GetRotStage())] == "" || row.Kind != nil && WasteKindName(row.GetKind()) == "" ||
				row.ProtectedReason != nil && validID(row.GetProtectedReason()) != nil ||
				row.CorpseClass != nil && !CorpseOf(row.GetCorpseClass()).Valid() {
				return contract("invalid waste item")
			}
		}
		return nil
	default:
		return contract("unrecognized waste outcome")
	}
}
