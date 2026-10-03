package observation

import (
	"iter"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// capturableEntities are the entity pawn rows the capture rule decides
// (#1742): every row the game marks an entity, or whose entity fact is
// unread, with the facts the rule reads. A pawn with no holding-platform
// target block is one the colony cannot capture, a known absence.
func capturableEntities(rows iter.Seq[*o.PawnState]) domain.Fact[[]policy.CapturableEntity] {
	out := []policy.CapturableEntity{}
	for row := range rows {
		a, ok := bridge.PawnAnomaly(row.Anomaly).Value()
		if !ok {
			continue
		}
		if entity, known := a.Entity.Value(); known && !entity {
			continue
		}
		e := policy.CapturableEntity{Pawn: domain.PawnID(row.GetPawn().GetId()), Dead: optional(row.Dead), Downed: optional(row.Downed), CanBeCaptured: domain.Unknown[bool](), Held: domain.Unknown[bool](), Need: a.MinContainmentStrength}
		if held, known := a.Held.Value(); known {
			if entity, _ := a.Entity.Value(); held == nil {
				// No block proves "cannot be captured" only for a pawn read
				// as an entity.
				if entity {
					e.CanBeCaptured, e.Held = domain.Known(false), domain.Known(false)
				}
			} else {
				e.CanBeCaptured, e.Held = held.CanBeCaptured, held.Held
			}
		}
		out = append(out, e)
	}
	return domain.Known(out)
}
