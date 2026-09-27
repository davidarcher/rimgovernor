package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Mirror helpers for the planning window: the rows it holds across steps
// in the recorded mirror (#795), each refresh read whole since #858.

// mirrorScope is the mirror scope of a step: the facts scope plus the map,
// so a map change is a keyframe of every section.
func mirrorScope(scope facts.Scope, identity *c.Identity) mirror.Scope {
	return mirror.Scope{Load: scope.Load, Map: identity.GetMapId(), Generation: scope.Generation}
}

func cellRows(cells []policy.SiteCell) map[domain.Cell]policy.SiteCell {
	out := make(map[domain.Cell]policy.SiteCell, len(cells))
	for _, row := range cells {
		out[row.Cell] = row
	}
	return out
}

// Section names of the review's own mirrored reads (#795 step 3).
const (
	pawnSectionName  = "pawns"
	benchSectionName = "benches"
)

// publishPawns publishes the review frame's colonist pawn detail as the
// pawn section keyed by pawn id; the recording keeps only the rows that
// changed.
func publishPawns(m *mirror.Mirror, scope mirror.Scope, observed *o.PawnSnapshot) {
	rows := make(map[string]*o.PawnState, len(observed.GetPawns()))
	for _, row := range observed.GetPawns() {
		rows[row.GetPawn().GetId()] = row
	}
	mirror.Put(m, scope, pawnSectionName, rows, mirror.At(observed.GetContext().GetTick()))
}

// publishBenches reads the gear bench census (each bench's bill stack and
// recipe catalog, bridge.ReadGearBenches) and publishes it as the bench
// section keyed by bench thing id, stamped with the step's tick.
func publishBenches(ctx context.Context, m *mirror.Mirror, scope mirror.Scope, native RoutineWorkBenchSource, id *c.Identity, tick int64) (mirror.Table[string, bridge.GearBenchRead], error) {
	census, _, err := native.ReadGearBenches(ctx, id)
	if err != nil {
		return mirror.Table[string, bridge.GearBenchRead]{}, err
	}
	rows := make(map[string]bridge.GearBenchRead, len(census))
	for _, row := range census {
		rows[row.Bench.ID] = row
	}
	return mirror.Put(m, scope, benchSectionName, rows, mirror.At(tick)), nil
}

// benchRows is a bench table in bench id order, as ReadGearBenches lists it.
func benchRows(rows map[string]bridge.GearBenchRead) []bridge.GearBenchRead {
	out := make([]bridge.GearBenchRead, 0, len(rows))
	for _, row := range rows {
		out = append(out, row)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Bench.ID < out[j].Bench.ID })
	return out
}
