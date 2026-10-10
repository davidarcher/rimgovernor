package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func odysseyBuildingFixture() *o.BuildingState {
	return &o.BuildingState{Building: &o.EntityRef{Id: proto.String("Building_Hatch1")}, Odyssey: &o.OdysseyBuilding{
		Hackable: &o.HackableState{ProgressPercent: proto.Float64(.25), Defence: proto.Float64(600), Hacked: proto.Bool(false), LockedOut: proto.Bool(false)},
		Portal:   &o.PortalState{PocketMapExists: proto.Bool(true), PocketMapId: proto.Int32(3), StockpileType: proto.String("Gravcore"), Layout: proto.String("AncientStockpile")},
	}}
}

// TestBuildingOdysseyRow: the row block lifts into typed facts, a
// field absent stays unknown, a failed sub-read is named by an issue, and
// malformed blocks are refused.
func TestBuildingOdysseyRow(t *testing.T) {
	row := odysseyBuildingFixture()
	if err := validateBuildingOdyssey(row.Odyssey); err != nil {
		t.Fatal(err)
	}
	hack, known := BuildingHack(row).Value()
	if !known {
		t.Fatal("hack unknown")
	}
	if p, ok := hack.ProgressPercent.Value(); !ok || p != .25 {
		t.Fatal("progress", p, ok)
	}
	if _, ok := hack.Autohack.Value(); ok {
		t.Fatal("an unread autohack flag must stay unknown")
	}
	portal, known := BuildingPortal(row).Value()
	if id, ok := portal.PocketMapID.Value(); !known || !ok || id != 3 {
		t.Fatal("portal", portal, known)
	}
	if kind, _ := portal.StockpileType.Value(); kind != "Gravcore" {
		t.Fatal("stockpile type", kind)
	}
	if _, ok := BuildingHack(&o.BuildingState{}).Value(); ok {
		t.Fatal("a Core-only row must stay unknown")
	}
	failed := &o.BuildingState{Odyssey: &o.OdysseyBuilding{Portal: row.Odyssey.Portal,
		Issues: []*o.ReadIssue{{Field: proto.String("hackable"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}}
	if err := validateBuildingOdyssey(failed.Odyssey); err != nil {
		t.Fatal(err)
	}
	if _, ok := BuildingHack(failed).Value(); ok {
		t.Fatal("a failed hack read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.OdysseyBuilding){
		"progress range":     func(v *o.OdysseyBuilding) { v.Hackable.ProgressPercent = proto.Float64(1.5) },
		"nan progress":       func(v *o.OdysseyBuilding) { v.Hackable.ProgressPercent = proto.Float64(math.NaN()) },
		"map without pocket": func(v *o.OdysseyBuilding) { v.Portal.PocketMapExists = proto.Bool(false) },
		"known and issued":   func(v *o.OdysseyBuilding) { v.Issues = []*o.ReadIssue{{Field: proto.String("hackable")}} },
	} {
		r := odysseyBuildingFixture()
		mutate(r.Odyssey)
		if validateBuildingOdyssey(r.Odyssey) == nil {
			t.Errorf("%s accepted", name)
		}
	}
	if checkBuildingListRow(row) != nil {
		t.Fatal("list row refused")
	}
}
