package bridge

import (
	"slices"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Unchanged singleton sections (#1347). Native leaves a section out of a
// frame while its content matches the last one it published, and lists
// every such section's watermark in every frame. The stream holds the last
// carried copy of each, so every frame the table is built from is whole: an
// omitted section whose watermark seq matches the held copy is that copy,
// current at the frame's tick, and is stamped with the frame's context. A
// section absent at another seq changed in a frame this reader skipped: it
// stays absent (reads wait, as for a section that failed to read) and the
// stream asks for a keyframe.

// elidedSection is one omittable BundleSnapshot section.
type elidedSection struct {
	get func(*o.BundleSnapshot) proto.Message
	set func(*o.BundleSnapshot, proto.Message, *c.ObservationContext)
}

var elidedSections = map[string]elidedSection{
	"emergency": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Emergency) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Emergency = m.(*o.StatusSnapshot)
			v.Emergency.Context = ctx
		},
	},
	"colony_facts": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.ColonyFacts) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.ColonyFacts = m.(*o.ColonyFactsSnapshot)
			v.ColonyFacts.Context = ctx
		},
	},
	"population": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Population) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Population = m.(*o.PopulationSnapshot)
			v.Population.Context = ctx
		},
	},
	"research": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Research) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Research = m.(*o.ResearchSnapshot)
			v.Research.Context = ctx
		},
	},
	"traders": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Traders) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.Traders = m.(*o.TradersSnapshot)
			v.Traders.Context = ctx
		},
	},
	"world_progression": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.WorldProgression) },
		set: func(v *o.BundleSnapshot, m proto.Message, ctx *c.ObservationContext) {
			v.WorldProgression = m.(*o.WorldProgressionSnapshot)
			v.WorldProgression.Context = ctx
		},
	},
}

// keyedSection is one keyed table (#1348): pawns, buildings and things.
// Native carries it as a keyframe, every row, or as a delta against the
// table published at the watermark's base_seq: the rows whose hash changed
// and the ids that left. The hold keeps the merged table; consumers read it
// whole and never see a delta.
type keyedSection struct {
	get func(*o.BundleSnapshot) proto.Message
	set func(*o.BundleSnapshot, proto.Message)
	// merge returns base with delta's changed rows and removed ids applied,
	// as a new message at ctx; a nil delta is base unchanged, restamped. A
	// base is never mutated: an earlier frame may still read it.
	merge func(base, delta proto.Message, ctx *c.ObservationContext) proto.Message
}

var keyedSections = map[string]keyedSection{
	"pawns": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Pawns) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Pawns, _ = m.(*o.PawnSnapshot) },
		merge: func(base, delta proto.Message, ctx *c.ObservationContext) proto.Message {
			b, d := base.(*o.PawnSnapshot), asDelta[*o.PawnSnapshot](delta)
			if d == nil {
				d = &o.PawnSnapshot{Completeness: b.Completeness, MeditateAssignmentAvailable: b.MeditateAssignmentAvailable}
			}
			return &o.PawnSnapshot{Context: ctx, Completeness: d.Completeness, MeditateAssignmentAvailable: d.MeditateAssignmentAvailable,
				Pawns: mergeRows(b.Pawns, d.Pawns, d.Removed, func(r *o.PawnState) string { return r.GetPawn().GetId() })}
		},
	},
	"buildings": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Buildings) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Buildings, _ = m.(*o.BuildingsSnapshot) },
		merge: func(base, delta proto.Message, ctx *c.ObservationContext) proto.Message {
			b, d := base.(*o.BuildingsSnapshot), asDelta[*o.BuildingsSnapshot](delta)
			if d == nil {
				d = &o.BuildingsSnapshot{PowerNetworks: b.PowerNetworks, Completeness: b.Completeness}
			}
			return &o.BuildingsSnapshot{Context: ctx, PowerNetworks: d.PowerNetworks, Completeness: d.Completeness,
				Buildings: mergeRows(b.Buildings, d.Buildings, d.Removed, func(r *o.BuildingState) string { return r.GetBuilding().GetId() })}
		},
	},
	"things": {
		get: func(v *o.BundleSnapshot) proto.Message { return nilMessage(v.Things) },
		set: func(v *o.BundleSnapshot, m proto.Message) { v.Things, _ = m.(*o.ThingsSnapshot) },
		merge: func(base, delta proto.Message, ctx *c.ObservationContext) proto.Message {
			b, d := base.(*o.ThingsSnapshot), asDelta[*o.ThingsSnapshot](delta)
			if d == nil {
				d = &o.ThingsSnapshot{}
			}
			return &o.ThingsSnapshot{Context: ctx, Things: mergeRows(b.Things, d.Things, d.Removed, func(r *o.Thing) string { return r.GetThing().GetId() })}
		},
	},
}

