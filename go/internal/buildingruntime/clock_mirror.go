package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// Mirror helpers for the planning window: the rows it holds across steps
// in the recorded mirror (#795), each refresh read whole since #858.

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

// publishThings publishes the review frame's things table (#1343) as the
// things section keyed by thing id.
func publishThings(m *facts.Store, scope facts.Scope, things bridge.Things, tick int64) {
	facts.PutKeyed(m, scope, bridge.ThingsSection, things.Table, facts.At(tick))
}

// publishPawns publishes the review frame's pawn table (#1343) as the pawn
// section keyed by pawn id; the recording keeps only the rows that
// changed.
func publishPawns(m *facts.Store, scope facts.Scope, pawns bridge.Pawns, tick int64) {
	facts.PutKeyed(m, scope, pawnSectionName, pawns.Table, facts.At(tick))
}

// publishBenches reads the gear bench census (each bench's bill stack and
// recipe catalog, bridge.ReadGearBenches) and publishes it as the bench
// section keyed by bench thing id, stamped with the step's tick.
func publishBenches(ctx context.Context, m *facts.Store, scope facts.Scope, native RoundsWorkBenchSource, id *c.Identity, tick int64) (facts.Table[string, bridge.GearBenchRead], error) {
	census, _, err := native.ReadGearBenches(ctx, id)
	if err != nil {
		return facts.Table[string, bridge.GearBenchRead]{}, err
	}
	rows := make(map[string]bridge.GearBenchRead, len(census))
	for _, row := range census {
		rows[row.Bench.ID] = row
	}
	return facts.PutTable(m, scope, benchSectionName, rows, facts.At(tick)), nil
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
