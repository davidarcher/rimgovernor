package bridge

import (
	"strconv"
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func clockTestRect(minX, minZ, maxX, maxZ int32) *k.Rectangle {
	return &k.Rectangle{Minimum: &c.Cell{X: proto.Int32(minX), Z: proto.Int32(minZ)}, Maximum: &c.Cell{X: proto.Int32(maxX), Z: proto.Int32(maxZ)}}
}

func clockTestInvalidated(o *k.ObservationInvalidated) *k.Event {
	return &k.Event{Owner: clockTestEpoch().Owner, Event: &k.Event_ObservationInvalidated{ObservationInvalidated: o}}
}

// An ObservationInvalidated (#359) is accepted with families alone (an
// older native), with entity ids up to the bound, and with one ordered
// rectangle; the ids must be distinct identifiers and the rectangle's
// corners present, nonnegative and ordered.
func TestClockObservationInvalidationScope(t *testing.T) {
	colony := []k.FactFamily{k.FactFamily_FACT_FAMILY_COLONY}
	many := make([]string, ClockInvalidationEntitiesMax)
	for i := range many {
		many[i] = "Zone_" + strconv.Itoa(i)
	}
	for name, o := range map[string]*k.ObservationInvalidated{
		"families only": {Families: colony, Reason: proto.String("game conditions")},
		"ids":           {Families: colony, EntityIds: []string{"Zone_7", "Zone_8"}},
		"ids at bound":  {Families: colony, EntityIds: many},
		"rectangle":     {Families: colony, Cells: clockTestRect(3, 4, 3, 4)},
		"both":          {Families: colony, EntityIds: []string{"Zone_7"}, Cells: clockTestRect(0, 0, 9, 9)},
	} {
		if err := clockEventsPage(clockWatchPage(clockTestInvalidated(o)), clockEventsRequest()); err != nil {
			t.Fatal(name, err)
		}
	}
	for name, o := range map[string]*k.ObservationInvalidated{
		"no family":        {EntityIds: []string{"Zone_7"}},
		"too many ids":     {Families: colony, EntityIds: append([]string{"Zone_x"}, many...)},
		"duplicate id":     {Families: colony, EntityIds: []string{"Zone_7", "Zone_7"}},
		"blank id":         {Families: colony, EntityIds: []string{" "}},
		"corner missing":   {Families: colony, Cells: &k.Rectangle{Minimum: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}}},
		"negative corner":  {Families: colony, Cells: clockTestRect(-1, 0, 3, 3)},
		"unordered x":      {Families: colony, Cells: clockTestRect(4, 0, 3, 3)},
		"unordered z":      {Families: colony, Cells: clockTestRect(0, 4, 3, 3)},
		"coordinate unset": {Families: colony, Cells: &k.Rectangle{Minimum: &c.Cell{X: proto.Int32(1)}, Maximum: &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}}},
	} {
		if err := clockEventsPage(clockWatchPage(clockTestInvalidated(o)), clockEventsRequest()); err == nil {
			t.Fatal(name, "accepted")
		}
	}
}
