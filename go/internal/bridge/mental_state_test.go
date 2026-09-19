package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestMentalStatePresence(t *testing.T) {
	for _, tt := range []struct {
		name, payload  string
		known, invalid bool
	}{
		{"absent", `{}`, false, false},
		{"older producer", `{"mentalState":"Berserk"}`, false, false},
		{"missing aggression", `{"mentalState":"Berserk","mentalStateTicks":30}`, false, false},
		{"missing age", `{"mentalState":"Berserk","mentalStateIsAggro":true}`, false, false},
		{"missing def", `{"mentalStateIsAggro":true,"mentalStateTicks":30}`, false, false},
		{"berserk", `{"mentalState":"Berserk","mentalStateIsAggro":true,"mentalStateTicks":30}`, true, false},
		{"false and zero", `{"mentalState":"Wander_Sad","mentalStateIsAggro":false,"mentalStateTicks":0}`, true, false},
		{"negative age", `{"mentalState":"Berserk","mentalStateIsAggro":true,"mentalStateTicks":-1}`, false, true},
		{"empty def", `{"mentalState":"","mentalStateIsAggro":true,"mentalStateTicks":0}`, false, true},
	} {
		t.Run(tt.name, func(t *testing.T) {
			row := &o.PawnState{}
			if err := protojson.Unmarshal([]byte(tt.payload), row); err != nil {
				t.Fatal(err)
			}
			row.Pawn = &o.EntityRef{Id: proto.String("pawn")}
			got, err := emergencyPawn(row)
			if (err != nil) != tt.invalid {
				t.Fatalf("error = %v", err)
			}
			state, known := got.MentalState.Value()
			if known != tt.known {
				t.Fatalf("known = %v", known)
			}
			if known && (state.DefName != row.GetMentalState() || state.IsAggro != row.GetMentalStateIsAggro() || state.TicksInState != row.GetMentalStateTicks()) {
				t.Fatalf("state = %+v", state)
			}
		})
	}
}

func TestMentalStateUnavailableConflict(t *testing.T) {
	for _, field := range []string{"mental_state", "mental_state_is_aggro", "mental_state_ticks"} {
		row := &o.PawnState{MentalState: proto.String("Berserk"), MentalStateIsAggro: proto.Bool(true), MentalStateTicks: proto.Int32(0),
			Issues: []*o.ReadIssue{{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
		if _, err := PawnMentalState(row); err == nil {
			t.Fatalf("accepted contradictory %s", field)
		}
	}
}
