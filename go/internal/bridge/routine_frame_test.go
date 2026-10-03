package bridge

import (
	"slices"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The frame's one buildings family (#1338) keeps open blueprints and frames
// as construction sites; the construction census is its built rows.
func TestDecodeRoutineFrameKeepsBlueprintSites(t *testing.T) {
	t.Parallel()
	frame := bundleTestSnapshot()
	row := func(id string, status o.BuildingStatus) *o.BuildingState {
		return &o.BuildingState{Building: &o.EntityRef{Id: proto.String(id)}, Status: status.Enum()}
	}
	frame.Buildings = &o.BuildingsSnapshot{Context: authorityTestContext(7), Buildings: []*o.BuildingState{
		row("blueprint", o.BuildingStatus_BUILDING_STATUS_BLUEPRINT), row("frame", o.BuildingStatus_BUILDING_STATUS_FRAME), row("wall", o.BuildingStatus_BUILDING_STATUS_BUILT)}}
	out, err := DecodeRoutineFrame(frame, nil)
	if err != nil {
		t.Fatal(err)
	}
	var sites, built []string
	for id, r := range out.Buildings.Rows.All() {
		sites = append(sites, id)
		if r.GetStatus() == o.BuildingStatus_BUILDING_STATUS_BUILT {
			built = append(built, id)
		}
	}
	slices.Sort(sites)
	if len(sites) != 3 || sites[0] != "blueprint" || sites[1] != "frame" || len(built) != 1 || built[0] != "wall" {
		t.Fatalf("sites %v built %v", sites, built)
	}
}

// DecodeRoutineFrame checks every section against the frame's own identity
// (#884): a section naming another colony, load or map is a contract error.
func TestDecodeRoutineFrameChecksSectionIdentity(t *testing.T) {
	t.Parallel()
	zones := func(ctx *c.ObservationContext) *o.ZonesSnapshot {
		return &o.ZonesSnapshot{Context: ctx, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
	}
	frame := bundleTestSnapshot()
	frame.Zones = zones(authorityTestContext(7))
	out, err := DecodeRoutineFrame(frame, nil)
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
			if _, err := DecodeRoutineFrame(frame, nil); err == nil {
				t.Fatal("foreign zones section accepted")
			}
		})
		t.Run("emergency/"+name, func(t *testing.T) {
			frame := bundleTestSnapshot()
			frame.Emergency.Context.Identity = identity
			if _, err := DecodeRoutineFrame(frame, nil); err == nil {
				t.Fatal("foreign emergency section accepted")
			}
		})
	}
	if _, err := DecodeRoutineFrame(&o.BundleSnapshot{}, nil); err == nil {
		t.Fatal("frame without a context accepted")
	}
}

// The routine frame a stream serves carries the frame's things table: a
// food stock's reference resolves against it, so a frame without it leaves
// the review's food supply unknown (#1571).
func TestRoutineFrameServesThingsTable(t *testing.T) {
	t.Parallel()
	frame := bundleTestSnapshot()
	frame.Things = &o.ThingsSnapshot{Context: authorityTestContext(7), Things: []*o.Thing{{Thing: &o.EntityRef{Id: proto.String("Thing_Pemmican1")}}}}
	var served *o.BundleSnapshot
	frameReplies(frame, EmergencyObservation{}, func(method string, _, reply proto.Message) {
		if method == routineFrameMethod {
			served = reply.(*o.BundleSnapshot)
		}
	})
	out, err := DecodeRoutineFrame(served, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := out.Tables.Things.Row(&c.Ref{Id: proto.String("Thing_Pemmican1")}); !ok {
		t.Fatalf("things table %v", out.Tables.Things)
	}
}
