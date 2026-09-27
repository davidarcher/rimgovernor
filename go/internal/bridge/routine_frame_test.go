package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// DecodeRoutineFrame checks every section against the frame's own identity
// (#884): a section naming another colony, load or map is a contract error.
func TestDecodeRoutineFrameChecksSectionIdentity(t *testing.T) {
	t.Parallel()
	zones := func(ctx *c.ObservationContext) *o.ZonesSnapshot {
		return &o.ZonesSnapshot{Context: ctx, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
	}
	frame := bundleTestSnapshot()
	frame.Zones = zones(authorityTestContext(7))
	out, err := DecodeRoutineFrame(frame)
	if err != nil || out.Zones == nil || out.Emergency.Context == nil {
		t.Fatal("matching frame refused", out, err)
	}
	for name, identity := range map[string]*c.Identity{
		"colony": {ColonyId: proto.String("other"), LoadToken: proto.String("load"), MapId: proto.Int32(0)},
		"load":   {ColonyId: proto.String("colony"), LoadToken: proto.String("other"), MapId: proto.Int32(0)},
		"map":    {ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)},
	} {
		t.Run("zones/"+name, func(t *testing.T) {
			ctx := authorityTestContext(7)
			ctx.Identity = identity
			frame := bundleTestSnapshot()
			frame.Zones = zones(ctx)
			if _, err := DecodeRoutineFrame(frame); err == nil {
				t.Fatal("foreign zones section accepted")
			}
		})
		t.Run("emergency/"+name, func(t *testing.T) {
			frame := bundleTestSnapshot()
			frame.Emergency.Context.Identity = identity
			frame.Emergency.Colonists.Context.Identity = identity
			if _, err := DecodeRoutineFrame(frame); err == nil {
				t.Fatal("foreign emergency section accepted")
			}
		})
	}
	if _, err := DecodeRoutineFrame(&o.BundleSnapshot{}); err == nil {
		t.Fatal("frame without a context accepted")
	}
}
