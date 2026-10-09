package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func homeColonistsFixture() *o.PawnSnapshot {
	return &o.PawnSnapshot{
		Context: pbContext(),
		Pawns: []*o.PawnState{{
			Pawn:     &o.EntityRef{Id: proto.String("pawn-1"), MapId: proto.Int32(0)},
			Settings: &o.PawnSettings{Work: []*o.WorkSetting{{DefName: proto.String("Doctor"), Disabled: proto.Bool(false), Priority: proto.Int32(1)}}},
		}},
		Completeness: &o.Completeness{Filtered: proto.Uint64(0)},
	}
}
func TestReadHomeColonistsAcceptsValidObservation(t *testing.T) {
	snapshot := homeColonistsFixture()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_list_pawns" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.ListPawnsRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if !q.Filter.GetColonist() || q.Filter.GetIncludeDead() || !q.Details.GetWork() || !q.Details.GetNeeds() || len(q.Filter.Ids) != 0 {
			t.Fatal(q)
		}
		// Native treats an unset detail family as requested and the read
		// refuses a reply carrying one, so every other family is declined
		// explicitly.
		for name, field := range map[string]*bool{"health": q.Details.Health, "equipment": q.Details.Equipment, "biography": q.Details.Biography, "settings": q.Details.Settings, "social": q.Details.Social, "animals": q.Details.Animals, "schedule": q.Details.Schedule} {
			if field == nil || *field {
				t.Fatal(name, q.Details)
			}
		}
		return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}), nil
	}}, time.Second)
	reply, raw, err := client.ReadHomeColonists(context.Background(), pbIdentity())
	if err != nil || len(raw.Envelope) == 0 || !proto.Equal(reply.GetObserved(), snapshot) {
		t.Fatal(reply, err)
	}
}
func TestReadHomeColonistsMalformedEvidence(t *testing.T) {
	edits := map[string]func(*o.PawnSnapshot){
		"world":     func(v *o.PawnSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"duplicate": func(v *o.PawnSnapshot) { v.Pawns = append(v.Pawns, v.Pawns[0]) },
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			snapshot := homeColonistsFixture()
			edit(snapshot)
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
				return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}), nil
			}}, time.Second)
			if _, _, err := client.ReadHomeColonists(context.Background(), pbIdentity()); err == nil {
				t.Fatal("malformed roster accepted")
			}
		})
	}
}
