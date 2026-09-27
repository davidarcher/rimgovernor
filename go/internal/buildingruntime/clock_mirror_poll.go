package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// MirrorPollNative is the native side of rimgovernor/mirror_poll (#795,
// bridge.Client.MirrorPoll). A native with it feeds the clock journal and
// the polled sections through one long poll; one without it keeps the
// bundle's events page and the per-review list reads.
type MirrorPollNative interface {
	MirrorPoll(context.Context, *mp.MirrorPollRequest) (*mp.MirrorPollReply, bridge.Result, error)
}

// writesPending is the optional client signal that a side-effect call is
// queued (bridge.Client.WritesPending): the poll then does not wait.
type writesPending interface{ WritesPending() bool }

// polledSections are the mirror sections mirror_poll serves, with the
// facts section each is filed under.
var polledSections = []struct {
	section mp.Section
	facts   facts.Section
}{
	{mp.Section_SECTION_BUILDINGS, facts.Buildings},
	{mp.Section_SECTION_BILLS, facts.Bills},
}

const mirrorPollSource = "rimgovernor/mirror_poll"

// mirrorAsks are the asks for the named facts sections: each from the
// mirror's watermark when the mirror holds it under scope, else a
// keyframe ask. The epoch is the one those watermarks belong to.
func (f *clockFacts) mirrorAsks(only map[facts.Section]bool) (*mp.Epoch, []*mp.SectionAsk) {
	f.mu.Lock()
	epoch := f.epoch
	f.mu.Unlock()
	view := f.mirror.View()
	asks := make([]*mp.SectionAsk, 0, len(polledSections))
	for _, polled := range polledSections {
		if only != nil && !only[polled.facts] {
			continue
		}
		ask := &mp.SectionAsk{Section: polled.section.Enum()}
		if mark, ok := view.Watermark(string(polled.facts)); ok && epoch != nil && mirrorScopeOf(epoch) == view.Scope {
			ask.Since = &mp.Watermark{Tick: proto.Int64(mark.Tick), Seq: proto.Uint32(uint32(mark.Seq))}
		}
		asks = append(asks, ask)
	}
	if epoch != nil {
		epoch = proto.Clone(epoch).(*mp.Epoch)
	}
	return epoch, asks
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
// poll's is dropped rather than merged backwards.
func (f *clockFacts) applyMirrorPage(page *mp.MirrorPage) mirrorPageApplied {
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
	for _, section := range page.Sections {
		if section.GetMore() && section.Body == nil {
			out.more = true
			continue
		}
		switch section.GetSection() {
		case mp.Section_SECTION_BUILDINGS:
			n, ok := applyMirrorSection(f, scope, storeScope, facts.Buildings, section, func(k *mp.Keyframe) []*o.BuildingState { return k.Buildings }, func(d *mp.Delta) []*o.BuildingState { return d.Buildings }, func(row *o.BuildingState) string { return row.GetBuilding().GetId() })
			if ok {
				out.served, out.changed = append(out.served, facts.Buildings), out.changed+n
			}
		case mp.Section_SECTION_PLANNING_CELLS:
			if n, ok := f.applyGridSection(scope, storeScope, section); ok {
				out.served, out.changed = append(out.served, facts.PlanningCells), out.changed+n
			}
		case mp.Section_SECTION_BILLS:
			n, ok := applyMirrorSection(f, scope, storeScope, facts.Bills, section, func(k *mp.Keyframe) []*o.BillStack { return k.Bills }, func(d *mp.Delta) []*o.BillStack { return d.Bills }, func(row *o.BillStack) string { return row.GetBench().GetId() })
			if ok {
				out.served, out.changed = append(out.served, facts.Bills), out.changed+n
			}
		}
	}
	return out
}

func applyMirrorSection[T proto.Message](f *clockFacts, scope mirror.Scope, storeScope facts.Scope, name facts.Section, section *mp.SectionPage, keyRows func(*mp.Keyframe) []T, deltaRows func(*mp.Delta) []T, id func(T) string) (int, bool) {
	table, held := mirror.Get[string, T](f.mirror, scope, string(name))
	file := func(t mirror.Table[string, T]) {
		facts.Put(f.store, storeScope, name, facts.Held[EntitySection[T]]{Value: t.Rows, AsOf: t.AsOf.Tick, Complete: true, Source: mirrorPollSource})
	}
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
	file(mirror.Put(f.mirror, scope, string(name), mirror.Merge(table.Rows, read), read.AsOf))
	return len(read.Rows) + len(read.Removed), true
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

// pollEntitySections brings the named entity sections current through one
// immediate mirror poll (no wait, no journal), admitted as a review read:
// the review's replacement for a delta list read per section. It reports
// the sections the page served; the rest fall back to their list reads.
func pollEntitySections(ctx context.Context, native MirrorPollNative, f *clockFacts, identity *c.Identity, sections map[facts.Section]bool) map[facts.Section]bool {
	epoch, asks := f.mirrorAsks(sections)
	if len(asks) == 0 {
		return nil
	}
	request := &mp.MirrorPollRequest{Identity: proto.Clone(identity).(*c.Identity), Epoch: epoch, Asks: asks, ByteBudget: proto.Uint32(bridge.MirrorPollMaxBytes)}
	reply, _, err := native.MirrorPoll(bridge.WithAdmissionClass(ctx, bridge.AdmissionObservation), request)
	if err != nil {
		clockSchedulerLog("mirror poll: review read failed, reading the sections' lists: %v", err)
		return nil
	}
	applied := f.applyMirrorPage(reply.GetPage())
	out := map[facts.Section]bool{}
	for _, section := range applied.served {
		out[section] = true
	}
	return out
}
