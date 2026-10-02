package bridge

import (
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
