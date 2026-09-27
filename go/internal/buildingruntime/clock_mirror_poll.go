package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// MirrorPollNative is the native side of rimgovernor/mirror_poll (#795,
// bridge.Client.MirrorPoll). A native with it feeds the clock journal and
// the polled sections through one long poll.
type MirrorPollNative interface {
	MirrorPoll(context.Context, *mp.MirrorPollRequest) (*mp.MirrorPollReply, bridge.Result, error)
}

// writesPending is the optional client signal that a side-effect call is
// queued (bridge.Client.WritesPending): the poll then does not wait.
type writesPending interface{ WritesPending() bool }

// polledSections are the entity sections mirror_poll serves, with the
// facts section each is filed under. The planning cells, pawns and colony
// facts sections are asked separately.
var polledSections = []struct {
	section mp.Section
	facts   facts.Section
}{
	{mp.Section_SECTION_BUILDINGS, facts.Buildings},
	{mp.Section_SECTION_BILLS, facts.Bills},
	{mp.Section_SECTION_ZONES, facts.Zones},
}

const mirrorPollSource = "rimgovernor/mirror_poll"

// mirrorAsks are the asks for the named facts sections (nil: the poll
// loop's set): each from the mirror's watermark when the mirror holds it
// under the epoch's scope, else a keyframe ask. A delta ask carries the
// resync backstop when it is due (mirror.ResyncDue, or a stale apply
// asked for it). The zones ride only for a native whose zones are the
// policy census (pollZones). The loop also carries the held planning
// window's grid. The epoch is the one those watermarks belong to.
func (f *clockFacts) mirrorAsks(only map[facts.Section]bool) (*mp.Epoch, []*mp.SectionAsk) {
	f.mu.Lock()
	epoch := f.epoch
	pollZones := f.pollZones
	var grid *mp.SectionAsk
	if only == nil && f.grid != nil && epoch != nil && f.gridScope == mirrorScopeOf(epoch) {
		grid = &mp.SectionAsk{Section: mp.Section_SECTION_PLANNING_CELLS.Enum(), Window: bridge.WireRect(f.grid.Rect), Since: mirrorWatermark(f.gridAt)}
	}
	f.mu.Unlock()
	view := f.mirror.View()
	asks := make([]*mp.SectionAsk, 0, len(polledSections)+1)
	for _, polled := range polledSections {
		if only != nil && !only[polled.facts] || only == nil && polled.facts == facts.Zones && !pollZones {
			continue
		}
		ask := &mp.SectionAsk{Section: polled.section.Enum()}
		if mark, ok := view.Watermark(string(polled.facts)); ok && epoch != nil && mirrorScopeOf(epoch) == view.Scope {
			ask.Since = mirrorWatermark(mark)
			f.askResync(ask, polled.facts)
		}
		asks = append(asks, ask)
	}
	if grid != nil {
		f.askResync(grid, facts.PlanningCells)
		asks = append(asks, grid)
	}
	if epoch != nil {
		epoch = proto.Clone(epoch).(*mp.Epoch)
	}
	return epoch, asks
}

// askResync sets a delta ask's resync backstop when it is due.
func (f *clockFacts) askResync(ask *mp.SectionAsk, section facts.Section) {
	due := f.mirror.ResyncDue(string(section))
	if f.store.ResyncDue(section) || due {
		ask.Resync = proto.Bool(true)
	}
}

func mirrorWatermark(w mirror.Watermark) *mp.Watermark {
	return &mp.Watermark{Tick: proto.Int64(w.Tick), Seq: proto.Uint32(uint32(w.Seq))}
}

// mirrorScopeOf is the mirror scope an epoch's rows belong to.
func mirrorScopeOf(epoch *mp.Epoch) mirror.Scope {
	return mirror.Scope{Load: epoch.GetIdentity().GetLoadToken(), Map: epoch.GetIdentity().GetMapId(), Generation: epoch.GetNativeGeneration()}
}

