package bridge

import (
	"math"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func sleepingWire() *o.UpkeepFacts {
	v := upkeepWire()
	person := proto.Clone(v.Items[0].Item).(*o.EntityRef)
	person.Id = proto.String("pawn")
	bed := proto.Clone(v.Items[0].Item).(*o.EntityRef)
	bed.Id = proto.String("bed")
	v.People = []*o.UpkeepPerson{{Pawn: &o.PawnState{Pawn: person}, OwnedBedId: proto.String(""), ComfortableMinC: proto.Float64(-10), ComfortableMaxC: proto.Float64(30)}}
	v.Beds = []*o.UpkeepBed{{Bed: bed, Slots: proto.Uint32(1), Humanlike: proto.Bool(true), Medical: proto.Bool(false), Prisoners: proto.Bool(false), Roofed: proto.Bool(true), TemperatureC: proto.Float64(-5), RestEffectiveness: proto.Float64(.8), Owners: []string{"pawn"}, AccessibleTo: []string{"pawn"}}}
	return v
}

func TestSleepingUpkeepBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(sleepingWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.People[0].Pawn.Pawn.MapId = proto.Int32(4) },
		func(v *o.UpkeepFacts) { v.Beds = append(v.Beds, v.Beds[0]) },
		func(v *o.UpkeepFacts) { v.People = append(v.People, v.People[0]) },
		func(v *o.UpkeepFacts) { v.Beds[0].Owners = []string{"pawn", "pawn"} },
		func(v *o.UpkeepFacts) { v.Beds[0].TemperatureC = proto.Float64(math.NaN()) },
		func(v *o.UpkeepFacts) { v.People[0].ComfortableMaxC = proto.Float64(-20) },
		func(v *o.UpkeepFacts) { v.Issues = []*o.ReadIssue{{Field: proto.String("beds")}} },
		func(v *o.UpkeepFacts) { v.Issues = []*o.ReadIssue{{Field: proto.String("people")}} },
	} {
		v := sleepingWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("invalid sleeping census accepted", v)
		}
	}
	v := sleepingWire()
	v.People[0].OwnedBedId = nil
	v.Beds[0].Roofed = nil
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("unknown field rejected", err)
	}
}
