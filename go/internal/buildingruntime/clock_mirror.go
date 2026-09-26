package buildingruntime

import (
	"context"
	"fmt"
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

// The mirrored sections (#795): each adapts one changed-since read to a
// mirror.Section. The mirror keeps the rows and their watermark and does
// the merge, tombstones, expiry fallback and resync backstop; the
// refreshers file its tables into the facts store the planners read.

// mirrorScope is the mirror scope of a step: the facts scope plus the map,
// so a map change is a keyframe of every section.
func mirrorScope(scope facts.Scope, identity *c.Identity) mirror.Scope {
	return mirror.Scope{Load: scope.Load, Map: identity.GetMapId(), Generation: scope.Generation}
}

// mirrorEvent logs a refresh whose resync compared a delta against a
// keyframe as `[facts] <section> resync drift=<n>`, the line the resync
// backstop has always written; non-zero drift is a bug against the native
// tracker.
func mirrorEvent(ctx context.Context, section facts.Section, out mirror.Outcome) {
	if !out.Checked {
		return
	}
	clockEvent(ctx, "facts", string(section)+"_resync", fmt.Sprintf("%s resync drift=%d", section, out.Drift), "drift", out.Drift, "requested", out.Requested, "since", out.Since.Tick, "changed", out.Changed, "removed", out.Removed, "unchanged", out.Unchanged)
}

// entityMirror is an entity list read (zones, buildings, bill stacks) as a
// mirror section, keyed by entity id.
type entityMirror[T proto.Message] struct {
	section facts.Section
	read    func(since int64) (bridge.EntityRows[T], error)
}

func (e entityMirror[T]) Name() string      { return string(e.section) }
func (e entityMirror[T]) Window() int64     { return bridge.EntityTombstoneWindow }
func (e entityMirror[T]) Equal(a, b T) bool { return proto.Equal(a, b) }
func (e entityMirror[T]) Read(_ context.Context, since mirror.Watermark) (mirror.Read[string, T], error) {
	rows, err := e.read(since.Tick)
	if err != nil {
		return mirror.Read[string, T]{}, err
	}
	return mirror.Read[string, T]{AsOf: mirror.At(rows.AsOf()), Delta: rows.Delta, Rows: rows.Rows, Removed: rows.Removed, Unchanged: rows.Unchanged}, nil
}

// zoneMirror is the policy zone census (observation.ZonesNative) as a
// mirror section. The census header (context, map token) is the last
// read's: every read, delta or full, describes the whole map at its tick.
type zoneMirror struct {
	native interface {
		ReadZoneSection(context.Context, *c.Identity, int64) (bridge.ZonesRead, bridge.Result, error)
	}
	id   *c.Identity
	last bridge.ZonesRead
}

func (z *zoneMirror) Name() string  { return string(facts.Zones) }
func (z *zoneMirror) Window() int64 { return bridge.EntityTombstoneWindow }

// Equal compares facts, excluding the CAS context stamp: an unchanged row
// keeps the tick it was last emitted at (bridge.ZoneDrift).
func (z *zoneMirror) Equal(a, b *o.ZoneState) bool {
	x, y := proto.Clone(a).(*o.ZoneState), proto.Clone(b).(*o.ZoneState)
	x.Snapshot, y.Snapshot = nil, nil
	return proto.Equal(x, y)
}

func (z *zoneMirror) Read(ctx context.Context, since mirror.Watermark) (mirror.Read[string, *o.ZoneState], error) {
	read, _, err := z.native.ReadZoneSection(ctx, z.id, since.Tick)
	if err != nil {
		return mirror.Read[string, *o.ZoneState]{}, err
	}
	z.last = read
	rows := make(map[string]*o.ZoneState, len(read.Rows))
	for _, row := range read.Rows {
		rows[row.GetId()] = row
	}
	return mirror.Read[string, *o.ZoneState]{AsOf: mirror.At(read.AsOf), Delta: read.Delta, Rows: rows, Removed: read.Removed, Unchanged: uint64(read.Unchanged), Counted: read.Delta}, nil
}

// census is the table as the zone census the planners read, rows in id
// order under the last read's header.
func (z *zoneMirror) census(table mirror.Table[string, *o.ZoneState]) bridge.ZonesRead {
	out := bridge.ZonesRead{Context: z.last.Context, MapSnapshot: z.last.MapSnapshot, AsOf: table.AsOf.Tick, Fallback: z.last.Fallback}
	out.Rows = make([]*o.ZoneState, 0, len(table.Rows))
	for _, row := range table.Rows {
		out.Rows = append(out.Rows, row)
	}
	sort.Slice(out.Rows, func(i, j int) bool { return out.Rows[i].GetId() < out.Rows[j].GetId() })
	return out
}

// cellMirror is the planning window over one region as a mirror section,
// keyed by cell; a delta's fogged cells are its tombstones. The cell grid
// keeps no tombstone window (#357): any watermark is answered.
type cellMirror struct {
	native PlanningWindowNative
	id     *c.Identity
	region policy.Rectangle
}

func (w cellMirror) Name() string                    { return string(facts.PlanningCells) }
func (w cellMirror) Equal(a, b policy.SiteCell) bool { return a == b }
func (w cellMirror) Read(ctx context.Context, since mirror.Watermark) (mirror.Read[domain.Cell, policy.SiteCell], error) {
	window, _, err := w.native.ReadPlanningWindow(ctx, w.id, w.region, since.Tick)
	if err != nil {
		return mirror.Read[domain.Cell, policy.SiteCell]{}, err
	}
	return mirror.Read[domain.Cell, policy.SiteCell]{AsOf: mirror.At(window.Context.GetTick()), Delta: window.Delta, Rows: cellRows(window.Cells), Removed: window.Fogged, Unchanged: window.Unchanged}, nil
}

func cellRows(cells []policy.SiteCell) map[domain.Cell]policy.SiteCell {
	out := make(map[domain.Cell]policy.SiteCell, len(cells))
	for _, row := range cells {
		out[row.Cell] = row
	}
	return out
}

// siteCells is a cell table as the window's rows, in row order.
func siteCells(rows map[domain.Cell]policy.SiteCell) []policy.SiteCell {
	out := make([]policy.SiteCell, 0, len(rows))
	for _, row := range rows {
		out = append(out, row)
	}
	bridge.SortSiteCells(out)
	return out
}