// mirrorPageApplied is what applying one mirror page did.
type mirrorPageApplied struct {
	// served lists the facts sections the page brought current.
	served []facts.Section
	// changed counts the rows and tombstones applied; more reports a
	// section left for the next poll.
	changed int
	more    bool
}

// applyMirrorPage lays a mirror page's sections over the mirror and files
// them into the facts store. A page for another world than the one the
// mirror holds (a load, map or generation the step has not rescoped to)
// is left for the step: the step rescopes, and the next poll asks again.
// A keyframe replaces a section it is newer than; a delta applies only
// over a table it continues (held at or after its from, before its to).
// Watermarks are monotonic per epoch, so a page overtaken by another
// poll's is dropped rather than merged backwards. A delta's resync
// keyframe replaces the merged table once the drift between them is
// logged. The pawns and colony facts sections are not filed here: the
// bridge seeded them into the step's read cache.
func (f *clockFacts) applyMirrorPage(ctx context.Context, page *mp.MirrorPage) mirrorPageApplied {
	var out mirrorPageApplied
	if page == nil || page.Epoch == nil {
		return out
	}
	scope := mirrorScopeOf(page.Epoch)
	held := f.mirror.View().Scope
	if held != (mirror.Scope{}) && held != scope {
		return out
	}
	storeScope := facts.Scope{Load: scope.Load, Generation: scope.Generation}
	if current := f.store.Scope(); current != (facts.Scope{}) && current != storeScope {
		return out
	}
	f.mu.Lock()
	f.epoch = proto.Clone(page.Epoch).(*mp.Epoch)
	f.mu.Unlock()
	serve := func(section facts.Section, n int, ok bool) {
		if ok {
			out.served, out.changed = append(out.served, section), out.changed+n
		}
	}
	for _, section := range page.Sections {
		if section.GetMore() && section.Body == nil {
			out.more = true
			continue
		}
		switch section.GetSection() {
		case mp.Section_SECTION_BUILDINGS:
			n, ok := applyMirrorSection(ctx, f, scope, facts.Buildings, section, func(k *mp.Keyframe) []*o.BuildingState { return k.Buildings }, func(d *mp.Delta) []*o.BuildingState { return d.Buildings }, func(row *o.BuildingState) string { return row.GetBuilding().GetId() }, entityFile[*o.BuildingState](f, storeScope, facts.Buildings))
			serve(facts.Buildings, n, ok)
		case mp.Section_SECTION_BILLS:
			n, ok := applyMirrorSection(ctx, f, scope, facts.Bills, section, func(k *mp.Keyframe) []*o.BillStack { return k.Bills }, func(d *mp.Delta) []*o.BillStack { return d.Bills }, func(row *o.BillStack) string { return row.GetBench().GetId() }, entityFile[*o.BillStack](f, storeScope, facts.Bills))
			serve(facts.Bills, n, ok)
		case mp.Section_SECTION_ZONES:
			header := section.GetKeyframe().GetZones()
			if header == nil {
				header = section.GetDelta().GetZones()
			}
			file := func(t mirror.Table[string, *o.ZoneState]) {
				facts.Put(f.store, storeScope, facts.Zones, facts.Held[bridge.ZonesRead]{Value: zonesCensus(header, t), AsOf: t.AsOf.Tick, Complete: true, Source: mirrorPollSource})
			}
			zones := func(z *o.ZonesSnapshot) []*o.ZoneState { return z.GetZones() }
			n, ok := applyMirrorSection(ctx, f, scope, facts.Zones, section, func(k *mp.Keyframe) []*o.ZoneState { return zones(k.Zones) }, func(d *mp.Delta) []*o.ZoneState { return zones(d.Zones) }, func(row *o.ZoneState) string { return row.GetId() }, file)
			serve(facts.Zones, n, ok)
		case mp.Section_SECTION_PLANNING_CELLS:
			n, ok := f.applyGridSection(ctx, scope, storeScope, section)
			serve(facts.PlanningCells, n, ok)
		case mp.Section_SECTION_PAWNS:
			serve(facts.Section("pawns"), 0, section.GetKeyframe() != nil)
		case mp.Section_SECTION_COLONY_FACTS:
			serve(facts.Section("colony_facts"), 0, section.GetKeyframe() != nil)
		}
	}
	return out
}

