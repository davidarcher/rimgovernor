package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
	"time"
)

func pawnsTestSnapshot() *o.PawnSnapshot {
	return &o.PawnSnapshot{Context: authorityTestContext(7), Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn-1"), MapId: proto.Int32(0)}, Drafted: proto.Bool(false), DraftClaim: &o.DraftClaimObservation{State: &o.DraftClaimObservation_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}}}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(9), Unreadable: proto.Uint64(0)}}
}
func TestPawnsFixedReadPreservesUnknown(t *testing.T) {
	id := pbIdentity()
	ids := []string{"pawn-1", "absent"}
	snapshot := pawnsTestSnapshot()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
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
		expected := &o.ListPawnsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, Filter: &o.PawnFilter{Ids: []string{"pawn-1", "absent"}, IncludeDead: proto.Bool(true)}, Details: &o.PawnDetails{Needs: proto.Bool(false), Health: proto.Bool(false), Equipment: proto.Bool(false), Biography: proto.Bool(false), Settings: proto.Bool(false), Social: proto.Bool(false), Animals: proto.Bool(false)}, Page: &c.PageRequest{Limit: proto.Uint32(2)}}
		if !proto.Equal(q, expected) {
			t.Fatal(q)
		}
		id.LoadToken = proto.String("changed")
		ids[0] = "changed"
		return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}), nil
	}}, time.Second)
	reply, raw, err := client.ReadPawns(context.Background(), id, ids)
	if err != nil || len(raw.Envelope) == 0 || !proto.Equal(reply.GetObserved(), snapshot) {
		t.Fatal(reply, err)
	}
	if reply.GetObserved().Pawns[0].Pawn.Snapshot != nil || reply.GetObserved().Pawns[0].Dead != nil {
		t.Fatal("unknown fabricated")
	}
}
func TestPawnsRequestRejectsBeforeCall(t *testing.T) {
	for _, ids := range [][]string{nil, {""}, {"pawn", "pawn"}, make([]string, 257), {"bad\x00id"}} {
		client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			t.Fatal("invalid request dispatched")
			return nil, nil
		}}, time.Second)
		if _, _, err := client.ReadPawns(context.Background(), pbIdentity(), ids); err == nil {
			t.Fatal(ids)
		}
	}
}
func TestPawnsMalformedEvidence(t *testing.T) {
	edits := map[string]func(*o.PawnSnapshot){
		"world":                                 func(v *o.PawnSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"unknown tick":                          func(v *o.PawnSnapshot) { v.Context.Tick = nil },
		"unknown generation allowed separately": func(v *o.PawnSnapshot) { v.Context.NativeGeneration = proto.Uint64(0) },
		"unrequested":                           func(v *o.PawnSnapshot) { v.Pawns[0].Pawn.Id = proto.String("other") },
		"duplicate": func(v *o.PawnSnapshot) {
			v.Pawns = append(v.Pawns, v.Pawns[0])
			v.Completeness.Matched = proto.Uint64(2)
			v.Completeness.Returned = proto.Uint64(2)
		},
		"partial":       func(v *o.PawnSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"count":         func(v *o.PawnSnapshot) { v.Completeness.Matched = proto.Uint64(10) },
		"unreadable":    func(v *o.PawnSnapshot) { v.Completeness.Unreadable = proto.Uint64(1) },
		"cursor":        func(v *o.PawnSnapshot) { v.Completeness.Page.NextCursor = proto.String("next") },
		"unknown count": func(v *o.PawnSnapshot) { v.Completeness.Filtered = nil },
		"map":           func(v *o.PawnSnapshot) { v.Pawns[0].Pawn.MapId = proto.Int32(1) },
		"nan":           func(v *o.PawnSnapshot) { v.Pawns[0].NearestColonistDistance = proto.Float64(math.NaN()) },
		"detail":        func(v *o.PawnSnapshot) { v.Pawns[0].Health = &o.PawnHealth{} },
		"contradictory issue": func(v *o.PawnSnapshot) {
			v.Pawns[0].Issues = append(v.Pawns[0].Issues, &o.ReadIssue{Field: proto.String("drafted"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}})
		},
		"empty claim": func(v *o.PawnSnapshot) { v.Pawns[0].DraftClaim = &o.DraftClaimObservation{} },
		"bad claim": func(v *o.PawnSnapshot) {
			v.Pawns[0].DraftClaim = &o.DraftClaimObservation{State: &o.DraftClaimObservation_Owned{Owned: &o.OwnedDraftClaim{}}}
		},
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			v := pawnsTestSnapshot()
			edit(v)
			if err := pawnsSnapshot(v, pbIdentity(), map[string]bool{"pawn-1": true, "pawn-2": true}); err == nil {
				t.Fatal("accepted invalid evidence")
			}
		})
	}
}
func TestPawnsOptionalCASAndClaims(t *testing.T) {
	snapshot := pawnsTestSnapshot()
	row := snapshot.Pawns[0]
	row.Issues = nil
	ref := &o.SnapshotRef{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), EntityId: proto.String("pawn-1"), Token: proto.String("opaque")}
	row.Pawn.Snapshot = ref
	row.DraftClaim = &o.DraftClaimObservation{State: &o.DraftClaimObservation_Owned{Owned: &o.OwnedDraftClaim{ClaimId: proto.String("claim"), PawnSnapshot: proto.Clone(ref).(*o.SnapshotRef)}}}
	for _, name := range []string{"valid", "wrongentity", "future", "wrongworld", "mismatchedtoken", "missingtoken"} {
		t.Run(name, func(t *testing.T) {
			v := proto.Clone(snapshot).(*o.PawnSnapshot)
			owned := v.Pawns[0].DraftClaim.GetOwned()
			switch name {
			case "wrongentity":
				owned.PawnSnapshot.EntityId = proto.String("other")
			case "future":
				owned.PawnSnapshot.Context.Tick = proto.Int64(v.Context.GetTick() + 1)
			case "wrongworld":
				owned.PawnSnapshot.Context.Identity.LoadToken = proto.String("other")
			case "mismatchedtoken":
				owned.PawnSnapshot.Token = proto.String("different")
			case "missingtoken":
				owned.PawnSnapshot.Token = nil
			}
			err := pawnsSnapshot(v, pbIdentity(), map[string]bool{"pawn-1": true})
			if (err == nil) != (name == "valid") {
				t.Fatal(err)
			}
		})
	}
	row.Pawn.Snapshot = nil
	row.DraftClaim = &o.DraftClaimObservation{State: &o.DraftClaimObservation_Unowned{Unowned: &o.NoOwnedDraftClaim{}}}
	snapshot.Context.NativeGeneration = nil
	if err := pawnsSnapshot(snapshot, pbIdentity(), map[string]bool{"pawn-1": true}); err != nil {
		t.Fatal(err)
	}
	row.DraftClaim = nil
	if err := pawnsSnapshot(snapshot, pbIdentity(), map[string]bool{"pawn-1": true}); err != nil {
		t.Fatal(err)
	}
}
func TestPawnsTypedFailuresAndEmptyQuery(t *testing.T) {
	for _, kind := range []string{"failure", "unavailable", "empty", "missing"} {
		t.Run(kind, func(t *testing.T) {
			reply := &o.ListPawnsReply{}
			switch kind {
			case "failure":
				reply.Outcome = &o.ListPawnsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}
			case "unavailable":
				reply.Outcome = &o.ListPawnsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}
			case "empty":
				v := pawnsTestSnapshot()
				v.Pawns = nil
				v.Completeness.Matched = proto.Uint64(0)
				v.Completeness.Returned = proto.Uint64(0)
				reply.Outcome = &o.ListPawnsReply_Observed{Observed: v}
			}
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				result := pbResult(reply)
				if kind == "failure" {
					result.IsError = true
				}
				return result, nil
			}}, time.Second)
			_, raw, err := client.ReadPawns(context.Background(), pbIdentity(), []string{"pawn-1"})
			if len(raw.Envelope) == 0 {
				t.Fatal("raw lost")
			}
			switch kind {
			case "empty":
				if err != nil {
					t.Fatal(err)
				}
			case "failure":
				var target *NativeFailure
				if !errors.As(err, &target) {
					t.Fatal(err)
				}
			case "unavailable":
				var target *NativeUnavailable
				if !errors.As(err, &target) {
					t.Fatal(err)
				}
			default:
				if err == nil {
					t.Fatal("missing outcome accepted")
				}
			}
		})
	}
}

