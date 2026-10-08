package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func frameExpeditionTrips(read *bridge.WorldProgressionRead, home domain.MapID) domain.Fact[[]policy.ExpeditionTrip] {
	if read == nil {
		return domain.Unknown[[]policy.ExpeditionTrip]()
	}
	rows := []policy.ExpeditionTrip{}
	for _, caravan := range read.Caravans {
		row := policy.ExpeditionTrip{Tile: domain.Known(caravan.Tile), Destination: optional(caravan.Destination)}
		for _, id := range caravan.PawnIDs {
			row.PawnIDs = append(row.PawnIDs, domain.PawnID(id))
		}
		rows = append(rows, row)
	}
	for _, assembly := range read.Assemblies {
		if domain.MapID(assembly.MapID) != home {
			continue
		}
		row := policy.ExpeditionTrip{Forming: true}
		for _, id := range assembly.PawnIDs {
			row.PawnIDs = append(row.PawnIDs, domain.PawnID(id))
		}
		rows = append(rows, row)
	}
	return domain.Known(rows)
}