// zonesCensus is a zones table as the census the planners read: the
// page's header, rows in id order.
func zonesCensus(header *o.ZonesSnapshot, t mirror.Table[string, *o.ZoneState]) bridge.ZonesRead {
	out := bridge.ZonesRead{Context: header.GetContext(), MapSnapshot: header.GetMapSnapshot(), AsOf: t.AsOf.Tick, Rows: make([]*o.ZoneState, 0, len(t.Rows))}
	for _, row := range t.Rows {
		out.Rows = append(out.Rows, row)
	}
	sort.Slice(out.Rows, func(i, j int) bool { return out.Rows[i].GetId() < out.Rows[j].GetId() })
	return out
}

// entityFile files an entity table into the store as its section.
func entityFile[T proto.Message](f *clockFacts, storeScope facts.Scope, name facts.Section) func(mirror.Table[string, T]) {
	return func(t mirror.Table[string, T]) {
		facts.Put(f.store, storeScope, name, facts.Held[EntitySection[T]]{Value: t.Rows, AsOf: t.AsOf.Tick, Complete: true, Source: mirrorPollSource})
	}
}

func applyMirrorSection[T proto.Message](ctx context.Context, f *clockFacts, scope mirror.Scope, name facts.Section, section *mp.SectionPage, keyRows func(*mp.Keyframe) []T, deltaRows func(*mp.Delta) []T, id func(T) string, file func(mirror.Table[string, T])) (int, bool) {
	table, held := mirror.Get[string, T](f.mirror, scope, string(name))
	var read mirror.Read[string, T]
	switch {
	case section.GetKeyframe() != nil:
		k := section.GetKeyframe()
		read = mirror.Read[string, T]{AsOf: mirrorMark(k.At), Rows: mirrorRowsByID(keyRows(k), id)}
		if held && !table.AsOf.Before(read.AsOf) {
			// Another poll already brought the section further.
			file(table)
			return 0, true
		}
	case section.GetDelta() != nil:
		d := section.GetDelta()
		from, to := mirrorMark(d.From), mirrorMark(d.To)
		if held && !table.AsOf.Before(to) {
			file(table)
			return 0, true
		}
		if !held || table.AsOf.Before(from) {
			return 0, false
		}
		read = mirror.Read[string, T]{AsOf: to, Delta: true, Rows: mirrorRowsByID(deltaRows(d), id), Removed: d.Tombstones}
	default:
		return 0, false
	}
	rows := mirror.Merge(table.Rows, read)
	if r := section.GetResync(); r != nil && read.Delta {
		full := mirrorRowsByID(keyRows(r), id)
		mirrorEvent(ctx, name, mirror.Outcome{Kind: mirror.Resync, Since: table.AsOf, Changed: len(read.Rows), Removed: len(read.Removed), Checked: true, Drift: mirror.Drift(rows, full, mirrorRowEqual[T])})
		rows = full
	}
	file(mirror.Put(f.mirror, scope, string(name), rows, read.AsOf))
	return len(read.Rows) + len(read.Removed), true
}

// mirrorRowEqual compares two rows' facts for the resync drift count,
// excluding the CAS stamps (top-level snapshot and context fields): an
// unchanged row keeps the tick it was last emitted at.
func mirrorRowEqual[T proto.Message](a, b T) bool {
	strip := func(m proto.Message) proto.Message {
		m = proto.Clone(m)
		r := m.ProtoReflect()
		r.Range(func(fd protoreflect.FieldDescriptor, _ protoreflect.Value) bool {
			if name := fd.Name(); name == "snapshot" || name == "context" {
				r.Clear(fd)
			}
			return true
		})
		return m
	}
	return proto.Equal(strip(a), strip(b))
}