// NativeObservationTools.JobRow(null, 0) reports known idle control facts,
// while current-job identity stays absent and explicitly not applicable.
func TestPawnsIdleNativeJobAndEmergencyProjection(t *testing.T) {
	job := &o.JobEvidence{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0), Issues: []*o.ReadIssue{{Field: proto.String("current_job"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum(), Detail: proto.String("Pawn has no current job.")}}}}
	snapshot := pawnsTestSnapshot()
	snapshot.Pawns[0].Job = job
	status := emergencyFixture()
	status.Colonists.Pawns = []*o.PawnState{emergencyRow("pawn-1")}
	status.Colonists.Pawns[0].Job = proto.Clone(job).(*o.JobEvidence)
	status.Colonists.Completeness = emergencyCounts(1)
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		switch arg.Tool {
		case "rimgovernor/observations_list_pawns":
			return pbResult(&o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: snapshot}}), nil
		case "rimgovernor/observations_read_status":
			return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: status}}), nil
		default:
			t.Fatalf("unexpected method %s", arg.Tool)
			return nil, nil
		}
	}}, time.Second)
	reply, _, err := client.ReadPawns(context.Background(), pbIdentity(), []string{"pawn-1"})
	if err != nil {
		t.Fatal(err)
	}
	got := reply.GetObserved().Pawns[0].Job
	if !proto.Equal(got, job) || got.DefName != nil || got.LoadId != nil || got.PlayerForced == nil || got.GetPlayerForced() || got.QueuedJobs == nil || got.GetQueuedJobs() != 0 {
		t.Fatal("idle presence lost", got)
	}
	emergency, _, err := client.ReadEmergency(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete {
		t.Fatal("idle job obscured complete emergency census")
	}
	if len(emergency.Facts.Colonists) != 1 {
		t.Fatal("idle pawn missing")
	}
	pawn := emergency.Facts.Colonists[0]
	for _, fact := range []domain.Fact[bool]{pawn.Dead, pawn.Downed, pawn.Bleeding, pawn.NeedsTend} {
		value, known := fact.Value()
		if !known || value {
			t.Fatal("idle job obscured healthy facts")
		}
	}
}
