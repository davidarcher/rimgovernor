package buildingruntime

import (
	"context"
	"errors"
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

type pawnReader interface {
	ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
}

// pawnSection is the colonists' routine pawn detail (ReadRoutinePawns for
// the emergency census's roster) as a mirror section keyed by pawn id. The
// bridge completes every #773 delta reply before returning it, so each
// read is a keyframe of the roster asked; the recording keeps only the
// rows that changed.
type pawnSection struct {
	native  pawnReader
	id      *c.Identity
	ids     []string
	last    *o.PawnSnapshot
	reply   *o.ListPawnsReply
	receipt bridge.Result
}

func (p *pawnSection) Name() string                 { return pawnSectionName }
func (p *pawnSection) Equal(a, b *o.PawnState) bool { return proto.Equal(a, b) }
func (p *pawnSection) Read(ctx context.Context, _ mirror.Watermark) (mirror.Read[string, *o.PawnState], error) {
	reply, receipt, err := p.native.ReadRoutinePawns(ctx, p.id, p.ids)
	p.reply, p.receipt = reply, receipt
	if err != nil {
		return mirror.Read[string, *o.PawnState]{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return mirror.Read[string, *o.PawnState]{}, errUnobserved
	}
	p.last = observed
	rows := make(map[string]*o.PawnState, len(observed.GetPawns()))
	for _, row := range observed.GetPawns() {
		rows[row.GetPawn().GetId()] = row
	}
	return mirror.Read[string, *o.PawnState]{AsOf: mirror.At(observed.GetContext().GetTick()), Rows: rows}, nil
}

// errUnobserved is a pawn reply without an observed snapshot (unavailable,
// a failure): nothing is published and the census judges the reply itself.
var errUnobserved = errors.New("pawn read not observed")

// readPawns is the review's pawn detail read through the mirror: the
// section is refreshed and the reply rebuilt from the published table, in
// the order the native listed the rows.
func readPawns(ctx context.Context, m *mirror.Mirror, scope mirror.Scope, native pawnReader, id *c.Identity, ids []string) (*o.ListPawnsReply, bridge.Result, error) {
	section := &pawnSection{native: native, id: id, ids: ids}
	table, _, err := mirror.Refresh(ctx, m, scope, 0, section)
	if errors.Is(err, errUnobserved) {
		return section.reply, section.receipt, nil
	}
	if err != nil {
		return nil, section.receipt, err
	}
	observed := &o.PawnSnapshot{Context: section.last.GetContext(), Completeness: section.last.GetCompleteness()}
	for _, row := range section.last.GetPawns() {
		if held, ok := table.Rows[row.GetPawn().GetId()]; ok {
			observed.Pawns = append(observed.Pawns, held)
		}
	}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: observed}}, section.receipt, nil
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