func mirrorMark(w *mp.Watermark) mirror.Watermark {
	return mirror.Watermark{Tick: w.GetTick(), Seq: uint64(w.GetSeq())}
}

func mirrorRowsByID[T any](rows []T, id func(T) string) map[string]T {
	out := make(map[string]T, len(rows))
	for _, row := range rows {
		out[id(row)] = row
	}
	return out
}

// reviewPoll is the review step's one immediate mirror poll (no wait, no
// journal), admitted as a review read, over the bundle's world: the named
// entity sections (buildings, bills, and zones for a policy zones
// native), the planning colony facts, and the routine pawn detail of ids
// when pawns is set. The bridge seeds the colony facts and pawns into the
// step's read cache, so the review's reads of them are hits; a section
// the page did not serve falls back to its dedicated read. It reports
// the sections the page served.
func reviewPoll(ctx context.Context, native MirrorPollNative, f *clockFacts, identity *c.Identity, sections map[facts.Section]bool, pawns []string) map[facts.Section]bool {
	epoch, asks := f.mirrorAsks(sections)
	asks = append(asks, bridge.MirrorColonyFactsAsk(identity))
	if len(pawns) > 0 {
		asks = append(asks, bridge.MirrorPawnsAsk(identity, pawns))
	}
	request := &mp.MirrorPollRequest{Identity: proto.Clone(identity).(*c.Identity), Epoch: epoch, Asks: asks, ByteBudget: proto.Uint32(bridge.MirrorPollMaxBytes)}
	reply, _, err := native.MirrorPoll(bridge.WithAdmissionClass(ctx, bridge.AdmissionObservation), request)
	if err != nil {
		clockSchedulerLog("mirror poll: review read failed, reading the sections directly: %v", err)
		return nil
	}
	applied := f.applyMirrorPage(ctx, reply.GetPage())
	out := map[facts.Section]bool{}
	for _, section := range applied.served {
		out[section] = true
	}
	return out
}

// resetEpoch drops the poll epoch: the next asks are keyframes.
func (f *clockFacts) resetEpoch() {
	f.mu.Lock()
	f.epoch = nil
	f.mu.Unlock()
}

// mirrorIdentity is the world the poll loop asks mirror_poll for (#795):
// the held epoch's, else the current scope from one bare bundle read.
func (s *ClockScheduler) mirrorIdentity(ctx context.Context, native ClockEventNative) (*c.Identity, error) {
	s.facts.mu.Lock()
	epoch := s.facts.epoch
	s.facts.mu.Unlock()
	if epoch.GetIdentity() != nil {
		return proto.Clone(epoch.Identity).(*c.Identity), nil
	}
	reply, _, err := native.ReadBundle(ctx, &o.BundleRequest{})
	if err != nil {
		return nil, err
	}
	current := reply.GetObserved().GetContext()
	if err = bridge.ValidateContext(current); err != nil {
		return nil, err
	}
	return proto.Clone(current.Identity).(*c.Identity), nil
}

// pollEntitySections brings the named entity sections current through one
// immediate mirror poll: reviewPoll without the census sections.
func pollEntitySections(ctx context.Context, native MirrorPollNative, f *clockFacts, identity *c.Identity, sections map[facts.Section]bool) map[facts.Section]bool {
	epoch, asks := f.mirrorAsks(sections)
	if len(asks) == 0 {
		return nil
	}
	request := &mp.MirrorPollRequest{Identity: proto.Clone(identity).(*c.Identity), Epoch: epoch, Asks: asks, ByteBudget: proto.Uint32(bridge.MirrorPollMaxBytes)}
	reply, _, err := native.MirrorPoll(bridge.WithAdmissionClass(ctx, bridge.AdmissionObservation), request)
	if err != nil {
		clockSchedulerLog("mirror poll: review read failed, keeping the held sections: %v", err)
		return nil
	}
	applied := f.applyMirrorPage(ctx, reply.GetPage())
	out := map[facts.Section]bool{}
	for _, section := range applied.served {
		out[section] = true
	}
	return out
}
