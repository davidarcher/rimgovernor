package buildingruntime

import "github.com/davidarcher/RimGovernor/go/internal/observation"

// stockpileZoneIDs are the native stockpile zones of a complete zones read;
// nil when the read is partial, so no zoned cell is taken for a stockpile.
func stockpileZoneIDs(projection observation.ColonyProjection) map[string]bool {
	if !projection.Zones.Complete {
		return nil
	}
	ids := map[string]bool{}
	for _, row := range projection.Zones.Value.Rows {
		if row.GetType() == "stockpile" {
			ids[row.GetId()] = true
		}
	}
	return ids
}
