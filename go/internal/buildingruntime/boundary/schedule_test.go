package boundary

import (
	"testing"

	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestScheduleMatchesMeditate(t *testing.T) {
	wanted := make([]string, 24)
	var slots []*n.TimetableSlot
	for h := range wanted {
		wanted[h] = "Anything"
		if h == 21 {
			wanted[h] = "Meditate"
		}
		slots = append(slots, &n.TimetableSlot{Hour: proto.Uint32(uint32(h)), AssignmentDefName: proto.String(wanted[h])})
	}
	if !ScheduleMatches(slots, wanted) {
		t.Fatal("Meditate slot did not match")
	}
	slots[21].AssignmentDefName = proto.String("Joy")
	if ScheduleMatches(slots, wanted) {
		t.Fatal("Joy matched Meditate")
	}
}