// asDelta is m as a T, the zero T for a nil m.
func asDelta[T proto.Message](m proto.Message) T {
	t, _ := m.(T)
	return t
}

// mergeRows is base without the removed ids, its rows replaced in place by
// the changed rows of the same id, then the changed rows base lacks.
func mergeRows[R any](base, changed []*R, removed []string, id func(*R) string) []*R {
	if len(changed) == 0 && len(removed) == 0 {
		return slices.Clone(base)
	}
	gone := make(map[string]bool, len(removed))
	for _, r := range removed {
		gone[r] = true
	}
	next := make(map[string]*R, len(changed))
	for _, r := range changed {
		next[id(r)] = r
	}
	out := make([]*R, 0, len(base)+len(changed))
	for _, r := range base {
		k := id(r)
		if gone[k] {
			continue
		}
		if n, ok := next[k]; ok {
			r = n
			delete(next, k)
		}
		out = append(out, r)
	}
	for _, r := range changed {
		if next[id(r)] == r {
			out = append(out, r)
		}
	}
	return out
}

// nilMessage keeps a typed nil section from becoming a non-nil interface.
func nilMessage[T proto.Message](m T) proto.Message {
	if m.ProtoReflect().IsValid() {
		return m
	}
	return nil
}

type heldSection struct {
	seq     uint64
	section proto.Message
}

// sectionHold is the last carried copy of each omittable section, for one
// world and native generation.
type sectionHold struct {
	world      *c.Identity
	generation uint64
	sections   map[string]heldSection
}

// fill completes v from the hold and records what v carries. held counts
// the sections filled in; gap reports a section v omits that the hold
// cannot supply (a keyframe is due).
func (h *sectionHold) fill(v *o.BundleSnapshot) (held int, gap bool) {
	ctx := v.GetContext()
	if h.sections == nil || !sameIdentity(h.world, ctx.GetIdentity()) || h.generation != ctx.GetNativeGeneration() {
		h.world, h.generation, h.sections = ctx.GetIdentity(), ctx.GetNativeGeneration(), map[string]heldSection{}
	}
	marked := map[string]bool{}
	for _, w := range v.GetWatermarks() {
		name := w.GetSection()
		if keyed, ok := keyedSections[name]; ok && !marked[name] {
			marked[name] = true
			whole := keyed.get(v) != nil && !w.GetDelta()
			if !h.fillKeyed(v, w, keyed, ctx) {
				gap = true
			} else if !whole {
				held++
			}
			continue
		}
		section, known := elidedSections[name]
		if !known || marked[name] {
			continue
		}
		marked[name] = true
		if m := section.get(v); m != nil {
			h.sections[name] = heldSection{seq: w.GetSeq(), section: m}
			continue
		}
		kept, ok := h.sections[name]
		if !ok || kept.seq != w.GetSeq() {
			gap = true
			continue
		}
		section.set(v, kept.section, ctx)
		held++
	}
	// A section without a watermark failed to read: nothing is held for it.
	for name := range h.sections {
		if !marked[name] {
			delete(h.sections, name)
		}
	}
	return held, gap
}

// fillKeyed completes v's keyed table from the hold and records it; false
// when the table is missing and the hold cannot supply it: a delta whose
// base is not the held seq, or an omission at an unheld seq. Either way the
// hold forgets the table and a keyframe is due.
func (h *sectionHold) fillKeyed(v *o.BundleSnapshot, w *o.SectionWatermark, k keyedSection, ctx *c.ObservationContext) bool {
	name := w.GetSection()
	kept, have := h.sections[name]
	m := k.get(v)
	switch {
	case m != nil && !w.GetDelta():
		h.sections[name] = heldSection{seq: w.GetSeq(), section: m}
		return true
	case m != nil && have && kept.seq == w.GetBaseSeq():
		merged := k.merge(kept.section, m, ctx)
		h.sections[name] = heldSection{seq: w.GetSeq(), section: merged}
		k.set(v, merged)
		return true
	case m == nil && have && kept.seq == w.GetSeq():
		k.set(v, k.merge(kept.section, nil, ctx))
		return true
	}
	delete(h.sections, name)
	k.set(v, nil)
	return false
}
