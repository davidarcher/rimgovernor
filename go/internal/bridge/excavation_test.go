package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func excavationSiteContext() *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)}
}
func excavationSiteRow(x, z int32, fogged bool) *o.ExcavationCell {
	row := &o.ExcavationCell{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Fogged: proto.Bool(fogged), MineDesignated: proto.Bool(false), Eligible: proto.Bool(false)}
	if fogged {
		row.Blocker = proto.String("Unknown excavation geometry")
		return row
	}
	row.MineableDefName, row.HitPoints, row.RoofDefName, row.HoldsRoof, row.Walkable, row.Eligible = proto.String("Granite"), proto.Int32(900), proto.String("RoofRockThick"), proto.Bool(true), proto.Bool(false), proto.Bool(true)
	return row
}
func excavationSiteReply(rows ...*o.ExcavationCell) *o.ExcavationSiteReply {
	return &o.ExcavationSiteReply{Outcome: &o.ExcavationSiteReply_Observed{Observed: &o.ExcavationSiteSnapshot{
		Context: excavationSiteContext(), Cells: rows,
		SupportAfterRemoval: o.ExcavationSupport_EXCAVATION_SUPPORT_SUPPORTED, RoofCellsChecked: proto.Uint32(12),
		CollapsePending: proto.Bool(false), WorkerAvailable: proto.Bool(true), WorkerIds: []string{"Human1"}, AccessReachable: proto.Bool(true),
	}}}
}
func readExcavationSite(t *testing.T, reply *o.ExcavationSiteReply, cells []domain.Cell) (ExcavationSite, error) {
	t.Helper()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_read_excavation_site" {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	site, _, err := client.ReadExcavationSite(context.Background(), pbIdentity(), cells, domain.Cell{X: 0, Z: 2})
	return site, err
}

func TestReadExcavationSiteDecodesRowsInRequestOrderAndKeepsFogUnknown(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 2}, {X: 2, Z: 2}}
	reply := excavationSiteReply(excavationSiteRow(1, 2, false), excavationSiteRow(2, 2, true))
	reply.GetObserved().SupportAfterRemoval = o.ExcavationSupport_EXCAVATION_SUPPORT_UNKNOWN
	site, err := readExcavationSite(t, reply, cells)
	if err != nil {
		t.Fatal(err)
	}
	if len(site.Cells) != 2 || site.Cells[0].Cell != cells[0] || site.Cells[1].Cell != cells[1] || site.Support != policy.ExcavationSupportUnknown {
		t.Fatal(site)
	}
	visible, fogged := site.Cells[0], site.Cells[1]
	if visible.Fogged || !visible.Eligible || visible.Definition != "Granite" || visible.Roof != "RoofRockThick" || !visible.HoldsRoof {
		t.Fatal(visible)
	}
	if !fogged.Fogged || fogged.Eligible || fogged.Definition != "" {
		t.Fatal(fogged)
	}
	if !site.WorkerAvailable || !site.AccessReachable || len(site.Workers) != 1 || site.RoofCellsChecked != 12 {
		t.Fatal(site)
	}
}

func TestReadExcavationSiteRejectsInconsistentReplies(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 2}}
	for name, edit := range map[string]func(*o.ExcavationSiteSnapshot){
		"row order": func(v *o.ExcavationSiteSnapshot) { v.Cells[0].Cell.X = proto.Int32(5) },
		"row count": func(v *o.ExcavationSiteSnapshot) { v.Cells = append(v.Cells, excavationSiteRow(2, 2, false)) },
		"eligible blocked": func(v *o.ExcavationSiteSnapshot) {
			v.Cells[0].Blocker = proto.String("Faction-owned excavation target is protected")
		},
		"support unspecified": func(v *o.ExcavationSiteSnapshot) {
			v.SupportAfterRemoval = o.ExcavationSupport_EXCAVATION_SUPPORT_UNSPECIFIED
		},
		"collapse pending": func(v *o.ExcavationSiteSnapshot) { v.CollapsePending = proto.Bool(true) },
		"workers without access": func(v *o.ExcavationSiteSnapshot) {
			v.AccessReachable = proto.Bool(false)
		},
		"worker flag without ids": func(v *o.ExcavationSiteSnapshot) { v.WorkerIds = nil },
		"foreign identity": func(v *o.ExcavationSiteSnapshot) {
			v.Context.Identity.ColonyId = proto.String("other")
		},
	} {
		t.Run(name, func(t *testing.T) {
			reply := excavationSiteReply(excavationSiteRow(1, 2, false))
			edit(reply.GetObserved())
			if _, err := readExcavationSite(t, reply, cells); err == nil {
				t.Fatal("inconsistent excavation site accepted")
			}
		})
	}
	if _, err := readExcavationSite(t, excavationSiteReply(), nil); err == nil {
		t.Fatal("empty request accepted")
	}
	if _, err := readExcavationSite(t, excavationSiteReply(), []domain.Cell{{X: 1, Z: 2}, {X: 1, Z: 2}}); err == nil {
		t.Fatal("duplicate cells accepted")
	}
}
