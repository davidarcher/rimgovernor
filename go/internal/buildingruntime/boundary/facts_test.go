package boundary

import (
	"testing"

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
