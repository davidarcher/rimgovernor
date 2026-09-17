package boundary

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestJobEvidenceExpectedJobIdle(t *testing.T) {
	job := &n.JobEvidence{
		PlayerForced: proto.Bool(false),
		QueuedJobs:   proto.Uint32(0),
		Issues:       []*n.ReadIssue{{Field: proto.String("current_job")}},
	}
	got, ok := JobEvidenceExpectedJob(job)
	if !ok || !got.Idle || got.JobID != nil {
		t.Fatalf("expected idle expected-job, got %+v ok=%v", got, ok)
	}
}

func TestJobEvidenceExpectedJobKnownLoadID(t *testing.T) {
	job := &n.JobEvidence{LoadId: proto.String("482"), PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)}
	got, ok := JobEvidenceExpectedJob(job)
	if !ok || got.Idle || got.JobID == nil || *got.JobID != 482 {
		t.Fatalf("expected job id 482, got %+v ok=%v", got, ok)
	}
}

func TestJobEvidenceExpectedJobUnknownCases(t *testing.T) {
	cases := []*n.JobEvidence{
		nil,
		{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)},                                                               // neither loadId nor current_job issue
		{LoadId: proto.String("not-a-number"), PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)},                         // unparseable
		{LoadId: proto.String("482"), Issues: []*n.ReadIssue{{Field: proto.String("current_job")}}, PlayerForced: proto.Bool(false)}, // contradictory
	}
	for i, job := range cases {
		if _, ok := JobEvidenceExpectedJob(job); ok {
			t.Fatalf("case %d: expected unknown, got ok", i)
		}
	}
}

// The bed assignment write compares the row-level pawn-state token, not the
// draft-control token on the EntityRef; the #98 sleeping run had every
// dispatch refused STALE_IDENTITY while the boundary sent the latter.
func TestPawnStateTokenIsTheRowLevelRef(t *testing.T) {
	ctx := &c.ObservationContext{Tick: proto.Int64(7)}
	row := func() *n.PawnState {
		return &n.PawnState{
			Pawn:     &n.EntityRef{Id: proto.String("pawn"), Snapshot: &n.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("control-token")}},
			Snapshot: &n.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String("pawn"), Token: proto.String("state-token")},
		}
	}
	if got, err := PawnStateToken(row(), ctx); err != nil || got != "state-token" {
		t.Fatal(got, err)
	}
	if got, err := PawnToken(row(), ctx); err != nil || got != "control-token" {
		t.Fatal(got, err)
	}
	for name, change := range map[string]func(*n.PawnState){
		"missing":       func(v *n.PawnState) { v.Snapshot = nil },
		"other entity":  func(v *n.PawnState) { v.Snapshot.EntityId = proto.String("other") },
		"other context": func(v *n.PawnState) { v.Snapshot.Context.Tick = proto.Int64(8) },
		"empty token":   func(v *n.PawnState) { v.Snapshot.Token = proto.String("") },
	} {
		v := row()
		change(v)
		if _, err := PawnStateToken(v, ctx); err == nil {
			t.Fatalf("%s: accepted", name)
		}
	}
}
