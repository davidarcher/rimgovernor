package buildingruntime

import (
	"context"
	"reflect"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
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

// pawnSection is the review frame's colonist pawn detail as a mirror
// section keyed by pawn id; the recording keeps only the rows that changed.
type pawnSection struct{ observed *o.PawnSnapshot }

func (p pawnSection) Name() string                 { return pawnSectionName }
func (p pawnSection) Equal(a, b *o.PawnState) bool { return proto.Equal(a, b) }
func (p pawnSection) Read(context.Context, mirror.Watermark) (mirror.Read[string, *o.PawnState], error) {
	rows := make(map[string]*o.PawnState, len(p.observed.GetPawns()))
	for _, row := range p.observed.GetPawns() {
		rows[row.GetPawn().GetId()] = row
	}
	return mirror.Read[string, *o.PawnState]{AsOf: mirror.At(p.observed.GetContext().GetTick()), Rows: rows}, nil
}

// publishPawns refreshes the pawn section from the review frame's rows.
func publishPawns(ctx context.Context, m *mirror.Mirror, scope mirror.Scope, observed *o.PawnSnapshot) error {
	_, _, err := mirror.Refresh(ctx, m, scope, 0, pawnSection{observed})
	return err
}

// benchSection is the gear bench census (each bench's bill stack and
// recipe catalog, bridge.ReadGearBenches) as a mirror section keyed by
// bench thing id. The native keeps no tracking for it, so every read is a
// keyframe stamped with the step's tick.
type benchSection struct {
	native RoutineWorkBenchSource
	id     *c.Identity
	tick   int64
}

func (b benchSection) Name() string                         { return benchSectionName }
func (b benchSection) Equal(x, y bridge.GearBenchRead) bool { return reflect.DeepEqual(x, y) }
func (b benchSection) Read(ctx context.Context, _ mirror.Watermark) (mirror.Read[string, bridge.GearBenchRead], error) {
	census, _, err := b.native.ReadGearBenches(ctx, b.id)
	if err != nil {
		return mirror.Read[string, bridge.GearBenchRead]{}, err
	}
	rows := make(map[string]bridge.GearBenchRead, len(census))
	for _, row := range census {
		rows[row.Bench.ID] = row
	}
	return mirror.Read[string, bridge.GearBenchRead]{AsOf: mirror.At(b.tick), Rows: rows}, nil
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
