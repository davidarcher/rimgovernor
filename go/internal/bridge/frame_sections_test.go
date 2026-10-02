package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// sectionFrame is base at tick, every context retagged, carrying the
// elided sections named in carried and a watermark for each of seqs.
func sectionFrame(base *o.BundleSnapshot, tick int64, seqs map[string]uint64, carried ...string) *o.BundleSnapshot {
	v := proto.Clone(base).(*o.BundleSnapshot)
	retagContexts(v, &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)})
	keep := map[string]bool{}
	for _, name := range carried {
		keep[name] = true
	}
	for name, section := range elidedSections {
		if !keep[name] && section.get(v) != nil {
			switch name {
			case "emergency":
				v.Emergency = nil
			case "colony_facts":
				v.ColonyFacts = nil
			case "population":
				v.Population = nil
			case "research":
				v.Research = nil
			case "traders":
				v.Traders = nil
			case "world_progression":
				v.WorldProgression = nil
			}
		}
	}
	for name, seq := range seqs {
		v.Watermarks = append(v.Watermarks, &o.SectionWatermark{Section: proto.String(name), Seq: proto.Uint64(seq), CapturedTick: proto.Int64(12)})
	}
	return v
}

// TestFramesHoldOmittedSections (#1347): sections omitted for many frames
// are served from the last frame that carried them, stamped with each
// frame's own tick, so no anchor or staleness check against the frame
// trips; a routine frame decodes whole.
func TestFramesHoldOmittedSections(t *testing.T) {
	client, server, ring := frameClient(t)
	seqs := map[string]uint64{"emergency": 1, "colony_facts": 1, "population": 1, "research": 1}
	ring.publish(t, sectionFrame(server.snapshot, 12, seqs, "emergency", "colony_facts", "population", "research"), 0)
	if n := server.familyReads(t, context.Background(), client); n != 0 {
		t.Fatalf("keyframe: %d native family reads, want 0", n)
	}
	for tick := int64(13); tick < 13+50; tick++ {
		seqs["emergency"]++ // the census changes every frame
		ring.publish(t, sectionFrame(server.snapshot, tick, seqs, "emergency"), 0)
		reply, _, err := client.ReadResearch(context.Background(), pbIdentity())
		if err != nil {
			t.Fatalf("tick %d: %v", tick, err)
		}
		if got := reply.Context.GetTick(); got != tick {
			t.Fatalf("held research at tick %d, want the frame's %d", got, tick)
		}
		frame, err := client.ReadRoutineFrame(context.Background(), pbIdentity(), nil)
		if err != nil {
			t.Fatalf("tick %d routine frame: %v", tick, err)
		}
		if frame.Colony == nil || frame.Population == nil || frame.Research == nil {
			t.Fatalf("tick %d: routine frame lost a held section", tick)
		}
		if got := frame.Colony.GetContext().GetTick(); got != tick {
			t.Fatalf("held colony facts at tick %d, want %d", got, tick)
		}
	}
	if n := server.familyCalls(); n != 0 {
		t.Fatalf("%d native family reads, want 0", n)
	}
	select {
	case request := <-server.opens:
		if request.GetKeyframe() {
			t.Fatal("keyframe requested without a gap")
		}
	default:
	}
}

// TestFramesKeyframeOnSeqGap (#1347): a section omitted at a seq the
// stream does not hold changed in a skipped frame. It is not served from
// the stale copy, and the stream asks native for a keyframe.
func TestFramesKeyframeOnSeqGap(t *testing.T) {
	client, server, ring := frameClient(t)
	seqs := map[string]uint64{"emergency": 1, "research": 1}
	ring.publish(t, sectionFrame(server.snapshot, 12, seqs, "emergency", "research"), 0)
	if _, _, err := client.ReadResearch(context.Background(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
	<-server.opens // the subscription's open
	seqs["research"] = 3
	ring.publish(t, sectionFrame(server.snapshot, 20, seqs, "emergency"), 0)
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, _, err := client.ReadResearch(ctx, pbIdentity()); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("research after a gap: %v, want unavailable", err)
	}
	select {
	case request := <-server.opens:
		if !request.GetKeyframe() || len(request.GetResourceSources())+len(request.GetDefinitions()) != 0 {
			t.Fatalf("open %v, want a bare keyframe request", request)
		}
	case <-time.After(time.Second):
		t.Fatal("no keyframe requested")
	}
	ring.publish(t, sectionFrame(server.snapshot, 21, seqs, "emergency", "research"), 0)
	if _, _, err := client.ReadResearch(context.Background(), pbIdentity()); err != nil {
		t.Fatalf("after the keyframe: %v", err)
	}
}

// TestSectionHoldDropsOnWorldChange: a hold never fills a frame of
// another world or native generation.
func TestSectionHoldDropsOnWorldChange(t *testing.T) {
	var h sectionHold
	base := &o.BundleSnapshot{Context: authorityTestContext(7), Research: &o.ResearchSnapshot{Context: authorityTestContext(7)}}
	v := sectionFrame(base, 12, map[string]uint64{"research": 1}, "research")
	if held, gap := h.fill(v); held != 0 || gap {
		t.Fatalf("first frame held %d gap %v", held, gap)
	}
	next := sectionFrame(base, 13, map[string]uint64{"research": 1})
	next.Context.NativeGeneration = proto.Uint64(8)
	if held, gap := h.fill(next); held != 0 || !gap || next.Research != nil {
		t.Fatalf("new generation held %d gap %v research %v", held, gap, next.Research)
	}
}
