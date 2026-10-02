package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A reference resolves only to a row whose service state is well formed;
// any other row, like an absent one, waits for a later frame.
func TestBuildingTableResolvesWellFormedRows(t *testing.T) {
	for _, phase := range []string{"valid", "unfuelled", "missing", "no-service", "no-settings", "negative-fuel", "bad-fuel-def", "nonfinite-output", "disconnected-network", "fuel-conflict"} {
		t.Run(phase, func(t *testing.T) {
			ref := &o.EntityRef{Id: proto.String("generator"), MapId: proto.Int32(0)}
			s := &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(false), PowerOutputW: proto.Float64(0), SwitchedOn: proto.Bool(true), PowerNetId: proto.String("net"), Fuel: proto.Float64(0), TargetFuel: proto.Float64(30), OutOfFuel: proto.Bool(true), BrokenDown: proto.Bool(false), AllowedFuelDefs: []string{"WoodLog"}}
			row := &o.BuildingState{Building: proto.Clone(ref).(*o.EntityRef), Service: s, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}
			switch phase {
			case "unfuelled":
				s.Fuel, s.TargetFuel, s.OutOfFuel, s.AllowedFuelDefs = nil, nil, nil, nil
				s.Issues = []*o.ReadIssue{{Field: proto.String("fuel"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			case "missing":
				row.Building.Id = proto.String("other")
			case "no-service":
				row.Service = nil
			case "no-settings":
				row.Settings = nil
			case "negative-fuel":
				s.Fuel = proto.Float64(-1)
			case "bad-fuel-def":
				s.AllowedFuelDefs = []string{""}
			case "nonfinite-output":
				s.PowerOutputW = proto.Float64(math.Inf(1))
			case "disconnected-network":
				s.Connected = proto.Bool(false)
			case "fuel-conflict":
				s.Issues = []*o.ReadIssue{{Field: proto.String("fuel"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			}
			table := BuildingTable(&o.BuildingsSnapshot{Buildings: []*o.BuildingState{row}})
			got, ok := table.Row(ref)
			if want := phase == "valid" || phase == "unfuelled"; ok != want || ok && got != row {
				t.Fatalf("%s: resolved %t, want %t", phase, ok, want)
			}
		})
	}
}
