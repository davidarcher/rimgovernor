package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestColonyPowerRejectsPartialOrAmbiguousCensus(t *testing.T) {
	for _, phase := range []string{"valid", "unknown", "duplicate", "partial", "foreign-map", "infinite", "extra-detail", "disconnected-network"} {
		t.Run(phase, func(t *testing.T) {
			row := &o.DevelopmentPower{BaseW: proto.Float64(-200), Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String("building"), MapId: proto.Int32(0)}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(true), PowerOutputW: proto.Float64(-200), SwitchedOn: proto.Bool(true), PowerNetId: proto.String("net")}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
			switch phase {
			case "unknown":
				row.BaseW = nil
				row.Building.Service = &o.BuildingServiceState{}
			case "duplicate":
				v.Power = append(v.Power, proto.Clone(row).(*o.DevelopmentPower))
				v.Completeness.Matched = proto.Uint64(2)
				v.Completeness.Returned = proto.Uint64(2)
			case "partial":
				v.Completeness.Page.Complete = proto.Bool(false)
			case "foreign-map":
				row.Building.Building.MapId = proto.Int32(1)
			case "infinite":
				row.BaseW = proto.Float64(math.Inf(1))
			case "extra-detail":
				row.Building.Settings.Flickable = proto.Bool(true)
			case "disconnected-network":
				row.Building.Service.Connected = proto.Bool(false)
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)})
			if (err == nil) != (phase == "valid" || phase == "unknown") {
				t.Fatal(phase, err)
			}
		})
	}
}
